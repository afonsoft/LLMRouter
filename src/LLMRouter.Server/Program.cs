using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Gateway;
using LLMRouter.Server.Endpoints;
using LLMRouter.Server.Mitm;
using Microsoft.EntityFrameworkCore;

// SPEC-016: CLI verbs — `llmrouter serve` (default), `reset-password`, `version`.
if (args is ["version" or "--version", ..])
{
    Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0");
    return;
}
if (args is ["reset-password", var newPw, ..])
{
    LLMRouter.Server.Cli.ResetPassword(newPw);
    return;
}
if (args is ["mcp-stdio", ..])
{
    Environment.Exit(await LLMRouter.Server.Cli.McpStdioAsync());
    return;
}
args = args.Where(a => a != "serve").ToArray();

var builder = WebApplication.CreateBuilder(args);
// SPEC-028: CONNECT/TLS-intercept proxy on LLMROUTER_PROXY_PORT (default 8889)
builder.Services.AddHostedService<LLMRouter.Server.Mitm.ConnectProxy>();
builder.Logging.AddProvider(LLMRouter.Server.Services.ConsoleLogBuffer.Instance);

// Resolved inside the AddDbContext factory so test-provided configuration
// (WebApplicationFactory ConfigureAppConfiguration) is visible — reading
// builder.Configuration here would run before the host's config sources merge.
builder.Services.AddDbContext<LlmRouterDbContext>((sp, o) =>
{
    var dbPath = sp.GetRequiredService<IConfiguration>()["Db:Path"]
        ?? Environment.GetEnvironmentVariable("LLMROUTER_DB_PATH")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMRouter", "llmrouter.db");
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    o.UseSqlite($"Data Source={dbPath}");
});
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<ComboPlanner>();
builder.Services.AddSingleton<ModelResolver>();
builder.Services.AddScoped<GatewayEngine>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("upstream").ConfigureHttpClient(c =>
{
    c.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddAuthentication("cookie")
    .AddCookie("cookie", o =>
    {
        o.Cookie.Name = "llmrouter_auth";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
    db.EnsureCreated();
    // Apply persisted resilience overrides to the cooldown tracker (SPEC-007).
    var srow = db.Settings.FirstOrDefault();
    if (srow is not null)
    {
        var sd = System.Text.Json.JsonDocument.Parse(srow.Data).RootElement;
        if (sd.ValueKind == System.Text.Json.JsonValueKind.Object
            && sd.TryGetProperty("resilience", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            int? th = r.TryGetProperty("failureThreshold", out var t1) && t1.TryGetInt32(out var i1) ? i1 : null;
            double? bs = r.TryGetProperty("cooldownBaseSeconds", out var t2) && t2.TryGetDouble(out var d2) ? d2 : null;
            double? ms = r.TryGetProperty("cooldownMaxSeconds", out var t3) && t3.TryGetDouble(out var d3) ? d3 : null;
            LLMRouter.Core.Resilience.CooldownTracker.Configure(th, bs, ms);
        }
    }
}

app.UseAuthentication();
app.UseAuthorization();
// .NET 10 static-asset fingerprinting: index.html references assets as
// "name#{.[fingerprint]}.ext" — the browser drops everything after '#', so the
// request arrives as "/_framework/name" (or "name.ext" that only exists
// fingerprinted). MapStaticAssets() is unavailable for this hosted-WASM setup,
// so rewrite the stem to the real fingerprinted file name BEFORE the blazor
// framework/static-files middleware (which then serves it normally).
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value ?? "";
    if (ctx.Request.Method == "GET" && p.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase))
    {
        var name = Path.GetFileName(p).Split('#')[0];
        var dir = FrameworkDir(ctx.RequestServices.GetRequiredService<IWebHostEnvironment>());
        if (dir != null)
        {
            var hit = FrameworkAssets.Resolve(dir, name);
            if (hit != null && !File.Exists(Path.Combine(dir, name)))
                ctx.Request.Path = "/_framework/" + Path.GetFileName(hit);
        }
    }
    await next();
});
app.UseBlazorFrameworkFiles();
app.Use(ForwardProxy.Invoke);
app.UseStaticFiles();
app.UseStatusCodePagesWithReExecute("/error/{0}");

app.MapAuthEndpoints();
app.MapGatewayEndpoints();
app.MapManagementEndpoints();
CompressionEndpoints.Map(app);
CliEndpoints.Map(app);
AnalyticsEndpoints.Map(app);
DbBackupEndpoints.Map(app);
app.MapQuotaProxyEndpoints();
app.MapToolsEndpoints();
app.MapOAuthEndpoints();
app.MapProtocolEndpoints();
app.MapDocsEndpoints();
app.MapInspectorEndpoints();
app.MapExtrasEndpoints();
app.MapVersionEndpoints();

app.MapFallbackToFile("index.html");
app.Run();

// locate the real _framework dir: publish wwwroot, else the client bin tree in dev.
static string? FrameworkDir(IWebHostEnvironment env)
{
    var pub = Path.Combine(env.WebRootPath ?? "", "_framework");
    if (Directory.Exists(pub)) return pub;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
    {
        var client = Path.Combine(d.FullName, "LLMRouter.Client");
        if (Directory.Exists(client))
            return Directory.EnumerateDirectories(client, "_framework", SearchOption.AllDirectories)
                .OrderByDescending(x => x).FirstOrDefault();
    }
    return null;
}

public partial class Program { }

public static class FrameworkAssets
{
    /// <summary>
    /// Resolve an unfingerprinted request name (e.g. "dotnet.js", "blazor.webassembly")
    /// to the fingerprinted file on disk. Never pick sourcemaps or compressed variants —
    /// "dotnet.js.*" would otherwise match "dotnet.js.map" and serve it as JS.
    /// </summary>
    public static string? Resolve(string dir, string name)
    {
        var ext = Path.GetExtension(name);
        // try "name.*" first (e.g. blazor.webassembly → blazor.webassembly.<fp>.js),
        // then "stem.*ext" (e.g. foo.js → foo.<fp>.js)
        return new[] { name + ".*", ext.Length > 0 ? name[..^ext.Length] + ".*" + ext : null }
            .Where(p => p != null)
            .SelectMany(p => Directory.EnumerateFiles(dir, p!))
            .Where(f => !f.EndsWith(".br") && !f.EndsWith(".gz") && !f.EndsWith(".map"))
            .OrderBy(f => f.Length).FirstOrDefault();
    }
}

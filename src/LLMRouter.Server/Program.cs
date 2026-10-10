using LLMRouter.Server;
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
builder.Services.AddSingleton<LLMRouter.Core.Jobs.JobScheduler>(sp =>
{
    var sched = new LLMRouter.Core.Jobs.JobScheduler(sp, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LLMRouter.Core.Jobs.JobScheduler>>());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.ProxyPoolHealthJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.UsagePruneJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.DbBackupJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.QuotaSchedulesJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.LogExportJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.AutoReenableJob());
    return sched;
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<LLMRouter.Core.Jobs.JobScheduler>());
builder.Services.AddHostedService<LLMRouter.Server.Endpoints.A2aTaskExecutor>();
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
builder.Services.AddSingleton<RateLimiter>();
// SPEC-074: serialized off-path writer for usage/telemetry/config writes
builder.Services.AddSingleton<LLMRouter.Core.Gateway.UsageWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LLMRouter.Core.Gateway.UsageWriter>());
builder.Services.AddScoped<GatewayEngine>(sp => new GatewayEngine(
    sp.GetRequiredService<LlmRouterDbContext>(),
    sp.GetRequiredService<ProviderRegistry>(),
    sp.GetRequiredService<ComboPlanner>(),
    sp.GetRequiredService<ModelResolver>(),
    sp.GetRequiredService<RateLimiter>(),
    sp.GetRequiredService<LLMRouter.Core.Gateway.UsageWriter>()));
builder.Services.AddMemoryCache();
builder.Services.AddHybridCache();
builder.Services.AddHttpClient("batches");
builder.Services.AddHttpClient("logexport");
builder.Services.AddSingleton<LLMRouter.Core.Routing.OneProxyState>();
builder.Services.AddHttpClient("upstream").ConfigureHttpClient(c =>
{
    c.Timeout = TimeSpan.FromMinutes(10);
})
// SPEC-047: route upstream calls through settings.oneproxy when configured
.ConfigurePrimaryHttpMessageHandler(sp =>
    new LLMRouter.Core.Routing.OneProxyHandler(
        sp.GetRequiredService<LLMRouter.Core.Routing.OneProxyState>()))
// SPEC-066: global upstream concurrency cap (settings.data.concurrency)
.AddHttpMessageHandler(() => new LLMRouter.Core.Gateway.ConcurrencyGateHandler());
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

// HybridCache backs the SPEC-074 hot-path cache (stampede protection + tag busts).
LLMRouter.Core.Gateway.HotCache.Configure(
    app.Services.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>());

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
        // SPEC-066: persisted global upstream concurrency cap
        if (sd.ValueKind == System.Text.Json.JsonValueKind.Object
            && sd.TryGetProperty("concurrency", out var cc) && cc.TryGetInt32(out var cap))
            LLMRouter.Core.Gateway.ConcurrencyGate.Set(cap);
    }
}

// SPEC-047: ip-filter — checked before auth/routing
app.Use(async (ctx, next) =>
{
    await using var scope = ctx.RequestServices
        .GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
    var db = scope.ServiceProvider.GetService<LLMRouter.Core.Data.LlmRouterDbContext>();
    if (db is not null)
    {
        var sdata = await LLMRouter.Core.Usage.PricingService.SettingsDataAsync(db);
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        if (!LLMRouter.Core.Routing.RoutingOps.IpAllowed(sdata, ip))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "ip blocked by ipFilter" });
            return;
        }
    }
    await next();
});

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
JobsEndpoints.Map(app);
RateLimitEndpoints.Map(app);
FileEndpoints.Map(app);
KeysQuotaEndpoints.Map(app);
LogExportEndpoints.Map(app);
PlaygroundEndpoints.Map(app);
OpenApiExplorerEndpoints.Map(app);
CacheEndpoints.Map(app);
DiscoveryEndpoints.Map(app);
IntelligenceEndpoints.Map(app);
SettingsOpsEndpoints.Map(app);
SettingsRoutingEndpoints.Map(app);
RelayEndpoints.Map(app);
SessionPoolEndpoints.Map(app);
CliDeviceEndpoints.Map(app);
VscodeEndpoints.Map(app);
EvalsEndpoints.Map(app);
A2aEndpoints.Map(app);
ConductorEndpoints.Map(app);
ProviderOpsEndpoints.Map(app);
ComboOpsEndpoints.Map(app);
ResilienceOpsEndpoints.Map(app);
ModelRegistryEndpoints.Map(app);
AutoCombosEndpoints.Map(app);
RoutingOpsEndpoints.Map(app);
CoreMiscEndpoints.Map(app);
MediaEndpoints.Map(app);
V1ExtrasEndpoints.Map(app);
AdminOpsEndpoints.Map(app);
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
        {
            // Dev-mode UseBlazorFrameworkFiles serves the Debug build — a stale
            // bin/Release _framework would rewrite dotnet.js to a fingerprint
            // dev can't serve (404 → WASM never boots). Prefer by env.
            var want = env.IsDevelopment() ? "Debug" : "Release";
            var dirs = Directory.EnumerateDirectories(client, "_framework", SearchOption.AllDirectories);
            return dirs.FirstOrDefault(x => x.Contains($"bin/{want}", StringComparison.OrdinalIgnoreCase))
                ?? dirs.OrderByDescending(x => x).FirstOrDefault();
        }
    }
    return null;
}

public partial class Program { }

using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Gateway;
using LLMRouter.Server.Endpoints;
using LLMRouter.Server.Mitm;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
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
app.UseBlazorFrameworkFiles();
app.Use(ForwardProxy.Invoke);
app.UseStaticFiles();
app.UseStatusCodePagesWithReExecute("/error/{0}");

app.MapAuthEndpoints();
app.MapGatewayEndpoints();
app.MapManagementEndpoints();
app.MapQuotaProxyEndpoints();
app.MapToolsEndpoints();
app.MapOAuthEndpoints();
app.MapProtocolEndpoints();
app.MapDocsEndpoints();
app.MapInspectorEndpoints();
app.MapExtrasEndpoints();

app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }

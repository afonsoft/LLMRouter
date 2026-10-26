using System.Text.Json;
using LLMRouter.Server;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Gateway;
using LLMRouter.Server.Endpoints;
using LLMRouter.Server.Mitm;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

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
args = args.Where(a => a != "serve" && a != "--headless").ToArray();

var builder = WebApplication.CreateBuilder(args);
// SPEC-028: CONNECT/TLS-intercept proxy on LLMROUTER_PROXY_PORT (default 8889)
builder.Services.AddHostedService<LLMRouter.Server.Mitm.ConnectProxy>();
builder.Services.AddSingleton<LLMRouter.Core.Jobs.JobScheduler>(sp =>
{
    var sched = new LLMRouter.Core.Jobs.JobScheduler(sp, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LLMRouter.Core.Jobs.JobScheduler>>());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.ProxyPoolHealthJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.UsagePruneJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.RetentionPurgeJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.DbBackupJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.QuotaSchedulesJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.LogExportJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.AutoReenableJob());
    sched.Register(new LLMRouter.Core.Jobs.BuiltinJobs.AlertsJob());
    return sched;
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<LLMRouter.Core.Jobs.JobScheduler>());
builder.Services.AddHostedService<LLMRouter.Server.Endpoints.A2aTaskExecutor>();
builder.Logging.AddProvider(LLMRouter.Server.Services.ConsoleLogBuffer.Instance);

// SPEC-101: OpenTelemetry — Prometheus /metrics always on; OTLP exporter
// (metrics+traces) when settings.observability.otlp is configured. Read
// best-effort before migrations run — absent table → OTLP simply stays off.
var obsCfg = LLMRouter.Core.Gateway.ObservabilitySettings.LoadEarly(
    builder.Configuration["Db:Path"]
    ?? Environment.GetEnvironmentVariable("LLMROUTER_DB_PATH")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LLMRouter", "llmrouter.db"));
var otel = builder.Services.AddOpenTelemetry();
otel.WithMetrics(m =>
{
    m.AddMeter(LLMRouter.Core.Gateway.GatewayMetrics.MeterName);
    m.AddPrometheusExporter();
    if (obsCfg.Otlp is { } mo)
        m.AddOtlpExporter(o => ApplyOtlp(o, mo));
});
otel.WithTracing(t =>
{
    t.AddSource(LLMRouter.Core.Gateway.GatewayMetrics.MeterName);
    t.AddAspNetCoreInstrumentation();
    if (obsCfg.Otlp is { } to)
        t.AddOtlpExporter(o => ApplyOtlp(o, to));
});

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
builder.Services.AddHttpClient("gami");
builder.Services.AddHttpClient("logexport");
builder.Services.AddHttpClient("alerts").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<LLMRouter.Core.Routing.OneProxyState>();
builder.Services.AddSingleton<LLMRouter.Server.Services.CliTools.CliToolService>();
builder.Services.AddSingleton<LLMRouter.Server.Services.AgentBridge.AgentBridgeService>();
builder.Services.AddSingleton<LLMRouter.Server.Services.TrafficInspector.TrafficInspectorService>();
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
// SPEC-101: live gauges — breaker state, cooldowns, lockouts, prompt-cache ratio.
LLMRouter.Core.Gateway.GatewayMetrics.RegisterRuntimeGauges();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
    // SPEC-109: EF migrations own the schema; honors settings.database.autoMigrate
    // and LLMR_SKIP_MIGRATE=1.
    LLMRouter.Core.Data.SchemaMigrator.ApplyFromEnvironment(
        db, msg => app.Logger.LogInformation("{Msg}", msg));
    // SPEC-110: chain audit rows written before the hash-chain feature (lazy,
    // idempotent — returns 0 once every row carries prevHash/hash).
    var chained = LLMRouter.Core.Extras.AuditLog.BackfillAsync(db).Result;
    if (chained > 0)
        app.Logger.LogInformation("audit hash-chain backfilled {Count} rows", chained);
    // SPEC-108: settings.usageSink.{sync,blockOnFull} tune the writer.
    LLMRouter.Core.Gateway.UsageWriter.ApplySettings(db);
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

// SPEC-096: maintenance mode — settings.maintenanceMode redirects non-API
// GET traffic to /maintenance; /api, static assets and the page itself pass through.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value ?? "";
    if (ctx.Request.Method == "GET" && !p.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
        && !p.StartsWith("/_", StringComparison.Ordinal)
        && !p.StartsWith("/maintenance", StringComparison.OrdinalIgnoreCase)
        && !p.Contains('.'))
    {
        await using var scope = ctx.RequestServices
            .GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var db = scope.ServiceProvider.GetService<LLMRouter.Core.Data.LlmRouterDbContext>();
        var sdata = db is null ? default
            : await LLMRouter.Core.Usage.PricingService.SettingsDataAsync(db);
        if (sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("maintenanceMode", out var mm)
            && mm.ValueKind == JsonValueKind.True)
        {
            ctx.Response.Redirect("/maintenance");
            return;
        }
    }
    await next();
});

// SPEC-101: settings.observability.metricsAuth gates /metrics behind a valid
// gateway api key (default off — local Prometheus scrapes need no auth). Read
// live so toggling the flag doesn't require a restart.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
    {
        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var sdata = await LLMRouter.Core.Gateway.HotReads.SettingsDataAsync(db);
        if (LLMRouter.Core.Gateway.ObservabilitySettings.Parse(sdata).MetricsAuth
            && !(await LLMRouter.Server.Endpoints.GatewayEndpoints.AuthenticatedKey(ctx, db)).Authed)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "metrics_auth_required" });
            return;
        }
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
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
// SPEC-086 headless mode: LLMROUTER_HEADLESS=1 (or Db:Headless / --headless)
// serves only the gateway + management APIs — no Blazor assets or UI routes.
var headless = builder.Configuration.GetValue<bool>("Db:Headless")
    || Environment.GetEnvironmentVariable("LLMROUTER_HEADLESS") == "1";
LLMRouter.Core.Security.HeadlessMode.Enabled = headless;

if (!headless)
{
    app.UseBlazorFrameworkFiles();
}
app.Use(ForwardProxy.Invoke);
if (!headless)
{
    app.UseStaticFiles();
}
app.UseStatusCodePagesWithReExecute("/error/{0}");

app.MapAuthEndpoints();
app.MapPrometheusScrapingEndpoint(); // SPEC-101: GET /metrics
ProviderScopedEndpoints.Map(app);
V1LeftoversEndpoints.Map(app);
UsageDepthEndpoints.Map(app);
ComboAnalyticsEndpoints.Map(app);
MgmtBatchEndpoints.Map(app);
MediumFeaturesEndpoints.Map(app);
SettingsExtrasEndpoints.Map(app);
app.MapGatewayEndpoints();
app.MapManagementEndpoints();
app.MapConfigRevisionEndpoints();
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
ConfigEndpoints.Map(app);
OpenApiExplorerEndpoints.Map(app);
CacheEndpoints.Map(app);
DiscoveryEndpoints.Map(app);
IntelligenceEndpoints.Map(app);
SettingsOpsEndpoints.Map(app);
SettingsRoutingEndpoints.Map(app);
RelayEndpoints.Map(app);
SessionPoolEndpoints.Map(app);
CliDeviceEndpoints.Map(app);
LLMRouter.Server.Endpoints.CliToolsEndpoints.Map(app);
LLMRouter.Server.Endpoints.AgentBridgeEndpoints.Map(app);
LLMRouter.Server.Endpoints.TrafficInspectorEndpoints.MapAndWire(app);
VscodeEndpoints.Map(app);
UserKeysEndpoints.Map(app);
PaymentEndpoints.Map(app);
PublicCatalogEndpoints.Map(app);
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
MemoryVectorEndpoints.Map(app);
AuditOpsEndpoints.Map(app);
TunnelEndpoints.Map(app);
VersionManagerEndpoints.Map(app);
ServiceEndpoints.Map(app);
SkillsExtrasEndpoints.Map(app);
RadarEndpoints.Map(app);
GamiEndpoints.Map(app);
BotsEndpoints.Map(app);
DetailEndpoints.Map(app);
AlertsEndpoints.Map(app);
SafetyEndpoints.Map(app);
BudgetsEndpoints.Map(app);
app.MapQuotaProxyEndpoints();
app.MapToolsEndpoints();
app.MapOAuthEndpoints();
app.MapOAuthImportEndpoints();
app.MapProviderAuthEndpoints();
app.MapProtocolEndpoints();
app.MapDocsEndpoints();
app.MapInspectorEndpoints();
app.MapExtrasEndpoints();
app.MapVersionEndpoints();

// SPEC-086: loopback-only rail for destructive/probing admin routes
// (settings.data.adminLocalOnly, default on).
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value ?? "";
    if (LLMRouter.Core.Security.SecurityRails.IsLocalOnlyPath(p))
    {
        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var localOnly = true; // default ON for destructive/probing ops
        var row = await db.Kv.FindAsync("settings", "data");
        if (row?.Value is { } raw
            && JsonSerializer.Deserialize<JsonElement>(raw) is { ValueKind: JsonValueKind.Object } data
            && data.TryGetProperty("adminLocalOnly", out var alo))
            localOnly = alo.ValueKind != JsonValueKind.False;
        if (localOnly && !LLMRouter.Core.Security.SecurityRails.IsLocal(ctx.Connection.RemoteIpAddress))
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"error\":\"admin_local_only\"}");
            return;
        }
    }
    await next();
});

// SPEC-104: headless config bootstrap — apply a llmr-config document once.
if (Environment.GetEnvironmentVariable("LLMR_CONFIG_IMPORT") is { Length: > 0 } cfgPath
    && File.Exists(cfgPath))
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<LLMRouter.Core.Registry.ProviderRegistry>();
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath))?.AsObject();
        if (doc is not null)
        {
            var res = await LLMRouter.Core.Config.ConfigAsCode.ImportAsync(
                doc, scopes: null, dryRun: false, redactedSecrets: "keep",
                db, registry, CancellationToken.None);
            app.Logger.LogInformation("LLMR_CONFIG_IMPORT applied: {counts} errors={errs}",
                string.Join(" ", res.Counts.Select(kv => $"{kv.Key}:{kv.Value}")), res.Errors.Count);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "LLMR_CONFIG_IMPORT failed for {path}", cfgPath);
    }
}

if (!headless)
{
    app.MapFallbackToFile("index.html");
}
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

static void ApplyOtlp(OpenTelemetry.Exporter.OtlpExporterOptions o,
    LLMRouter.Core.Gateway.ObservabilitySettings.OtlpConfig c)
{
    o.Endpoint = new Uri(c.Endpoint);
    o.Protocol = c.Protocol.Replace('/', '_').Equals("http_protobuf", StringComparison.OrdinalIgnoreCase)
        ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
        : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
    if (c.Headers.Count > 0)
        o.Headers = string.Join(",", c.Headers.Select(kv => $"{kv.Key}={kv.Value}"));
}

public partial class Program { }

using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-066: admin &amp; misc support APIs — concurrency cap, proxy-pool
/// visibility, network/storage/db health, omniroute compat status, hooks,
/// policies, tags, assess, fallback chains, search stats, monitoring,
/// health/degradation, telemetry summary, token-health, headroom lifecycle.
/// </summary>
public static class AdminOpsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Started = DateTime.UtcNow;

    private static async Task<JsonObject> DataNode(LlmRouterDbContext db)
    {
        var row = await db.Settings.FindAsync(1);
        return JsonNode.Parse(row?.Data ?? "{}")!.AsObject();
    }

    private static async Task SaveData(LlmRouterDbContext db, JsonObject data)
    {
        var row = await db.Settings.FindAsync(1)
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        row.Data = data.ToJsonString();
        await db.SaveChangesAsync();
    }

    // headroom lifecycle state (SPEC-066 compat surface)
    private static bool _headroomRunning;
    private static DateTime? _headroomSince;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- global upstream concurrency cap
        g.MapGet("/admin/concurrency", () => Results.Json(new
        {
            limit = ConcurrencyGate.Limit,
            inFlight = ConcurrencyGate.InFlight,
        }, JsonOpts));
        g.MapPut("/admin/concurrency", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var limit = b.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n) ? n : 0;
            ConcurrencyGate.Set(limit);
            var data = await DataNode(db);
            data["concurrency"] = limit;
            await SaveData(db, data);
            await Core.Extras.Extras.AuditAsync(db, "admin.concurrency", $"limit={limit}");
            return Results.Json(new { ok = true, limit }, JsonOpts);
        });

        // ---- proxy-pool visibility: {providerId: [poolIds]}
        g.MapGet("/admin/proxy-pool-visibility", async (LlmRouterDbContext db) =>
        {
            var data = await DataNode(db);
            return Results.Json(new { visibility = data["proxyPoolVisibility"] ?? new JsonObject() }, JsonOpts);
        });
        g.MapPut("/admin/proxy-pool-visibility", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonNode.ParseAsync(ctx.Request.Body);
            var data = await DataNode(db);
            data["proxyPoolVisibility"] = b?["visibility"]?.DeepClone() ?? new JsonObject();
            await SaveData(db, data);
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- network info probe
        g.MapGet("/network/info", async (IHttpClientFactory hf) =>
        {
            string? egress = null;
            try
            {
                var c = hf.CreateClient("logexport");
                c.Timeout = TimeSpan.FromSeconds(5);
                egress = (await c.GetStringAsync("https://api.ipify.org")).Trim();
            }
            catch { }
            return Results.Json(new
            {
                egressIp = egress,
                hostName = System.Net.Dns.GetHostName(),
                addresses = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                    .Select(a => a.ToString()).Take(8).ToList(),
                httpVersion = "1.1",
            }, JsonOpts);
        });

        // ---- env repair: data dir + perms sanity
        g.MapPost("/system/env/repair", (IConfiguration cfg) =>
        {
            var fixed_ = new List<string>();
            var dbPath = cfg["Db:Path"];
            if (dbPath is not null)
            {
                var dir = Path.GetDirectoryName(dbPath)!;
                if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); fixed_.Add($"created {dir}"); }
                if (!File.Exists(dbPath)) fixed_.Add("db file absent (will be created on boot)");
            }
            var logs = Path.Combine(AppContext.BaseDirectory, "logs");
            if (!Directory.Exists(logs)) { Directory.CreateDirectory(logs); fixed_.Add($"created {logs}"); }
            return Results.Json(new { ok = true, fixes = fixed_ }, JsonOpts);
        });

        // ---- storage + db health
        g.MapGet("/storage/health", (IConfiguration cfg) =>
        {
            var dbPath = cfg["Db:Path"];
            long? size = null, wal = null, free = null;
            if (dbPath is not null && File.Exists(dbPath))
            {
                size = new FileInfo(dbPath).Length;
                var walPath = dbPath + "-wal";
                if (File.Exists(walPath)) wal = new FileInfo(walPath).Length;
                free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dbPath))!).AvailableFreeSpace;
            }
            return Results.Json(new { dbPath, dbBytes = size, walBytes = wal, freeBytes = free }, JsonOpts);
        });
        g.MapGet("/db/health", async (LlmRouterDbContext db) =>
        {
            string? integrity;
            try
            {
                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check";
                integrity = (await cmd.ExecuteScalarAsync())?.ToString();
                await conn.CloseAsync();
            }
            catch (Exception ex) { integrity = $"error: {ex.Message}"; }
            var tables = await db.ProviderConnections.CountAsync()
                + await db.UsageHistory.CountAsync() >= 0;
            return Results.Json(new { ok = integrity == "ok", integrity, tables }, JsonOpts);
        });

        // ---- omniroute compat status + route preview (reuse simulate)
        g.MapGet("/omniroute/status", async (LlmRouterDbContext db) => Results.Json(new
        {
            mode = "dotnet",
            version = "0.1.0",
            uptimeSec = (int)(DateTime.UtcNow - Started).TotalSeconds,
            headless = Environment.GetEnvironmentVariable("LLMROUTER_HEADLESS") == "1",
            providers = await db.ProviderConnections.CountAsync(c => c.IsActive),
            combos = await db.Combos.CountAsync(),
        }, JsonOpts));

        // ---- middleware hooks + policies (JSON rule registry)
        g.MapGet("/middleware/hooks", async (LlmRouterDbContext db) =>
        {
            var data = await DataNode(db);
            return Results.Json(new { hooks = data["middlewareHooks"] ?? new JsonArray() }, JsonOpts);
        });
        g.MapPut("/middleware/hooks", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonNode.ParseAsync(ctx.Request.Body);
            var data = await DataNode(db);
            data["middlewareHooks"] = b?["hooks"]?.DeepClone() ?? new JsonArray();
            await SaveData(db, data);
            return Results.Json(new { ok = true }, JsonOpts);
        });


        // tags on conns+keys already exist via the Tag table (/api/tags,
        // CoreMiscEndpoints) — PUT data.tags mirrors them into connection JSON.
        g.MapPut("/provider-connections/{id}/tags", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound();
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var d = Core.ProviderOps.ProviderRules.DataOf(c);
            d["tags"] = b.GetProperty("tags").Clone();
            c.Data = JsonSerializer.Serialize(d);
            c.UpdatedAt = DateTime.UtcNow.ToString("o");
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- assess: capability probe for one connection (models + features)
        g.MapPost("/provider-connections/{id}/assess", async (string id, LlmRouterDbContext db, IHttpClientFactory hf, ProviderRegistry r, CancellationToken ct) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound();
            var d2 = Core.ProviderOps.ProviderRules.DataOf(c);
            var baseUrl = d2.TryGetValue("baseUrl", out var b) ? b.GetString() : null;
            if (baseUrl is null) return Results.BadRequest(new { error = "no baseUrl" });
            var client = hf.CreateClient("upstream");
            List<string> models = []; var features = new List<string>();
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
                var p = r.GetProvider(c.Provider);
                var secret = d2.TryGetValue("apiKey", out var k) ? k.GetString() : null;
                if (p is not null && secret is { Length: > 0 })
                {
                    var ah = new Dictionary<string, string>();
                    GatewayEngine.ApplyAuth(ah, p, secret);
                    foreach (var kv in ah) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
                var resp = await client.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var j = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                    if (j.TryGetProperty("data", out var d))
                        models = d.EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "").ToList();
                    features.Add("models");
                }
            }
            catch { }
            return Results.Json(new { connection = c.Id, models = models.Count, features, modelNames = models.Take(20) }, JsonOpts);
        });

        // fallback/chains lives in ResilienceOpsEndpoints (existing impl).

        // ---- search providers + stats (mediaKinds search conns + usage)
        g.MapGet("/search/providers", async (LlmRouterDbContext db) =>
        {
            var conns = (await db.ProviderConnections.Where(c => c.IsActive).ToListAsync())
                .Where(c => Core.Routing.MediaKinds.KindsOf(c).Contains("search"))
                .Select(c => new { c.Id, c.Provider, c.Name }).ToList();
            return Results.Json(new { providers = conns }, JsonOpts);
        });
        g.MapGet("/search/stats", async (LlmRouterDbContext db) =>
        {
            var rows = await db.UsageHistory.Where(u => u.Endpoint == "search").ToListAsync();
            return Results.Json(new
            {
                requests = rows.Count,
                ok = rows.Count(r => r.Status is "200" or "ok" or "success"),
                tokens = rows.Sum(r => r.PromptTokens + r.CompletionTokens),
                avgLatencyMs = rows.Count > 0 ? rows.Average(r => r.LatencyMs) : 0,
            }, JsonOpts);
        });

        // ---- monitoring aggregates + health/degradation + telemetry/token-health
        g.MapGet("/monitoring/compression", async (LlmRouterDbContext db) =>
        {
            var data = await DataNode(db);
            var engines = data["engines"] ?? new JsonObject();
            return Results.Json(new { engines }, JsonOpts);
        });
        g.MapGet("/monitoring/health", async (LlmRouterDbContext db) =>
        {
            var recent = await db.UsageHistory.OrderByDescending(u => u.Timestamp).Take(200).ToListAsync();
            var errors = recent.Count(r => !(r.Status is "200" or "ok" or "success"));
            return Results.Json(new
            {
                checked_ = recent.Count, errors,
                errorRate = recent.Count > 0 ? (double)errors / recent.Count : 0,
                degraded = recent.Count >= 10 && errors * 2 > recent.Count,
            }, JsonOpts);
        });
        g.MapGet("/health/degradation", async (LlmRouterDbContext db) =>
        {
            var recent = await db.UsageHistory.OrderByDescending(u => u.Timestamp).Take(100).ToListAsync();
            var errors = recent.Count(r => !(r.Status is "200" or "ok" or "success"));
            var degraded = recent.Count >= 10 && errors * 2 > recent.Count;
            return Results.Json(new { degraded, errors, window = recent.Count }, JsonOpts);
        });
        g.MapGet("/telemetry/summary", async (LlmRouterDbContext db) =>
        {
            var rows = await db.UsageHistory.ToListAsync();
            return Results.Json(new
            {
                requests = rows.Count,
                tokens = rows.Sum(r => r.PromptTokens + r.CompletionTokens),
                cost = rows.Sum(r => r.Cost),
                avgLatencyMs = rows.Count > 0 ? rows.Average(r => r.LatencyMs) : 0,
                providers = rows.Select(r => r.Provider).Distinct().Count(),
            }, JsonOpts);
        });
        // token-health lives in OAuthEndpoints (existing impl).

        // ---- headroom lifecycle (compat surface: start/status/stop)
        g.MapPost("/headroom/start", () =>
        {
            _headroomRunning = true;
            _headroomSince = DateTime.UtcNow;
            return Results.Json(new { running = true }, JsonOpts);
        });
        g.MapGet("/headroom/status", () => Results.Json(new
        {
            running = _headroomRunning,
            since = _headroomSince?.ToString("o"),
            engine = "headroom",
        }, JsonOpts));
        g.MapPost("/headroom/stop", () =>
        {
            _headroomRunning = false;
            return Results.Json(new { running = false }, JsonOpts);
        });
    }
}

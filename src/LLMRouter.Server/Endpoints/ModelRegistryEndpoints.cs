using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-071: model registry — per-model capability overrides (consumed by the
/// gateway's capability-based combo reordering), the synced available-models
/// catalog refreshed from each provider's live /models, and free-provider
/// rankings scored by real availability + latency.
/// </summary>
public static class ModelRegistryEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(30);
    private const int MaxSyncProviders = 50;

    // providers that are inherently free/local (same basis as /api/free-tiers)
    private static readonly string[] LocalFree =
        ["ollama", "lmstudio", "llamacpp", "vllm", "jan", "localai", "custom", "custom-node"];

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- capability overrides CRUD ----
        // capabilityOverrides rows carry a JSON object of capability flags
        // ({vision,tools,json,streaming,reasoning,...}) that REPLACE the
        // registry-detected capability set for that provider/model pair in
        // ComboPlanner.GetCapabilitiesForModel.
        g.MapGet("/model-capability-overrides", async (LlmRouterDbContext db, string? provider) =>
        {
            var q = db.CapabilityOverrides.AsQueryable();
            if (!string.IsNullOrWhiteSpace(provider))
                q = q.Where(o => o.Provider == provider);
            var rows = await q.OrderBy(o => o.Provider).ThenBy(o => o.Model).ToListAsync();
            return Results.Json(new
            {
                overrides = rows.Select(o => new
                {
                    o.Id, o.Provider, o.Model,
                    capabilities = AuthEndpoints.Parse(o.Capabilities),
                    o.UpdatedAt,
                }),
                total = rows.Count,
            }, JsonOpts);
        });

        g.MapPost("/model-capability-overrides", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var provider = Get(b, "provider");
            var model = Get(b, "model");
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
                return Results.BadRequest(new { error = "provider and model are required" });
            if (!b.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "capabilities must be a JSON object" });

            var row = await db.CapabilityOverrides
                .FirstOrDefaultAsync(o => o.Provider == provider && o.Model == model);
            var created = row is null;
            row ??= db.CapabilityOverrides.Add(new CapabilityOverride
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Provider = provider,
                Model = model,
            }).Entity;
            row.Capabilities = caps.GetRawText();
            row.UpdatedAt = Now();
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "capability_override.upsert", $"{provider}/{model}");
            return Results.Json(new
            {
                created,
                @override = new { row.Id, row.Provider, row.Model, capabilities = caps, row.UpdatedAt },
            }, JsonOpts);
        });

        g.MapPut("/model-capability-overrides/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var row = await db.CapabilityOverrides.FindAsync(id);
            if (row is null) return Results.NotFound(new { error = "not found" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (Get(b, "provider") is { } p) row.Provider = p;
            if (Get(b, "model") is { } m) row.Model = m;
            if (b.TryGetProperty("capabilities", out var caps))
            {
                if (caps.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest(new { error = "capabilities must be a JSON object" });
                row.Capabilities = caps.GetRawText();
            }
            row.UpdatedAt = Now();
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "capability_override.update", $"{row.Provider}/{row.Model}");
            return Results.Json(new
            {
                @override = new { row.Id, row.Provider, row.Model, capabilities = AuthEndpoints.Parse(row.Capabilities), row.UpdatedAt },
            }, JsonOpts);
        });

        g.MapDelete("/model-capability-overrides/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var row = await db.CapabilityOverrides.FindAsync(id);
            if (row is null) return Results.NotFound(new { error = "not found" });
            db.CapabilityOverrides.Remove(row);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "capability_override.delete", $"{row.Provider}/{row.Model}");
            return Results.Json(new { success = true });
        });

        // ---- synced available models ----
        // GET lists the syncedModels catalog; POST refreshes it per provider
        // from the provider's live models endpoint (modelsUrl), using the first
        // active connection for credentials. Models that disappear upstream are
        // kept but flagged Available=false.
        g.MapGet("/synced-available-models", async (LlmRouterDbContext db, string? provider, bool? available) =>
        {
            var q = db.SyncedModels.AsQueryable();
            if (!string.IsNullOrWhiteSpace(provider))
                q = q.Where(s => s.Provider == provider);
            if (available is { } av)
                q = q.Where(s => s.Available == av);
            var rows = await q.OrderBy(s => s.Provider).ThenBy(s => s.Model).ToListAsync();
            return Results.Json(new { models = rows, total = rows.Count }, JsonOpts);
        });

        g.MapPost("/synced-available-models", async (HttpContext ctx, LlmRouterDbContext db,
            ProviderRegistry r, IHttpClientFactory hf, CancellationToken ct) =>
        {
            string? onlyProvider = null;
            if (ctx.Request.ContentLength > 0)
            {
                var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
                onlyProvider = Get(b, "provider");
            }

            // sync targets: providers with at least one active connection
            var conns = await db.ProviderConnections
                .Where(c => c.IsActive)
                .OrderBy(c => c.Provider).ThenBy(c => c.Priority).ThenBy(c => c.Name)
                .ToListAsync(ct);
            var providerIds = conns.Select(c => c.Provider)
                .Where(pid => onlyProvider is null || pid == onlyProvider)
                .Distinct()
                .Take(MaxSyncProviders)
                .ToList();
            if (providerIds.Count == 0)
                return Results.BadRequest(new { error = onlyProvider is null
                    ? "no active provider connections to sync"
                    : $"no active connection for provider: {onlyProvider}" });

            var results = new List<object>();
            var synced = 0;
            var okProviders = new List<string>();
            foreach (var pid in providerIds)
            {
                var p = r.GetProvider(pid) ?? await NodeResolver.ResolveAsync(db, pid, ct);
                var conn = conns.First(c => c.Provider == pid);
                var modelsUrl = p is null ? null : r.GetModelsUrl(p);
                if (p is null || modelsUrl is null)
                {
                    results.Add(new { provider = pid, ok = false, count = 0, error = "no models endpoint" });
                    continue;
                }
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(SyncTimeout);
                    var ids = await FetchModelIdsAsync(p, conn, modelsUrl, hf, cts.Token);
                    if (ids is null)
                    {
                        results.Add(new { provider = pid, ok = false, count = 0, error = "upstream fetch failed" });
                        continue;
                    }
                    var count = await UpsertSyncedAsync(db, pid, ids);
                    synced++;
                    okProviders.Add(pid);
                    results.Add(new { provider = pid, ok = true, count });
                }
                catch (Exception ex)
                {
                    results.Add(new { provider = pid, ok = false, count = 0, error = ex.Message });
                }
            }
            await db.SaveChangesAsync(ct);
            await Core.Extras.Extras.AuditAsync(db, "models.sync", $"{synced}/{providerIds.Count} providers");

            // SPEC-076: flag combo steps pinned to models a successful sync
            // dropped; opt-in auto-prune removes them but never empties a combo.
            // Detection must not fail the sync, so errors are swallowed.
            var staleModelRefs = new List<StaleComboRefs.Ref>();
            var prunedModelRefs = new List<StaleComboRefs.Ref>();
            try
            {
                foreach (var pid in okProviders)
                    staleModelRefs.AddRange(await StaleComboRefs.FindAsync(db, pid, ct));
                var sdata = await HotReads.SettingsDataAsync(db);
                var autoPrune = sdata.ValueKind == JsonValueKind.Object
                    && sdata.TryGetProperty(StaleComboRefs.AutoPruneSetting, out var ap)
                    && ap.ValueKind == JsonValueKind.True;
                if (autoPrune && staleModelRefs.Count > 0)
                {
                    prunedModelRefs = await StaleComboRefs.PruneAsync(db, staleModelRefs, ct);
                    foreach (var rf in prunedModelRefs)
                        await Core.Extras.Extras.AuditAsync(db, "combo.stale_model_ref.pruned", rf.ComboName);
                }
            }
            catch { }

            return Results.Json(new { synced, total = providerIds.Count, results, staleModelRefs, prunedModelRefs }, JsonOpts);
        });

        // ---- free provider rankings ----
        // Provider-level rollup of the free-tier set: availability (active
        // connections + request success rate) and latency from usageHistory,
        // with breaker/cooldown penalties. Ranked best-first.
        g.MapGet("/free-provider-rankings", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections.ToListAsync();
            var free = conns.Where(c =>
                LocalFree.Contains(c.Provider)
                || c.Data.Contains("\"free\":true", StringComparison.OrdinalIgnoreCase)
                || c.Data.Contains("\"freeTier\":true", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var freeIds = free.Select(c => c.Id).ToHashSet();

            var stats = await db.UsageHistory
                .Where(u => freeIds.Contains(u.ConnectionId ?? ""))
                .GroupBy(u => u.Provider)
                .Select(x => new
                {
                    provider = x.Key,
                    requests = x.Count(),
                    errors = x.Count(u => u.Status != "ok" && u.Status != "200" && u.Status != null),
                    avgLatency = x.Average(u => (double)u.LatencyMs),
                }).ToListAsync();

            var ranked = free.GroupBy(c => c.Provider).Select(grp =>
            {
                var provider = grp.Key;
                var total = grp.Count();
                var active = grp.Count(c => c.IsActive);
                var st = stats.FirstOrDefault(s => s.provider == provider);
                var successRate = st is { requests: > 0 }
                    ? 1.0 - (double)st.errors / st.requests
                    : (active > 0 ? 1.0 : 0.0);
                var availability = total > 0 ? (double)active / total : 0;
                var avgLatency = st?.avgLatency ?? 0;
                var breaker = Core.Resilience.ProviderBreaker.GetState(provider);
                var cooldownSec = grp
                    .Select(c => Core.Resilience.CooldownTracker.Remaining(c.Id).TotalSeconds)
                    .DefaultIfEmpty(0).Max();

                var score = 60 * successRate
                    + 20 * availability
                    + 20 * (1 - Math.Min(1, avgLatency / 10000))
                    - (breaker == Core.Resilience.ProviderBreaker.State.Open ? 50
                        : breaker == Core.Resilience.ProviderBreaker.State.HalfOpen ? 25 : 0)
                    - Math.Min(20, cooldownSec / 30);

                return new
                {
                    provider,
                    connections = total,
                    activeConnections = active,
                    requests = st?.requests ?? 0,
                    errorRate = st is { requests: > 0 } ? Math.Round((double)st.errors / st.requests, 3) : 0,
                    avgLatencyMs = (long)avgLatency,
                    availability = Math.Round(availability * 100, 1),
                    successRate = Math.Round(successRate * 100, 1),
                    breaker = breaker.ToString(),
                    cooldownSec = (int)cooldownSec,
                    score = Math.Round(score, 1),
                };
            }).OrderByDescending(x => x.score).ToList();

            var rankings = ranked.Select((x, i) => new
            {
                rank = i + 1,
                x.provider, x.connections, x.activeConnections, x.requests,
                x.errorRate, x.avgLatencyMs, x.availability, x.successRate,
                x.breaker, x.cooldownSec, x.score,
            }).ToList();
            var healthy = ranked.Count(x => x.activeConnections > 0
                && x.breaker != "Open" && x.cooldownSec <= 0);
            return Results.Json(new { providers = rankings, total = rankings.Count, healthy }, JsonOpts);
        });
    }

    /// <summary>Fetch the provider's live model id list from its models endpoint.</summary>
    private static async Task<List<string>?> FetchModelIdsAsync(
        ProviderEntry p, ProviderConnection conn, string modelsUrl,
        IHttpClientFactory hf, CancellationToken ct)
    {
        var baseUrl = GatewayEngine.ConnectionBaseUrl(conn, p);
        var url = modelsUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? modelsUrl : $"{baseUrl}/{modelsUrl.TrimStart('/')}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        // SPEC-077: discovery sends auth + the connection's customHeaders — a key
        // needing a routing header (e.g. anthropic-workspace-id) can list models too.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (GatewayEngine.ConnectionSecret(conn) is { } secret)
            GatewayEngine.ApplyAuth(headers, p, secret);
        CustomHeaders.Apply(headers, conn.Data);
        foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        var resp = await hf.CreateClient("upstream").SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        return data.EnumerateArray()
            .Select(x => x.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "")
            .Where(id => id.Length > 0)
            .ToList();
    }

    /// <summary>Upsert syncedModels rows for one provider; stale rows become Available=false.</summary>
    private static async Task<int> UpsertSyncedAsync(LlmRouterDbContext db, string provider, List<string> ids)
    {
        var now = Now();
        var existing = await db.SyncedModels.Where(s => s.Provider == provider).ToListAsync();
        var byModel = existing.ToDictionary(s => s.Model, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!seen.Add(id)) continue;
            if (byModel.TryGetValue(id, out var row))
            {
                row.Available = true;
                row.LastSyncAt = now;
            }
            else
            {
                db.SyncedModels.Add(new SyncedModel
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Provider = provider,
                    Model = id,
                    Available = true,
                    LastSyncAt = now,
                });
            }
        }
        foreach (var row in existing.Where(s => !seen.Contains(s.Model)))
        {
            row.Available = false;
            row.LastSyncAt = now;
        }
        return seen.Count;
    }

    private static string? Get(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

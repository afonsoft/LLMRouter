using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-091: combo/route analytics + budgets — health dashboard, forecast,
/// scoring inspector, trace, route-explain, latency stats, provider limits,
/// window costs, token-limits, cache-health, and budget CRUD + enforcement.
/// </summary>
public static class ComboAnalyticsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    internal const string BudgetScope = "budgets";

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();
        g.MapGet("/usage/combo-health-dashboard", HealthDashboard);
        g.MapGet("/usage/combo-forecast", Forecast);
        g.MapGet("/usage/combo-scoring-inspector", ScoringInspector);
        g.MapGet("/usage/combo-trace/{comboId}", ComboTrace);
        g.MapGet("/usage/route-explain/{requestId}", RouteExplain);
        g.MapGet("/usage/model-latency-stats", ModelLatencyStats);
        g.MapGet("/usage/provider-limits", ProviderLimits);
        g.MapGet("/usage/provider-window-costs", ProviderWindowCosts);
        g.MapGet("/usage/token-limits", TokenLimits);
        g.MapGet("/usage/cache-health", CacheHealth);
        g.MapGet("/usage/budget", BudgetGet);
        g.MapMethods("/usage/budget", ["PUT"], BudgetPut);
        g.MapGet("/usage/budget/bulk", BudgetBulkGet);
        g.MapMethods("/usage/budget/bulk", ["POST"], BudgetBulkPost);
    }

    private static string? MetaProp(string? meta, string key)
    {
        if (meta is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static double ErrorRate(IEnumerable<UsageRecord> rows)
    {
        var list = rows.ToList();
        return list.Count == 0 ? 0 : (double)list.Count(u => u.Status != "ok" && u.Status != "success") / list.Count;
    }

    /// <summary>Combo model "prov-a/m1" matches usage rows by full id or provider+bare-model.</summary>
    private static bool ModelMatches(UsageRecord u, string comboModel)
    {
        if (u.Model == comboModel) return true;
        var slash = comboModel.IndexOf('/');
        var prov = slash > 0 ? comboModel[..slash] : comboModel;
        var bare = slash > 0 ? comboModel[(slash + 1)..] : comboModel;
        return u.Provider == prov && u.Model == bare;
    }

    private static List<string> ComboModels(Combo c)
    {
        try { return JsonSerializer.Deserialize<List<string>>(c.Models) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Per-combo dashboard: score + per-model availability + error rates.</summary>
    private static async Task HealthDashboard(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var combos = await db.Combos.ToListAsync(ct);
        var rows = await db.UsageHistory.OrderByDescending(u => u.Timestamp).Take(10000).ToListAsync(ct);
        var h1 = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss");
        var d1 = DateTime.UtcNow.AddHours(-24).ToString("yyyy-MM-dd HH:mm:ss");
        var items = combos.Select(c =>
        {
            var models = ComboModels(c);
            var states = models.Select(m =>
            {
                var mine = rows.Where(u => ModelMatches(u, m));
                return new
                {
                    model = m,
                    calls1h = mine.Count(u => string.Compare(u.Timestamp, h1) >= 0),
                    errorRate1h = ErrorRate(mine.Where(u => string.Compare(u.Timestamp, h1) >= 0)),
                    errorRate24h = ErrorRate(mine.Where(u => string.Compare(u.Timestamp, d1) >= 0)),
                    lastFailure = mine.Where(u => u.Status != "ok" && u.Status != "success")
                        .MaxBy(u => u.Timestamp)?.Timestamp,
                };
            }).ToList();
            var score = states.Count == 0 ? 1.0 : 1.0 - states.Average(s => s.errorRate24h);
            return new { comboId = c.Id, c.Name, score, models = states };
        });
        await ctx.Response.WriteAsJsonAsync(new { items }, JsonOpts, ct);
    }

    /// <summary>Deterministic forecast: models degrading (rising error rate / cooldown).</summary>
    private static async Task Forecast(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var combos = await db.Combos.ToListAsync(ct);
        var rows = await db.UsageHistory.OrderByDescending(u => u.Timestamp).Take(10000).ToListAsync(ct);
        var recent = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss");
        var prior = DateTime.UtcNow.AddHours(-6).ToString("yyyy-MM-dd HH:mm:ss");
        var items = combos.Select(c =>
        {
            var degrading = ComboModels(c).Where(m =>
            {
                var r1 = ErrorRate(rows.Where(u => ModelMatches(u, m)
                    && string.Compare(u.Timestamp, recent) >= 0));
                var r6 = ErrorRate(rows.Where(u => ModelMatches(u, m)
                    && string.Compare(u.Timestamp, prior) >= 0
                    && string.Compare(u.Timestamp, recent) < 0));
                return r1 > r6 && r1 > 0;
            }).ToList();
            return new
            {
                comboId = c.Id, c.Name,
                trend = degrading.Count == 0 ? "stable" : "degrading",
                degradingModels = degrading,
            };
        });
        await ctx.Response.WriteAsJsonAsync(new { items }, JsonOpts, ct);
    }

    /// <summary>16-factor scoring dump per combo model.</summary>
    private static async Task ScoringInspector(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var comboId = ctx.Request.Query["comboId"].FirstOrDefault();
        var combos = await db.Combos.ToListAsync(ct);
        var picked = comboId is null ? combos : combos.Where(c => c.Id == comboId || c.Name == comboId).ToList();
        var rows = await db.UsageHistory.ToListAsync(ct);
        var items = picked.Select(c =>
        {
            var candidates = ComboModels(c).Select(m =>
            {
                var prov = m.Contains('/') ? m[..m.IndexOf('/')] : m;
                var mine = rows.Where(u => ModelMatches(u, m)).ToList();
                var lats = mine.Select(u => (double)u.LatencyMs).OrderBy(x => x).ToList();
                return new IntelligentScoring.Candidate(
                    prov, m,
                    QuotaRemaining: 50,
                    CircuitBreakerState: "CLOSED",
                    CostPer1MTokens: 0,
                    P95LatencyMs: lats.Count == 0 ? 0 : lats[(int)(lats.Count * 0.95)],
                    LatencyStdDev: 0,
                    ErrorRate: ErrorRate(mine));
            }).ToList();
            var scored = IntelligentScoring.ScorePool(candidates, new IntelligentScoring.Weights(), _ => 0.5);
            return new
            {
                comboId = c.Id, c.Name,
                models = scored.Select(s => new { s.Provider, model = s.Model, s.Factors, total = s.Score }),
            };
        });
        await ctx.Response.WriteAsJsonAsync(new { items }, JsonOpts, ct);
    }

    /// <summary>Last routings for a combo from requestDetails x-routing metadata.</summary>
    private static async Task ComboTrace(HttpContext ctx, LlmRouterDbContext db, string comboId)
    {
        var ct = ctx.RequestAborted;
        var n = int.TryParse(ctx.Request.Query["limit"], out var lim) ? Math.Min(lim, 100) : 20;
        var rows = await db.RequestDetails.OrderByDescending(d => d.Timestamp).Take(500).ToListAsync(ct);
        var traces = rows.Where(d => d.Data.Contains(comboId, StringComparison.Ordinal)).Take(n)
            .Select(d =>
            {
                string? chosen = null, reason = null;
                try
                {
                    using var doc = JsonDocument.Parse(d.Data);
                    var r = doc.RootElement;
                    chosen = r.TryGetProperty("x-routing-chosen", out var c) ? c.GetString()
                        : r.TryGetProperty("routing", out var ro) && ro.TryGetProperty("chosen", out var c2) ? c2.GetString() : null;
                    reason = r.TryGetProperty("x-routing-reason", out var rs) ? rs.GetString()
                        : r.TryGetProperty("routing", out var ro2) && ro2.TryGetProperty("reason", out var rs2) ? rs2.GetString() : null;
                }
                catch (JsonException) { /* malformed detail row */ }
                return new { requestId = d.Id, at = d.Timestamp, d.Provider, d.Model, chosen, reason, d.Status };
            });
        await ctx.Response.WriteAsJsonAsync(new { comboId, items = traces }, JsonOpts, ct);
    }

    /// <summary>Post-hoc explanation of a logged request's routing decision.</summary>
    private static async Task RouteExplain(HttpContext ctx, LlmRouterDbContext db, string requestId)
    {
        var ct = ctx.RequestAborted;
        var d = await db.RequestDetails.FindAsync([requestId], ct);
        if (d is null) { ctx.Response.StatusCode = 404; return; }
        JsonElement? routing = null;
        try
        {
            using var doc = JsonDocument.Parse(d.Data);
            if (doc.RootElement.TryGetProperty("routing", out var r)) routing = r.Clone();
            else if (doc.RootElement.TryGetProperty("x-routing", out var xr)) routing = xr.Clone();
        }
        catch (JsonException) { /* malformed detail row */ }
        await ctx.Response.WriteAsJsonAsync(new
        {
            requestId,
            d.Provider, d.Model, d.Status, at = d.Timestamp,
            decision = routing,
        }, JsonOpts, ct);
    }

    /// <summary>p50/p95/p99 per model over ?window hours (default 24).</summary>
    private static async Task ModelLatencyStats(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var hours = int.TryParse(ctx.Request.Query["window"], out var w) ? w : 24;
        var cutoff = DateTime.UtcNow.AddHours(-hours).ToString("yyyy-MM-dd HH:mm:ss");
        var rows = await db.UsageHistory
            .Where(u => string.Compare(u.Timestamp, cutoff) >= 0 && u.Model != null)
            .ToListAsync(ct);
        static double P(List<long> s, double p) => s.Count == 0 ? 0 : s.OrderBy(x => x).ElementAt((int)Math.Min(s.Count - 1, s.Count * p));
        var items = rows.GroupBy(u => u.Model!).Select(gx =>
        {
            var lats = gx.Select(u => u.LatencyMs).ToList();
            return new { model = gx.Key, calls = lats.Count, p50 = P(lats, 0.5), p95 = P(lats, 0.95), p99 = P(lats, 0.99) };
        });
        await ctx.Response.WriteAsJsonAsync(new { windowHours = hours, items }, JsonOpts, ct);
    }

    /// <summary>Per-provider rate-limit quotas + headroom this window.</summary>
    private static async Task ProviderLimits(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var limits = await db.RateLimits.Where(l => l.Scope == "provider" && l.Enabled).ToListAsync(ct);
        var minuteAgo = DateTime.UtcNow.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm:ss");
        var rows = await db.UsageHistory
            .Where(u => string.Compare(u.Timestamp, minuteAgo) >= 0).ToListAsync(ct);
        var items = limits.Select(l => new
        {
            provider = l.ScopeValue, l.Rpm, l.Tpm, l.Burst,
            consumedRpm = rows.Count(u => u.Provider == l.ScopeValue),
            consumedTpm = rows.Where(u => u.Provider == l.ScopeValue).Sum(u => u.PromptTokens + u.CompletionTokens),
            headroomRpm = l.Rpm > 0 ? Math.Max(0, l.Rpm - rows.Count(u => u.Provider == l.ScopeValue)) : (int?)null,
        });
        await ctx.Response.WriteAsJsonAsync(new { items }, JsonOpts, ct);
    }

    /// <summary>Cost per provider per window bucket (?bucket=hours|days|weeks).</summary>
    private static async Task ProviderWindowCosts(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var bucket = ctx.Request.Query["bucket"].FirstOrDefault() ?? "days";
        var rows = await db.UsageHistory.ToListAsync(ct);
        string Key(UsageRecord u)
        {
            if (!DateTime.TryParse(u.Timestamp, out var t)) return "unknown";
            return bucket switch
            {
                "hours" => t.ToString("yyyy-MM-dd HH:00"),
                "weeks" => $"{t.Year}-W{(t.DayOfYear / 7) + 1}",
                _ => t.ToString("yyyy-MM-dd"),
            };
        }
        var items = rows.GroupBy(u => (u.Provider ?? "unknown", Key(u))).Select(gx => new
        {
            provider = gx.Key.Item1, window = gx.Key.Item2,
            calls = gx.Count(), cost = gx.Sum(x => x.Cost),
            inputTokens = gx.Sum(x => x.PromptTokens), outputTokens = gx.Sum(x => x.CompletionTokens),
        }).OrderByDescending(x => x.window);
        await ctx.Response.WriteAsJsonAsync(new { bucket, items }, JsonOpts, ct);
    }

    /// <summary>Per-key token caps vs usage.</summary>
    private static async Task TokenLimits(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var keys = await db.ApiKeys.Where(k => k.IsActive).ToListAsync(ct);
        var items = new List<object>();
        foreach (var k in keys)
        {
            var limits = await Core.Routing.KeyQuota.LimitsAsync(db, k.Key);
            var used = await Core.Routing.KeyQuota.DailyUsedAsync(db, k.Key);
            var daily = limits.ValueKind == JsonValueKind.Object
                && limits.TryGetProperty("dailyTokens", out var d) && d.TryGetInt64(out var dn) ? dn : 0;
            items.Add(new { keyId = k.Id, k.Name, dailyTokens = daily, usedToday = used, remaining = daily > 0 ? Math.Max(0, daily - used) : (long?)null });
        }
        await ctx.Response.WriteAsJsonAsync(new { items }, JsonOpts, ct);
    }

    /// <summary>HybridCache/HotCache counters + kv blob sizes.</summary>
    private static async Task CacheHealth(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var cache = Core.Gateway.HotCache.Default;
        var kvRows = await db.Kv.ToListAsync(ct);
        var hot = kvRows.GroupBy(k => k.Scope).Select(gx => new { scope = gx.Key, entries = gx.Count(), bytes = gx.Sum(k => k.Value?.Length ?? 0) })
            .OrderByDescending(x => x.bytes).Take(20);
        await ctx.Response.WriteAsJsonAsync(new
        {
            hits = cache.Hits, misses = cache.Misses,
            kvScopes = hot,
        }, JsonOpts, ct);
    }

    // ---- budgets ----

    private static async Task<KvEntry> SettingsKv(LlmRouterDbContext db, CancellationToken ct)
        => await db.Kv.FindAsync(["settings", "root"], ct)
            ?? new KvEntry { Scope = "settings", Key = "root", Value = "{}" };

    private static async Task BudgetGet(HttpContext ctx, LlmRouterDbContext db)
    {
        var row = await SettingsKv(db, ctx.RequestAborted);
        JsonElement? budget = null;
        try
        {
            using var doc = JsonDocument.Parse(row.Value);
            if (doc.RootElement.TryGetProperty("usageBudget", out var b)) budget = b.Clone();
        }
        catch (JsonException) { /* malformed settings */ }
        await ctx.Response.WriteAsJsonAsync(new { budget }, JsonOpts, ctx.RequestAborted);
    }

    private static async Task BudgetPut(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ct);
        var row = await SettingsKv(db, ct);
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Value) ?? [];
        root["usageBudget"] = body;
        var exists = await db.Kv.FindAsync(["settings", "root"], ct) is not null;
        row.Value = JsonSerializer.Serialize(root, JsonOpts);
        if (!exists) db.Kv.Add(row);
        await db.SaveChangesAsync(ct);
        await ctx.Response.WriteAsJsonAsync(new { budget = body }, JsonOpts, ct);
    }

    private static async Task BudgetBulkGet(HttpContext ctx, LlmRouterDbContext db)
    {
        var rows = await db.Kv.Where(k => k.Scope == BudgetScope).ToListAsync(ctx.RequestAborted);
        var items = rows.Select(r =>
        {
            try { return JsonSerializer.Deserialize<JsonElement>(r.Value); }
            catch (JsonException) { return (JsonElement?)null; }
        }).Where(x => x is not null);
        await ctx.Response.WriteAsJsonAsync(new { items }, JsonOpts, ctx.RequestAborted);
    }

    private static async Task BudgetBulkPost(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ct);
        if (body.ValueKind != JsonValueKind.Array)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "body must be an array" }, JsonOpts, ct);
            return;
        }
        var set = 0;
        foreach (var item in body.EnumerateArray())
        {
            var scope = item.TryGetProperty("scope", out var s) ? s.GetString() : null;
            var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (scope is null || id is null) continue;
            var key = $"{scope}:{id}";
            var row = await db.Kv.FindAsync([BudgetScope, key], ct);
            var val = item.GetRawText();
            if (row is null) db.Kv.Add(new KvEntry { Scope = BudgetScope, Key = key, Value = val });
            else row.Value = val;
            set++;
        }
        await db.SaveChangesAsync(ct);
        await ctx.Response.WriteAsJsonAsync(new { set }, JsonOpts, ct);
    }

    /// <summary>
    /// Budget gate for /v1 dispatch — called from Chat after model resolution.
    /// Returns false when it already wrote a 429 budget_exceeded response.
    /// </summary>
    internal static async Task<bool> BudgetGate(HttpContext ctx, LlmRouterDbContext db, string model, string? comboId)
    {
        var ct = ctx.RequestAborted;
        var rows = await db.Kv.Where(k => k.Scope == BudgetScope).ToListAsync(ct);
        if (rows.Count == 0) return true;
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        foreach (var r in rows)
        {
            string? scope = null, id = null;
            double cap = 0;
            try
            {
                using var doc = JsonDocument.Parse(r.Value);
                scope = doc.RootElement.TryGetProperty("scope", out var s) ? s.GetString() : null;
                id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
                cap = doc.RootElement.TryGetProperty("dailyUsd", out var d) && d.TryGetDouble(out var dn) ? dn : 0;
            }
            catch (JsonException) { continue; }
            if (cap <= 0 || scope is null || id is null) continue;

            var applies = scope == "combo" && string.Equals(comboId ?? model, id, StringComparison.OrdinalIgnoreCase)
                || scope == "provider" && model.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase);
            if (!applies) continue;

            var spent = await db.UsageHistory
                .Where(u => string.Compare(u.Timestamp, today) >= 0)
                .Where(u => scope == "combo"
                    ? (u.Meta != null && u.Meta.Contains($"\"comboId\":\"{id}\""))
                    : u.Provider == id)
                .SumAsync(u => u.Cost, ct);
            if (spent >= cap)
            {
                ctx.Response.StatusCode = 429;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        code = "budget_exceeded",
                        message = $"Daily budget of ${cap} exceeded for {scope} '{id}' (spent ${spent:F4}).",
                        scope, id, dailyUsd = cap, spentUsd = spent,
                    },
                }, JsonOpts), ct);
                return false;
            }
        }
        return true;
    }
}

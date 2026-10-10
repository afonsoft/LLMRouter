using System.Collections.Concurrent;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Resilience;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-020: combo selection strategies (ported from upstream
/// open-sse/services/combo.ts). Each strategy orders the combo's candidate
/// "provider/model" list; the gateway still cascades in that order.
/// fusion/pipeline are handled at the endpoint level (fan-out / chained
/// execution), not here.
/// </summary>
public static class ComboStrategies
{
    private static readonly Random Rng = new();

    /// <summary>comboName → last successfully-served "provider/model" (lkgp).</summary>
    private static readonly ConcurrentDictionary<string, string> LastGood = new();

    /// <summary>comboName|promptPrefixHash → last model used for that prefix (cache-optimized).</summary>
    private static readonly ConcurrentDictionary<string, string> CacheAffinity = new();

    /// <summary>Strategies executed at the endpoint level instead of ordered here.</summary>
    public static bool IsExecutionStrategy(string? kind) =>
        kind is "fusion" or "pipeline" or "vision-adapter";

    /// <summary>Recorded when a target serves a request successfully.</summary>
    public static void RecordSuccess(string? comboName, string providerModel)
    {
        if (comboName is not null) LastGood[comboName] = providerModel;
    }

    /// <summary>Recorded with the prompt fingerprint for cache affinity.</summary>
    public static void RecordCacheHit(string? comboName, string promptHash, string providerModel)
    {
        if (comboName is not null) CacheAffinity[$"{comboName}|{promptHash}"] = providerModel;
    }

    /// <summary>Stable hash of the request's text prefix for cache affinity.</summary>
    public static string PromptHash(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b) return "";
        try
        {
            var text = b.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array
                ? string.Join(" ", ms.EnumerateArray()
                    .Select(m => m.TryGetProperty("content", out var c)
                        ? c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText()
                        : ""))
                : b.GetRawText();
            return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(text[..Math.Min(200, text.Length)])))[..16];
        }
        catch { return ""; }
    }

    /// <summary>Parse "provider/model~weight" → (ref, weight). Default weight 1.</summary>
    private static (string Ref, int Weight) ParseWeighted(string entry)
    {
        var tilde = entry.LastIndexOf('~');
        if (tilde > 0 && int.TryParse(entry[(tilde + 1)..], out var w) && w > 0)
            return (entry[..tilde], w);
        return (entry, 1);
    }

    /// <summary>
    /// Order combo members per strategy. Returns the full candidate list
    /// (except strict-random which returns a single pick by design).
    /// </summary>
    public static async Task<List<string>> OrderAsync(
        string? kind, string? comboName, List<string> models,
        LlmRouterDbContext db, ProviderRegistry registry,
        JsonElement? requestBody = null, int stickyLimit = 1,
        CancellationToken ct = default)
    {
        if (models.Count <= 1) return models.ToList();
        var k = (kind ?? "fallback").ToLowerInvariant();

        switch (k)
        {
            case "round-robin":
                return ComboPlanner.GetRotatedModels(models, comboName, "round-robin", stickyLimit);
            case "random":
                return Shuffle(models);
            case "strict-random":
                return [models[Rng.Next(models.Count)]];
            case "lkgp":
                return MoveToHead(models, comboName is not null && LastGood.TryGetValue(comboName, out var lg) ? lg : null);
            case "cache-optimized":
            {
                var hash = PromptHash(requestBody);
                var hit = comboName is not null && CacheAffinity.TryGetValue($"{comboName}|{hash}", out var cm) ? cm : null;
                return MoveToHead(models, hit);
            }
            case "weighted":
            {
                var weighted = models.Select(ParseWeighted).ToList();
                // weighted-random pick for the head, remainder ordered by weight desc
                var total = weighted.Sum(x => x.Weight);
                var roll = Rng.Next(total);
                var head = weighted[0].Ref;
                var acc = 0;
                foreach (var (r, w) in weighted)
                {
                    acc += w;
                    if (roll < acc) { head = r; break; }
                }
                var rest = weighted.Where(x => x.Ref != head).OrderByDescending(x => x.Weight).Select(x => x.Ref);
                return new[] { head }.Concat(rest).ToList();
            }
            case "least-used":
            case "cost-optimized":
            case "p2c":
            case "auto":
            {
                var stats = await UsageStatsAsync(db, ct);
                var key = k;
                List<string> ordered;
                if (key == "least-used")
                {
                    ordered = models
                        .OrderBy(m => stats.TryGetValue(m, out var s) ? s.Requests : 0)
                        .ToList();
                }
                else if (key == "cost-optimized")
                {
                    // models with no usage data sort first (unknown ≈ free)
                    ordered = models
                        .OrderBy(m => stats.TryGetValue(m, out var s) ? s.AvgCost : 0)
                        .ToList();
                }
                else if (key == "p2c")
                {
                    // power of two choices: sample two, pick lower latency
                    var a = models[Rng.Next(models.Count)];
                    var b = models[Rng.Next(models.Count)];
                    var la = stats.TryGetValue(a, out var sa) ? sa.AvgLatency : double.MaxValue / 2;
                    var lb = stats.TryGetValue(b, out var sb) ? sb.AvgLatency : double.MaxValue / 2;
                    var head = la <= lb ? a : b;
                    ordered = new[] { head }.Concat(models.Where(m => m != head)).ToList();
                }
                else
                {
                    // auto: composite rank over cost, latency, usage (simplified
                    // upstream 16-factor score — we track 3 of them here)
                    var ranked = models.Select(m =>
                    {
                        stats.TryGetValue(m, out var s);
                        return (m, s);
                    }).ToList();
                    double Rank(double v, IEnumerable<double?> all)
                    {
                        var vals = all.Where(x => x is not null).Select(x => x!.Value).OrderBy(x => x).ToList();
                        if (vals.Count == 0) return 0;
                        var idx = vals.FindIndex(x => x >= v);
                        return idx < 0 ? vals.Count : idx;
                    }
                    var costs = ranked.Select(x => (double?)x.s?.AvgCost);
                    var lats = ranked.Select(x => (double?)x.s?.AvgLatency);
                    var reqs = ranked.Select(x => (double?)x.s?.Requests);
                    ordered = ranked
                        .OrderBy(x =>
                            0.4 * (x.s is null ? 0 : Rank(x.s.AvgCost, costs))
                          + 0.3 * (x.s is null ? 0 : Rank(x.s.AvgLatency, lats))
                          + 0.2 * (x.s is null ? 0 : Rank(x.s.Requests, reqs))
                          + (x.s is null ? 0 : 0.1))
                        .Select(x => x.m)
                        .ToList();
                }
                return ordered;
            }
            case "quota-weighted":
            case "headroom":
            {
                var remaining = await RemainingQuotaScoreAsync(db, models, ct);
                if (k == "headroom")
                    return models.OrderByDescending(m => remaining.GetValueOrDefault(m, 1)).ToList();
                // quota-weighted: weighted-random head by remaining quota fraction
                var totalQ = remaining.Values.Sum();
                if (totalQ <= 0) return models;
                var rollQ = Rng.NextDouble() * totalQ;
                string headQ = models[0];
                double accQ = 0;
                foreach (var m in models)
                {
                    accQ += remaining.GetValueOrDefault(m, 1);
                    if (rollQ < accQ) { headQ = m; break; }
                }
                return new[] { headQ }
                    .Concat(models.Where(m => m != headQ).OrderByDescending(m => remaining.GetValueOrDefault(m, 1)))
                    .ToList();
            }
            case "reset-aware":
            {
                // models whose connections are cooling sort last, earliest reset first
                var remaining = await CooldownRemainingAsync(db, models, ct);
                return models.OrderBy(m => remaining.GetValueOrDefault(m, TimeSpan.Zero)).ToList();
            }
            case "context-optimized":
            {
                var estTokens = EstimateTokens(requestBody);
                int LenFor(string m)
                {
                    var slash = m.IndexOf('/');
                    var p = slash > 0 ? registry.GetProvider(m[..slash]) : null;
                    if (p is null) return 0;
                    var mid = slash > 0 ? m[(slash + 1)..] : m;
                    var rm = p.Models?.FirstOrDefault(x =>
                        string.Equals(x.Id, mid, StringComparison.OrdinalIgnoreCase));
                    return rm?.ContextLength ?? p.DefaultContextLength ?? 0;
                }
                // smallest adequate context first; inadequate contexts last
                return models
                    .OrderBy(m => LenFor(m) >= estTokens ? 0 : 1)
                    .ThenBy(m => LenFor(m))
                    .ToList();
            }
            default:
                return models.ToList(); // fallback / priority / unknown → list order
        }
    }

    private static List<string> MoveToHead(IReadOnlyList<string> models, string? head)
    {
        if (head is null || !models.Contains(head)) return models.ToList();
        return new[] { head }.Concat(models.Where(m => m != head)).ToList();
    }

    private static List<string> Shuffle(IReadOnlyList<string> models)
    {
        var list = models.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    private sealed record UsageStats(long Requests, double AvgCost, double AvgLatency);

    /// <summary>Aggregate usage per "provider/model" ref across UsageHistory.</summary>
    private static async Task<Dictionary<string, UsageStats>> UsageStatsAsync(
        LlmRouterDbContext db, CancellationToken ct)
    {
        var rows = await db.UsageHistory
            .Where(u => u.Provider != null && u.Model != null)
            .GroupBy(u => new { u.Provider, u.Model })
            .Select(g => new
            {
                g.Key.Provider,
                g.Key.Model,
                Requests = g.Count(),
                AvgCost = g.Average(u => u.Cost),
                AvgLatency = g.Average(u => (double)u.LatencyMs),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(
            r => $"{r.Provider}/{r.Model}",
            r => new UsageStats(r.Requests, r.AvgCost, r.AvgLatency));
    }

    /// <summary>
    /// Remaining-quota score per model (0..1): the minimum over each active
    /// connection's configured-limit fraction and the provider's quota-window
    /// fraction — scoped to the requested model's own provider windows (upstream
    /// #16054). A model with no constraints scores 1.
    /// </summary>
    private static async Task<Dictionary<string, double>> RemainingQuotaScoreAsync(
        LlmRouterDbContext db, List<string> models, CancellationToken ct)
    {
        var result = new Dictionary<string, double>();
        var conns = await db.ProviderConnections.Where(c => c.IsActive).ToListAsync(ct);
        foreach (var m in models)
        {
            var slash = m.IndexOf('/');
            var providerId = slash > 0 ? m[..slash] : m;
            var score = await QuotaWindows.RemainingFractionAsync(db, providerId, ct);
            foreach (var c in conns.Where(c => c.Provider == providerId))
            {
                var st = await QuotaTracker.StateAsync(db, c);
                if (st.DailyLimit is { } d && d > 0)
                    score = Math.Min(score, Math.Max(0, (double)(d - st.DailyUsed) / d));
                if (st.MonthlyLimit is { } mo && mo > 0)
                    score = Math.Min(score, Math.Max(0, (double)(mo - st.MonthlyUsed) / mo));
            }
            result[m] = score;
        }
        return result;
    }

    /// <summary>Max remaining cooldown across each model's connections.</summary>
    private static async Task<Dictionary<string, TimeSpan>> CooldownRemainingAsync(
        LlmRouterDbContext db, List<string> models, CancellationToken ct)
    {
        var result = new Dictionary<string, TimeSpan>();
        var conns = await db.ProviderConnections.Where(c => c.IsActive).ToListAsync(ct);
        foreach (var m in models)
        {
            var slash = m.IndexOf('/');
            var providerId = slash > 0 ? m[..slash] : m;
            var worst = TimeSpan.Zero;
            foreach (var c in conns.Where(c => c.Provider == providerId))
            {
                var rem = CooldownTracker.Remaining(c.Id);
                if (rem > worst) worst = rem;
            }
            result[m] = worst;
        }
        return result;
    }

    /// <summary>Rough token estimate for the request body (≈4 chars/token).</summary>
    private static int EstimateTokens(JsonElement? body) =>
        body is { ValueKind: JsonValueKind.Object } b ? b.GetRawText().Length / 4 : 0;
}

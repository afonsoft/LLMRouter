using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Resilience;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// Auto-router strategies — port of upstream autoCombo/routerStrategy.ts +
/// nadirStrategy.ts. A combo whose Kind is one of <see cref="IsAutoStrategy"/>
/// routes its candidate pool through these pluggable strategies instead of the
/// simple ordering kinds. Nadir additionally asks Nadir's decision API which
/// model the prompt needs (fail-open, bounded, minimal egress).
/// </summary>
public static class AutoRouter
{
    public static readonly HashSet<string> StrategyNames = new(StringComparer.Ordinal)
    { "rules", "score", "cost", "eco", "latency", "fast", "sla-aware", "sla", "lkgp", "nadir" };

    public static bool IsAutoStrategy(string? kind) =>
        kind is not null && StrategyNames.Contains(kind);

    // ── candidates ──────────────────────────────────────────────────────────

    public sealed record Candidate(
        string Provider, string Model,
        string CircuitBreakerState,          // "CLOSED" | "HALF_OPEN" | "OPEN" | "DEGRADED"
        double QuotaFraction,                // 0..1 (1 = unconstrained)
        double CostPer1MTokens,
        double P95LatencyMs, double AvgE2ELatencyMs, double LatencyStdDev,
        double ErrorRate);

    public sealed record Decision(
        string Provider, string Model, string Strategy,
        string Reason, int CandidatesConsidered, double FinalScore);

    /// <summary>Strategy config read from settings.data.autoRouter (+ env fallbacks for nadir).</summary>
    public sealed record Config(
        double ExplorationRate,
        double SlaTargetP95Ms, double SlaMaxErrorRate, double SlaMaxCostPer1MTokens,
        bool SlaHardConstraints,
        bool LkgpEnabled,
        string? NadirApiKey, string? NadirBaseUrl, int NadirTimeoutMs);

    private const double DefaultSlaP95Ms = 2000, DefaultSlaErr = 0.05;
    public const int NadirDefaultTimeoutMs = 2000, NadirMaxTimeoutMs = 30_000,
        NadirFailureCooldownMs = 30_000, NadirMaxPromptChars = 16_000, NadirMaxMenuItems = 100;
    public const string NadirDefaultBaseUrl = "https://api.getnadir.com";
    private static readonly ConcurrentDictionary<string, DateTimeOffset> NadirCooldown = new();

    /// <summary>Build per-model candidates with live telemetry from usageHistory.</summary>
    public static async Task<List<Candidate>> CandidatesAsync(
        LlmRouterDbContext db, List<string> models,
        CancellationToken ct = default)
    {
        var stats = await db.UsageHistory
            .Where(u => u.Provider != null && u.Model != null)
            .GroupBy(u => new { u.Provider, u.Model })
            .Select(g => new
            {
                g.Key.Provider, g.Key.Model,
                Requests = g.Count(),
                Errors = g.Count(u => u.Status != "ok" && u.Status != "success" && u.Status != "200"),
                AvgLatency = g.Average(u => (double)u.LatencyMs),
                AvgCost = g.Average(u => u.Cost),
                AvgTokens = g.Average(u => (double)(u.PromptTokens + u.CompletionTokens)),
                Latencies = g.Select(u => (double)u.LatencyMs).ToList(),
            })
            .ToListAsync(ct);
        var byKey = stats.ToDictionary(s => $"{s.Provider}/{s.Model}", StringComparer.OrdinalIgnoreCase);

        var result = new List<Candidate>(models.Count);
        foreach (var m in models)
        {
            var slash = m.IndexOf('/');
            var providerId = slash > 0 ? m[..slash] : m;
            var quota = await RemainingFractionAsync(db, providerId, ct);
            var breaker = ProviderBreaker.GetState(providerId) switch
            {
                ProviderBreaker.State.Open => "OPEN",
                ProviderBreaker.State.HalfOpen => "HALF_OPEN",
                ProviderBreaker.State.Degraded => "DEGRADED",
                _ => "CLOSED",
            };
            byKey.TryGetValue(m, out var s);
            var (p95, stddev) = s is null ? (0.0, 0.0) : P95AndStdDev(s.Latencies);
            result.Add(new Candidate(
                providerId, m,
                breaker, quota,
                s is { AvgTokens: > 0 } ? s.AvgCost / s.AvgTokens * 1e6 : s?.AvgCost ?? 0,
                p95, s?.AvgLatency ?? 0, stddev,
                s is { Requests: > 0 } ? (double)s.Errors / s.Requests : 0));
        }
        return result;
    }

    private static (double p95, double stddev) P95AndStdDev(List<double> latencies)
    {
        if (latencies.Count == 0) return (0, 0);
        var sorted = latencies.OrderBy(x => x).ToList();
        var p95 = sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
        var avg = sorted.Average();
        var std = Math.Sqrt(sorted.Average(x => (x - avg) * (x - avg)));
        return (p95, std);
    }

    private static Task<double> RemainingFractionAsync(LlmRouterDbContext db, string provider, CancellationToken ct) =>
        QuotaWindows.RemainingFractionAsync(db, provider, ct);

    // ── config ─────────────────────────────────────────────────────────────

    public static async Task<Config> LoadConfigAsync(LlmRouterDbContext db, CancellationToken ct = default)
    {
        var sdata = await HotReads.SettingsDataAsync(db);
        var ar = sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("autoRouter", out var el) ? el : default;
        double Num(JsonElement e, string n, double d) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)
            && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
        bool Bool(JsonElement e, string n, bool d) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
        string? Str(JsonElement e, string n) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var sla = ar.ValueKind == JsonValueKind.Object && ar.TryGetProperty("sla", out var s) ? s : default;
        var nadir = ar.ValueKind == JsonValueKind.Object && ar.TryGetProperty("nadir", out var n) ? n : default;
        var lkgp = ar.ValueKind == JsonValueKind.Object && ar.TryGetProperty("lkgp", out var l) ? l : default;

        var timeout = (int)Num(nadir, "timeoutMs", NadirDefaultTimeoutMs);
        return new Config(
            Math.Clamp(Num(ar, "explorationRate", 0), 0, 1),
            Num(sla, "targetP95Ms", DefaultSlaP95Ms),
            Math.Clamp(Num(sla, "maxErrorRate", DefaultSlaErr), 0, 1),
            Num(sla, "maxCostPer1MTokens", 0),
            Bool(sla, "hardConstraints", false),
            Bool(lkgp, "enabled", true),
            Str(nadir, "apiKey")
                ?? Environment.GetEnvironmentVariable("LLMR_NADIR_API_KEY")
                ?? Environment.GetEnvironmentVariable("OMNIROUTE_NADIR_API_KEY"),
            Str(nadir, "baseUrl")
                ?? Environment.GetEnvironmentVariable("LLMR_NADIR_BASE_URL")
                ?? Environment.GetEnvironmentVariable("OMNIROUTE_NADIR_BASE_URL"),
            Math.Clamp(timeout > 0 ? timeout : NadirDefaultTimeoutMs, 1, NadirMaxTimeoutMs));
    }

    // ── strategies ─────────────────────────────────────────────────────────

    /// <summary>Order the pool by <paramref name="strategy"/>; head of the list is the decision.</summary>
    public static async Task<List<string>> OrderAsync(
        string strategy, List<Candidate> pool, Config cfg,
        JsonElement? messages, LlmRouterDbContext db, CancellationToken ct)
    {
        var ranked = strategy switch
        {
            "score" => RankScore(pool, cfg),
            "cost" or "eco" => pool
                .Where(c => c.CircuitBreakerState != "OPEN").DefaultIfEmpty()
                .OrderBy(c => c.CostPer1MTokens).ToList(),
            "latency" or "fast" => RankLatency(pool),
            "sla-aware" or "sla" => RankSla(pool, cfg),
            "lkgp" => await RankLkgpAsync(pool, cfg, db, ct),
            "nadir" => await RankNadirAsync(pool, cfg, messages, db, ct),
            _ => RankRules(pool), // "rules" + unknown
        };
        return ranked.Select(c => c.Model).ToList();
    }

    /// <summary>rules: weighted composite — quota .25, health .2, cost .2, latency .15, reliability .1, stability .1.</summary>
    private static List<Candidate> RankRules(List<Candidate> pool)
    {
        var eligible = pool.Where(c => c.CircuitBreakerState != "OPEN").ToList();
        var set = eligible.Count > 0 ? eligible : pool;
        var maxCost = Math.Max(set.Max(c => c.CostPer1MTokens), 0.001);
        var maxLat = Math.Max(set.Max(c => c.AvgE2ELatencyMs), 1);
        var maxStd = Math.Max(set.Max(c => c.LatencyStdDev), 1);
        double Health(Candidate c) => c.CircuitBreakerState switch
        { "CLOSED" => 1, "DEGRADED" => 0.75, "HALF_OPEN" => 0.5, _ => 0 };
        return set.OrderByDescending(c =>
                c.QuotaFraction * 0.25 + Health(c) * 0.2
                + (1 - Math.Min(1, c.CostPer1MTokens / maxCost)) * 0.2
                + (1 - Math.Min(1, c.AvgE2ELatencyMs / maxLat)) * 0.15
                + (1 - c.ErrorRate) * 0.1
                + (1 - Math.Min(1, c.LatencyStdDev / maxStd)) * 0.1)
            .ToList();
    }

    /// <summary>score: same composite; explorationRate re-rolls inside the top group.</summary>
    private static List<Candidate> RankScore(List<Candidate> pool, Config cfg)
    {
        var ranked = RankRules(pool);
        if (ranked.Count > 1 && cfg.ExplorationRate > 0 && Random.Shared.NextDouble() < cfg.ExplorationRate)
        {
            var pick = Random.Shared.Next(ranked.Count);
            (ranked[0], ranked[pick]) = (ranked[pick], ranked[0]);
        }
        return ranked;
    }

    /// <summary>latency: e2e latency dominant + failure rate + stability + breaker.</summary>
    private static List<Candidate> RankLatency(List<Candidate> pool)
    {
        var set = pool.Where(c => c.CircuitBreakerState != "OPEN").ToList();
        if (set.Count == 0) set = pool;
        var maxLat = Math.Max(set.Max(c => c.AvgE2ELatencyMs), 1);
        var maxStd = Math.Max(set.Max(c => c.LatencyStdDev), 1);
        double Health(Candidate c) => c.CircuitBreakerState switch
        { "CLOSED" => 1, "DEGRADED" => 0.75, "HALF_OPEN" => 0.5, _ => 0 };
        return set.OrderByDescending(c =>
                (1 - Math.Min(1, c.AvgE2ELatencyMs / maxLat)) * 0.45
                + (1 - c.ErrorRate) * 0.25
                + (1 - Math.Min(1, c.LatencyStdDev / maxStd)) * 0.15
                + Health(c) * 0.15)
            .ToList();
    }

    /// <summary>sla-aware: SLO scoring — faithful port of upstream SLAStrategyImpl.</summary>
    private static List<Candidate> RankSla(List<Candidate> pool, Config cfg)
    {
        var set = pool.Where(c => c.CircuitBreakerState != "OPEN").ToList();
        if (set.Count == 0) set = pool;
        if (set.Count == 0) return set;
        var maxCost = Math.Max(set.Max(c => c.CostPer1MTokens), 0.001);
        var maxStd = Math.Max(set.Max(c => c.LatencyStdDev), 0.001);

        static double ScoreAtOrBelow(double value, double threshold) =>
            threshold <= 0 ? (value == 0 ? 1 : 0)
                : Math.Clamp(threshold / Math.Max(value, 1e-6), 0, 1);
        static double InvNorm(double value, double max) =>
            max <= 0 ? 1 : Math.Clamp(1 - Math.Max(0, value) / max, 0, 1);
        double Health(Candidate c) => c.CircuitBreakerState switch
        { "CLOSED" => 1, "HALF_OPEN" => 0.5, "DEGRADED" => 0.5, _ => 0 };
        double Violation(Candidate c)
        {
            var v = c.CircuitBreakerState == "OPEN" ? 1.0 : 0;
            if (c.P95LatencyMs > cfg.SlaTargetP95Ms)
                v += (c.P95LatencyMs - cfg.SlaTargetP95Ms) / cfg.SlaTargetP95Ms;
            if (c.ErrorRate > cfg.SlaMaxErrorRate)
                v += cfg.SlaMaxErrorRate > 0
                    ? (c.ErrorRate - cfg.SlaMaxErrorRate) / cfg.SlaMaxErrorRate
                    : c.ErrorRate;
            if (cfg.SlaMaxCostPer1MTokens > 0 && c.CostPer1MTokens > cfg.SlaMaxCostPer1MTokens)
                v += (c.CostPer1MTokens - cfg.SlaMaxCostPer1MTokens) / cfg.SlaMaxCostPer1MTokens;
            return v;
        }

        var scored = set.Select(c =>
        {
            var latencyScore = ScoreAtOrBelow(c.P95LatencyMs, cfg.SlaTargetP95Ms);
            var errorScore = ScoreAtOrBelow(c.ErrorRate, cfg.SlaMaxErrorRate);
            var costScore = cfg.SlaMaxCostPer1MTokens > 0
                ? ScoreAtOrBelow(c.CostPer1MTokens, cfg.SlaMaxCostPer1MTokens)
                : InvNorm(c.CostPer1MTokens, maxCost);
            var stability = InvNorm(c.LatencyStdDev, maxStd);
            var score = latencyScore * 0.35 + errorScore * 0.35
                + Health(c) * 0.15 + costScore * 0.1 + stability * 0.05;
            return (c, violation: Violation(c), score);
        }).ToList();
        return (cfg.SlaHardConstraints
                ? scored.OrderBy(s => s.violation).ThenByDescending(s => s.score)
                : scored.OrderByDescending(s => s.score))
            .Select(s => s.c).ToList();
    }

    /// <summary>lkgp: last successful provider first, then rules.</summary>
    private static async Task<List<Candidate>> RankLkgpAsync(
        List<Candidate> pool, Config cfg, LlmRouterDbContext db, CancellationToken ct)
    {
        if (cfg.LkgpEnabled)
        {
            var lastGood = await db.UsageHistory
                .Where(u => u.Status == "ok" || u.Status == "success" || u.Status == "200")
                .OrderByDescending(u => u.Timestamp)
                .Select(u => u.Provider)
                .FirstOrDefaultAsync(ct);
            var lkg = pool.Where(c =>
                c.Provider == lastGood && c.CircuitBreakerState != "OPEN").ToList();
            if (lkg.Count > 0)
                return lkg.Concat(RankRules(pool.Where(c => c.Provider != lastGood).ToList())).ToList();
        }
        return RankRules(pool);
    }

    // ── nadir: decision API picks the model, rules picks within it ──────────

    /// <summary>Last `user` message text — string or joined text parts (upstream extractLastUserText).</summary>
    public static string? ExtractLastUserText(JsonElement? messages)
    {
        if (messages is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("messages", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var items = arr.EnumerateArray().ToList();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var msg = items[i];
            if (msg.ValueKind != JsonValueKind.Object
                || !msg.TryGetProperty("role", out var role)
                || role.GetString() != "user") continue;
            if (!msg.TryGetProperty("content", out var content)) return null;
            if (content.ValueKind == JsonValueKind.String)
            {
                var t = content.GetString()?.Trim();
                return string.IsNullOrEmpty(t) ? null : t;
            }
            if (content.ValueKind != JsonValueKind.Array) return null;
            var text = string.Join("\n", content.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("type", out var pt)
                    && (pt.GetString() is "text" or "input_text")
                    && p.TryGetProperty("text", out var tx)
                    && tx.ValueKind == JsonValueKind.String)
                .Select(p => p.GetProperty("text").GetString()!));
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        return null;
    }

    /// <summary>Normalize a nadir baseUrl — strip trailing slashes + a pasted /v1.</summary>
    public static string? NormalizeNadirBaseUrl(string? raw)
    {
        var v = (raw ?? "").Trim().TrimEnd('/');
        if (v.EndsWith("/v1", StringComparison.Ordinal)) v = v[..^3].TrimEnd('/');
        return v.StartsWith("http://", StringComparison.Ordinal)
            || v.StartsWith("https://", StringComparison.Ordinal) ? v : null;
    }

    private static async Task<List<Candidate>> RankNadirAsync(
        List<Candidate> pool, Config cfg, JsonElement? messages,
        LlmRouterDbContext db, CancellationToken ct)
    {
        var healthy = pool.Where(c => c.CircuitBreakerState != "OPEN").ToList();
        var candidates = healthy.Count > 0 ? healthy : pool;
        var prompt = ExtractLastUserText(messages);
        var baseUrl = cfg.NadirBaseUrl is { } b ? NormalizeNadirBaseUrl(b) : NadirDefaultBaseUrl;
        if (prompt is null || baseUrl is null || candidates.Count == 0)
            return RankRules(candidates);
        if (NadirCooldown.TryGetValue(baseUrl, out var until) && until > DateTimeOffset.UtcNow)
            return RankRules(candidates);

        try
        {
            var menu = candidates.Select(c => c.Model).Distinct().Take(NadirMaxMenuItems).ToList();
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(cfg.NadirTimeoutMs) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/bucket")
            {
                Content = JsonContent.Create(new
                {
                    prompt = prompt[..Math.Min(prompt.Length, NadirMaxPromptChars)],
                    menu,
                    source = "omniroute",
                }),
            };
            if (cfg.NadirApiKey is { Length: > 0 } key)
                req.Headers.TryAddWithoutValidation("X-API-Key", key);
            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var selected = doc.RootElement.TryGetProperty("selected_model", out var sm)
                && sm.ValueKind == JsonValueKind.String ? sm.GetString() : null;
            var matching = selected is null ? [] : candidates.Where(c => c.Model == selected).ToList();
            if (matching.Count == 0) return RankRules(candidates);
            // Nadir picked the model; rules picks the remaining order for fallbacks.
            return matching.Concat(RankRules(candidates.Where(c => c.Model != selected).ToList())).ToList();
        }
        catch
        {
            NadirCooldown[baseUrl] = DateTimeOffset.UtcNow.AddMilliseconds(NadirFailureCooldownMs);
            return RankRules(candidates);
        }
    }
}

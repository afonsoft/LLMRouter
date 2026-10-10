using System.Text.Json;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-084: faithful port of upstream open-sse/services/autoCombo/scoring.ts —
/// the 16-factor weighted scorer behind the `auto`/`lkgp` intelligent
/// strategies. Factor and weight names, defaults, mode-pack presets and
/// neutral-value conventions match the TS implementation 1:1.
/// </summary>
public static class IntelligentScoring
{
    static double Clamp01(double v) => !double.IsFinite(v) ? 0 : Math.Clamp(v, 0, 1);

    /// <summary>All 16 factors, each contractually [0,1].</summary>
    public sealed record Factors(
        double Quota, double Health, double CostInv, double LatencyInv,
        double TaskFit, double Stability, double TierPriority, double TierAffinity,
        double SpecificityMatch, double ContextAffinity, double CacheAffinity,
        double SessionAvailability, double ResetWindowAffinity,
        double ConnectionDensity, double Quality, double Reliability);

    /// <summary>Weight profile (16 fields). Defaults == upstream DEFAULT_WEIGHTS.</summary>
    public sealed record Weights(
        double Quota = 0.1429, double Health = 0.1605, double CostInv = 0.1429,
        double LatencyInv = 0.1143, double TaskFit = 0.0762, double Stability = 0.0476,
        double TierPriority = 0.0476, double TierAffinity = 0.0476,
        double SpecificityMatch = 0.0476, double ContextAffinity = 0.0476,
        double CacheAffinity = 0, double SessionAvailability = 0.0476,
        double ResetWindowAffinity = 0, double ConnectionDensity = 0.0476,
        double Quality = 0.03, double Reliability = 0)
    {
        public double Sum() => Quota + Health + CostInv + LatencyInv + TaskFit + Stability
            + TierPriority + TierAffinity + SpecificityMatch + ContextAffinity
            + CacheAffinity + SessionAvailability + ResetWindowAffinity
            + ConnectionDensity + Quality + Reliability;

        /// <summary>Upstream validateWeights: sum ≈ 1.0 (±0.01).</summary>
        public bool IsValid() => Math.Abs(Sum() - 1.0) < 0.01;
    }

    /// <summary>Upstream normalizeScoringWeights: clamp negatives to 0, renormalize; all-zero → defaults.</summary>
    public static Weights NormalizeWeights(IDictionary<string, double>? raw)
    {
        if (raw is null || raw.Count == 0) return new Weights();
        double Get(string k) => raw.TryGetValue(k, out var v) && double.IsFinite(v) && v >= 0 ? v : 0;
        var w = new Weights(
            Get("quota"), Get("health"), Get("costInv"), Get("latencyInv"), Get("taskFit"),
            Get("stability"), Get("tierPriority"), Get("tierAffinity"), Get("specificityMatch"),
            Get("contextAffinity"), Get("cacheAffinity"), Get("sessionAvailability"),
            Get("resetWindowAffinity"), Get("connectionDensity"), Get("quality"), Get("reliability"));
        var t = w.Sum();
        if (t <= 0) return new Weights();
        return new Weights(
            w.Quota / t, w.Health / t, w.CostInv / t, w.LatencyInv / t, w.TaskFit / t,
            w.Stability / t, w.TierPriority / t, w.TierAffinity / t, w.SpecificityMatch / t,
            w.ContextAffinity / t, w.CacheAffinity / t, w.SessionAvailability / t,
            w.ResetWindowAffinity / t, w.ConnectionDensity / t, w.Quality / t, w.Reliability / t);
    }

    /// <summary>Upstream modePacks.ts — 6 preset weight profiles (exact values).</summary>
    public static readonly IReadOnlyDictionary<string, Weights> ModePacks =
        new Dictionary<string, Weights>
        {
            ["ship-fast"] = new(0.1133, 0.2667, 0.0276, 0.3048, 0.0952, 0,
                0.0376, 0, 0, 0.0095, 0, 0.0476, 0, 0.0476, 0.02, 0.03),
            ["cost-saver"] = new(0.1133, 0.181, 0.3324, 0.0476, 0.0952, 0.0476,
                0.0376, 0, 0, 0, 0, 0.0476, 0, 0.0476, 0.02, 0.03),
            ["quality-first"] = new(0.0752, 0.1714, 0.0276, 0.0476, 0.3524, 0.1429,
                0.0276, 0, 0, 0, 0, 0.0476, 0, 0.0476, 0.03, 0.03),
            ["offline-friendly"] = new(0.3324, 0.2667, 0.0752, 0.0476, 0, 0.0952,
                0.0376, 0, 0, 0, 0, 0.0476, 0, 0.0476, 0.02, 0.03),
            ["reliability-first"] = new(0.1133, 0.3524, 0.0181, 0.0476, 0.0952, 0.1905,
                0.0276, 0, 0, 0, 0, 0.0476, 0, 0.0476, 0.02, 0.04),
            ["chaos-mode"] = new(0.0376, 0.4, 0.014, 0.0186, 0.1905, 0.1714,
                0.004, 0, 0, 0.0186, 0, 0.0476, 0, 0.0476, 0.02, 0.03),
        };

    /// <summary>Upstream getModePack: named pack, or defaults when unknown/custom.</summary>
    public static Weights ResolveModePack(string? modePack) =>
        modePack is not null && ModePacks.TryGetValue(modePack, out var w) ? w : new Weights();

    /// <summary>One candidate in the scoring pool (upstream ProviderCandidate).</summary>
    public sealed record Candidate(
        string Provider, string Model,
        double QuotaRemaining,            // percent 0..100
        string CircuitBreakerState,       // CLOSED | HALF_OPEN | OPEN | DEGRADED
        double CostPer1MTokens,
        double P95LatencyMs, double LatencyStdDev, double ErrorRate,
        double? FailureRate = null,
        string? AccountTier = null,       // ultra | pro | standard | free
        double? QuotaResetIntervalSecs = null,
        double? ContextAffinity = null,
        double? CacheAffinity = null,
        double? SessionAvailability = null,
        double? ResetWindowAffinity = null,
        double? Quality = null,
        int? ConnectionPoolSize = null,
        string? ConnectionId = null);

    public sealed record Scored(string Provider, string Model, double Score, Factors Factors, string? ConnectionId);

    /// <summary>Upstream calculateScore — weighted sum clamped to [0,1].</summary>
    public static double CalculateScore(Factors f, Weights w) => Clamp01(
        w.Quota * f.Quota + w.Health * f.Health + w.CostInv * f.CostInv
        + w.LatencyInv * f.LatencyInv + w.TaskFit * f.TaskFit + w.Stability * f.Stability
        + w.TierPriority * f.TierPriority + w.TierAffinity * f.TierAffinity
        + w.SpecificityMatch * f.SpecificityMatch + w.ContextAffinity * f.ContextAffinity
        + w.CacheAffinity * f.CacheAffinity + w.SessionAvailability * f.SessionAvailability
        + w.ResetWindowAffinity * f.ResetWindowAffinity
        + w.ConnectionDensity * f.ConnectionDensity
        + w.Quality * f.Quality + w.Reliability * f.Reliability);

    /// <summary>Upstream calculateTierScore — account tier + quota-reset bonus.</summary>
    public static double CalculateTierScore(string? tier, double? quotaResetIntervalSecs)
    {
        var baseScore = (tier ?? "").ToLowerInvariant() switch
        { "ultra" => 1.0, "pro" => 0.67, "standard" => 0.33, "free" => 0.0, _ => 0.33 };
        var resetBonus = quotaResetIntervalSecs is > 0
            ? Math.Max(0, 1 - quotaResetIntervalSecs.Value / 2_592_000) : 0;
        return Math.Min(1, baseScore * 0.8 + resetBonus * 0.2);
    }

    /// <summary>Upstream tierAffinity — needs a manifest routing hint (recommendedMinTier).</summary>
    public static double CalculateTierAffinity(string tier, string? recommendedMinTier)
    {
        if (recommendedMinTier is null) return 0.5;
        string[] order = ["free", "cheap", "premium"];
        var p = Array.IndexOf(order, tier); var m = Array.IndexOf(order, recommendedMinTier);
        if (p < 0 || m < 0) return 0.5;
        if (p == m) return 1.0;
        return Math.Abs(p - m) == 1 ? 0.7 : 0.3;
    }

    /// <summary>Upstream specificityMatch — tier vs hint specificity score.</summary>
    public static double CalculateSpecificityMatch(string tier, double specificityScore)
    {
        if (tier == "free") return specificityScore <= 15 ? 0.9 : 0.2;
        if (tier == "cheap") return specificityScore is > 15 and <= 50 ? 0.9 : 0.4;
        if (tier == "premium") return specificityScore > 50 ? 0.9 : 0.3;
        return 0.5;
    }

    /// <summary>Upstream reliabilityFactor: 1 - bounded failure (else error) rate.</summary>
    public static double ReliabilityFactor(double? failureRate, double? errorRate)
    {
        var v = failureRate ?? errorRate;
        var bounded = v is null || !double.IsFinite(v.Value) || v < 0 ? 0 : Math.Min(1, v.Value);
        return Clamp01(1 - bounded);
    }

    /// <summary>Upstream computePoolMaxima — pool-wide maxima for normalization.</summary>
    public static (double maxCost, double maxLatency, double maxStdDev) PoolMaxima(IReadOnlyList<Candidate> pool)
    {
        double maxCost = 0.001, maxLat = 1, maxStd = 0.001;
        foreach (var p in pool)
        {
            if (p.CostPer1MTokens > maxCost) maxCost = p.CostPer1MTokens;
            if (p.P95LatencyMs > maxLat) maxLat = p.P95LatencyMs;
            if (p.LatencyStdDev > maxStd) maxStd = p.LatencyStdDev;
        }
        return (maxCost, maxLat, maxStd);
    }

    /// <summary>
    /// Upstream calculateFactors. `tier`/`recommendedMinTier`/`specificity`
    /// feed the manifest-hint factors (neutral 0.5 when absent).
    /// </summary>
    public static Factors CalculateFactors(
        Candidate c, (double maxCost, double maxLat, double maxStd) maxima,
        double taskFitness,
        string? tier = null, string? recommendedMinTier = null, double? specificity = null)
    {
        var (maxCost, maxLat, maxStd) = maxima;
        return new Factors(
            Quota: Clamp01(c.QuotaRemaining / 100),
            Health: c.CircuitBreakerState switch
            { "CLOSED" => 1.0, "DEGRADED" => 0.75, "HALF_OPEN" => 0.5, _ => 0.0 },
            CostInv: Clamp01(1 - c.CostPer1MTokens / maxCost),
            LatencyInv: Clamp01(1 - c.P95LatencyMs / maxLat),
            TaskFit: Clamp01(taskFitness),
            Stability: Clamp01(1 - c.LatencyStdDev / maxStd),
            TierPriority: CalculateTierScore(c.AccountTier, c.QuotaResetIntervalSecs),
            TierAffinity: tier is null ? 0.5 : CalculateTierAffinity(tier, recommendedMinTier),
            SpecificityMatch: tier is null || specificity is null ? 0.5
                : CalculateSpecificityMatch(tier, specificity.Value),
            ContextAffinity: Clamp01(c.ContextAffinity ?? 0.5),
            CacheAffinity: Clamp01(c.CacheAffinity ?? 0),
            SessionAvailability: Clamp01(c.SessionAvailability ?? 1),
            ResetWindowAffinity: Clamp01(c.ResetWindowAffinity ?? 0.5),
            ConnectionDensity: Clamp01(((c.ConnectionPoolSize ?? 1) - 1) / 10.0),
            Quality: Clamp01(c.Quality ?? 0.5),
            Reliability: ReliabilityFactor(c.FailureRate, c.ErrorRate));
    }

    /// <summary>Upstream scorePool — factors for every candidate, sorted desc.</summary>
    public static List<Scored> ScorePool(
        IReadOnlyList<Candidate> pool, Weights weights,
        Func<Candidate, double> taskFitness,
        Func<Candidate, (string? tier, string? minTier, double? spec)>? hintFor = null)
    {
        var maxima = PoolMaxima(pool);
        return pool.Select(c =>
            {
                var (tier, minTier, spec) = hintFor?.Invoke(c) ?? (null, null, null);
                var factors = CalculateFactors(c, maxima, taskFitness(c), tier, minTier, spec);
                return new Scored(c.Provider, c.Model, CalculateScore(factors, weights), factors, c.ConnectionId);
            })
            .OrderByDescending(s => s.Score)
            .ToList();
    }
}

/// <summary>
/// SPEC-084: faithful port of upstream taskFitness.ts layers 4–5 —
/// versioned FITNESS_TABLE + wildcard boosts over a neutral 0.5.
/// Layers 1–3 (user override / arena_elo / models.dev tier) resolve through
/// ModelIntelligence at runtime; this static table is the final fallback.
/// </summary>
public static class TaskFitness
{
    private static readonly Dictionary<string, Dictionary<string, double>> FitnessTable = new()
    {
        ["coding"] = new()
        {
            ["gpt-4o"] = 0.9, ["gpt-4o-mini"] = 0.8, ["gpt-4-turbo"] = 0.88,
            ["o3"] = 0.95, ["o4-mini"] = 0.88,
            ["gemini-2.5-pro"] = 0.92, ["gemini-2.5-flash"] = 0.82,
            ["deepseek-coder"] = 0.9, ["deepseek-v3"] = 0.85, ["deepseek-r1"] = 0.88,
            ["deepseek-chat"] = 0.84, ["deepseek-v3.2"] = 0.86,
            ["grok-3"] = 0.8, ["glm-5.1"] = 0.78, ["minimax-m2.5"] = 0.75, ["minimax-m2"] = 0.72,
        },
        ["review"] = new()
        {
            ["gpt-4o"] = 0.88, ["gpt-4o-mini"] = 0.72, ["o3"] = 0.92,
            ["gemini-2.5-pro"] = 0.93, ["deepseek-r1"] = 0.85, ["deepseek-v3"] = 0.8,
        },
        ["planning"] = new()
        {
            ["gpt-4o"] = 0.88, ["o3"] = 0.95, ["gemini-2.5-pro"] = 0.93, ["deepseek-r1"] = 0.85,
        },
        ["analysis"] = new()
        {
            ["gemini-2.5-pro"] = 0.95, ["gemini-3.1-pro"] = 0.95, ["gpt-4o"] = 0.85,
            ["o3"] = 0.93, ["deepseek-r1"] = 0.88, ["deepseek-chat"] = 0.8,
            ["glm-5.1"] = 0.82, ["minimax-m2.5"] = 0.76,
        },
        ["debugging"] = new()
        {
            ["gpt-4o"] = 0.88, ["deepseek-coder"] = 0.9, ["deepseek-v3"] = 0.82,
        },
        ["documentation"] = new()
        {
            ["gpt-4o"] = 0.92, ["gpt-4o-mini"] = 0.85, ["deepseek-v3"] = 0.78,
        },
        ["default"] = new()
        {
            ["gpt-4o"] = 0.85, ["gemini-3.1-pro"] = 0.85, ["deepseek-v3"] = 0.75,
            ["deepseek-chat"] = 0.74, ["grok-3"] = 0.73, ["glm-5.1"] = 0.75, ["minimax-m2.5"] = 0.7,
        },
    };

    private static readonly (string pattern, string taskType, double boost)[] WildcardBoosts =
    [
        ("coder", "coding", 0.15), ("code", "coding", 0.1), ("fast", "coding", 0.05),
        ("thinking", "planning", 0.1), ("thinking", "analysis", 0.1),
    ];

    private static readonly HashSet<char> SegmentSeparators = ['-', '.', '/'];

    /// <summary>Upstream matchesAtSegmentBoundary — pattern must not straddle '-'/'.'/'/'.</summary>
    public static bool MatchesAtSegmentBoundary(string model, string pattern)
    {
        if (pattern.Length == 0) return false;
        var index = model.IndexOf(pattern, StringComparison.Ordinal);
        while (index >= 0)
        {
            var startsAtBoundary = index == 0 || SegmentSeparators.Contains(model[index - 1]);
            var endIndex = index + pattern.Length;
            var endsAtBoundary = endIndex == model.Length || SegmentSeparators.Contains(model[endIndex]);
            if (startsAtBoundary && endsAtBoundary) return true;
            index = model.IndexOf(pattern, index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>Layer 4: static table lookup (longest pattern wins), else null.</summary>
    public static double? StaticTableScore(string model, string taskType)
    {
        var m = model.ToLowerInvariant(); var t = taskType.ToLowerInvariant();
        var table = FitnessTable.TryGetValue(t, out var tb) ? tb : FitnessTable["default"];
        foreach (var (pattern, score) in table.OrderByDescending(kv => kv.Key.Length))
            if (MatchesAtSegmentBoundary(m, pattern)) return score;
        return null;
    }

    /// <summary>Layer 5: wildcard boosts over the 0.5 baseline.</summary>
    public static double WildcardScore(string model, string taskType)
    {
        var m = model.ToLowerInvariant(); var t = taskType.ToLowerInvariant();
        var score = 0.5;
        foreach (var (pattern, task, boost) in WildcardBoosts)
            if (m.Contains(pattern) && t == task) score += boost;
        return Math.Min(1.0, score);
    }

    /// <summary>Task types advertised by the upstream getTaskTypes().</summary>
    public static readonly string[] TaskTypes =
        ["coding", "review", "planning", "analysis", "debugging", "documentation", "default"];
}

/// <summary>
/// SPEC-084: model intelligence store (upstream model_intelligence) — per-model,
/// per-task-type fitness scores from arena_elo sync or user overrides.
/// kv scope "modelIntelligence", key "{source}:{model}:{taskType}", value the score.
/// </summary>
public static class ModelIntelligence
{
    public const string Scope = "modelIntelligence";

    public static async Task<double?> LookupAsync(Data.LlmRouterDbContext db,
        string source, string model, string taskType)
    {
        var norm = model.ToLowerInvariant();
        var row = await db.Kv.FindAsync(Scope, $"{source}:{norm}:{taskType.ToLowerInvariant()}");
        if (row is null) row = await db.Kv.FindAsync(Scope, $"{source}:{norm}:default");
        return row is not null && double.TryParse(row.Value, out var v) ? v : null;
    }

    /// <summary>Full chain: user_override → arena_elo → static table → wildcard → 0.5.</summary>
    public static async Task<double> GetTaskFitnessAsync(Data.LlmRouterDbContext db,
        string model, string taskType)
    {
        return await LookupAsync(db, "user_override", model, taskType)
            ?? await LookupAsync(db, "arena_elo", model, taskType)
            ?? TaskFitness.StaticTableScore(model, taskType)
            ?? TaskFitness.WildcardScore(model, taskType);
    }

    public static async Task SetAsync(Data.LlmRouterDbContext db,
        string source, string model, string taskType, double score)
    {
        var key = $"{source}:{model.ToLowerInvariant()}:{taskType.ToLowerInvariant()}";
        var row = await db.Kv.FindAsync(Scope, key);
        if (row is null) db.Kv.Add(new Data.KvEntry { Scope = Scope, Key = key, Value = score.ToString("R") });
        else row.Value = score.ToString("R");
    }

    /// <summary>ELO-derived quality signal [0,1] for the scoring `quality` factor.</summary>
    public static async Task<double?> QualityAsync(Data.LlmRouterDbContext db, string model)
        => await LookupAsync(db, "arena_elo", model, "default");
}

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-084: faithful port of upstream lib/routing/adaptiveRouting.ts —
/// multiplicative candidate scoring with allow/warn/deny allocation,
/// explanations (reasons + factors), and the failover policy gate.
/// </summary>
public static class AdaptiveRouting
{
    public enum Allocation { Allow, Warn, Deny }
    public enum Circuit { Closed, Open, HalfOpen }

    /// <summary>Upstream ProviderQuotaStatus.</summary>
    public enum QuotaStatus { Exhausted, ApproachingLimit, Unavailable, Unknown, Healthy }

    public sealed record RoutingCandidate(
        string ProviderId, string ModelId,
        double CapabilityScore,
        Allocation Allocation,
        double HealthScore,
        Circuit Circuit,
        QuotaStatus Quota,
        double? LatencyMs = null,
        double? ErrorRate = null,
        double? ModelPreference = null,
        double? CostPreference = null);

    public sealed record RoutingExplanation(
        string ProviderId, string ModelId, double Score,
        bool Eligible, List<string> Reasons, Dictionary<string, object> Factors);

    public sealed record RankedResult(RoutingExplanation? Selected, List<RoutingExplanation> Candidates);

    static double Clamp(double v, double fallback = 0) =>
        !double.IsFinite(v) ? fallback : Math.Clamp(v, 0, 1);

    static double QuotaFactor(QuotaStatus q) => q switch
    {
        QuotaStatus.Exhausted => 0,
        QuotaStatus.ApproachingLimit => 0.65,
        QuotaStatus.Unavailable => 0.9,
        _ => 1,
    };

    static double LatencyFactor(double? latencyMs) =>
        latencyMs is null || !double.IsFinite(latencyMs.Value)
            ? 1 : Math.Max(0.4, 1 - Math.Min(latencyMs.Value, 30_000) / 50_000);

    /// <summary>Upstream scoreCandidate — multiplicative factor chain + reasons.</summary>
    public static RoutingExplanation ScoreCandidate(RoutingCandidate c)
    {
        var capability = Clamp(c.CapabilityScore);
        var allocation = c.Allocation == Allocation.Deny ? 0 : c.Allocation == Allocation.Warn ? 0.85 : 1;
        var health = Clamp(c.HealthScore, 0.5);
        var reliability = 1 - Clamp(c.ErrorRate ?? 0);
        var latency = LatencyFactor(c.LatencyMs);
        var preference = Clamp(c.ModelPreference ?? 0.5, 0.5);
        var cost = Clamp(c.CostPreference ?? 1, 1);
        var quota = QuotaFactor(c.Quota);
        var circuit = c.Circuit == Circuit.Open ? 0 : c.Circuit == Circuit.HalfOpen ? 0.5 : 1;
        var score = Math.Round(
            capability * allocation * health * reliability * latency * preference * cost * quota * circuit, 6);

        var reasons = new List<string>
        {
            capability >= 0.8 ? "capability match" : "partial capability match",
            c.Allocation == Allocation.Allow
                ? "allocation permitted"
                : c.Allocation == Allocation.Warn
                    ? "allocation permitted with warning"
                    : "allocation denied",
            health >= 0.8 ? "provider healthy" : "provider health degraded",
            $"circuit {c.Circuit.ToString().ToLowerInvariant().Replace("halfopen", "half_open")}",
            c.Quota == QuotaStatus.Unknown
                ? "quota state unknown but not exhausted"
                : $"quota {c.Quota.ToString().ToLowerInvariant()}",
        };
        if (c.LatencyMs is not null) reasons.Add($"latency {Math.Round(c.LatencyMs.Value)}ms");
        if (c.ErrorRate is not null) reasons.Add($"{Math.Round(c.ErrorRate.Value * 100)}% recent errors");

        return new RoutingExplanation(
            c.ProviderId, c.ModelId, score,
            Eligible: score > 0 && c.Allocation != Allocation.Deny
                && c.Circuit != Circuit.Open && c.Quota != QuotaStatus.Exhausted,
            reasons,
            new Dictionary<string, object>
            {
                ["capability"] = capability, ["allocation"] = allocation, ["health"] = health,
                ["reliability"] = reliability, ["latency"] = latency, ["preference"] = preference,
                ["cost"] = cost, ["quota"] = quota, ["circuit"] = circuit,
            });
    }

    /// <summary>Upstream rankCandidates — sorted desc; first eligible wins.</summary>
    public static RankedResult RankCandidates(IEnumerable<RoutingCandidate> candidates)
    {
        var ranked = candidates.Select(ScoreCandidate).OrderByDescending(r => r.Score).ToList();
        return new RankedResult(ranked.FirstOrDefault(r => r.Eligible), ranked);
    }

    /// <summary>Upstream FailoverPolicy + DEFAULT_FAILOVER_POLICY.</summary>
    public sealed record FailoverPolicy(
        int MaxProviderAttempts = 3,
        bool AllowCrossProviderFallback = true,
        bool RetryRateLimited = true,
        bool RetryTimeouts = true);

    public static readonly FailoverPolicy DefaultFailoverPolicy = new();

    /// <summary>
    /// Upstream shouldFailover — cross-provider fallback allowed only for
    /// retryable failures, rate limits and timeouts per policy, plus
    /// network errors and provider 5xx.
    /// </summary>
    public static bool ShouldFailover(string failureType, bool retryable, FailoverPolicy? policy = null)
    {
        var p = policy ?? DefaultFailoverPolicy;
        if (!p.AllowCrossProviderFallback || !retryable) return false;
        return failureType switch
        {
            "rate_limit" => p.RetryRateLimited,
            "timeout" => p.RetryTimeouts,
            _ => failureType is "network_error" or "provider_5xx",
        };
    }
}

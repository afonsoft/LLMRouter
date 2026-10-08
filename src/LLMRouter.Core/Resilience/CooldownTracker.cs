using System.Collections.Concurrent;

namespace LLMRouter.Core.Resilience;

/// <summary>
/// Connection cooldown tracker (mirrors upstream tokenRefreshCircuit/proxyHealth):
/// N consecutive failures → skip the connection for a backoff window.
/// </summary>
public static class CooldownTracker
{
    private sealed record State(int Failures, DateTimeOffset Until);
    private static readonly ConcurrentDictionary<string, State> States = new();

    public static int FailureThreshold { get; private set; } = 3;
    public static TimeSpan BaseCooldown { get; private set; } = TimeSpan.FromSeconds(30);
    public static TimeSpan MaxCooldown { get; private set; } = TimeSpan.FromMinutes(5);

    /// <summary>Apply resilience overrides from settings.data.resilience (SPEC-007).</summary>
    public static void Configure(int? failureThreshold, double? baseSeconds, double? maxSeconds)
    {
        if (failureThreshold is > 0) FailureThreshold = failureThreshold.Value;
        if (baseSeconds is > 0) BaseCooldown = TimeSpan.FromSeconds(baseSeconds.Value);
        if (maxSeconds is > 0) MaxCooldown = TimeSpan.FromSeconds(maxSeconds.Value);
    }

    public static void ReportSuccess(string connectionId) =>
        States.TryRemove(connectionId, out _);

    public static void ReportFailure(string connectionId)
    {
        States.AddOrUpdate(connectionId,
            _ => new State(1, DateTimeOffset.MinValue),
            (_, s) =>
            {
                var failures = s.Failures + 1;
                var until = failures >= FailureThreshold
                    ? DateTimeOffset.UtcNow + CooldownFor(failures)
                    : s.Until;
                return new State(failures, until);
            });
    }

    private static TimeSpan CooldownFor(int failures) => TimeSpan.FromMilliseconds(Math.Min(
        BaseCooldown.TotalMilliseconds * Math.Pow(2, failures - FailureThreshold),
        MaxCooldown.TotalMilliseconds));

    public static bool IsCooling(string connectionId) =>
        States.TryGetValue(connectionId, out var s) && s.Until > DateTimeOffset.UtcNow;

    public static void Clear(string connectionId) => States.TryRemove(connectionId, out _);

    public sealed record CooldownInfo(string ConnectionId, int Failures, bool Cooling, string? CooldownUntil);

    public static IReadOnlyList<CooldownInfo> Snapshot() =>
        States.Where(kv => kv.Value.Until > DateTimeOffset.UtcNow || kv.Value.Failures > 0)
            .Select(kv => new CooldownInfo(
                kv.Key,
                kv.Value.Failures,
                kv.Value.Until > DateTimeOffset.UtcNow,
                kv.Value.Until > DateTimeOffset.MinValue ? kv.Value.Until.ToString("o") : null))
            .ToList();
}

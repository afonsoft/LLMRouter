using System.Collections.Concurrent;

namespace LLMRouter.Core.Resilience;

/// <summary>
/// SPEC-021: provider-level circuit breaker (upstream src/shared/utils/circuitBreaker.ts).
/// 4 states — CLOSED / DEGRADED / OPEN / HALF_OPEN — with lazy recovery:
/// reads refresh OPEN→HALF_OPEN once the reset timeout elapses.
/// Only provider-level statuses trip it: 408/500/502/503/504.
/// </summary>
public static class ProviderBreaker
{
    public enum State { Closed, Degraded, Open, HalfOpen }

    public sealed record Profile(int DegradeThreshold, int OpenThreshold, TimeSpan Reset);

    /// <summary>Thresholds per auth profile (upstream PROVIDER_PROFILES).</summary>
    public static readonly IReadOnlyDictionary<string, Profile> Profiles =
        new Dictionary<string, Profile>
        {
            ["oauth"] = new(5, 8, TimeSpan.FromSeconds(60)),
            ["apikey"] = new(7, 12, TimeSpan.FromSeconds(30)),
            ["local"] = new(int.MaxValue, 2, TimeSpan.FromSeconds(15)),
        };

    /// <summary>Statuses that indicate a provider-level failure (not account/model).</summary>
    public static readonly HashSet<int> TripStatuses = [408, 500, 502, 503, 504];

    private sealed record Entry(int Failures, DateTimeOffset OpenedAt, DateTimeOffset LastFailureAt);

    private static readonly ConcurrentDictionary<string, Entry> States = new();

    /// <summary>Anti-thundering-herd: failures inside this window count once. Settable for tests.</summary>
    public static TimeSpan HerdWindow { get; set; } = TimeSpan.FromSeconds(2);

    public static string ProfileFor(string? authType) =>
        string.Equals(authType, "oauth", StringComparison.OrdinalIgnoreCase) ? "oauth"
        : IsLocalProfile(authType) ? "local" : "apikey";

    private static bool IsLocalProfile(string? authType) =>
        authType is "local" or "none" or "free";

    /// <summary>Current effective state (lazy OPEN→HALF_OPEN refresh).</summary>
    public static State GetState(string providerId, string? authType = null)
    {
        if (!States.TryGetValue(providerId, out var e)) return State.Closed;
        var profile = Profiles[ProfileFor(authType)];
        if (e.Failures >= profile.OpenThreshold)
            return DateTimeOffset.UtcNow - e.OpenedAt >= profile.Reset ? State.HalfOpen : State.Open;
        if (e.Failures >= profile.DegradeThreshold) return State.Degraded;
        return State.Closed;
    }

    /// <summary>Whether traffic may flow to the provider (HALF_OPEN allows a probe).</summary>
    public static bool CanExecute(string providerId, string? authType = null) =>
        GetState(providerId, authType) != State.Open;

    public static void ReportSuccess(string providerId) => States.TryRemove(providerId, out _);

    /// <summary>Record a provider-level failure. Returns the resulting state.</summary>
    public static State ReportFailure(string providerId, string? authType = null)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = Profiles[ProfileFor(authType)];
        States.AddOrUpdate(providerId,
            _ => new Entry(1, DateTimeOffset.MinValue, now),
            (_, e) =>
            {
                // anti-thundering-herd: a burst of concurrent failures counts once
                if (now - e.LastFailureAt < HerdWindow) return e;
                var failures = e.Failures + 1;
                var opened = failures >= profile.OpenThreshold && e.OpenedAt == DateTimeOffset.MinValue
                    ? now : e.OpenedAt;
                return new Entry(failures, opened, now);
            });
        return GetState(providerId, authType);
    }

    /// <summary>Record an upstream status; trips only for provider-level codes.</summary>
    public static void ReportStatus(string providerId, int status, string? authType = null)
    {
        if (status < 400) ReportSuccess(providerId);
        else if (TripStatuses.Contains(status)) ReportFailure(providerId, authType);
    }

    public sealed record Info(string ProviderId, string State, int Failures, string? OpenedAt, string? RetryAfter);

    public static IReadOnlyList<Info> Snapshot() =>
        States.Select(kv =>
        {
            var st = GetState(kv.Key);
            var profile = Profiles["apikey"]; // informational default
            string? retry = st == State.Open
                ? (kv.Value.OpenedAt + profile.Reset - DateTimeOffset.UtcNow).ToString()
                : null;
            return new Info(kv.Key, st.ToString().ToUpperInvariant(), kv.Value.Failures,
                kv.Value.OpenedAt == DateTimeOffset.MinValue ? null : kv.Value.OpenedAt.ToString("o"),
                retry);
        }).ToList();

    public static void Clear(string providerId) => States.TryRemove(providerId, out _);
    public static void ClearAll() => States.Clear();
}

/// <summary>
/// SPEC-021: model lockout — quarantine (provider + connection + model) on
/// model-scoped failures (429 per-model quota, 404 missing model, permission
/// errors) without disabling the whole connection.
/// </summary>
public static class ModelLockout
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Locked = new();
    private static readonly TimeSpan DefaultLock = TimeSpan.FromMinutes(10);

    /// <summary>Statuses that scope to the model rather than the connection.</summary>
    public static bool IsModelScoped(int status, string? body = null) =>
        status == 404 || (status == 429 && body?.Contains("model", StringComparison.OrdinalIgnoreCase) == true);

    private static string Key(string provider, string connId, string model) => $"{provider}/{connId}/{model}";

    public static void Lock(string provider, string connId, string model, TimeSpan? duration = null) =>
        Locked[Key(provider, connId, model)] = DateTimeOffset.UtcNow + (duration ?? DefaultLock);

    public static bool IsLocked(string provider, string connId, string model) =>
        Locked.TryGetValue(Key(provider, connId, model), out var until)
        && until > DateTimeOffset.UtcNow;

    public static void Unlock(string provider, string connId, string model) =>
        Locked.TryRemove(Key(provider, connId, model), out _);

    public static IReadOnlyList<string> Snapshot() =>
        Locked.Where(kv => kv.Value > DateTimeOffset.UtcNow)
            .Select(kv => $"{kv.Key} until {kv.Value:o}").ToList();
}

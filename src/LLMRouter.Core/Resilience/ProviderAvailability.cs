namespace LLMRouter.Core.Resilience;

/// <summary>
/// Typed provider availability — port of upstream src/lib/providerAvailability.ts
/// (#15918). A pure classifier: maps a connection's stored fields to an explicit,
/// discriminated state. It never mutates and never clears a terminal state: an old
/// or unverifiable terminal resolves to STALE_TERMINAL (re-verify with real
/// provider evidence), never silently to AVAILABLE.
/// </summary>
public static class ProviderAvailability
{
    /// <summary>Stored test statuses that mean "do not auto-recover on a timer".</summary>
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.Ordinal)
        { "credits_exhausted", "banned", "expired" };

    /// <summary>Terminal states confirmed longer ago than this are STALE_TERMINAL.</summary>
    public static readonly TimeSpan DefaultStaleness = TimeSpan.FromHours(24);

    public sealed record Input(
        bool HasCredential = true,
        bool? IsActive = null,
        string? TestStatus = null,
        string? LastErrorType = null,
        string? RateLimitedUntil = null,
        string? LastErrorAt = null,
        TimeSpan? StalenessThreshold = null);

    public sealed record Availability(
        string State,
        string? Action = null,
        string? NextEligibleRecheckAt = null,
        string? PreviousState = null,
        bool? Retryable = null);

    public static Availability Resolve(Input input, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        if (!input.HasCredential) return new Availability("NO_CREDENTIAL");
        if (input.IsActive is false) return new Availability("DISABLED");

        var status = (input.TestStatus ?? "").Trim().ToLowerInvariant();
        var threshold = input.StalenessThreshold ?? DefaultStaleness;

        if (TerminalStatuses.Contains(status))
        {
            // A terminal state is only trusted as current when it was confirmed recently.
            var confirmedAt = ToInstant(input.LastErrorAt);
            var fresh = confirmedAt is not null && now - confirmedAt.Value <= threshold;
            if (!fresh) return new Availability("STALE_TERMINAL", PreviousState: status);

            if (status == "expired") return new Availability("AUTH_EXPIRED", Action: "REAUTHENTICATE");
            if (status == "credits_exhausted")
            {
                var recheck = ToInstant(input.RateLimitedUntil);
                return recheck is not null
                    ? new Availability("QUOTA_EXHAUSTED",
                        NextEligibleRecheckAt: recheck.Value.UtcDateTime.ToString("o"))
                    : new Availability("QUOTA_EXHAUSTED");
            }
            // "banned": operator/provider disabled it; not self-recovering.
            return new Availability("DISABLED");
        }

        // Non-terminal: a future cooldown or a recorded error is retryable-unhealthy.
        var cooldownUntil = ToInstant(input.RateLimitedUntil);
        var coolingDown = cooldownUntil is not null && cooldownUntil > now;
        if (coolingDown || !string.IsNullOrWhiteSpace(input.LastErrorType))
            return new Availability("UNHEALTHY", Retryable: true);

        return new Availability("AVAILABLE");
    }

    /// <summary>Parse an ISO-8601 string or an epoch-ms numeric string, else null.</summary>
    private static DateTimeOffset? ToInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        if (long.TryParse(t, out var ms) && t == ms.ToString())
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return DateTimeOffset.TryParse(t, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dto) ? dto : null;
    }
}

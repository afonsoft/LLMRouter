using System.Globalization;

namespace LLMRouter.Shared.Ux;

public sealed record TimeRange(DateTimeOffset From, DateTimeOffset To, DateTimeOffset CompareFrom, DateTimeOffset CompareTo)
{
    public static TimeRange FromPreset(string? preset, DateTimeOffset? now = null)
    {
        var end = now ?? DateTimeOffset.UtcNow;
        var span = preset?.ToLowerInvariant() switch
        {
            "1h" => TimeSpan.FromHours(1),
            "1d" or "today" => TimeSpan.FromDays(1),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            "90d" => TimeSpan.FromDays(90),
            _ => TimeSpan.FromDays(1)
        };
        var from = end - span;
        return new(from, end, from - span, from);
    }

    public static TimeRange FromQuery(string? from, string? to, bool compare, DateTimeOffset? now = null)
    {
        var fallback = FromPreset("1d", now);
        if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start) ||
            !DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end) ||
            end <= start)
            return compare ? fallback : fallback with { CompareFrom = fallback.From };

        var span = end - start;
        return new(start, end, compare ? start - span : start, start);
    }

    public string ToQuery(bool compare = true) =>
        $"from={Uri.EscapeDataString(From.ToUniversalTime().ToString("O"))}" +
        $"&to={Uri.EscapeDataString(To.ToUniversalTime().ToString("O"))}" +
        (compare ? "&cmp=1" : "");
}

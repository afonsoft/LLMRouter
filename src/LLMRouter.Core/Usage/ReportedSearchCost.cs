using System.Collections.Concurrent;
using System.Text.Json;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Usage;

/// <summary>
/// SPEC-097: provider-reported search cost, opt-in (upstream
/// open-sse/handlers/search/reportedCost.ts #16088). A trusted endpoint may
/// report `usage.search_cost_usd` in its JSON body; when the connection opted
/// in via data.trustReportedCost, that amount is billed instead of the
/// computed price. Reports dedupe per (provider, request_id) in-process and a
/// malformed report never zeroes the cost.
/// </summary>
public static class ReportedSearchCost
{
    public const double MaxReportedUsd = 100;
    private const int MaxRememberedRequestIds = 10_000;

    private static readonly ConcurrentDictionary<string, byte> Seen = new();

    /// <summary>Trusted iff the connection opts in via data.trustReportedCost=true.</summary>
    public static bool IsTrusted(ProviderConnection c)
    {
        try
        {
            var d = JsonSerializer.Deserialize<JsonElement>(c.Data);
            return d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("trustReportedCost", out var t)
                && t.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    /// <summary>
    /// Read a provider-reported USD cost from a search response body, or null.
    /// Accepts `usage.search_cost_usd` (finite, 0..<see cref="MaxReportedUsd"/>);
    /// `usage.request_id` dedupes — a repeated request id reports null so it is
    /// not billed twice.
    /// </summary>
    public static double? ReadReportedUsd(string providerId, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var doc = JsonDocument.Parse(body).RootElement;
            if (!doc.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
                return null;
            if (!u.TryGetProperty("search_cost_usd", out var c)
                || !c.TryGetDouble(out var usd) || usd < 0 || usd > MaxReportedUsd)
                return null;
            var reqId = u.TryGetProperty("request_id", out var r) ? r.GetString() : null;
            if (reqId is { Length: > 0 })
            {
                var key = $"{providerId}{reqId}";
                if (!Seen.TryAdd(key, 0)) return null;
                if (Seen.Count > MaxRememberedRequestIds)
                    Seen.TryRemove(Seen.Keys.First(), out _);
            }
            return usd;
        }
        catch { return null; }
    }
}

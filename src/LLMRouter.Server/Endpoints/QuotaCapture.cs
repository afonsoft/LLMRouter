using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-117: passive quota telemetry — translate rate-limit headers an
/// upstream response already carries into quotaSnapshots rows
/// (upstream feeds the same table from usage/fetcher probes).
/// </summary>
public static class QuotaCapture
{
    /// <summary>
    /// Parse anthropic-ratelimit-* and x-ratelimit-* headers on <paramref
    /// name="resp"/> and persist deduped snapshots for the connection that
    /// served the request. No-op when the response carries no quota data.
    /// </summary>
    public static async Task CaptureAsync(LlmRouterDbContext db,
        string provider, string? connectionId, HttpResponseMessage resp)
    {
        var windows = FromHeaders(resp);
        if (windows.Count == 0) return;
        try { await QuotaSnapshots.SaveAsync(db, provider, connectionId, windows); }
        catch { /* telemetry must never break the request path */ }
    }

    public static Dictionary<string, QuotaSnapshots.Window> FromHeaders(HttpResponseMessage resp)
    {
        var windows = new Dictionary<string, QuotaSnapshots.Window>();

        // anthropic-ratelimit-{requests,input-tokens,output-tokens,tokens}-*
        foreach (var (key, w) in ProviderOpsEndpoints.AnthropicQuotaProbe.ReadWindows(resp))
        {
            if (w is not Dictionary<string, object?> d) continue;
            double? pct = d.TryGetValue("remainingPercentage", out var p)
                && p is int or long or double or float
                    ? Convert.ToDouble(p) : null;
            var reset = d.TryGetValue("resetAt", out var r) ? r as string : null;
            windows[key] = new QuotaSnapshots.Window(pct, pct is <= 0, reset);
        }

        // x-ratelimit-{limit,remaining,reset}-{requests,tokens} (OpenRouter-style)
        foreach (var key in new[] { "requests", "tokens" })
        {
            if (!TryHeader(resp, $"x-ratelimit-limit-{key}", out var limitRaw)
                || !TryHeader(resp, $"x-ratelimit-remaining-{key}", out var remRaw)
                || !double.TryParse(limitRaw, out var limit) || limit <= 0
                || !double.TryParse(remRaw, out var remaining))
                continue;
            TryHeader(resp, $"x-ratelimit-reset-{key}", out var reset);
            var pct = Math.Round(remaining / limit * 100, 2);
            windows.TryAdd(key, new QuotaSnapshots.Window(pct, pct <= 0, reset));
        }

        return windows;
    }

    /// <summary>Persist the probe result map emitted by AnthropicQuotaProbe.</summary>
    public static async Task SaveProbeAsync(LlmRouterDbContext db,
        string provider, string connectionId, Dictionary<string, object?> quotas)
    {
        var windows = new Dictionary<string, QuotaSnapshots.Window>();
        foreach (var (key, w) in quotas)
        {
            if (w is not Dictionary<string, object?> d) continue;
            double? pct = d.TryGetValue("remainingPercentage", out var p)
                && p is int or long or double or float
                    ? Convert.ToDouble(p) : null;
            var reset = d.TryGetValue("resetAt", out var r) ? r as string : null;
            windows[key] = new QuotaSnapshots.Window(pct, pct is <= 0, reset);
        }
        if (windows.Count > 0)
            await QuotaSnapshots.SaveAsync(db, provider, connectionId, windows);
    }

    private static bool TryHeader(HttpResponseMessage resp, string name, out string? value)
    {
        value = null;
        if (resp.Headers.TryGetValues(name, out var v)) value = v.FirstOrDefault();
        else if (resp.Content.Headers.TryGetValues(name, out v)) value = v.FirstOrDefault();
        return value is not null;
    }
}

using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-041: per-key usage limits stored in kv — `keyquota` scope holds the
/// configured caps ({rpm?,tpm?,dailyTokens?}), `keyusage` scope holds daily
/// token counters "{key}:{yyyyMMdd}" -> count. rpm/tpm are additionally
/// enforced through SPEC-039 rateLimits rows (see KeysQuotaEndpoints); the
/// daily cap is checked here by the gateway before dispatch.
/// </summary>
public static class KeyQuota
{
    public const string QuotaScope = "keyquota";
    public const string UsageScope = "keyusage";

    public static string DayKey(string apiKey) => $"{apiKey}:{DateTime.UtcNow:yyyyMMdd}";

    public static async Task<JsonElement> LimitsAsync(LlmRouterDbContext db, string apiKey)
    {
        var row = await db.Kv.FindAsync(QuotaScope, apiKey);
        return row is null ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(row.Value).RootElement;
    }

    public static async Task<long> DailyUsedAsync(LlmRouterDbContext db, string apiKey)
    {
        var row = await db.Kv.FindAsync(UsageScope, DayKey(apiKey));
        return row is not null && long.TryParse(row.Value, out var n) ? n : 0;
    }

    /// <summary>
    /// True when the key already burned its configured dailyTokens cap.
    /// SPEC-083: optional `models` glob list in limits (upstream qtSd/ key
    /// scoping) — when present, the cap applies only to matching models.
    /// </summary>
    public static async Task<bool> DailyCapExceededAsync(LlmRouterDbContext db, string apiKey, string? model = null)
    {
        if (apiKey == "dashboard") return false;
        var limits = await LimitsAsync(db, apiKey);
        if (!limits.TryGetProperty("dailyTokens", out var d) || !d.TryGetInt64(out var cap) || cap <= 0)
            return false;
        if (model is not null && limits.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
        {
            var globs = ms.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToArray();
            if (globs.Length > 0 && !globs.Any(g => ComboMappings.GlobMatch(g, model)))
                return false; // cap scoped to other models only
        }
        return await DailyUsedAsync(db, apiKey) >= cap;
    }

    /// <summary>Today's per-model counter rows (qtSd port): {key}:{date}:{model}.</summary>
    public static async Task<Dictionary<string, long>> ModelUsageAsync(LlmRouterDbContext db, string apiKey)
    {
        var prefix = DayKey(apiKey) + ":";
        var rows = await db.Kv.AsNoTracking()
            .Where(r => r.Scope == UsageScope && r.Key.StartsWith(prefix)).ToListAsync();
        return rows.Where(r => long.TryParse(r.Value, out _))
            .ToDictionary(r => r.Key[prefix.Length..], r => long.Parse(r.Value));
    }

    /// <summary>Credit prompt+completion tokens toward the key's daily counter
    /// (and the per-model qtSd counter when model is given).</summary>
    public static async Task CreditDailyAsync(LlmRouterDbContext db, string apiKey, int tokens, string? model = null)
    {
        if (apiKey == "dashboard" || tokens <= 0) return;
        foreach (var k in model is null ? new[] { DayKey(apiKey) } : new[] { DayKey(apiKey), DayKey(apiKey) + ":" + model })
        {
            var row = await db.Kv.FindAsync(UsageScope, k);
            var cur = row is not null && long.TryParse(row.Value, out var n) ? n : 0;
            if (row is null) db.Kv.Add(new KvEntry { Scope = UsageScope, Key = k, Value = (cur + tokens).ToString() });
            else row.Value = (cur + tokens).ToString();
        }
    }

    /// <summary>Delete every daily counter row (quota-schedules job reset).</summary>
    public static async Task<int> ResetDailyCountersAsync(LlmRouterDbContext db, CancellationToken ct = default)
    {
        var stale = await db.Kv.Where(r => r.Scope == UsageScope).ToListAsync(ct);
        db.Kv.RemoveRange(stale);
        return stale.Count;
    }
}

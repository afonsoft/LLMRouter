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

    /// <summary>True when the key already burned its configured dailyTokens cap.</summary>
    public static async Task<bool> DailyCapExceededAsync(LlmRouterDbContext db, string apiKey)
    {
        if (apiKey == "dashboard") return false;
        var limits = await LimitsAsync(db, apiKey);
        if (!limits.TryGetProperty("dailyTokens", out var d) || !d.TryGetInt64(out var cap) || cap <= 0)
            return false;
        return await DailyUsedAsync(db, apiKey) >= cap;
    }

    /// <summary>Credit prompt+completion tokens toward the key's daily counter.</summary>
    public static async Task CreditDailyAsync(LlmRouterDbContext db, string apiKey, int tokens)
    {
        if (apiKey == "dashboard" || tokens <= 0) return;
        var k = DayKey(apiKey);
        var row = await db.Kv.FindAsync(UsageScope, k);
        var cur = row is not null && long.TryParse(row.Value, out var n) ? n : 0;
        if (row is null) db.Kv.Add(new KvEntry { Scope = UsageScope, Key = k, Value = (cur + tokens).ToString() });
        else row.Value = (cur + tokens).ToString();
    }

    /// <summary>Delete every daily counter row (quota-schedules job reset).</summary>
    public static async Task<int> ResetDailyCountersAsync(LlmRouterDbContext db, CancellationToken ct = default)
    {
        var stale = await db.Kv.Where(r => r.Scope == UsageScope).ToListAsync(ct);
        db.Kv.RemoveRange(stale);
        return stale.Count;
    }
}

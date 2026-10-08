using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// Per-connection token quota caps (SPEC-008, upstream /dashboard/quota):
/// connections carry optional data.quotaDaily / data.quotaMonthly caps; the
/// cascade skips connections whose usageHistory sum for the period already
/// reached the cap. Usage is measured in prompt+completion tokens.
/// </summary>
public static class QuotaTracker
{
    public sealed record QuotaState(long DailyUsed, long MonthlyUsed, long? DailyLimit, long? MonthlyLimit)
    {
        public bool DailyExhausted => DailyLimit is { } d && DailyUsed >= d;
        public bool MonthlyExhausted => MonthlyLimit is { } m && MonthlyUsed >= m;
        public bool Exhausted => DailyExhausted || MonthlyExhausted;
    }

    private static (long? daily, long? monthly) Limits(string dataJson)
    {
        try
        {
            var d = JsonDocument.Parse(dataJson).RootElement;
            long? Get(string k) =>
                d.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) && n > 0 ? n : null;
            return (Get("quotaDaily"), Get("quotaMonthly"));
        }
        catch { return (null, null); }
    }

    public static async Task<QuotaState> StateAsync(LlmRouterDbContext db, ProviderConnection conn)
    {
        var (daily, monthly) = Limits(conn.Data);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        long dailyUsed = 0, monthlyUsed = 0;
        if (daily is not null)
            dailyUsed = await db.UsageHistory
                .Where(u => u.ConnectionId == conn.Id && string.Compare(u.Timestamp, today) >= 0)
                .SumAsync(u => (long)u.PromptTokens + u.CompletionTokens);
        if (monthly is not null)
            monthlyUsed = await db.UsageHistory
                .Where(u => u.ConnectionId == conn.Id && string.Compare(u.Timestamp, month) >= 0)
                .SumAsync(u => (long)u.PromptTokens + u.CompletionTokens);
        return new(dailyUsed, monthlyUsed, daily, monthly);
    }

    /// <summary>True when the connection exhausted any configured cap.</summary>
    public static async Task<bool> ExhaustedAsync(LlmRouterDbContext db, ProviderConnection conn)
    {
        var (daily, monthly) = Limits(conn.Data);
        if (daily is null && monthly is null) return false;
        return (await StateAsync(db, conn)).Exhausted;
    }
}

using Microsoft.EntityFrameworkCore;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-073: sliding quota windows per provider (upstream providers/quota-windows).
/// A provider is skipped while any of its windows is saturated — requests or
/// tokens in the last WindowMinutes reached the configured cap.
/// </summary>
public static class QuotaWindows
{
    public static async Task<bool> ExceededAsync(LlmRouterDbContext db, string provider, CancellationToken ct = default)
    {
        var wins = await db.QuotaWindows.Where(w => w.Provider == provider).ToListAsync(ct);
        if (wins.Count == 0) return false;
        var now = DateTime.UtcNow;
        foreach (var w in wins)
        {
            var since = now.AddMinutes(-Math.Max(w.WindowMinutes, 1)).ToString("yyyy-MM-dd HH:mm:ss");
            var rows = await db.UsageHistory
                .Where(u => u.Provider == provider && string.Compare(u.Timestamp, since) >= 0)
                .Select(u => new { u.PromptTokens, u.CompletionTokens })
                .ToListAsync(ct);
            if (w.MaxRequests > 0 && rows.Count >= w.MaxRequests) return true;
            if (w.MaxTokens > 0 && rows.Sum(r => r.PromptTokens + r.CompletionTokens) >= w.MaxTokens) return true;
        }
        return false;
    }

    /// <summary>
    /// Remaining capacity fraction (0..1) of the provider's tightest configured
    /// window; 1 when the provider has no windows. Used by quota-weighted combo
    /// scoring — scoped to the requested model's provider windows (upstream #16054).
    /// </summary>
    public static async Task<double> RemainingFractionAsync(
        LlmRouterDbContext db, string provider, CancellationToken ct = default)
    {
        var wins = await db.QuotaWindows.Where(w => w.Provider == provider).ToListAsync(ct);
        if (wins.Count == 0) return 1;
        var now = DateTime.UtcNow;
        var worst = 1.0;
        foreach (var w in wins)
        {
            var since = now.AddMinutes(-Math.Max(w.WindowMinutes, 1)).ToString("yyyy-MM-dd HH:mm:ss");
            var rows = await db.UsageHistory
                .Where(u => u.Provider == provider && string.Compare(u.Timestamp, since) >= 0)
                .Select(u => new { u.PromptTokens, u.CompletionTokens })
                .ToListAsync(ct);
            if (w.MaxRequests > 0)
                worst = Math.Min(worst, Math.Max(0, (double)(w.MaxRequests - rows.Count) / w.MaxRequests));
            if (w.MaxTokens > 0)
            {
                var used = rows.Sum(r => r.PromptTokens + r.CompletionTokens);
                worst = Math.Min(worst, Math.Max(0, (double)(w.MaxTokens - used) / w.MaxTokens));
            }
        }
        return worst;
    }
}

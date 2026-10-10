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
}

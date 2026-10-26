using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Budgets;

/// <summary>
/// SPEC-150: enforcement de budgets no pipeline /v1. Roda DEPOIS dos key gates,
/// com model já resolvido do body. Scopes avaliados: gateway (global), user
/// (auth.UserId), key (apiKey raw), model (request model). Spend vem de
/// usageHistory; grants ativos somam ao limite USD.
/// </summary>
public static class BudgetGate
{
    public enum Verdict { Allow, HardBlock, Held }

    public record Eval(Verdict Verdict, Budget? Budget, long? RequestId);

    /// <summary>Início do período corrente (UTC) como ISO comparável a usageHistory.Timestamp.</summary>
    public static string PeriodStart(string period, DateTime now)
    {
        var d = period switch
        {
            "daily" => now.Date,
            "weekly" => now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)), // segunda
            _ => new DateTime(now.Year, now.Month, 1),
        };
        return d.ToString("yyyy-MM-dd");
    }

    private static bool AppliesTo(Budget b, string? userId, string apiKey, string model) =>
        b.Scope switch
        {
            "gateway" => true,
            "user" => userId is not null && b.OwnerId == userId,
            "key" => b.OwnerId == apiKey,
            "model" => b.OwnerId == model,
            _ => false,
        };

    /// <summary>Spend no período (USD e tokens) para o escopo do budget.</summary>
    public static async Task<(double Usd, long Tokens)> SpendAsync(
        LlmRouterDbContext db, Budget b, string since)
    {
        var q = db.UsageHistory.AsNoTracking().Where(u => string.Compare(u.Timestamp, since) >= 0);
        q = b.Scope switch
        {
            "gateway" => q,
            "user" => q.Where(u => u.UserId == b.OwnerId),
            "key" => q.Where(u => u.ApiKey == b.OwnerId),
            "model" => q.Where(u => u.Model == b.OwnerId),
            _ => q.Where(u => false),
        };
        var rows = await q.GroupBy(_ => 1)
            .Select(g => new { Usd = g.Sum(x => x.Cost), Tok = g.Sum(x => x.PromptTokens + x.CompletionTokens) })
            .FirstOrDefaultAsync();
        return (rows?.Usd ?? 0, rows?.Tok ?? 0);
    }

    /// <summary>Grants ativos (não revogados, não expirados) somam ao limite USD.</summary>
    public static async Task<double> ActiveGrantUsdAsync(LlmRouterDbContext db, string budgetId, string now)
    {
        var grants = await db.OverageGrants.AsNoTracking()
            .Where(g => g.BudgetId == budgetId && !g.Revoked && string.Compare(g.ExpiresAt, now) > 0)
            .ToListAsync();
        return grants.Sum(g => g.Amount);
    }

    /// <summary>Avalia todos os budgets habilitados que se aplicam ao caller.
    /// Retorna no primeiro block/held encontrado (hard tem precedência sobre held).</summary>
    public static async Task<Eval> EvaluateAsync(
        LlmRouterDbContext db, string? userId, string apiKey, string model)
    {
        var now = DateTime.UtcNow;
        var nowIso = now.ToString("O");
        var budgets = await db.Budgets.AsNoTracking().Where(b => b.Enabled).ToListAsync();
        Eval? held = null;
        foreach (var b in budgets)
        {
            if (!AppliesTo(b, userId, apiKey, model)) continue;
            var since = PeriodStart(b.Period, now);
            var (usd, tokens) = await SpendAsync(db, b, since);
            var limitUsd = b.LimitUsd is { } lu ? lu + await ActiveGrantUsdAsync(db, b.Id, nowIso) : (double?)null;
            var overUsd = limitUsd is { } l && usd >= l;
            var overTok = b.LimitTokens is { } lt && tokens >= lt;
            if (!overUsd && !overTok) continue;
            if (b.Mode == "hard")
                return new Eval(Verdict.HardBlock, b, null);
            if (b.Mode == "held" && held is null)
            {
                var requester = userId ?? apiKey;
                var req = await db.OverageRequests.FirstOrDefaultAsync(
                    r => r.BudgetId == b.Id && r.Requester == requester && r.State == "pending");
                if (req is null)
                {
                    req = new OverageRequest
                    {
                        BudgetId = b.Id, Requester = requester,
                        Amount = 0, CreatedAt = nowIso, State = "pending",
                    };
                    db.OverageRequests.Add(req);
                    await db.SaveChangesAsync();
                }
                held = new Eval(Verdict.Held, b, req.Id);
            }
        }
        return held ?? new Eval(Verdict.Allow, null, null);
    }
}

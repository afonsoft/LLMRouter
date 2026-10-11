using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// SPEC-111: efeitos colaterais de uma linha de usageHistory atribuída a um
/// usuário/virtual-key — incrementa userKeys.spendUsd (reset lazy de período)
/// e debita o custo no creditLedger (balanceAfter). DEVE rodar num escritor
/// serial (UsageWriter ou o caminho sync) — balanceAfter assume ordem única.
/// </summary>
public static class UsageSideEffects
{
    public static async Task ApplyAsync(LlmRouterDbContext db, UsageRecord row,
        CancellationToken ct = default)
    {
        if (row.UserKeyId is { } ukid && row.Cost != 0)
        {
            var uk = await db.UserKeys.FirstOrDefaultAsync(k => k.Id == ukid, ct);
            if (uk is not null)
                VirtualKeyGate.ApplySpend(uk, row.Cost, DateTime.UtcNow);
        }
        if (row.UserId is { } uid && row.Cost != 0)
        {
            var prev = await db.CreditLedger.Where(l => l.UserId == uid)
                .OrderByDescending(l => l.Id)
                .Select(l => (double?)l.BalanceAfter)
                .FirstOrDefaultAsync(ct);
            db.CreditLedger.Add(new CreditLedgerRow
            {
                UserId = uid,
                Delta = -row.Cost,
                Reason = "usage",
                UsageId = row.Id,
                BalanceAfter = (prev ?? 0) - row.Cost,
                CreatedAt = DateTime.UtcNow.ToString("O"),
            });
        }
    }
}

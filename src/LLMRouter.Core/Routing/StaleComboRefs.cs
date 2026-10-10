using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// Stale combo model refs — port of upstream staleModelRefs.ts + staleModelPrune.ts
/// (#13505 + #15925). After a successful model sync, combo steps pinned to a
/// "provider/model" the authoritative catalog no longer lists are flagged. When
/// the auto-prune setting is on they are removed — but a combo is never emptied.
/// </summary>
public static class StaleComboRefs
{
    /// <summary>Settings.data flag that turns flagging into pruning (default OFF).</summary>
    public const string AutoPruneSetting = "comboAutoPruneStaleSteps";

    public sealed record Ref(string ComboId, string ComboName, string Step);

    /// <summary>
    /// Explicit "provider/model" combo steps whose model is absent from (or flagged
    /// unavailable in) the provider's synced catalog. Only consulted when the
    /// provider has at least one available synced row (an empty/failed sync flags
    /// nothing).
    /// </summary>
    public static async Task<List<Ref>> FindAsync(
        LlmRouterDbContext db, string provider, CancellationToken ct = default)
    {
        var synced = await db.SyncedModels.Where(s => s.Provider == provider).ToListAsync(ct);
        var available = synced.Where(s => s.Available).Select(s => s.Model)
            .ToHashSet(StringComparer.Ordinal);
        if (available.Count == 0) return [];

        var refs = new List<Ref>();
        foreach (var combo in await db.Combos.ToListAsync(ct))
            foreach (var step in Steps(combo))
            {
                var slash = step.IndexOf('/');
                if (slash <= 0) continue;
                if (!step[..slash].Equals(provider, StringComparison.Ordinal)) continue;
                if (!available.Contains(step[(slash + 1)..]))
                    refs.Add(new Ref(combo.Id, combo.Name, step));
            }
        return refs;
    }

    /// <summary>
    /// Remove the stale steps behind <paramref name="refs"/>. A combo whose every
    /// step is stale is left alone so pruning never empties it. Returns the refs
    /// whose steps were actually removed.
    /// </summary>
    public static async Task<List<Ref>> PruneAsync(
        LlmRouterDbContext db, List<Ref> refs, CancellationToken ct = default)
    {
        var pruned = new List<Ref>();
        foreach (var group in refs.GroupBy(r => r.ComboId))
        {
            var combo = await db.Combos.FindAsync([group.Key], ct);
            if (combo is null) continue;
            var steps = Steps(combo);
            var stale = group.Select(g => g.Step).ToHashSet(StringComparer.Ordinal);
            var kept = steps.Where(s => !stale.Contains(s)).ToList();
            if (kept.Count == 0 || kept.Count == steps.Count) continue;
            combo.Models = JsonSerializer.Serialize(kept);
            combo.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            pruned.AddRange(group);
        }
        if (pruned.Count > 0) await db.SaveChangesAsync(ct);
        return pruned;
    }

    private static List<string> Steps(Combo combo)
    {
        try { return JsonSerializer.Deserialize<List<string>>(combo.Models) ?? []; }
        catch { return []; }
    }
}

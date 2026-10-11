using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Extras;

/// <summary>
/// SPEC-057: gravação de eventos de auditoria estruturados na tabela
/// <c>auditEvents</c> ({actor, action, target, meta, ip, at}), com espelho no
/// kv "audit"/"log" para manter o /api/audit legado funcionando.
/// SPEC-110: tamper-evident hash chain — cada linha carrega <c>prevHash</c>
/// (hash da linha anterior por Id) e <c>hash</c> =
/// SHA256("prevHash|actor|action|target|meta|ip|at"). O tail lock garante
/// ordem de cadeia = ordem de inserção mesmo com writers concorrentes.
/// </summary>
public static class AuditLog
{
    /// <summary>Locks the chain tail: one writer at a time reads prevHash +
    /// appends. SaveChanges is serialized inside — throughput here is low
    /// (admin/dashboard actions, not per-request).</summary>
    private static readonly SemaphoreSlim TailLock = new(1, 1);

    /// <summary>SHA256 hex of the canonical chain payload.</summary>
    public static string ChainHash(AuditEvent e) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{e.PrevHash}|{e.Actor}|{e.Action}|{e.Target}|{e.Meta}|{e.Ip}|{e.At}")));

    /// <summary>
    /// Grava um AuditEvent encadeado (prevHash/hash) e espelha a linha no kv
    /// "audit"/"log" legado. Erros de espelhamento não propagam.
    /// </summary>
    public static async Task RecordAsync(LlmRouterDbContext db, string? actor,
        string action, string? target = null, object? meta = null, string? ip = null)
    {
        var e = new AuditEvent
        {
            Actor = actor,
            Action = action,
            Target = target,
            Meta = meta is null ? null : JsonSerializer.Serialize(meta),
            Ip = ip,
            At = DateTime.UtcNow.ToString("O"),
        };
        await TailLock.WaitAsync();
        try
        {
            e.PrevHash = await db.AuditEvents
                .Where(x => x.Hash != null)
                .OrderByDescending(x => x.Id)
                .Select(x => x.Hash)
                .FirstOrDefaultAsync();
            e.Hash = ChainHash(e);
            db.AuditEvents.Add(e);
            await db.SaveChangesAsync();
        }
        finally { TailLock.Release(); }
        try { await Extras.MirrorKvAsync(db, action, target ?? ""); }
        catch { /* espelho legado é best-effort */ }
    }

    /// <summary>
    /// SPEC-110: backfill — chains rows written before the feature existed
    /// (Hash == null), oldest→newest, reusing the last chained hash as the
    /// anchor. Lazy one-shot: safe to call on every startup.
    /// </summary>
    public static async Task<int> BackfillAsync(LlmRouterDbContext db, CancellationToken ct = default)
    {
        var anchored = await db.AuditEvents.Where(x => x.Hash != null)
            .OrderByDescending(x => x.Id).Select(x => new { x.Id, x.Hash })
            .FirstOrDefaultAsync(ct);
        var unchained = await db.AuditEvents.Where(x => x.Hash == null)
            .OrderBy(x => x.Id).ToListAsync(ct);
        if (unchained.Count == 0) return 0;
        var prev = anchored?.Hash;
        var i = 0;
        foreach (var e in unchained)
        {
            e.PrevHash = i == 0 ? (anchored?.Id is not null && anchored.Id < e.Id ? anchored.Hash : null) : prev;
            e.Hash = ChainHash(e);
            prev = e.Hash; i++;
        }
        await db.SaveChangesAsync(ct);
        return unchained.Count;
    }

    /// <summary>
    /// SPEC-110: verify a contiguous suffix — walks rows in Id order
    /// recomputing each hash; the first row may be a genesis row (PrevHash
    /// null) or an anchor (its own PrevHash points into pre-range history —
    /// trusted). Reports the first broken row's Id if any.
    /// </summary>
    public static async Task<(bool Ok, int Checked, long? FirstBrokenId)> VerifyAsync(
        LlmRouterDbContext db, long? fromId = null, CancellationToken ct = default)
    {
        var q = db.AuditEvents.OrderBy(x => x.Id).AsQueryable();
        if (fromId is not null) q = q.Where(x => x.Id >= fromId);
        var rows = await q.ToListAsync(ct);
        string? prevHash = null;
        var n = 0;
        foreach (var e in rows)
        {
            if (e.Hash is null)
            {
                if (n > 0)
                    return (false, n, e.Id); // unchained row mid-chain
                continue; // legacy prefix rows — skipped, not counted
            }
            if (n == 0)
            {
                // anchor row: its prevHash points to history outside the
                // checked range (or null at genesis) — trusted by contract.
                if (e.Hash != ChainHash(e)) return (false, 0, e.Id);
            }
            else
            {
                if (e.PrevHash != prevHash || e.Hash != ChainHash(e))
                    return (false, n, e.Id);
            }
            n++; prevHash = e.Hash;
        }
        return (true, n, null);
    }

    /// <summary>
    /// Deriva o actor a partir do cabeçalho de auth: Bearer/x-api-key vira
    /// <c>apiKey:sk-a…</c>; demais casos retornam <paramref name="fallback"/>.
    /// </summary>
    public static string ActorFromAuth(string? authorization, string? xApiKey, string fallback)
    {
        var tok = authorization is { Length: > 7 } a && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? a[7..]
            : xApiKey;
        return tok is { Length: > 0 } t
            ? (t.Length > 8 ? $"apiKey:{t[..4]}…" : "apiKey:?")
            : fallback;
    }
}

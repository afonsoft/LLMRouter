using System.Text.Json;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Extras;

/// <summary>
/// SPEC-057: gravação de eventos de auditoria estruturados na tabela
/// <c>auditEvents</c> ({actor, action, target, meta, ip, at}), com espelho no
/// kv "audit"/"log" para manter o /api/audit legado funcionando.
/// </summary>
public static class AuditLog
{
    /// <summary>
    /// Grava um AuditEvent e espelha a linha no kv "audit"/"log" legado.
    /// Erros de espelhamento não propagam (auditoria legada é best-effort).
    /// </summary>
    public static async Task RecordAsync(LlmRouterDbContext db, string? actor,
        string action, string? target = null, object? meta = null, string? ip = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Actor = actor,
            Action = action,
            Target = target,
            Meta = meta is null ? null : JsonSerializer.Serialize(meta),
            Ip = ip,
            At = DateTime.UtcNow.ToString("O"),
        });
        await db.SaveChangesAsync();
        try { await Extras.MirrorKvAsync(db, action, target ?? ""); }
        catch { /* espelho legado é best-effort */ }
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

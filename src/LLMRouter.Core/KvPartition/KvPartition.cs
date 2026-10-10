using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.KvPartition;

/// <summary>SPEC-087: migra blobs JSON quentes do kv para linhas por item (uma vez, marcada via _migrated).</summary>
public static class KvPartition
{
    /// <summary>Migra kv 'audit'/'log' (array JSON) → linhas auditEvents; marca kv._migrated:audit.</summary>
    public static async Task<int> MigrateAuditAsync(LlmRouterDbContext db)
    {
        if (await db.Kv.FindAsync("_migrated", "audit") is not null) return 0;
        var blob = await db.Kv.FindAsync("audit", "log");
        var n = 0;
        if (blob is not null && !await db.AuditEvents.AnyAsync())
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(blob.Value) ?? [];
                foreach (var e in list)
                {
                    db.AuditEvents.Add(new AuditEvent
                    {
                        Actor = e.GetValueOrDefault("actor", "system"),
                        Action = e.GetValueOrDefault("action", ""),
                        Target = e.GetValueOrDefault("detail", e.GetValueOrDefault("target", "")),
                        At = e.GetValueOrDefault("at", DateTime.UtcNow.ToString("O")),
                    });
                    n++;
                }
                await db.SaveChangesAsync();
            }
            catch { /* blob malformado — segue sem migrar */ }
        }
        db.Kv.Add(new KvEntry { Scope = "_migrated", Key = "audit", Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync();
        return n;
    }

    /// <summary>Split genérico: blob array em kv '{scope}'/'{key}' → linhas '{scope}:{key}:{i}'.</summary>
    public static async Task<int> SplitBlobAsync(LlmRouterDbContext db, string scope, string key)
    {
        var marker = $"{scope}:{key}";
        if (await db.Kv.FindAsync("_migrated", marker) is not null) return 0;
        var blob = await db.Kv.FindAsync(scope, key);
        var n = 0;
        if (blob is not null && blob.Value.TrimStart().StartsWith('['))
        {
            var items = JsonSerializer.Deserialize<List<JsonElement>>(blob.Value) ?? [];
            for (var i = 0; i < items.Count; i++)
                db.Kv.Add(new KvEntry { Scope = scope, Key = $"{key}:{i}", Value = items[i].GetRawText() });
            n = items.Count;
            db.Kv.Remove(blob);
        }
        db.Kv.Add(new KvEntry { Scope = "_migrated", Key = marker, Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync();
        return n;
    }
}

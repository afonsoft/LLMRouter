using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Extras;

/// <summary>
/// SPEC-027 kv index: collections stored as blobs under kv `{scope}/items`
/// (memory, webhooks, batches) also mirror each element into an index row
/// `{scope}/items/{id}` — direct lookups without parsing the whole blob.
/// Readers stay on the blob (compat); writers call Upsert/Remove alongside.
/// </summary>
public static class KvIndex
{
    public static async Task UpsertAsync(LlmRouterDbContext db, string scope, string id, JsonElement item)
    {
        var key = $"items/{id}";
        var row = await db.Kv.FindAsync(scope, key);
        if (row is null) db.Kv.Add(new KvEntry { Scope = scope, Key = key, Value = item.GetRawText() });
        else row.Value = item.GetRawText();
    }

    public static async Task RemoveAsync(LlmRouterDbContext db, string scope, string id)
    {
        var row = await db.Kv.FindAsync(scope, $"items/{id}");
        if (row is not null) db.Kv.Remove(row);
    }

    /// <summary>Rebuild every index row from the blob (migration/backfill).</summary>
    public static async Task<int> ReindexAsync(LlmRouterDbContext db, string scope)
    {
        var raw = (await db.Kv.FindAsync(scope, "items"))?.Value
            ?? (await db.Kv.FindAsync(scope, "list"))?.Value;
        if (raw is null) return 0;
        var n = 0;
        foreach (var item in JsonDocument.Parse(raw).RootElement.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var i) ? i.GetString() ?? i.GetRawText().Trim('"') : null;
            if (id is null) continue;
            await UpsertAsync(db, scope, id, item);
            n++;
        }
        return n;
    }
}

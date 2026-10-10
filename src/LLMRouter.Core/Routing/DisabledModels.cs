using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-081 — disabled models (9router disabledModelsDb parity). A disabled
/// model is hidden from /v1/models and skipped during combo candidate
/// expansion. Stored in kv scope "disabledModels", key = provider id,
/// value = JSON array of model ids.
/// </summary>
public static class DisabledModels
{
    public const string Scope = "disabledModels";

    public static async Task<HashSet<string>> ForProviderAsync(LlmRouterDbContext db, string provider)
    {
        var v = await db.Kv.AsNoTracking()
            .Where(k => k.Scope == Scope && k.Key == provider)
            .Select(k => k.Value).FirstOrDefaultAsync();
        if (string.IsNullOrEmpty(v)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(v)?.ToHashSet() ?? []; }
        catch { return []; }
    }

    public static async Task<Dictionary<string, string[]>> AllAsync(LlmRouterDbContext db)
    {
        var rows = await db.Kv.AsNoTracking().Where(k => k.Scope == Scope)
            .Select(k => new { k.Key, k.Value }).ToListAsync();
        var map = new Dictionary<string, string[]>();
        foreach (var r in rows)
        {
            try { map[r.Key] = JsonSerializer.Deserialize<string[]>(r.Value) ?? []; }
            catch { /* ignore malformed rows */ }
        }
        return map;
    }

    public static bool IsDisabled(IReadOnlyDictionary<string, string[]> map, string provider, string model) =>
        map.TryGetValue(provider, out var arr) && arr.Contains(model, StringComparer.OrdinalIgnoreCase);

    public static async Task SetForProviderAsync(LlmRouterDbContext db, string provider, IEnumerable<string> models)
    {
        var row = await db.Kv.FirstOrDefaultAsync(k => k.Scope == Scope && k.Key == provider);
        var arr = models.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (arr.Length == 0)
        {
            if (row is not null) db.Kv.Remove(row);
        }
        else if (row is null)
            db.Kv.Add(new KvEntry { Scope = Scope, Key = provider, Value = JsonSerializer.Serialize(arr) });
        else
            row.Value = JsonSerializer.Serialize(arr);
        await db.SaveChangesAsync();
    }
}

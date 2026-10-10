using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Radar;

/// <summary>
/// SPEC-055: radar — feeds públicos de catálogo/offers/intel/referrals,
/// sync configurável e combos sugeridos a partir do catálogo.
/// </summary>
public static class RadarStore
{
    /// <summary>Kinds suportados.</summary>
    public static readonly string[] Kinds = ["catalog", "offer", "intel", "referral"];

    /// <summary>Sources configuradas em settings.data.radar.sources.{kind} = [urls].</summary>
    public static async Task<Dictionary<string, string[]>> SourcesAsync(LlmRouterDbContext db)
    {
        var s = await db.Settings.FindAsync(1);
        var map = new Dictionary<string, string[]>();
        if (s is not null)
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
            if (d.TryGetValue("radar", out var r) && r.TryGetProperty("sources", out var src))
                map = JsonSerializer.Deserialize<Dictionary<string, string[]>>(src.GetRawText()) ?? [];
        }
        return map;
    }

    /// <summary>Grava sources.</summary>
    public static async Task SaveSourcesAsync(LlmRouterDbContext db, Dictionary<string, string[]> sources)
    {
        var s = await db.Settings.FindAsync(1)
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
        var r = d.TryGetValue("radar", out var x) && x.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(x.GetRawText()) ?? []
            : [];
        r["sources"] = JsonSerializer.SerializeToElement(sources);
        d["radar"] = JsonSerializer.SerializeToElement(r);
        s.Data = JsonSerializer.Serialize(d);
        await db.SaveChangesAsync();
    }

    /// <summary>Sync de um kind: baixa feeds (JSON array ou {items:[...]}) e faz upsert por (kind,itemKey).</summary>
    public static async Task<(int added, string? error)> SyncKindAsync(
        LlmRouterDbContext db, HttpClient http, string kind, string[] urls)
    {
        var added = 0; string? error = null;
        foreach (var u in urls)
        {
            try
            {
                using var d = JsonDocument.Parse(await http.GetStringAsync(u));
                var items = d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement
                    : d.RootElement.TryGetProperty("items", out var arr) ? arr : default;
                if (items.ValueKind != JsonValueKind.Array) continue;
                foreach (var it in items.EnumerateArray())
                {
                    var key = it.TryGetProperty("id", out var k) ? k.GetRawText()
                        : it.TryGetProperty("slug", out var s2) ? s2.GetRawText()
                        : it.GetRawText()[..Math.Min(80, it.GetRawText().Length)];
                    var title = it.TryGetProperty("name", out var n) ? n.GetString()
                        : it.TryGetProperty("title", out var t) ? t.GetString() : key;
                    var existing = await db.RadarItems.FirstOrDefaultAsync(x => x.Kind == kind && x.ItemKey == key);
                    if (existing is null)
                    {
                        db.RadarItems.Add(new RadarItem
                        {
                            Kind = kind, ItemKey = key, Title = title,
                            Data = it.GetRawText()[..Math.Min(8000, it.GetRawText().Length)],
                            At = DateTime.UtcNow.ToString("O"),
                        });
                        added++;
                    }
                    else { existing.Title = title; existing.Data = it.GetRawText()[..Math.Min(8000, it.GetRawText().Length)]; }
                }
            }
            catch (Exception ex) { error = ex.Message; }
        }
        await db.SaveChangesAsync();
        await MarkSyncAsync(db, kind);
        return (added, error);
    }

    /// <summary>Marca lastSync do kind em settings.data.radar.lastSync.</summary>
    public static async Task MarkSyncAsync(LlmRouterDbContext db, string kind)
    {
        var s = await db.Settings.FindAsync(1) ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
        var r = d.TryGetValue("radar", out var x) && x.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(x.GetRawText()) ?? [] : [];
        r["lastSync." + kind] = JsonSerializer.SerializeToElement(DateTime.UtcNow.ToString("O"));
        d["radar"] = JsonSerializer.SerializeToElement(r);
        s.Data = JsonSerializer.Serialize(d);
        await db.SaveChangesAsync();
    }

    /// <summary>Status: contagem por kind + lastSync.</summary>
    public static async Task<object> StatusAsync(LlmRouterDbContext db)
    {
        var counts = await db.RadarItems.GroupBy(x => x.Kind).Select(g => new { g.Key, n = g.Count() }).ToListAsync();
        var s = await db.Settings.FindAsync(1);
        object? last = null;
        if (s is not null)
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
            if (d.TryGetValue("radar", out var r)) last = r;
        }
        return new { counts = counts.ToDictionary(c => c.Key, c => c.n), lastSync = last };
    }

    /// <summary>Sugere combos: chains de modelos free-tier do catálogo.</summary>
    public static async Task<List<object>> SuggestCombosAsync(LlmRouterDbContext db)
    {
        var free = await db.RadarItems.Where(x => x.Kind == "offer" || x.Kind == "catalog")
            .Take(50).ToListAsync();
        var suggestions = new List<object>();
        var groups = free.GroupBy(f =>
        {
            try { return JsonDocument.Parse(f.Data ?? "{}").RootElement.TryGetProperty("provider", out var p) ? p.GetString() : "?"; }
            catch { return "?"; }
        });
        foreach (var g in groups.Where(g => g.Key is not null).Take(5))
        {
            suggestions.Add(new
            {
                name = $"free-{g.Key}",
                members = g.Select(x => x.Title).Take(4).ToArray(),
                source = "radar",
            });
        }
        return suggestions;
    }
}

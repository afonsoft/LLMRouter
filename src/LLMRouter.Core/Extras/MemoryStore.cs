using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Extras;

/// <summary>
/// SPEC-033: pluggable memory backends (upstream memory/backend.ts +
/// obsidianBackend.ts + notion/api.ts). Selected by settings.memory.backend:
///  - "kv" (default): kv memory/items JSON array (SPEC-015 behavior)
///  - "obsidian": one .md file per item in settings.obsidian.vaultPath
///  - "notion": one Notion page per item under settings.notion.parentId
/// Settings endpoints: /api/settings/obsidian {vaultPath}, /api/settings/notion
/// {token, parentId} (token masked on read).
/// </summary>
public static class MemoryStore
{
    public sealed record Item(string Id, DateTime At, string Content, string Tags);

    public static string Backend(JsonElement? settings) =>
        settings is { ValueKind: JsonValueKind.Object } s
        && s.TryGetProperty("memory", out var m) && m.TryGetProperty("backend", out var b)
            ? b.GetString() ?? "kv" : "kv";

    public static async Task<List<Item>> ListAsync(LlmRouterDbContext db,
        IHttpClientFactory hf, JsonElement? settings)
    {
        switch (Backend(settings))
        {
            case "obsidian": return ObsidianList(VaultPath(settings));
            case "notion": return await NotionListAsync(hf, settings);
            default: return KvList(db);
        }
    }

    public static async Task<Item> AddAsync(LlmRouterDbContext db,
        IHttpClientFactory hf, JsonElement? settings, string content, string tags)
    {
        var item = new Item(Guid.NewGuid().ToString("N")[..8], DateTime.UtcNow, content, tags);
        switch (Backend(settings))
        {
            case "obsidian": ObsidianAdd(VaultPath(settings), item); break;
            case "notion": item = await NotionAddAsync(hf, settings, item); break;
            default: KvAdd(db, item); break;
        }
        return item;
    }

    public static async Task<bool> RemoveAsync(LlmRouterDbContext db,
        IHttpClientFactory hf, JsonElement? settings, string id)
    {
        switch (Backend(settings))
        {
            case "obsidian": return ObsidianRemove(VaultPath(settings), id);
            case "notion": return await NotionRemoveAsync(hf, settings, id);
            default: return KvRemove(db, id);
        }
    }

    // ---------- kv backend ----------

    private static List<Item> KvList(LlmRouterDbContext db)
    {
        var raw = db.Kv.Find("memory", "items")?.Value;
        if (raw is null) return [];
        var list = new List<Item>();
        foreach (var m in JsonDocument.Parse(raw).RootElement.EnumerateArray())
            list.Add(new(m.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                m.TryGetProperty("at", out var a) && a.TryGetDateTime(out var ad) ? ad : DateTime.UtcNow,
                m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                m.TryGetProperty("tags", out var t) ? t.GetString() ?? "" : ""));
        return list;
    }

    private static void KvAdd(LlmRouterDbContext db, Item item)
    {
        var row = db.Kv.Find("memory", "items")
            ?? db.Kv.Add(new KvEntry { Scope = "memory", Key = "items", Value = "[]" }).Entity;
        var items = JsonNode.Parse(row.Value)!.AsArray();
        items.Add(new JsonObject
        {
            ["id"] = item.Id, ["at"] = item.At, ["content"] = item.Content, ["tags"] = item.Tags,
        });
        row.Value = items.ToJsonString();
        db.SaveChanges();
    }

    private static bool KvRemove(LlmRouterDbContext db, string id)
    {
        var row = db.Kv.Find("memory", "items");
        if (row is null) return false;
        var items = JsonNode.Parse(row.Value)!.AsArray();
        var next = new JsonArray(items.Where(m => m?["id"]?.GetValue<string>() != id)
            .Select(m => JsonNode.Parse(m!.ToJsonString())!).ToArray());
        var removed = next.Count != items.Count;
        row.Value = next.ToJsonString();
        db.SaveChanges();
        return removed;
    }

    // ---------- obsidian backend (vault .md files) ----------

    private static string? VaultPath(JsonElement? s) =>
        s is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty("obsidian", out var ob) && ob.TryGetProperty("vaultPath", out var v)
            ? v.GetString() : null;

    private static string FileFor(string dir, string id) =>
        Path.Combine(dir, $"llmrouter-{id}.md");

    private static List<Item> ObsidianList(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return [];
        var list = new List<Item>();
        foreach (var f in Directory.EnumerateFiles(dir, "llmrouter-*.md"))
        {
            var lines = File.ReadAllLines(f);
            var tags = "";
            var content = new List<string>();
            var inFm = lines.Length > 0 && lines[0] == "---";
            for (var i = inFm ? 1 : 0; i < lines.Length; i++)
            {
                if (inFm)
                {
                    if (lines[i] == "---") inFm = false;
                    else if (lines[i].StartsWith("tags:")) tags = lines[i][5..].Trim();
                    continue;
                }
                content.Add(lines[i]);
            }
            var id = Path.GetFileNameWithoutExtension(f)["llmrouter-".Length..];
            list.Add(new Item(id, File.GetLastWriteTimeUtc(f), string.Join('\n', content).Trim(), tags));
        }
        return list;
    }

    private static void ObsidianAdd(string? dir, Item item)
    {
        if (dir is null) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FileFor(dir, item.Id),
            $"---\nid: {item.Id}\nat: {item.At:o}\ntags: {item.Tags}\n---\n{item.Content}\n");
    }

    private static bool ObsidianRemove(string? dir, string id)
    {
        if (dir is null) return false;
        var f = FileFor(dir, id);
        if (!File.Exists(f)) return false;
        File.Delete(f);
        return true;
    }

    // ---------- notion backend (Notion REST v1) ----------

    private static (string? Token, string? ParentId) NotionCfg(JsonElement? s)
    {
        if (s is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("notion", out var n))
            return (n.TryGetProperty("token", out var t) ? t.GetString() : null,
                    n.TryGetProperty("parentId", out var p) ? p.GetString() : null);
        return (null, null);
    }

    private static HttpClient NotionClient(IHttpClientFactory hf, string token)
    {
        var c = hf.CreateClient("upstream");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Notion-Version", "2022-06-28");
        return c;
    }

    private static async Task<List<Item>> NotionListAsync(IHttpClientFactory hf, JsonElement? s)
    {
        var (token, _) = NotionCfg(s);
        if (token is null) return [];
        var client = NotionClient(hf, token);
        var resp = await client.PostAsJsonAsync("https://api.notion.com/v1/search",
            new { filter = new { value = "page", property = "object" }, page_size = 50 });
        if (!resp.IsSuccessStatusCode) return [];
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var list = new List<Item>();
        foreach (var r in doc.GetProperty("results").EnumerateArray())
        {
            var title = r.TryGetProperty("properties", out var props)
                && props.TryGetProperty("title", out var tp)
                && tp.TryGetProperty("title", out var arr) && arr.GetArrayLength() > 0
                    ? arr[0].GetProperty("plain_text").GetString() ?? "" : "";
            list.Add(new(r.GetProperty("id").GetString()!,
                r.TryGetProperty("created_time", out var ct) && ct.TryGetDateTime(out var cd) ? cd : DateTime.UtcNow,
                title, "notion"));
        }
        return list;
    }

    private static async Task<Item> NotionAddAsync(IHttpClientFactory hf, JsonElement? s, Item item)
    {
        var (token, parentId) = NotionCfg(s);
        if (token is null || parentId is null) return item;
        var client = NotionClient(hf, token);
        var resp = await client.PostAsJsonAsync("https://api.notion.com/v1/pages", new
        {
            parent = new { page_id = parentId },
            properties = new
            {
                title = new { title = new[] { new { text = new { content = item.Content[..Math.Min(80, item.Content.Length)] } } } },
            },
            children = new[]
            {
                new { paragraph = new { rich_text = new[] { new { text = new { content = item.Content } } } } },
                new { paragraph = new { rich_text = new[] { new { text = new { content = $"tags: {item.Tags} | id: {item.Id}" } } } } },
            },
        });
        return resp.IsSuccessStatusCode ? item : item;
    }

    private static async Task<bool> NotionRemoveAsync(IHttpClientFactory hf, JsonElement? s, string id)
    {
        var (token, _) = NotionCfg(s);
        if (token is null) return false;
        var client = NotionClient(hf, token);
        // archive (soft-delete) the page whose id matches (or starts with) the given id
        var pages = await NotionListAsync(hf, s);
        var hit = pages.FirstOrDefault(p => p.Id == id || p.Id.Replace("-", "").StartsWith(id.Replace("-", "")));
        if (hit is null) return false;
        var resp = await client.PatchAsJsonAsync($"https://api.notion.com/v1/pages/{hit.Id}",
            new { archived = true });
        return resp.IsSuccessStatusCode;
    }
}

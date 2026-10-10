using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Memory;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-062: qdrant/vector memory — settings, provider lists, engine status,
/// health, reindex, retrieve-preview, summarize.
/// </summary>
public static class MemoryVectorEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static async Task<JsonElement?> SettingsEl(LlmRouterDbContext db)
    {
        var row = await db.Settings.FindAsync(1);
        return row is null ? null : JsonSerializer.Deserialize<JsonElement>(row.Data);
    }

    private static async Task<JsonObject> SettingsNode(LlmRouterDbContext db)
    {
        var row = await db.Settings.FindAsync(1);
        return JsonNode.Parse(row?.Data ?? "{}")!.AsObject();
    }

    private static async Task SaveSettings(LlmRouterDbContext db, JsonObject data)
    {
        var row = await db.Settings.FindAsync(1)
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        row.Data = data.ToJsonString();
        await db.SaveChangesAsync();
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- settings/qdrant {url, key, collection, mode}
        g.MapGet("/settings/qdrant", async (LlmRouterDbContext db) =>
        {
            var (mode, url, key, col) = VectorMemory.Cfg(await SettingsEl(db));
            return Results.Json(new
            {
                mode, url, collection = col, hasKey = key is not null,
            }, JsonOpts);
        });
        g.MapPut("/settings/qdrant", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var data = await SettingsNode(db);
            data["qdrant"] ??= new JsonObject();
            var q = data["qdrant"]!.AsObject();
            if (b.TryGetProperty("url", out var u)) q["url"] = u.GetString();
            if (b.TryGetProperty("key", out var k) && k.GetString() is { Length: > 0 }) q["key"] = k.GetString();
            if (b.TryGetProperty("collection", out var c)) q["collection"] = c.GetString();
            data["memory"] ??= new JsonObject();
            var mem = data["memory"]!.AsObject();
            mem["vector"] ??= new JsonObject();
            if (b.TryGetProperty("mode", out var m)) mem["vector"]!["mode"] = m.GetString();
            await SaveSettings(db, data);
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- embedding-providers + rerank-providers (config rows)
        g.MapGet("/memory/embedding-providers", async (LlmRouterDbContext db) =>
            Results.Json(new { providers = VectorMemory.EmbeddingProviders(await SettingsEl(db)) }, JsonOpts));
        g.MapGet("/memory/rerank-providers", async (LlmRouterDbContext db) =>
        {
            var s = await SettingsEl(db);
            var list = s is { ValueKind: JsonValueKind.Object } o
                && o.TryGetProperty("memory", out var m) && m.ValueKind == JsonValueKind.Object
                && m.TryGetProperty("rerankProviders", out var e) && e.ValueKind == JsonValueKind.Array
                ? e : JsonDocument.Parse("[]").RootElement;
            return Results.Json(new { providers = list }, JsonOpts);
        });
        g.MapPut("/memory/embedding-providers", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var data = await SettingsNode(db);
            data["memory"] ??= new JsonObject();
            data["memory"]!["embeddingProviders"] = JsonNode.Parse(b.GetProperty("providers").GetRawText());
            await SaveSettings(db, data);
            return Results.Json(new { ok = true }, JsonOpts);
        });
        g.MapPut("/memory/rerank-providers", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var data = await SettingsNode(db);
            data["memory"] ??= new JsonObject();
            data["memory"]!["rerankProviders"] = JsonNode.Parse(b.GetProperty("providers").GetRawText());
            await SaveSettings(db, data);
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- engine-status + health
        g.MapGet("/memory/engine-status", async (LlmRouterDbContext db) =>
        {
            var s = await SettingsEl(db);
            var (mode, url, _, col) = VectorMemory.Cfg(s);
            var items = mode == "qdrant"
                ? (int?)null
                : await db.Kv.CountAsync(k => k.Scope == "vectorMem");
            return Results.Json(new
            {
                backend = "vector", mode, url, collection = col, items,
                embeddingProviders = VectorMemory.EmbeddingProviders(s).Count,
                localEmbedderDims = VectorMemory.LocalDims,
            }, JsonOpts);
        });
        g.MapGet("/memory/health", async (LlmRouterDbContext db, IHttpClientFactory hf) =>
        {
            var (mode, url, key, col) = VectorMemory.Cfg(await SettingsEl(db));
            if (mode != "qdrant") return Results.Json(new { healthy = true, mode }, JsonOpts);
            try
            {
                var client = hf.CreateClient("logexport");
                client.Timeout = TimeSpan.FromSeconds(5);
                if (key is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("api-key", key);
                var r = await client.GetAsync($"{url!.TrimEnd('/')}/collections/{col}");
                return Results.Json(new { healthy = r.IsSuccessStatusCode, mode, status = (int)r.StatusCode }, JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { healthy = false, mode, error = ex.Message }, JsonOpts);
            }
        });

        // ---- reindex: push every memory item into the vector store
        g.MapPost("/memory/reindex", async (LlmRouterDbContext db, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var s = await SettingsEl(db);
            var items = await Core.Extras.MemoryStore.ListAsync(db, hf, s);
            var n = 0;
            foreach (var it in items)
            {
                await VectorMemory.UpsertAsync(db, hf, s, new VectorMemory.Item(it.Id, it.At, it.Content, it.Tags), ct);
                n++;
            }
            await Core.Extras.Extras.AuditAsync(db, "memory.reindex", $"{n} items");
            return Results.Json(new { reindexed = n }, JsonOpts);
        });

        // ---- retrieve-preview: semantic query → ranked items
        g.MapPost("/memory/retrieve-preview", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var q = b.TryGetProperty("q", out var qq) ? qq.GetString() ?? "" :
                b.TryGetProperty("query", out var q2) ? q2.GetString() ?? "" : "";
            var topK = b.TryGetProperty("topK", out var t) && t.TryGetInt32(out var tk) ? Math.Clamp(tk, 1, 50) : 5;
            var hits = await VectorMemory.SearchAsync(db, hf, await SettingsEl(db), q, topK, ct);
            return Results.Json(new
            {
                query = q,
                hits = hits.Select(h => new { h.Item.Id, h.Item.Content, h.Item.Tags, h.Score }),
            }, JsonOpts);
        });

        // ---- summarize: LLM-compress an item (or raw content) via internal chat
        g.MapPost("/memory/summarize", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            string? content = b.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (content is null && b.TryGetProperty("id", out var id))
            {
                var items = await Core.Extras.MemoryStore.ListAsync(db, hf, await SettingsEl(db));
                content = items.FirstOrDefault(i => i.Id == id.GetString())?.Content;
            }
            if (content is null) return Results.BadRequest(new { error = "id or content required" });
            var text = await InternalChat.CallAsync(ctx.RequestServices, "auto/best",
                $"Compress this memory item to <=80 words, keeping key facts:\n\n{content}", "memory-summarize");
            return Results.Json(new { summary = text }, JsonOpts);
        });
    }
}

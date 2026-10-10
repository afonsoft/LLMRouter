using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Memory;

/// <summary>
/// SPEC-062: vector memory backend — embeddings + semantic retrieval.
/// `settings.memory.vector.mode`:
///   "embedded" (default fallback): vectors stored in kv 'vectorMem', brute-force
///     cosine over stored vectors; local char-ngram embedder when no provider set;
///   "qdrant": points upserted to a user-supplied Qdrant REST endpoint.
/// Embeddings come from `settings.memory.embeddingProviders[]` ({provider,model,dims})
/// using existing provider connections' /v1/embeddings; the local embedder is the
/// offline fallback (deterministic hashed n-grams, dim 128).
/// </summary>
public static class VectorMemory
{
    public const int LocalDims = 128;
    private const string Scope = "vectorMem";

    public sealed record Item(string Id, DateTime At, string Content, string Tags);
    public sealed record Hit(Item Item, double Score);

    /// <summary>Vector settings block from settings.memory.vector / settings.qdrant.</summary>
    public static (string Mode, string? Url, string? Key, string Collection) Cfg(JsonElement? s)
    {
        var mode = "embedded"; string? url = null, key = null; var col = "llmrouter";
        if (s is { ValueKind: JsonValueKind.Object } o)
        {
            if (o.TryGetProperty("qdrant", out var q) && q.ValueKind == JsonValueKind.Object)
            {
                if (q.TryGetProperty("url", out var u)) url = u.GetString();
                if (q.TryGetProperty("key", out var k)) key = k.GetString();
                if (q.TryGetProperty("collection", out var c) && c.GetString() is { Length: > 0 } cs) col = cs;
            }
            if (o.TryGetProperty("memory", out var m) && m.ValueKind == JsonValueKind.Object
                && m.TryGetProperty("vector", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("mode", out var mm) && mm.GetString() is { Length: > 0 } ms) mode = ms;
                if (v.TryGetProperty("url", out var u2)) url ??= u2.GetString();
                if (v.TryGetProperty("collection", out var c2) && c2.GetString() is { Length: > 0 } cs2) col = cs2;
            }
            if (url is not null) mode = "qdrant";
        }
        return (mode, url, key, col);
    }

    /// <summary>Embedding provider rows: [{provider, model, dims}] from settings.memory.embeddingProviders.</summary>
    public static List<JsonElement> EmbeddingProviders(JsonElement? s) =>
        s is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty("memory", out var m) && m.ValueKind == JsonValueKind.Object
        && m.TryGetProperty("embeddingProviders", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().ToList() : [];

    /// <summary>Deterministic local embedder: hashed char n-grams → dim-128 unit vector.</summary>
    public static float[] LocalEmbed(string text)
    {
        var v = new float[LocalDims];
        var t = text.ToLowerInvariant();
        for (var n = 2; n <= 4; n++)
            for (var i = 0; i + n <= t.Length; i++)
            {
                var h = Hash(t.AsSpan(i, n));
                v[h % LocalDims] += 1f / n;
            }
        var norm = (float)Math.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;

        static int Hash(ReadOnlySpan<char> s)
        {
            unchecked { var h = 5381; foreach (var c in s) h = h * 33 + c; return h & 0x7fffffff; }
        }
    }

    /// <summary>Embed via the first configured provider conn (POST {baseUrl}/v1/embeddings); null → caller falls back to local.</summary>
    public static async Task<float[]?> ProviderEmbedAsync(LlmRouterDbContext db,
        IHttpClientFactory hf, JsonElement? s, string text, CancellationToken ct = default)
    {
        var cfg = EmbeddingProviders(s).FirstOrDefault();
        if (cfg.ValueKind != JsonValueKind.Object) return null;
        var provider = cfg.TryGetProperty("provider", out var p) ? p.GetString() : null;
        var model = cfg.TryGetProperty("model", out var m) ? m.GetString() : null;
        if (provider is null || model is null) return null;
        var conn = await db.ProviderConnections.AsNoTracking()
            .Where(c => c.IsActive && c.Provider == provider)
            .OrderBy(c => c.Priority).FirstOrDefaultAsync(ct);
        var baseUrl = conn is null ? null : Core.ProviderOps.ProviderRules.DataOf(conn)
            .TryGetValue("baseUrl", out var b) ? b.GetString() : null;
        if (baseUrl is null) return null;
        var client = hf.CreateClient("logexport");
        try
        {
            var r = await client.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/v1/embeddings",
                new { model, input = text }, ct);
            if (!r.IsSuccessStatusCode) return null;
            var json = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (json.TryGetProperty("data", out var d) && d.GetArrayLength() > 0
                && d[0].TryGetProperty("embedding", out var e))
                return e.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
        }
        catch { }
        return null;
    }

    /// <summary>Embed text: provider when configured+reachable, else local n-grams.</summary>
    public static async Task<float[]> EmbedAsync(LlmRouterDbContext db,
        IHttpClientFactory hf, JsonElement? s, string text, CancellationToken ct = default) =>
        await ProviderEmbedAsync(db, hf, s, text, ct) ?? LocalEmbed(text);

    /// <summary>Index one item under the configured mode.</summary>
    public static async Task UpsertAsync(LlmRouterDbContext db, IHttpClientFactory hf,
        JsonElement? s, Item item, CancellationToken ct = default)
    {
        var (mode, url, key, col) = Cfg(s);
        var vector = await EmbedAsync(db, hf, s, item.Content, ct);
        if (mode == "qdrant" && url is not null)
        {
            var client = hf.CreateClient("logexport");
            if (key is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("api-key", key);
            var body = new
            {
                points = new[]
                {
                    new
                    {
                        id = PointId(item.Id),
                        vector,
                        payload = new { itemId = item.Id, item.Content, item.Tags, at = item.At.ToString("o") },
                    },
                },
            };
            await client.PutAsJsonAsync($"{url.TrimEnd('/')}/collections/{col}/points?wait=true", body, ct);
            return;
        }
        var row = await db.Kv.FindAsync(Scope, item.Id);
        var val = JsonSerializer.Serialize(new { item.Content, item.Tags, at = item.At.ToString("o"), vector });
        if (row is null) db.Kv.Add(new KvEntry { Scope = Scope, Key = item.Id, Value = val });
        else row.Value = val;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Semantic search → ranked items (topK).</summary>
    public static async Task<List<Hit>> SearchAsync(LlmRouterDbContext db, IHttpClientFactory hf,
        JsonElement? s, string query, int topK = 5, CancellationToken ct = default)
    {
        var (mode, url, key, col) = Cfg(s);
        var qv = await EmbedAsync(db, hf, s, query, ct);
        if (mode == "qdrant" && url is not null)
        {
            var client = hf.CreateClient("logexport");
            if (key is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("api-key", key);
            var r = await client.PostAsJsonAsync($"{url.TrimEnd('/')}/collections/{col}/points/search",
                new { vector = qv, limit = topK, with_payload = true }, ct);
            if (!r.IsSuccessStatusCode) return [];
            var json = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            var hits = new List<Hit>();
            if (json.TryGetProperty("result", out var res))
                foreach (var h in res.EnumerateArray())
                {
                    var p = h.GetProperty("payload");
                    hits.Add(new Hit(new Item(
                        p.TryGetProperty("itemId", out var i) ? i.GetString() ?? "" : "",
                        p.TryGetProperty("at", out var a) && DateTime.TryParse(a.GetString(), out var dt) ? dt : DateTime.UtcNow,
                        p.TryGetProperty("Content", out var c1) ? c1.GetString() ?? "" : p.TryGetProperty("content", out var c2) ? c2.GetString() ?? "" : "",
                        p.TryGetProperty("Tags", out var t1) ? t1.GetString() ?? "" : p.TryGetProperty("tags", out var t2) ? t2.GetString() ?? "" : ""),
                        h.TryGetProperty("score", out var sc) ? sc.GetDouble() : 0));
                }
            return hits;
        }
        var rows = await db.Kv.Where(k => k.Scope == Scope).ToListAsync(ct);
        return rows.Select(r =>
            {
                var d = JsonSerializer.Deserialize<JsonElement>(r.Value);
                var vec = d.GetProperty("vector").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                return new Hit(new Item(r.Key,
                    d.TryGetProperty("at", out var a) && DateTime.TryParse(a.GetString(), out var dt) ? dt : DateTime.MinValue,
                    d.GetProperty("Content").GetString() ?? d.GetProperty("content").GetString() ?? "",
                    d.TryGetProperty("Tags", out var t) ? t.GetString() ?? "" : ""),
                    Cosine(qv, vec));
            })
            .OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    /// <summary>Qdrant point ids must be numeric/uuid — hash item id to a positive long.</summary>
    private static long PointId(string id)
    {
        unchecked { long h = 5381; foreach (var c in id) h = h * 33 + c; return h & 0x7fffffffffffffff; }
    }

    private static double Cosine(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (Math.Sqrt(na) * Math.Sqrt(nb)) : 0;
    }
}

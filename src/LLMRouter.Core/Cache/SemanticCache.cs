using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Memory;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Cache;

/// <summary>
/// SPEC-106: semantic (similarity) prompt cache — opt-in layer behind the
/// exact-match <see cref="PromptCache"/>. Order: exact hit → semantic hit
/// (cosine ≥ threshold) → upstream. Embeddings come from the normal gateway's
/// provider connections via <see cref="VectorMemory"/> (provider → local
/// n-gram fallback); failures fall open to the exact layer only.
/// Settings: settings.data.promptCache.semantic
///   {enabled, threshold: 0.0-1.0, embeddingModel, maxEntries}
/// Disabled by default — similarity cache misfires on agentic/tool traffic.
/// </summary>
public static class SemanticCache
{
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    public sealed record Cfg(bool Enabled, double Threshold, string? EmbeddingModel, int MaxEntries);

    public static Cfg Read(JsonElement sdata)
    {
        var enabled = false; var threshold = 0.85; string? model = null; var max = 5000;
        if (sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("promptCache", out var pc) && pc.ValueKind == JsonValueKind.Object
            && pc.TryGetProperty("semantic", out var sm) && sm.ValueKind == JsonValueKind.Object)
        {
            if (sm.TryGetProperty("enabled", out var e)) enabled = e.ValueKind == JsonValueKind.True;
            if (sm.TryGetProperty("threshold", out var t) && t.TryGetDouble(out var d))
                threshold = Math.Clamp(d, 0, 1);
            if (sm.TryGetProperty("embeddingModel", out var m) && m.GetString() is { Length: > 0 } ms)
                model = ms;
            if (sm.TryGetProperty("maxEntries", out var mx) && mx.TryGetInt32(out var i)) max = i;
        }
        return new Cfg(enabled, threshold, model, max);
    }

    /// <summary>Bodies that must never hit either cache layer: tool use and
    /// high-temperature sampling (stream is gated at the call site).</summary>
    public static bool BypassesBody(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return true;
        if (body.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array
            && t.GetArrayLength() > 0) return true;
        if (body.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array
            && tc.GetArrayLength() > 0) return true;
        if (body.TryGetProperty("temperature", out var tp) && tp.TryGetDouble(out var tv)
            && tv > 0.2) return true;
        return false;
    }

    /// <summary>Connection-level opt-out: data.cacheOptOut → never serve/store
    /// cached responses on this connection (sensitive tenants).</summary>
    public static bool ConnOptOut(string? connData) =>
        connData is not null
        && connData.Contains("\"cacheOptOut\"", StringComparison.OrdinalIgnoreCase)
        && connData.Contains("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Prompt text for embedding: all message contents joined.</summary>
    public static string TextOf(JsonElement body)
    {
        var sb = new StringBuilder();
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray())
            {
                if (m.TryGetProperty("content", out var c))
                {
                    if (c.ValueKind == JsonValueKind.String) sb.Append(c.GetString()).Append(' ');
                    else if (c.ValueKind == JsonValueKind.Array)
                        foreach (var part in c.EnumerateArray())
                            if (part.TryGetProperty("text", out var tx))
                                sb.Append(tx.GetString()).Append(' ');
                }
            }
        return sb.ToString().Trim();
    }

    private static long _hits, _misses; private static double _simSum; private static long _simN;

    public static void ResetStats() { _hits = 0; _misses = 0; _simSum = 0; _simN = 0; }

    private static double Cosine(float[] a, byte[] blob)
    {
        var b = new float[blob.Length / 4];
        Buffer.BlockCopy(blob, 0, b, 0, blob.Length);
        var n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (Math.Sqrt(na) * Math.Sqrt(nb)) : 0;
    }

    /// <summary>Semantic lookup: embed prompt, scan recent entries, return the
    /// best row ≥ threshold. Embedding failures fall open (null → upstream).</summary>
    public static async Task<(string Response, double Similarity)?> LookupAsync(
        LlmRouterDbContext db, IHttpClientFactory hf, JsonElement sdata,
        string model, float[] vec, Cfg cfg, CancellationToken ct = default)
    {
        var rows = await db.SemanticCache.AsNoTracking()
            .OrderByDescending(e => e.CreatedAt).Take(cfg.MaxEntries).ToListAsync(ct);
        SemanticCacheEntry? best = null; var sim = 0.0;
        foreach (var r in rows)
        {
            if (r.Model != model) continue;
            var s = Cosine(vec, r.Embedding);
            if (s > sim) { sim = s; best = r; }
        }
        if (best is null || sim < cfg.Threshold) { Interlocked.Increment(ref _misses); return null; }
        Interlocked.Increment(ref _hits);
        Interlocked.Increment(ref _simN);
        System.Threading.Interlocked.Exchange(ref _simSum, _simSum + sim);
        var tracked = await db.SemanticCache.FindAsync([best.Id], ct);
        if (tracked is not null) { tracked.HitCount++; await db.SaveChangesAsync(ct); }
        return (best.ResponseJson, sim);
    }

    /// <summary>Store prompt vector + response; evicts oldest past maxEntries.</summary>
    public static async Task StoreAsync(
        LlmRouterDbContext db, JsonElement sdata, string promptHash,
        string model, float[] vec, string responseJson, Cfg cfg, CancellationToken ct = default)
    {
        var blob = new byte[vec.Length * 4];
        Buffer.BlockCopy(vec, 0, blob, 0, blob.Length);
        db.SemanticCache.Add(new SemanticCacheEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12], Embedding = blob,
            PromptHash = promptHash, Model = model, ResponseJson = responseJson,
            CreatedAt = Now(),
        });
        var excess = await db.SemanticCache.CountAsync(ct) + 1 - cfg.MaxEntries;
        if (excess > 0)
        {
            var oldest = await db.SemanticCache.OrderBy(e => e.CreatedAt).Take(excess).ToListAsync(ct);
            db.SemanticCache.RemoveRange(oldest);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Embedding vector for prompt text via the configured model —
    /// settings.memory.embeddingProviders conn, else local n-grams.</summary>
    public static async Task<float[]> EmbedAsync(LlmRouterDbContext db,
        IHttpClientFactory hf, JsonElement sdata, Cfg cfg, string promptText,
        CancellationToken ct = default) =>
        await VectorMemory.EmbedAsync(db, hf, sdata, promptText, ct);

    public static object Stats(int entries, int hitTotal) => new
    {
        entries,
        hits = Interlocked.Read(ref _hits),
        misses = Interlocked.Read(ref _misses),
        storedHits = hitTotal,
        hitRate = _hits + _misses == 0 ? 0 : (double)_hits / (_hits + _misses),
        avgSimilarity = _simN == 0 ? 0 : _simSum / _simN,
    };
}

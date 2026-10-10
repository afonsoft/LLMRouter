using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Cache;

/// <summary>
/// SPEC-045: prompt cache — replay stored upstream responses for identical
/// requests. Keys on a SHA256 of the normalized request (canonical property
/// order, volatile keys like stream/metadata/user stripped).
/// Settings: settings.data.promptCache {enabled, ttlMinutes, maxEntries}.
/// </summary>
public static class PromptCache
{
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    // keys that must not affect cache identity
    private static readonly HashSet<string> Volatile =
        new(StringComparer.OrdinalIgnoreCase)
            { "stream", "stream_options", "metadata", "user", "store", "n" };

    public static bool Enabled(JsonElement sdata) =>
        sdata.ValueKind == JsonValueKind.Object
        && sdata.TryGetProperty("promptCache", out var pc) && pc.ValueKind == JsonValueKind.Object
        && pc.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement sdata, string key, int def) =>
        sdata.ValueKind == JsonValueKind.Object
        && sdata.TryGetProperty("promptCache", out var pc) && pc.ValueKind == JsonValueKind.Object
        && pc.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : def;

    /// <summary>Stable SHA256 over the canonicalized request (model + params + messages).</summary>
    public static string ComputeHash(string inbound, string model, JsonElement body)
    {
        var node = body.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(body.GetRawText())!.AsObject() : new JsonObject();
        foreach (var k in node.Select(p => p.Key).ToList())
            if (Volatile.Contains(k)) node.Remove(k);
        var canonical = Canonicalize(node);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{inbound}|{model}|{canonical}"));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case null: return "null";
            case JsonObject o:
                return "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => $"{JsonSerializer.Serialize(p.Key)}:{Canonicalize(p.Value)}")) + "}";
            case JsonArray a:
                return "[" + string.Join(",", a.Select(Canonicalize)) + "]";
            case JsonValue v:
                return v.ToJsonString();
            default: return node.ToJsonString();
        }
    }

    public static bool IsReasoningRequest(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
        && (body.TryGetProperty("reasoning", out _) || body.TryGetProperty("thinking", out _));

    /// <summary>Hit: returns the stored response JSON and bumps Hits; misses and expired → null.</summary>
    public static async Task<string?> LookupAsync(LlmRouterDbContext db, string hash, CancellationToken ct = default)
    {
        var e = await db.CacheEntries.FirstOrDefaultAsync(c => c.Hash == hash, ct);
        if (e is null || string.Compare(e.ExpiresAt, Now(), StringComparison.Ordinal) <= 0) return null;
        e.Hits++;
        await db.SaveChangesAsync(ct);
        return e.Response;
    }

    /// <summary>Store a completed response; evicts the oldest rows past maxEntries.</summary>
    public static async Task StoreAsync(
        LlmRouterDbContext db, JsonElement sdata, string hash,
        string provider, string model, string request, string response,
        bool reasoning, int tokensSaved, CancellationToken ct = default)
    {
        var ttl = Int(sdata, "ttlMinutes", 60);
        var max = Int(sdata, "maxEntries", 500);
        db.CacheEntries.Add(new CacheEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12], Hash = hash,
            Provider = provider, Model = model, Request = request, Response = response,
            Reasoning = reasoning ? 1 : 0, TokensSaved = tokensSaved,
            CreatedAt = Now(),
            ExpiresAt = DateTime.UtcNow.AddMinutes(ttl).ToString("yyyy-MM-dd HH:mm:ss"),
        });
        var excess = await db.CacheEntries.CountAsync(ct) + 1 - max;
        if (excess > 0)
        {
            var oldest = await db.CacheEntries.OrderBy(c => c.CreatedAt).Take(excess).ToListAsync(ct);
            db.CacheEntries.RemoveRange(oldest);
        }
        await db.SaveChangesAsync(ct);
    }
}

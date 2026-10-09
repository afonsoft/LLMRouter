using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LLMRouter.Core.Compression;

/// <summary>
/// Principal-scoped, bounded in-memory store for CCR-retrievable blocks.
/// Mirrors the upstream `storage: "memory"` store: key `${principalId} ${hash}`,
/// retrieval counter → ramp, TTL + global caps.
/// </summary>
public static class CcrStore
{
    public const int MaxEntries = 5_000;
    public const int MaxBlockBytes = 2 * 1024 * 1024;
    public const int RetrievalThreshold = 3;
    public const int DefaultTtlSeconds = 24 * 60 * 60;

    public sealed class Entry
    {
        public required string PrincipalId { get; init; }
        public required string Hash { get; init; }
        public required string Content { get; init; }
        public long CreatedAt { get; init; }
        public long ExpiresAt { get; set; }
        public int RetrievalCount { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Entry> Store = new();

    private static string Key(string principalId, string hash) => $"{principalId} {hash}";

    public static string HashOf(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..24].ToLowerInvariant();

    /// <summary>Store a block; returns its 24-hex hash, or null when over budget/too large.</summary>
    public static string? Put(string content, string principalId, int ttlSeconds = DefaultTtlSeconds)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxBlockBytes) return null;
        var hash = HashOf(content);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entry = new Entry
        {
            PrincipalId = principalId,
            Hash = hash,
            Content = content,
            CreatedAt = now,
            ExpiresAt = now + Math.Min(ttlSeconds, 7 * 24 * 60 * 60) * 1000L,
        };
        Store[Key(principalId, hash)] = entry;
        EnforceBudget(now);
        return hash;
    }

    /// <summary>Fetch a block (management scope = no principal → searches every principal's copy).</summary>
    public static string? Get(string hash, string? principalId = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Entry? e = null;
        if (principalId is not null) Store.TryGetValue(Key(principalId, hash), out e);
        else e = Store.Values.FirstOrDefault(v => v.Hash == hash);
        if (e is null) return null;
        if (e.ExpiresAt < now) { Store.TryRemove(Key(e.PrincipalId, hash), out _); return null; }
        return e.Content;
    }

    /// <summary>Record a retrieval; returns the new count for that principal's block.</summary>
    public static int RecordRetrieval(string hash, string principalId)
    {
        if (!Store.TryGetValue(Key(principalId, hash), out var e)) return 0;
        return ++e.RetrievalCount;
    }

    /// <summary>True once the block hit the retrieval threshold for this principal — skip compressing it.</summary>
    public static bool ShouldSkipCompression(string hash, string principalId) =>
        Store.TryGetValue(Key(principalId, hash), out var e) && e.RetrievalCount >= RetrievalThreshold;

    /// <summary>Prior-retrieval ramp: raises effective minChars for hot blocks (H8).</summary>
    public static int EffectiveMinChars(string hash, string principalId, int baseMinChars, int rampFactor = 2) =>
        Store.TryGetValue(Key(principalId, hash), out var e)
            ? baseMinChars + e.RetrievalCount * rampFactor * baseMinChars / 10
            : baseMinChars;

    private static void EnforceBudget(long now)
    {
        if (Store.Count <= MaxEntries) return;
        foreach (var kv in Store.Where(kv => kv.Value.ExpiresAt < now).ToList())
            Store.TryRemove(kv.Key, out _);
        if (Store.Count <= MaxEntries) return;
        foreach (var kv in Store.OrderBy(kv => kv.Value.CreatedAt).Take(Store.Count - MaxEntries).ToList())
            Store.TryRemove(kv.Key, out _);
    }

    public static (int Entries, long Bytes) Stats() =>
        (Store.Count, Store.Values.Sum(e => (long)Encoding.UTF8.GetByteCount(e.Content)));

    public static void Clear() => Store.Clear();

    /// <summary>Clear one principal's entries, or everything when principalId is null.</summary>
    public static void Clear(string? principalId)
    {
        if (principalId is null) { Store.Clear(); return; }
        foreach (var k in Store.Keys.Where(k => k.StartsWith(principalId + " ", StringComparison.Ordinal)).ToArray())
            Store.TryRemove(k, out _);
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// SPEC-074: process-wide cache for hot-path config reads (settings, keys,
/// combos, rate limits, pools, quotas). Entries carry a short TTL as a safety
/// net; LlmRouterDbContext busts the whole cache whenever a non-telemetry
/// entity is written, so mutations through any endpoint/jobs take effect
/// immediately. Telemetry tables (usage history, request details, sessions,
/// runs) never bust — they are written every request.
/// </summary>
public sealed class HotCache
{
    public static HotCache Default { get; } = new();

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 20_000 });
    private readonly ConcurrentDictionary<string, byte> _keys = new();

    // Observability counters (tests + diagnostics).
    public long Hits => _hits;
    public long Misses => _misses;

    public async Task<T> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        if (_cache.TryGetValue(key, out var cached) && cached is T hit)
        {
            Interlocked.Increment(ref _hits);
            return hit;
        }
        Interlocked.Increment(ref _misses);
        var value = await factory();
        _cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
        _keys.TryAdd(key, 0);
        return value;
    }
    private long _hits, _misses;

    public void InvalidateAll()
    {
        foreach (var k in _keys.Keys) _cache.Remove(k);
        _keys.Clear();
    }

    /// <summary>Drop one cached entry (e.g. a quota verdict a usage write just
    /// made stale).</summary>
    public void Invalidate(string key)
    {
        _cache.Remove(key);
        _keys.TryRemove(key, out _);
    }
}

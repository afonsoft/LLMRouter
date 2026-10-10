using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// SPEC-074: process-wide cache for hot-path config reads (settings, keys,
/// combos, rate limits, pools, quotas).
///
/// Backed by <see cref="Microsoft.Extensions.Caching.Hybrid.HybridCache"/> when
/// the host registers it (Program.cs calls <see cref="Configure"/>): that adds
/// per-key stampede protection (concurrent misses share one factory call) and
/// tag-based invalidation. Every entry carries the <c>"cfg"</c> tag, so
/// <see cref="InvalidateAll"/> is a single RemoveByTagAsync; narrower groups
/// (e.g. <c>"dcap:{apiKey}"</c>) bust selectively via <see cref="InvalidateTag"/>.
/// When no HybridCache was configured (unit tests without a host) the legacy
/// in-process MemoryCache path is used. LlmRouterDbContext busts the whole
/// cache whenever a non-telemetry entity is written.
/// </summary>
public sealed class HotCache
{
    /// <summary>Tag applied to every entry — InvalidateAll removes by it.</summary>
    public const string AllTag = "cfg";

    public static HotCache Default { get; } = new();

    private HybridCache? _hybrid;

    // Legacy fallback path (host-less usage).
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 20_000 });
    private readonly ConcurrentDictionary<string, byte> _keys = new();

    // Observability counters (tests + diagnostics). HybridCache doesn't expose
    // hit-vs-miss, so count total calls minus factory invocations.
    public long Hits => _calls - _misses;
    public long Misses => _misses;
    private long _calls, _misses;

    /// <summary>Wire the DI HybridCache in (called once from Program.cs).</summary>
    public static void Configure(HybridCache hybrid) => Default._hybrid = hybrid;

    public async Task<T> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory,
        string[]? tags = null)
    {
        Interlocked.Increment(ref _calls);
        if (_hybrid is { } h)
        {
            var allTags = tags is { Length: > 0 }
                ? tags.Append(AllTag).ToArray()
                : new[] { AllTag };
            try
            {
                return await h.GetOrCreateAsync(
                    key,
                    async ct =>
                    {
                        Interlocked.Increment(ref _misses);
                        return await factory();
                    },
                    new HybridCacheEntryOptions { Expiration = ttl, LocalCacheExpiration = ttl },
                    allTags);
            }
            catch (ObjectDisposedException)
            {
                // Configured instance disposed (e.g. a test host was torn down
                // while the static Default still references it) — fall back to
                // the in-process cache instead of failing requests.
                _hybrid = null;
            }
        }

        if (_cache.TryGetValue(key, out var cached) && cached is T hit)
            return hit;
        Interlocked.Increment(ref _misses);
        var value = await factory();
        _cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
        _keys.TryAdd(key, 0);
        return value;
    }

    public void InvalidateAll()
    {
        if (_hybrid is { } h)
        {
            try
            {
                // L1 removal completes synchronously; safe to wait here.
                h.RemoveByTagAsync(AllTag).GetAwaiter().GetResult();
                return;
            }
            catch (ObjectDisposedException) { _hybrid = null; }
        }
        foreach (var k in _keys.Keys) _cache.Remove(k);
        _keys.Clear();
    }

    /// <summary>Drop one cached entry (e.g. a quota verdict a usage write just
    /// made stale).</summary>
    public void Invalidate(string key)
    {
        if (_hybrid is { } h)
        {
            try
            {
                h.RemoveAsync(key).GetAwaiter().GetResult();
                return;
            }
            catch (ObjectDisposedException) { _hybrid = null; }
        }
        _cache.Remove(key);
        _keys.TryRemove(key, out _);
    }

    /// <summary>Drop every entry carrying this tag — e.g. all daily-cap
    /// verdicts of one key regardless of model suffix.</summary>
    public void InvalidateTag(string tag)
    {
        if (_hybrid is { } h)
        {
            try { h.RemoveByTagAsync(tag).GetAwaiter().GetResult(); }
            catch (ObjectDisposedException) { _hybrid = null; }
        }
        // Legacy path has no tags — callers that need group bust should also
        // hit InvalidateAll on the config-write path (already wired).
    }
}

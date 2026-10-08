using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// Egress proxy pools (SPEC-008, mirrors upstream proxy-pools + freeProxyProviders):
/// a pool is a ProxyPool row whose Data is {"name":..., "proxies":[{id,url,active,
/// failCount,latencyMs}]}; connections opt in via data.proxyPoolId. Proxies are
/// picked round-robin; a proxy marked inactive or failCount >= 3 is skipped.
/// </summary>
public static class ProxyPoolService
{
    private static readonly ConcurrentDictionary<string, int> Cursors = new();
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();
    private static readonly ConcurrentDictionary<string, int> FailCounts = new();

    public sealed record PoolProxy(string Id, string Url, bool Active, int FailCount, long? LatencyMs);

    public static string? PoolIdOf(ProviderConnection conn) =>
        Data(conn).TryGetProperty("proxyPoolId", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    public static JsonElement Data(ProviderConnection conn)
    {
        try { return JsonDocument.Parse(conn.Data).RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    public static List<PoolProxy> Proxies(ProxyPool pool)
    {
        try
        {
            var d = JsonDocument.Parse(pool.Data).RootElement;
            if (!d.TryGetProperty("proxies", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];
            return arr.EnumerateArray().Select(e => new PoolProxy(
                e.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                !e.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False,
                e.TryGetProperty("failCount", out var f) && f.TryGetInt32(out var n) ? n : 0,
                e.TryGetProperty("latencyMs", out var l) && l.TryGetInt64(out var lm) ? lm : null))
                .Where(x => x.Url != "").ToList();
        }
        catch { return []; }
    }

    /// <summary>Next usable proxy in the pool (round-robin), or null.</summary>
    public static PoolProxy? Pick(ProxyPool pool)
    {
        var usable = Proxies(pool).Where(x => x.Active && x.FailCount < 3).ToList();
        if (usable.Count == 0) return null;
        var i = (int)(Cursors.AddOrUpdate(pool.Id, 0, (_, v) => v + 1) % uint.MaxValue % usable.Count);
        return usable[i];
    }

    /// <summary>
    /// HttpClient routed through the pool's next proxy, or null when the
    /// connection has no pool or the pool is exhausted. Callers fall back to the
    /// shared "upstream" client.
    /// </summary>
    public static async Task<HttpClient?> ClientForAsync(LlmRouterDbContext db, ProviderConnection conn)
    {
        var poolId = PoolIdOf(conn);
        if (poolId is null) return null;
        var pool = await db.ProxyPools.FindAsync(poolId);
        if (pool is null || !pool.IsActive) return null;
        var proxy = Pick(pool);
        if (proxy is null) return null;
        return Clients.GetOrAdd(proxy.Url, url =>
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(url),
                UseProxy = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        });
    }

    /// <summary>Increment a proxy's failure count (>=3 disables it in the pool row).</summary>
    public static void ReportFailure(ProxyPool pool, string proxyId) =>
        FailCounts.AddOrUpdate($"{pool.Id}:{proxyId}", 1, (_, v) => v + 1);

    public static int FailureCount(ProxyPool pool, string proxyId) =>
        FailCounts.TryGetValue($"{pool.Id}:{proxyId}", out var n) ? n : 0;
}

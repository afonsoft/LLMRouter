using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-049: session pools — DB-backed pool of warm upstream sessions shared
/// across requests (upstream sessionPool.ts). Sessions are leased with a TTL
/// so a crashed request can't hold one forever; rate-limits put a session in
/// cooldown with backoff, repeated failures mark it dead.
/// </summary>
public static class SessionPoolOps
{
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private static string NowPlus(double seconds) =>
        DateTime.UtcNow.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Top the pool up to MinSize with fresh idle sessions.</summary>
    public static async Task EnsureMinAsync(LlmRouterDbContext db, SessionPoolRow pool)
    {
        var live = await db.PoolSessions.CountAsync(s => s.PoolId == pool.Id && s.Health != "dead");
        for (var i = live; i < pool.MinSize; i++)
            db.PoolSessions.Add(NewSession(pool));
        await db.SaveChangesAsync();
    }

    private static PoolSession NewSession(SessionPoolRow pool) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        PoolId = pool.Id,
        State = "idle",
        Health = "healthy",
        CreatedAt = Now(),
    };

    /// <summary>
    /// Lease the next usable session. Busy sessions whose lease expired are
    /// reclaimed first; if everything is taken and the pool is under MaxSize a
    /// new session is created. Returns null when the pool is exhausted.
    /// </summary>
    public static async Task<PoolSession?> AcquireAsync(LlmRouterDbContext db, SessionPoolRow pool)
    {
        var now = Now();
        var sessions = await db.PoolSessions.Where(s => s.PoolId == pool.Id).ToListAsync();

        // reclaim expired leases + expired cooldowns
        var dirty = false;
        foreach (var s in sessions)
        {
            if (s.State == "busy" && s.BusyUntil is not null
                && string.CompareOrdinal(s.BusyUntil, now) <= 0)
            { s.State = "idle"; s.BusyUntil = null; dirty = true; }
            if (s.State == "cooldown" && s.CooldownUntil is not null
                && string.CompareOrdinal(s.CooldownUntil, now) <= 0)
            { s.State = "idle"; s.CooldownUntil = null; dirty = true; }
        }

        var free = sessions.Where(s => s.State == "idle" && s.Health != "dead").ToList();
        PoolSession? picked;
        if (free.Count > 0)
        {
            picked = pool.Strategy == "least-used"
                ? free.OrderBy(s => s.TotalRequests).ThenBy(s => s.Id).First()
                // round-robin: least-recently-used first
                : free.OrderBy(s => s.LastUsedAt ?? "").ThenBy(s => s.Id).First();
        }
        else if (sessions.Count(s => s.Health != "dead") < pool.MaxSize)
        {
            picked = NewSession(pool);
            sessions.Add(picked);
            db.PoolSessions.Add(picked);
            dirty = true;
        }
        else return null;

        picked.State = "busy";
        picked.BusyUntil = NowPlus(pool.LeaseSeconds);
        picked.LastUsedAt = now;
        picked.TotalRequests++;
        await db.SaveChangesAsync();
        return picked;
    }

    /// <summary>
    /// Return a leased session. success → idle/healthy; rateLimited → cooldown
    /// with exponential backoff; other failure → cooldown, dead after 3
    /// consecutive fails.
    /// </summary>
    public static async Task ReleaseAsync(LlmRouterDbContext db, PoolSession s,
        bool success, bool rateLimited = false)
    {
        if (success)
        {
            s.State = "idle";
            s.Health = "healthy";
            s.ConsecutiveFails = 0;
            s.SuccessfulRequests++;
            s.BusyUntil = s.CooldownUntil = null;
        }
        else
        {
            s.ConsecutiveFails++;
            if (s.ConsecutiveFails >= 3)
            {
                s.State = "dead";
                s.Health = "dead";
                s.BusyUntil = s.CooldownUntil = null;
            }
            else
            {
                s.State = "cooldown";
                s.Health = "degraded";
                s.BusyUntil = null;
                var backoff = rateLimited
                    ? Math.Min(30 * Math.Pow(2, s.ConsecutiveFails - 1), 300)
                    : 15;
                s.CooldownUntil = NowPlus(backoff);
            }
        }
        s.LastUsedAt = Now();
        db.PoolSessions.Update(s);
        await db.SaveChangesAsync();
    }

    /// <summary>Reset every session to idle/healthy (spec: drain).</summary>
    public static async Task DrainAsync(LlmRouterDbContext db, SessionPoolRow pool)
    {
        foreach (var s in await db.PoolSessions.Where(s => s.PoolId == pool.Id).ToListAsync())
        {
            s.State = "idle";
            s.Health = "healthy";
            s.ConsecutiveFails = 0;
            s.BusyUntil = s.CooldownUntil = null;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Evict dead sessions and top back up to MinSize (spec: refresh).</summary>
    public static async Task RefreshAsync(LlmRouterDbContext db, SessionPoolRow pool)
    {
        db.PoolSessions.RemoveRange(
            db.PoolSessions.Where(s => s.PoolId == pool.Id && s.Health == "dead"));
        await db.SaveChangesAsync();
        await EnsureMinAsync(db, pool);
    }
}

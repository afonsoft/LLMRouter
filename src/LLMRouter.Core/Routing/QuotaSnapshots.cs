using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-117: provider-reported quota telemetry (upstream
/// src/lib/db/quotaSnapshots.ts). Snapshots land in the quotaSnapshots table —
/// deduped so only the first observation or a real change writes a row —
/// and feed GET /api/usage/utilization (bucketed AVG per provider/connection).
/// </summary>
public static class QuotaSnapshots
{
    /// <summary>Bucket sizes per utilization range (upstream BUCKET_SIZES).</summary>
    public static readonly IReadOnlyDictionary<string, int> BucketSizes =
        new Dictionary<string, int> { ["1h"] = 1, ["24h"] = 10, ["7d"] = 60, ["30d"] = 360 };

    public static readonly string[] ValidRanges = ["1h", "24h", "7d", "30d"];

    public sealed record Window(double? RemainingPct, bool? Exhausted,
        string? ResetAt = null, long? DurationMs = null);

    public sealed record Point(string Timestamp, string Provider,
        double RemainingPct, bool IsExhausted, string WindowKey);

    /// <summary>
    /// Persist one observation per window. Dedup (upstream #4438): skip when
    /// the latest row for the same provider+connection+window carries the same
    /// remainingPct/exhausted — idle connections don't churn rows.
    /// </summary>
    public static async Task<int> SaveAsync(LlmRouterDbContext db,
        string provider, string? connectionId,
        IReadOnlyDictionary<string, Window> windows)
    {
        var written = 0;
        foreach (var (key, w) in windows)
        {
            var last = await db.QuotaSnapshots
                .Where(s => s.Provider == provider && s.ConnectionId == connectionId
                    && s.WindowKey == key)
                .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
                .Select(s => new { s.RemainingPercentage, s.IsExhausted })
                .FirstOrDefaultAsync();
            var exhausted = (w.Exhausted ?? (w.RemainingPct <= 0)) ? 1 : 0;
            if (last is not null
                && last.RemainingPercentage == w.RemainingPct
                && last.IsExhausted == exhausted)
                continue;
            db.QuotaSnapshots.Add(new QuotaSnapshot
            {
                Id = 0,
                Provider = provider,
                ConnectionId = connectionId,
                WindowKey = key,
                RemainingPercentage = w.RemainingPct,
                IsExhausted = exhausted,
                NextResetAt = w.ResetAt,
                WindowDurationMs = w.DurationMs,
                CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            written++;
        }
        if (written > 0) await db.SaveChangesAsync();
        return written;
    }

    /// <summary>
    /// Bucketed series for the utilization chart. aggregateBy "connection"
    /// keys points as "provider:connectionId" (upstream contract).
    /// </summary>
    public static async Task<List<Point>> AggregatedAsync(LlmRouterDbContext db,
        string since, int bucketMinutes, string aggregateBy = "provider",
        string? providerFilter = null)
    {
        var q = db.QuotaSnapshots.Where(s => string.Compare(s.CreatedAt, since) >= 0);
        if (providerFilter is not null) q = q.Where(s => s.Provider == providerFilter);
        var rows = await q.OrderBy(s => s.CreatedAt)
            .Select(s => new { s.Provider, s.ConnectionId, s.WindowKey,
                s.RemainingPercentage, s.IsExhausted, s.CreatedAt })
            .ToListAsync();

        var bucketSec = bucketMinutes * 60L;
        string Bucket(string ts) =>
            DateTime.TryParse(ts, out var d)
                ? new DateTimeOffset(d - TimeSpan.FromTicks(d.Ticks % TimeSpan.FromSeconds(bucketSec).Ticks),
                    TimeSpan.Zero).ToString("yyyy-MM-dd HH:mm:ss")
                : ts;

        var byConn = aggregateBy == "connection";
        return rows
            .GroupBy(r => (
                bucket: Bucket(r.CreatedAt),
                provider: byConn ? $"{r.Provider}:{r.ConnectionId}" : r.Provider,
                r.WindowKey))
            .Select(g => new Point(
                g.Key.bucket, g.Key.provider,
                Math.Round(g.Average(r => r.RemainingPercentage ?? 0), 2),
                g.Any(r => r.IsExhausted == 1),
                g.Key.WindowKey))
            .OrderBy(p => p.Timestamp)
            .ToList();
    }

    /// <summary>Latest observation per (provider, connection, window).</summary>
    public static async Task<List<QuotaSnapshot>> LatestAsync(LlmRouterDbContext db)
    {
        var rows = await db.QuotaSnapshots
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .ToListAsync();
        return rows
            .GroupBy(s => (s.Provider, s.ConnectionId, s.WindowKey))
            .Select(g => g.First())
            .ToList();
    }
}

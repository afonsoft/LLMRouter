using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-036: /api/usage/* analytics endpoints backing the analytics/* pages.</summary>
public static class AnalyticsEndpoints
{
    private sealed record ComboHealthRow(string Combo, long Requests, double SuccessRate, long AvgLatencyMs, object? LastError, double Score);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static bool Ok(string? status) => status is "200" or "ok" or "success";
    private static string Since(int days) => DateTime.UtcNow.AddDays(-Math.Max(1, days)).ToString("yyyy-MM-dd HH:mm:ss");
    private static int Days(HttpContext ctx, int def = 7) =>
        int.TryParse(ctx.Request.Query["days"], out var d) ? Math.Clamp(d, 1, 365) : def;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/usage/history", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var q = db.UsageHistory.AsQueryable();
            var provider = ctx.Request.Query["provider"].ToString();
            var model = ctx.Request.Query["model"].ToString();
            var status = ctx.Request.Query["status"].ToString();
            if (!string.IsNullOrEmpty(provider)) q = q.Where(r => r.Provider == provider);
            if (!string.IsNullOrEmpty(model)) q = q.Where(r => r.Model == model);
            if (!string.IsNullOrEmpty(status)) q = q.Where(r => r.Status == status);
            var limit = int.TryParse(ctx.Request.Query["limit"], out var l) ? Math.Clamp(l, 1, 1000) : 200;
            var offset = int.TryParse(ctx.Request.Query["offset"], out var o) ? Math.Max(0, o) : 0;
            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(r => r.Timestamp).Skip(offset).Take(limit).ToListAsync();
            return Results.Json(new { rows, total, limit, offset }, JsonOpts);
        });

        g.MapGet("/usage/analytics", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var cutoff = Since(Days(ctx));
            var rows = await db.UsageHistory.Where(r => string.Compare(r.Timestamp, cutoff) >= 0).ToListAsync();
            var ok = rows.Count(r => Ok(r.Status));
            return Results.Json(new
            {
                requests = rows.Count,
                successRate = rows.Count > 0 ? Math.Round(ok * 10000.0 / rows.Count) / 100.0 : 0,
                promptTokens = rows.Sum(r => r.PromptTokens),
                completionTokens = rows.Sum(r => r.CompletionTokens),
                cost = Math.Round(rows.Sum(r => r.Cost), 6),
                avgLatencyMs = rows.Count > 0 ? (long)rows.Average(r => r.LatencyMs) : 0,
                topModels = rows.GroupBy(r => r.Model ?? "?").OrderByDescending(x => x.Count()).Take(10)
                    .Select(x => new { model = x.Key, requests = x.Count(), tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens), cost = Math.Round(x.Sum(r => r.Cost), 6) }),
                topProviders = rows.GroupBy(r => r.Provider ?? "?").OrderByDescending(x => x.Count()).Take(10)
                    .Select(x => new { provider = x.Key, requests = x.Count(), cost = Math.Round(x.Sum(r => r.Cost), 6) }),
                daily = rows.GroupBy(r => r.Timestamp[..10]).OrderBy(x => x.Key)
                    .Select(x => new { date = x.Key, requests = x.Count(), tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens), cost = Math.Round(x.Sum(r => r.Cost), 6), errors = x.Count(r => !Ok(r.Status)) }),
            }, JsonOpts);
        });

        g.MapGet("/usage/combo-health", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var cutoff = Since(Days(ctx));
            var rows = await db.UsageHistory.Where(r => string.Compare(r.Timestamp, cutoff) >= 0).ToListAsync();
            var comboNames = await db.Combos.Select(c => c.Name).ToListAsync();
            var comboSet = comboNames.ToHashSet();
            var health = rows
                .Where(r => r.Model is not null && comboSet.Contains(r.Model))
                .GroupBy(r => r.Model!)
                .Select(x =>
                {
                    var o = x.OrderByDescending(r => r.Timestamp).ToList();
                    var okCount = x.Count(r => Ok(r.Status));
                    var lastErr = o.FirstOrDefault(r => !Ok(r.Status));
                    var score = x.Count() == 0 ? 0
                        : Math.Round((okCount * 0.7 + Math.Max(0, 100 - x.Average(r => r.LatencyMs) / 100.0) * 0.3) , 2);
                    return new ComboHealthRow(
                        x.Key, x.Count(),
                        Math.Round(okCount * 10000.0 / x.Count()) / 100.0,
                        (long)x.Average(r => r.LatencyMs),
                        lastErr is null ? null : new Dictionary<string, object?> { ["at"] = lastErr.Timestamp, ["status"] = lastErr.Status, ["error"] = lastErr.Meta },
                        score);
                }).ToList();
            // combos with no traffic still show up
            foreach (var name in comboNames.Where(n => health.All(h => h.Combo != n)))
                health.Add(new ComboHealthRow(name, 0, 0, 0, null, 0));
            return Results.Json(new { combos = health.OrderByDescending(h => h.Score) }, JsonOpts);
        });

        g.MapGet("/usage/utilization", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var cutoff = Since(Days(ctx));
            var rows = await db.UsageHistory.Where(r => string.Compare(r.Timestamp, cutoff) >= 0).ToListAsync();
            return Results.Json(new
            {
                byProvider = rows.GroupBy(r => r.Provider ?? "?").OrderByDescending(x => x.Count())
                    .Select(x => new
                    {
                        provider = x.Key, requests = x.Count(),
                        tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens),
                        cost = Math.Round(x.Sum(r => r.Cost), 6),
                        errorRate = Math.Round(x.Count(r => !Ok(r.Status)) * 10000.0 / x.Count()) / 100.0,
                        avgLatencyMs = (long)x.Average(r => r.LatencyMs),
                    }),
                byConnection = rows.Where(r => r.ConnectionId is not null)
                    .GroupBy(r => r.ConnectionId!).OrderByDescending(x => x.Count())
                    .Select(x => new
                    {
                        connectionId = x.Key, requests = x.Count(),
                        tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens),
                        cost = Math.Round(x.Sum(r => r.Cost), 6),
                        errorRate = Math.Round(x.Count(r => !Ok(r.Status)) * 10000.0 / x.Count()) / 100.0,
                        providers = x.Select(r => r.Provider).Distinct().ToArray(),
                    }),
                byApiKey = rows.Where(r => r.ApiKey is not null)
                    .GroupBy(r => r.ApiKey!).OrderByDescending(x => x.Count())
                    .Select(x => new
                    {
                        apiKey = x.Key.Length > 12 ? x.Key[..8] + "…" : x.Key,
                        requests = x.Count(),
                        tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens),
                        cost = Math.Round(x.Sum(r => r.Cost), 6),
                    }),
            }, JsonOpts);
        });

        g.MapGet("/usage/model-latency", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var cutoff = Since(Days(ctx));
            var rows = await db.UsageHistory.Where(r => string.Compare(r.Timestamp, cutoff) >= 0 && r.LatencyMs > 0).ToListAsync();
            long Pct(List<long> s, double p) => s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)Math.Floor(s.Count * p))];
            return Results.Json(new
            {
                models = rows.GroupBy(r => r.Model ?? "?").OrderByDescending(x => x.Count())
                    .Select(x =>
                    {
                        var lat = x.Select(r => r.LatencyMs).OrderBy(v => v).ToList();
                        return new
                        {
                            model = x.Key, requests = x.Count(),
                            avgMs = (long)x.Average(r => r.LatencyMs),
                            p50Ms = Pct(lat, 0.5), p95Ms = Pct(lat, 0.95), maxMs = lat.LastOrDefault(),
                        };
                    }),
            }, JsonOpts);
        });

        g.MapGet("/usage/requests-by-provider-date", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var cutoff = Since(Days(ctx));
            var rows = await db.UsageHistory.Where(r => string.Compare(r.Timestamp, cutoff) >= 0).ToListAsync();
            return Results.Json(new
            {
                rows = rows.GroupBy(r => (date: r.Timestamp[..10], provider: r.Provider ?? "?"))
                    .OrderBy(x => x.Key.date).ThenBy(x => x.Key.provider)
                    .Select(x => new { date = x.Key.date, provider = x.Key.provider, requests = x.Count(), tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens), cost = Math.Round(x.Sum(r => r.Cost), 6) }),
            }, JsonOpts);
        });

        g.MapGet("/usage/key-quota", async (LlmRouterDbContext db) =>
        {
            var rows = await db.UsageHistory.Where(r => r.ApiKey != null).ToListAsync();
            var keys = await db.ApiKeys.ToListAsync();
            return Results.Json(new
            {
                keys = rows.GroupBy(r => r.ApiKey!).Select(x =>
                {
                    var k = keys.FirstOrDefault(kk => kk.Key == x.Key);
                    return new
                    {
                        apiKey = x.Key.Length > 12 ? x.Key[..8] + "…" : x.Key,
                        name = k?.Name,
                        requests = x.Count(),
                        tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens),
                        cost = Math.Round(x.Sum(r => r.Cost), 6),
                        lastUsed = x.Max(r => r.Timestamp),
                    };
                }).OrderByDescending(x => x.requests),
            }, JsonOpts);
        });
    }
}

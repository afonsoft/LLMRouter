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
            // SPEC-118: upstream contract — ?range=7d|30d|90d|180d|365d|all&presets=1d,7d,30d.
            var range = ctx.Request.Query["range"].ToString();
            if (range.Length > 0)
            {
                if (range is not ("7d" or "30d" or "90d" or "180d" or "365d" or "all"))
                    return Results.Json(new { error = $"invalid range '{range}'" }, JsonOpts, statusCode: 400);
                var since = range == "all" ? ""
                    : DateTime.UtcNow.AddDays(-int.Parse(range[..^1])).ToString("yyyy-MM-dd HH:mm:ss");
                var rrows = await db.UsageHistory
                    .Where(r => since.Length == 0 || string.Compare(r.Timestamp, since) >= 0)
                    .ToListAsync();

                static string TierOf(UsageRecord r)
                {
                    if (r.Meta is not null && r.Meta.Contains("serviceTier"))
                        try
                        {
                            var t = JsonDocument.Parse(r.Meta).RootElement
                                .TryGetProperty("serviceTier", out var st) ? st.GetString() : null;
                            if (t is "priority" or "flex") return t;
                        }
                        catch (JsonException) { }
                    return "standard";
                }
                static bool IsFallback(UsageRecord r) => r.Meta is not null && r.Meta.Contains("fallback");

                static List<Dictionary<string, object>> RowsBy(IEnumerable<IGrouping<string, UsageRecord>> gs, string field) =>
                    gs.Select(x => new Dictionary<string, object>
                    {
                        [field] = x.Key,
                        ["requests"] = x.Count(),
                        ["promptTokens"] = x.Sum(r => r.PromptTokens),
                        ["completionTokens"] = x.Sum(r => r.CompletionTokens),
                        ["totalTokens"] = x.Sum(r => r.PromptTokens + r.CompletionTokens),
                        ["cost"] = Math.Round(x.Sum(r => r.Cost), 6),
                    }).OrderByDescending(x => (double)x["cost"]).ToList();

                var byProvider = RowsBy(rrows.GroupBy(r => r.Provider ?? "?"), "provider");
                var byModel = RowsBy(rrows.GroupBy(r => r.Model ?? "?"), "model");
                var byApiKey = RowsBy(rrows.GroupBy(r => r.ApiKey ?? "?"), "apiKey");
                var byAccount = RowsBy(rrows.GroupBy(r => r.ConnectionId ?? "?"), "account");
                var byServiceTier = rrows.GroupBy(TierOf)
                    .Select(x => new
                    {
                        serviceTier = x.Key, label = x.Key,
                        requests = x.Count(),
                        promptTokens = x.Sum(r => r.PromptTokens),
                        completionTokens = x.Sum(r => r.CompletionTokens),
                        totalTokens = x.Sum(r => r.PromptTokens + r.CompletionTokens),
                        cost = Math.Round(x.Sum(r => r.Cost), 6),
                        savings = 0.0, usageSavingsTokens = 0,
                    }).OrderBy(t => t.serviceTier == "priority" ? 0 : t.serviceTier == "flex" ? 1 : 2).ToList();
                var dailyTrend = rrows.GroupBy(r => r.Timestamp[..10]).OrderBy(x => x.Key)
                    .Select(x => new
                    {
                        date = x.Key, requests = x.Count(),
                        tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens),
                        cost = Math.Round(x.Sum(r => r.Cost), 6),
                        errors = x.Count(r => !Ok(r.Status)),
                    }).ToList();

                var fallbackCount = rrows.Count(IsFallback);
                var streak = 0;
                var day = DateTime.UtcNow.Date;
                var daysWith = dailyTrend.Select(d => d.date).ToHashSet();
                if (!daysWith.Contains(day.ToString("yyyy-MM-dd"))) day = day.AddDays(-1);
                while (daysWith.Contains(day.ToString("yyyy-MM-dd"))) { streak++; day = day.AddDays(-1); }

                var presets = new Dictionary<string, object>();
                foreach (var p in ctx.Request.Query["presets"].ToString().Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (p is not ("1d" or "7d" or "30d" or "90d")) continue;
                    var pc = DateTime.UtcNow.AddDays(-int.Parse(p[..^1])).ToString("yyyy-MM-dd HH:mm:ss");
                    presets[p] = new { totalCost = Math.Round(rrows.Where(r => string.Compare(r.Timestamp, pc) >= 0).Sum(r => r.Cost), 6) };
                }

                return Results.Json(new
                {
                    summary = new
                    {
                        totalCost = Math.Round(rrows.Sum(r => r.Cost), 6),
                        totalRequests = rrows.Count,
                        uniqueModels = rrows.Select(r => r.Model).Where(m => m is not null).Distinct().Count(),
                        uniqueAccounts = rrows.Select(r => r.ConnectionId).Where(c => c is not null).Distinct().Count(),
                        uniqueApiKeys = rrows.Select(r => r.ApiKey).Where(k => k is not null).Distinct().Count(),
                        totalTokens = rrows.Sum(r => r.PromptTokens + r.CompletionTokens),
                        promptTokens = rrows.Sum(r => r.PromptTokens),
                        completionTokens = rrows.Sum(r => r.CompletionTokens),
                        fallbackCount,
                        fallbackRatePct = rrows.Count > 0 ? Math.Round(fallbackCount * 10000.0 / rrows.Count) / 100.0 : 0,
                        requestedModelCoveragePct = rrows.Count > 0
                            ? Math.Round((rrows.Count - fallbackCount) * 10000.0 / rrows.Count) / 100.0 : 0,
                        streak,
                    },
                    byProvider, byModel, byApiKey, byAccount, byServiceTier, dailyTrend,
                    presetSummaries = presets,
                    includesFlatRateEstimates = false,
                }, JsonOpts);
            }

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

            // ---- SPEC-117: Combo Health Autopilot (upstream
            // lib/usage/comboHealthDashboard → autopilot report) ----
            var combosFull = await db.Combos.ToListAsync();
            var quotaLatest = await Core.Routing.QuotaSnapshots.LatestAsync(db);
            var exhaustedProviders = quotaLatest
                .Where(s => s.IsExhausted == 1)
                .Select(s => s.Provider).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var breakerOpen = Core.Resilience.ProviderBreaker.Snapshot()
                .Where(i => i.State is "OPEN" or "HALFOPEN")
                .Select(i => i.ProviderId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var autopilotCombos = new List<object>();
            var issueCount = 0; var actionable = 0; var anyCritical = false;
            var down = 0; var degraded = 0; var healthy = 0;
            foreach (var c in combosFull)
            {
                var h = health.FirstOrDefault(x => x.Combo == c.Name)
                    ?? new ComboHealthRow(c.Name, 0, 0, 0, null, 0);
                var models = JsonDocument.Parse(c.Models).RootElement
                    .EnumerateArray().Select(m => m.GetString() ?? "").Where(s => s.Length > 0).ToList();
                var providers = models.Select(m => m.Split('/', 2)[0])
                    .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var issues = new List<object>();
                var severities = new List<string>();
                void Issue(string severity, string kind, string title, string rec,
                    string? provider, params (string type, string label, string href)[] actions)
                {
                    issueCount++;
                    severities.Add(severity);
                    if (severity != "info") actionable++;
                    if (severity == "critical") anyCritical = true;
                    issues.Add(new
                    {
                        id = $"{c.Id}:{kind}",
                        severity, kind, title,
                        recommendation = rec,
                        evidence = new { requests = h.Requests, successRate = h.SuccessRate, score = h.Score },
                        target = new { comboId = c.Id, comboName = c.Name, provider },
                        actions = actions.Select(a => new
                        {
                            type = a.type, mode = "manual", label = a.label,
                            href = a.href,
                            target = new { comboId = c.Id, comboName = c.Name, provider },
                        }).ToArray(),
                    });
                }

                if (models.Count == 0)
                    Issue("critical", "combo_no_targets", "Combo has no targets",
                        "Add at least one provider/model target to this combo.", null,
                        ("open_combo_editor", "Open combo editor", $"/dashboard/combos/{c.Id}"));
                else if (h.Requests == 0)
                    Issue("info", "combo_no_recent_traffic", "No recent traffic",
                        "The combo saw no requests in this window.", null,
                        ("run_combo_test", "Run cookbook", "/dashboard/combos"));
                if (h.Requests >= 10 && h.SuccessRate < 50)
                    Issue("critical", "combo_low_success_rate", "Combo success rate below target",
                        "Success rate under 50%. Review failing targets or reorder the combo.", null,
                        ("open_combo_editor", "Open combo editor", $"/dashboard/combos/{c.Id}"),
                        ("run_combo_test", "Run cookbook", "/dashboard/combos"));
                else if (h.Requests >= 10 && h.SuccessRate < 90)
                    Issue("warning", "combo_low_success_rate", "Combo success rate below target",
                        "Success rate under 90%. Check the latest errors on the request log.", null,
                        ("open_combo_editor", "Open combo editor", $"/dashboard/combos/{c.Id}"));
                if (h.LastError is not null)
                    Issue("info", "target_last_error", "Recent upstream error",
                        "Inspect the last failed request on the request log.", null,
                        ("open_combo_editor", "Open combo editor", $"/dashboard/combos/{c.Id}"));
                foreach (var p in providers)
                {
                    if (exhaustedProviders.Contains(p))
                        Issue("critical", "target_quota_exhausted", "Target quota exhausted",
                            $"Provider {p} reported 0% remaining quota.", p,
                            ("review_quota_limits", "Review quota limits", "/dashboard/providers"),
                            ("open_combo_editor", "Open combo editor", $"/dashboard/combos/{c.Id}"));
                    else if (await Core.Routing.QuotaWindows.ExceededAsync(db, p))
                        Issue("warning", "target_quota_exhausted", "Provider quota window saturated",
                            $"Configured quota window for {p} is saturated.", p,
                            ("review_quota_limits", "Review quota limits", "/dashboard/providers"));
                    if (breakerOpen.Contains(p))
                        Issue("warning", "provider_health_issue", "Provider health issue",
                            $"Circuit breaker for {p} is open; requests are being skipped.", p,
                            ("open_provider_health_autopilot", "Open health autopilot", "/dashboard/health"));
                }

                var state = h.Requests > 0 && h.Score < 40 ? "down"
                    : h.Score < 80 || severities.Any(s => s != "info") ? "degraded"
                    : "healthy";
                _ = state == "down" ? down++ : state == "degraded" ? degraded++ : healthy++;
                autopilotCombos.Add(new
                {
                    comboId = c.Id, comboName = c.Name,
                    strategy = c.Kind ?? "round-robin",
                    score = h.Score, state, issues,
                });
            }
            var status = down > 0 || anyCritical
                ? "critical"
                : degraded > 0 || actionable > 0 ? "warning" : "healthy";
            var autopilot = new
            {
                status,
                summary = new
                {
                    issueCount, suggestionCount = actionable,
                    actionableCount = actionable,
                    downCount = down, degradedCount = degraded, healthyCount = healthy,
                    totalCombos = combosFull.Count,
                },
                combos = autopilotCombos,
            };
            return Results.Json(new
            {
                combos = health.OrderByDescending(h => h.Score),
                autopilot,
            }, JsonOpts);
        });

        g.MapGet("/usage/utilization", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            // SPEC-117: upstream contract — range + aggregateBy serve bucketed
            // quotaSnapshots (provider utilization over time).
            if (ctx.Request.Query.TryGetValue("range", out var rangeParam)
                && rangeParam.Count > 0)
            {
                var range = rangeParam.ToString();
                if (!Core.Routing.QuotaSnapshots.ValidRanges.Contains(range))
                    return Results.Json(
                        new { error = "Invalid range. Must be one of: 1h, 24h, 7d, 30d" },
                        JsonOpts, statusCode: 400);
                var since = (range switch
                {
                    "1h" => DateTime.UtcNow.AddHours(-1),
                    "24h" => DateTime.UtcNow.AddDays(-1),
                    "7d" => DateTime.UtcNow.AddDays(-7),
                    _ => DateTime.UtcNow.AddDays(-30),
                }).ToString("yyyy-MM-dd HH:mm:ss");
                var aggregateBy = ctx.Request.Query["aggregateBy"].ToString() == "connection"
                    ? "connection" : "provider";
                var providerFilter = ctx.Request.Query["provider"].ToString();
                var bucketMinutes = Core.Routing.QuotaSnapshots.BucketSizes[range];
                var data = await Core.Routing.QuotaSnapshots.AggregatedAsync(db,
                    since, bucketMinutes, aggregateBy,
                    string.IsNullOrEmpty(providerFilter) ? null : providerFilter);
                var providers = data.Select(d => d.Provider).Distinct().ToArray();
                object? connectionMeta = null;
                if (aggregateBy == "connection")
                {
                    var ids = data.Select(d => d.Provider.Split(':', 2))
                        .Where(p => p.Length == 2).Select(p => p[1])
                        .Distinct().ToArray();
                    var conns = await db.ProviderConnections
                        .Where(c => ids.Contains(c.Id))
                        .Select(c => new { c.Id, c.Name, c.Email }).ToListAsync();
                    connectionMeta = conns.ToDictionary(c => c.Id,
                        c => new { email = c.Email, name = c.Name, displayName = c.Name ?? c.Email });
                }
                return Results.Json(new
                {
                    timeRange = range,
                    bucketSizeMinutes = bucketMinutes,
                    providers,
                    data,
                    connectionMeta,
                }, JsonOpts);
            }

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

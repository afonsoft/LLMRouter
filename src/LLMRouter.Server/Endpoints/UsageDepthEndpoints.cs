using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-090: usage/log depth — call-logs, request-logs, proxy-logs, by-run,
/// om-usage, reset-credit cards, plus /api/logs/{console,export} aliases.
/// </summary>
public static class UsageDepthEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/usage/call-logs", CallLogs);
        g.MapGet("/usage/call-logs/meta", CallLogsMeta);
        g.MapGet("/usage/call-logs/{id:long}", CallLogItem);
        g.MapGet("/usage/request-logs", RequestLogs);
        g.MapGet("/usage/proxy-logs", ProxyLogs);
        g.MapGet("/usage/by-run", ByRun);
        g.MapGet("/usage/om-usage", OmUsage);
        g.MapGet("/usage/codex-reset-credit", (HttpContext c, LlmRouterDbContext d) => ResetCard(c, d, "codex"));
        g.MapGet("/usage/glm-reset-card", (HttpContext c, LlmRouterDbContext d) => ResetCard(c, d, "glm"));

        // aliases for the upstream log paths (log stream + prune already exist).
        g.MapGet("/logs/console", ConsoleTail);
        g.MapGet("/logs/export", LogExport);
    }

    private static string? MetaProp(string? meta, string key)
    {
        if (meta is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Per-LLM-call rows with filters; {items,total} upstream shape.</summary>
    private static async Task CallLogs(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var q = ctx.Request.Query;
        var rows = await db.UsageHistory.OrderByDescending(u => u.Timestamp).Take(5000).ToListAsync(ct);
        // SPEC-116: multi-provider filter — accepts repeated providers[]/providers params and csv.
        var providers = q["providers"].Concat(q["providers[]"])
            .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filtered = rows.Where(u =>
            (providers.Count == 0 || (u.Provider is not null && providers.Contains(u.Provider)))
            && (q["provider"].FirstOrDefault() is not { } pv || u.Provider == pv)
            && (q["model"].FirstOrDefault() is not { } mv || u.Model == mv)
            && (q["status"].FirstOrDefault() is not { } sv || StatusMatch(u.Status, sv))
            && (q["key"].FirstOrDefault() is not { } kv || u.ApiKey == kv)
            && (q["conn"].FirstOrDefault() is not { } cn || u.ConnectionId == cn)
            && (q["type"].FirstOrDefault() is not { } ty || u.Endpoint == ty)
            && (q["correlationId"].FirstOrDefault() is not { } rid || (u.Meta?.Contains(rid, StringComparison.Ordinal) ?? false))
            && (q["from"].FirstOrDefault() is not { } f || string.Compare(u.Timestamp, f) >= 0)
            && (q["to"].FirstOrDefault() is not { } t || string.Compare(u.Timestamp, t) <= 0)
            && (q["q"].FirstOrDefault() is not { } s
                || (u.Model?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                || (u.Provider?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                || (u.ApiKey?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                || (u.Meta?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();
        var total = filtered.Count;
        var items = filtered
            .Skip(int.TryParse(q["offset"], out var off) ? off : 0)
            .Take(Math.Min(int.TryParse(q["limit"], out var lim) ? lim : 100, 1000))
            .Select(u => new
            {
                u.Id, at = u.Timestamp, u.Provider, u.Model, key = u.ApiKey, u.Status,
                type = u.Endpoint, u.ConnectionId, u.LatencyMs,
                inputTokens = u.PromptTokens, outputTokens = u.CompletionTokens, u.Cost,
            });
        await ctx.Response.WriteAsJsonAsync(new { items, total }, JsonOpts, ct);
    }

    /// <summary>SPEC-116: status param supports exact value or class prefixes (2xx/4xx/5xx/err).</summary>
    private static bool StatusMatch(string? status, string sv) => sv switch
    {
        "4xx" or "5xx" or "2xx" or "3xx" => status is not null && status.StartsWith(sv[0]),
        "err" or "error" => status is "err" or "error"
            || (status is not null && (status.StartsWith('4') || status.StartsWith('5'))),
        _ => status == sv,
    };

    /// <summary>/api/usage/call-logs/meta — distinct filter options over the recent window.</summary>
    private static async Task CallLogsMeta(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var rows = await db.UsageHistory.OrderByDescending(u => u.Timestamp).Take(5000)
            .Select(u => new { u.Provider, u.Model, u.ApiKey, u.Endpoint, u.ConnectionId }).ToListAsync(ct);
        await ctx.Response.WriteAsJsonAsync(new
        {
            providers = rows.Select(r => r.Provider).Where(v => v is not null).Distinct().OrderBy(v => v),
            models = rows.Select(r => r.Model).Where(v => v is not null).Distinct().OrderBy(v => v),
            keys = rows.Select(r => r.ApiKey).Where(v => v is not null).Distinct().OrderBy(v => v),
            types = rows.Select(r => r.Endpoint).Where(v => v is not null).Distinct().OrderBy(v => v),
            accounts = rows.Select(r => r.ConnectionId).Where(v => v is not null).Distinct().OrderBy(v => v),
        }, JsonOpts, ct);
    }

    /// <summary>Single call row + its requestDetails payload.</summary>
    private static async Task CallLogItem(HttpContext ctx, LlmRouterDbContext db, long id)
    {
        var ct = ctx.RequestAborted;
        var u = await db.UsageHistory.FindAsync([id], ct);
        if (u is null) { ctx.Response.StatusCode = 404; return; }
        var detail = await db.RequestDetails
            .Where(d => d.Provider == u.Provider && d.Model == u.Model && d.Timestamp == u.Timestamp)
            .FirstOrDefaultAsync(ct)
            ?? await db.RequestDetails
                .Where(d => d.Provider == u.Provider && d.Model == u.Model)
                .OrderByDescending(d => d.Timestamp).FirstOrDefaultAsync(ct);
        await ctx.Response.WriteAsJsonAsync(new { call = u, detail = detail?.Data }, JsonOpts, ct);
    }

    /// <summary>Per-incoming-request view: groups usageHistory by Meta.requestId.</summary>
    private static async Task RequestLogs(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var rows = await db.UsageHistory.OrderByDescending(u => u.Timestamp).Take(5000).ToListAsync(ct);
        var items = rows
            .GroupBy(u => MetaProp(u.Meta, "requestId") ?? $"call-{u.Id}")
            .Select(gx =>
            {
                var first = gx.OrderByDescending(x => x.Timestamp).First();
                return new
                {
                    requestId = gx.Key,
                    path = MetaProp(first.Meta, "path") ?? first.Endpoint,
                    method = MetaProp(first.Meta, "method") ?? "POST",
                    status = first.Status,
                    duration = gx.Sum(x => x.LatencyMs),
                    calls = gx.Count(),
                    at = first.Timestamp,
                };
            });
        await ctx.Response.WriteAsJsonAsync(new { items, total = items.Count() }, JsonOpts, ct);
    }

    /// <summary>Egress proxy attribution: Meta.proxyId groups.</summary>
    private static async Task ProxyLogs(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var rows = await db.UsageHistory
            .Where(u => u.Meta != null)
            .OrderByDescending(u => u.Timestamp).Take(5000).ToListAsync(ct);
        var items = rows
            .Select(u => new { u, proxyId = MetaProp(u.Meta, "proxyId") })
            .Where(x => x.proxyId is not null)
            .Select(x => new
            {
                x.u.Id, x.proxyId, x.u.Provider, x.u.Model, x.u.Status, x.u.LatencyMs, at = x.u.Timestamp,
            })
            .ToList();
        var byProxy = items.GroupBy(i => i.proxyId).Select(gx => new
        {
            proxyId = gx.Key,
            calls = gx.Count(),
            avgLatencyMs = gx.Average(x => x.LatencyMs),
            failures = gx.Count(x => x.Status != "ok" && x.Status != "success"),
        });
        await ctx.Response.WriteAsJsonAsync(new { items, byProxy, total = items.Count }, JsonOpts, ct);
    }

    /// <summary>Usage rollup per run/session id (Meta.runId).</summary>
    private static async Task ByRun(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var runFilter = ctx.Request.Query["runId"].FirstOrDefault();
        var rows = await db.UsageHistory.Where(u => u.Meta != null)
            .OrderByDescending(u => u.Timestamp).Take(10000).ToListAsync(ct);
        var items = rows
            .Select(u => new { u, runId = MetaProp(u.Meta, "runId") })
            .Where(x => x.runId is not null && (runFilter is null || x.runId == runFilter))
            .GroupBy(x => x.runId!)
            .Select(gx => new
            {
                runId = gx.Key,
                calls = gx.Count(),
                inputTokens = gx.Sum(x => x.u.PromptTokens),
                outputTokens = gx.Sum(x => x.u.CompletionTokens),
                cost = gx.Sum(x => x.u.Cost),
                lastAt = gx.Max(x => x.u.Timestamp),
            });
        await ctx.Response.WriteAsJsonAsync(new { items, total = items.Count() }, JsonOpts, ct);
    }

    /// <summary>OM counters: totals per endpoint family.</summary>
    private static async Task OmUsage(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var rows = await db.UsageHistory.ToListAsync(ct);
        static string Family(string? e) => e switch
        {
            null => "mgmt",
            var x when x.Contains("chat") || x.Contains("messages") || x.Contains("responses") => "chat",
            var x when x.Contains("embed") => "embeddings",
            var x when x.Contains("image") || x.Contains("audio") || x.Contains("video")
                || x.Contains("music") || x.Contains("ocr") => "media",
            _ => "mgmt",
        };
        var totals = rows.GroupBy(u => Family(u.Endpoint)).Select(gx => new
        {
            family = gx.Key,
            calls = gx.Count(),
            inputTokens = gx.Sum(x => x.PromptTokens),
            outputTokens = gx.Sum(x => x.CompletionTokens),
            cost = gx.Sum(x => x.Cost),
        });
        await ctx.Response.WriteAsJsonAsync(new { totals }, JsonOpts, ct);
    }

    /// <summary>Remaining-credit card for provider families using quota windows.</summary>
    private static async Task ResetCard(HttpContext ctx, LlmRouterDbContext db, string familyPrefix)
    {
        var ct = ctx.RequestAborted;
        var conns = await db.ProviderConnections
            .Where(c => c.IsActive && c.Provider.StartsWith(familyPrefix)).ToListAsync(ct);
        if (conns.Count == 0) { await ctx.Response.WriteAsJsonAsync(new { family = familyPrefix, cards = Array.Empty<object>() }, JsonOpts, ct); return; }
        var windowStart = DateTime.UtcNow.AddHours(-5).ToString("yyyy-MM-dd HH:mm:ss");
        var rows = await db.UsageHistory
            .Where(u => conns.Select(c => c.Provider).Contains(u.Provider!) && string.Compare(u.Timestamp, windowStart) >= 0)
            .ToListAsync(ct);
        var cards = conns.Select(c =>
        {
            var used = rows.Where(u => u.Provider == c.Provider).Sum(u => u.PromptTokens + u.CompletionTokens);
            return new
            {
                provider = c.Provider,
                connectionId = c.Id,
                usedTokens = used,
                cap = (long?)null,
                windowPercent = 0.0,
                resetsAt = DateTime.UtcNow.AddHours(5).ToString("o"),
            };
        });
        await ctx.Response.WriteAsJsonAsync(new { family = familyPrefix, cards }, JsonOpts, ct);
    }

    /// <summary>/api/logs/console — ring buffer tail {items:[{ts,level,message}]}.</summary>
    private static async Task ConsoleTail(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var tail = int.TryParse(q["tail"], out var t) ? t : int.TryParse(q["limit"], out var l) ? l : 200;
        var level = q["level"].FirstOrDefault();
        var items = Services.ConsoleLogBuffer.Instance.Recent(Math.Min(tail, 500))
            .Select(line =>
            {
                var lvl = line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("error", StringComparison.OrdinalIgnoreCase) ? "error"
                    : line.Contains("warn", StringComparison.OrdinalIgnoreCase) ? "warning" : "info";
                return new { ts = (string?)null, level = lvl, message = line };
            })
            .Where(x => level is null || x.level == level);
        await ctx.Response.WriteAsJsonAsync(new { items }, JsonOpts, ctx.RequestAborted);
    }

    /// <summary>/api/logs/export — filtered requestDetails as .jsonl (or .csv) download.</summary>
    private static async Task LogExport(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        var q = ctx.Request.Query;
        var rows = await db.RequestDetails.OrderByDescending(x => x.Timestamp)
            .Take(Math.Min(int.TryParse(q["limit"], out var lim) ? lim : 1000, 10000)).ToListAsync(ct);
        var filtered = rows.Where(r =>
            (q["from"].FirstOrDefault() is not { } f || string.Compare(r.Timestamp, f) >= 0)
            && (q["to"].FirstOrDefault() is not { } t || string.Compare(r.Timestamp, t) <= 0)
            && (q["level"].FirstOrDefault() is not { } lv || r.Status == lv))
            .ToList();
        var fmt = q["format"].FirstOrDefault() ?? "jsonl";
        var sb = new StringBuilder();
        if (fmt == "csv")
        {
            sb.AppendLine("id,timestamp,provider,model,status");
            foreach (var r in filtered)
                sb.AppendLine($"{r.Id},{r.Timestamp},{r.Provider},{r.Model},{r.Status}");
        }
        else
        {
            foreach (var r in filtered)
                sb.AppendLine(JsonSerializer.Serialize(
                    new { r.Id, r.Timestamp, r.Provider, r.Model, r.Status, r.Data }, JsonOpts));
        }
        ctx.Response.ContentType = fmt == "csv" ? "text/csv" : "application/x-ndjson";
        ctx.Response.Headers.ContentDisposition =
            $"attachment; filename=llmrouter-logs-{DateTime.UtcNow:yyyyMMddHHmmss}.{fmt}";
        await ctx.Response.WriteAsync(sb.ToString(), ct);
    }
}

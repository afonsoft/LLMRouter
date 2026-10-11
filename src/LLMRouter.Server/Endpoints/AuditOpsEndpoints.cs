using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-057: superfície de auditoria — eventos estruturados (auditEvents),
/// trilha de tool calls MCP (mcpToolCalls) e export de compliance json/csv.
/// </summary>
public static class AuditOpsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia os endpoints de auditoria/compliance.</summary>
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api");

        // ---- eventos estruturados com filtros ----
        g.MapGet("/audit/events", async (LlmRouterDbContext db,
            string? actor, string? action, string? target, string? category,
            string? before, string? after, int? limit) =>
        {
            var q = db.AuditEvents.AsQueryable();
            if (!string.IsNullOrEmpty(actor)) q = q.Where(e => e.Actor == actor);
            if (!string.IsNullOrEmpty(action)) q = q.Where(e => e.Action == action);
            if (!string.IsNullOrEmpty(target)) q = q.Where(e => e.Target != null && e.Target.Contains(target));
            if (!string.IsNullOrEmpty(category) && category != "all")
            {
                if (category == "system")
                    q = q.Where(e => !AllPrefixes.Any(p => e.Action.StartsWith(p)));
                else if (PrefixesOf(category) is { Length: > 0 } prefixes)
                    q = q.Where(e => prefixes.Any(p => e.Action.StartsWith(p)));
                else
                    return Results.Json(new { events = Array.Empty<object>() }, JsonOpts);
            }
            if (!string.IsNullOrEmpty(before)) q = q.Where(e => e.At.CompareTo(before) < 0);
            if (!string.IsNullOrEmpty(after)) q = q.Where(e => e.At.CompareTo(after) > 0);
            var rows = await q.OrderByDescending(e => e.At).Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync();
            return Results.Json(new
            {
                events = rows.Select(e => new
                {
                    e.Id, e.Actor, e.Action, e.Target, e.Meta, e.Ip, e.At,
                    category = CategoryOf(e.Action),
                }),
            }, JsonOpts);
        });

        // ---- MCP tool calls ----
        g.MapGet("/mcp/audit", async (LlmRouterDbContext db, string? tool, int? limit) =>
        {
            var q = db.McpToolCalls.AsQueryable();
            if (!string.IsNullOrEmpty(tool)) q = q.Where(c => c.Tool == tool);
            var rows = await q.OrderByDescending(c => c.At).Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync();
            return Results.Json(new { calls = rows }, JsonOpts);
        });
        g.MapGet("/mcp/audit/stats", async (LlmRouterDbContext db) =>
        {
            var rows = await db.McpToolCalls.ToListAsync();
            return Results.Json(new
            {
                total = rows.Count,
                failed = rows.Count(r => !r.Ok),
                byTool = rows.GroupBy(r => r.Tool).Select(x => new
                {
                    tool = x.Key, calls = x.Count(),
                    failures = x.Count(r => !r.Ok),
                    avgDurationMs = x.Average(r => r.DurationMs),
                }).OrderByDescending(x => x.calls),
            }, JsonOpts);
        });

        // ---- compliance export: filtered json/csv ----
        g.MapGet("/compliance/audit-log", async (HttpContext ctx, LlmRouterDbContext db,
            string? actor, string? action, string? from, string? to, string? format) =>
        {
            var q = db.AuditEvents.AsQueryable();
            if (!string.IsNullOrEmpty(actor)) q = q.Where(e => e.Actor == actor);
            if (!string.IsNullOrEmpty(action)) q = q.Where(e => e.Action == action);
            if (!string.IsNullOrEmpty(from)) q = q.Where(e => e.At.CompareTo(from) >= 0);
            if (!string.IsNullOrEmpty(to)) q = q.Where(e => e.At.CompareTo(to) <= 0);
            var rows = await q.OrderBy(e => e.At).ToListAsync();

            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder("at,actor,action,target,ip,meta\n");
                foreach (var e in rows)
                    sb.AppendLine(string.Join(',',
                        Csv(e.At), Csv(e.Actor), Csv(e.Action), Csv(e.Target), Csv(e.Ip), Csv(e.Meta)));
                ctx.Response.ContentType = "text/csv; charset=utf-8";
                ctx.Response.Headers.ContentDisposition = "attachment; filename=audit-log.csv";
                await ctx.Response.WriteAsync(sb.ToString());
                return Results.Empty;
            }
            return Results.Json(new { count = rows.Count, events = rows }, JsonOpts);
        });
    }

    /// <summary>SPEC-113: categoria de um evento a partir do prefixo da ação (domínio.verbo).</summary>
    private static readonly (string Name, string[] Prefixes)[] Categories =
    [
        ("providers", ["provider.", "providers."]),
        ("combos", ["combo.", "combos.", "fusion.", "pipeline."]),
        ("apikeys", ["apikey.", "apikeys.", "key.", "userkey.", "keyquota."]),
        ("settings", ["settings.", "config.", "featureflag.", "sidebar.", "webhooks."]),
        ("quota", ["quota.", "rate.", "limit.", "usage."]),
        ("auth", ["auth.", "user.", "users.", "oauth.", "login.", "token."]),
    ];

    private static readonly string[] AllPrefixes = Categories.SelectMany(c => c.Prefixes).ToArray();

    private static string[] PrefixesOf(string category) =>
        Categories.FirstOrDefault(c => c.Name == category).Prefixes ?? [];

    internal static string CategoryOf(string action)
    {
        foreach (var c in Categories)
            foreach (var p in c.Prefixes)
                if (action.StartsWith(p, StringComparison.Ordinal))
                    return c.Name;
        return "system";
    }

    private static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }
}

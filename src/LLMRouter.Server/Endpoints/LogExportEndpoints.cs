using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Logging;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-042: log-export destinations CRUD + run/test/status/types.</summary>
public static class LogExportEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private static readonly string[] Types = ["webhook", "file", "s3-compatible"];

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/log-export/types", () =>
            Results.Json(new
            {
                types = Types.Select(t => new
                {
                    id = t,
                    config = t == "webhook" ? new[] { "url", "fmt", "headers", "scheduleMinutes" }
                        : t == "s3-compatible" ? new[] { "url", "fmt", "headers", "scheduleMinutes" }
                        : new[] { "path", "fmt", "scheduleMinutes" },
                }),
                formats = new[] { "jsonl", "csv" },
            }, JsonOpts));

        g.MapGet("/log-export/destinations", async (LlmRouterDbContext db) =>
            Results.Json(new { destinations = await db.LogExportDestinations.ToListAsync() }, JsonOpts));

        g.MapGet("/log-export/status", async (LlmRouterDbContext db) =>
            Results.Json(new
            {
                status = (await db.LogExportDestinations.ToListAsync()).Select(d => new
                {
                    d.Id, d.Name, d.Type, d.Enabled, d.LastRunAt, d.LastRunStatus, d.LastRunDetail,
                }),
            }, JsonOpts));

        g.MapPost("/log-export/destinations", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var type = b.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (!Types.Contains(type))
                return Results.Json(new { error = $"type must be one of {string.Join(',', Types)}" }, JsonOpts, statusCode: 400);
            var d = new LogExportDestination
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "dest" : "dest",
                Type = type,
                Config = b.TryGetProperty("config", out var c) ? c.GetRawText() : "{}",
                Filters = b.TryGetProperty("filters", out var f) ? f.GetRawText() : "{}",
                Enabled = !b.TryGetProperty("enabled", out var e) || e.GetBoolean(),
                CreatedAt = Now(), UpdatedAt = Now(),
            };
            db.LogExportDestinations.Add(d);
            await db.SaveChangesAsync();
            return Results.Json(new { destination = d }, JsonOpts);
        });

        g.MapPut("/log-export/destinations/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var d = await db.LogExportDestinations.FindAsync(id);
            if (d is null) return NotFound("destination");
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out var n)) d.Name = n.GetString() ?? d.Name;
            if (b.TryGetProperty("type", out var t))
            {
                var tt = t.GetString() ?? "";
                if (!Types.Contains(tt)) return Results.Json(new { error = "bad type" }, JsonOpts, statusCode: 400);
                d.Type = tt;
            }
            if (b.TryGetProperty("config", out var c)) d.Config = c.GetRawText();
            if (b.TryGetProperty("filters", out var f)) d.Filters = f.GetRawText();
            if (b.TryGetProperty("enabled", out var e)) d.Enabled = e.GetBoolean();
            d.UpdatedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(new { destination = d }, JsonOpts);
        });

        g.MapDelete("/log-export/destinations/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var d = await db.LogExportDestinations.FindAsync(id);
            if (d is null) return NotFound("destination");
            db.LogExportDestinations.Remove(d);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        // real run: select + deliver + persist status
        g.MapPost("/log-export/destinations/{id}/run", async (string id, LlmRouterDbContext db, IHttpClientFactory hf) =>
        {
            var d = await db.LogExportDestinations.FindAsync(id);
            if (d is null) return NotFound("destination");
            var detail = await LogExporter.RunAsync(db, d, hf);
            return Results.Json(new { status = d.LastRunStatus, detail }, JsonOpts);
        });

        // test: format only, no delivery — shows what would be sent
        g.MapPost("/log-export/destinations/{id}/test", async (string id, LlmRouterDbContext db) =>
        {
            var d = await db.LogExportDestinations.FindAsync(id);
            if (d is null) return NotFound("destination");
            var filters = JsonDocument.Parse(d.Filters).RootElement;
            var cfg = JsonDocument.Parse(d.Config).RootElement;
            var fmt = cfg.TryGetProperty("fmt", out var f) && f.GetString() is { Length: > 0 } x ? x : "jsonl";
            var rows = await LogExporter.ApplyFilters(db.RequestDetails, filters)
                .OrderByDescending(r => r.Timestamp).Take(5).ToListAsync();
            return Results.Json(new
            {
                ok = true, type = d.Type, fmt,
                matched = await LogExporter.ApplyFilters(db.RequestDetails, filters).CountAsync(),
                sample = LogExporter.FormatRows(rows, fmt),
            }, JsonOpts);
        });
    }

    private static IResult NotFound(string what) =>
        Results.Json(new { error = $"{what} not found" }, JsonOpts, statusCode: 404);
}

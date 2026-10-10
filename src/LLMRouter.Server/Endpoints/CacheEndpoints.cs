using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-045: prompt-cache introspection + maintenance.</summary>
public static class CacheEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/cache/entries", async (HttpRequest req, LlmRouterDbContext db) =>
        {
            var skip = int.TryParse(req.Query["skip"], out var s) ? Math.Max(0, s) : 0;
            var take = int.TryParse(req.Query["take"], out var t) ? Math.Clamp(t, 1, 200) : 50;
            var provider = req.Query["provider"].FirstOrDefault();
            var model = req.Query["model"].FirstOrDefault();
            var q = db.CacheEntries.AsQueryable();
            if (!string.IsNullOrEmpty(provider)) q = q.Where(c => c.Provider == provider);
            if (!string.IsNullOrEmpty(model)) q = q.Where(c => c.Model == model);
            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(c => c.CreatedAt).Skip(skip).Take(take)
                .Select(c => new { c.Id, c.Provider, c.Model, c.Reasoning, c.TokensSaved, c.Hits, c.CreatedAt, c.ExpiresAt })
                .ToListAsync();
            return Results.Json(new { entries = rows, total }, JsonOpts);
        });

        g.MapGet("/cache/stats", async (LlmRouterDbContext db) =>
        {
            var entries = await db.CacheEntries.ToListAsync();
            var hits = entries.Sum(e => e.Hits);
            var calls = await db.RequestDetails.CountAsync();
            return Results.Json(new
            {
                size = entries.Count,
                hits,
                tokensSaved = entries.Sum(e => (long)e.TokensSaved * e.Hits),
                hitRate = calls + hits > 0 ? Math.Round((double)hits / (calls + hits), 3) : 0,
            }, JsonOpts);
        });

        g.MapDelete("/cache/entries/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var e = await db.CacheEntries.FindAsync(id);
            if (e is null) return Results.Json(new { error = "entry not found" }, JsonOpts, statusCode: 404);
            db.CacheEntries.Remove(e);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        g.MapPost("/cache/purge", async (LlmRouterDbContext db) =>
        {
            var n = await db.CacheEntries.CountAsync();
            db.CacheEntries.RemoveRange(await db.CacheEntries.ToListAsync());
            await db.SaveChangesAsync();
            return Results.Json(new { purged = n }, JsonOpts);
        });

        g.MapGet("/cache/reasoning", async (LlmRouterDbContext db) =>
            Results.Json(new
            {
                entries = await db.CacheEntries.Where(c => c.Reasoning == 1)
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new { c.Id, c.Provider, c.Model, c.Hits, c.CreatedAt })
                    .ToListAsync(),
            }, JsonOpts));
    }
}

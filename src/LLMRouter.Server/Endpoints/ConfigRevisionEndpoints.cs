using System.Text.Json;
using LLMRouter.Core.Config;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

public static class ConfigRevisionEndpoints
{
    public static void MapConfigRevisionEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/config").RequireAuthorization();
        g.MapGet("/revisions", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var area = ctx.Request.Query["area"].FirstOrDefault();
            return Results.Json(await ConfigRevisionStore.ListAsync(db, area, ctx.RequestAborted));
        });
        g.MapGet("/diff", async (long id, LlmRouterDbContext db) =>
        {
            var row = await db.ConfigRevisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return row is null ? Results.NotFound() : Results.Json(ConfigRevisionStore.Diff(row));
        });
        g.MapPost("/rollback", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (!req.TryGetProperty("id", out var id) || !id.TryGetInt64(out var revisionId))
                return Results.BadRequest(new { error = "id_required" });
            var row = await db.ConfigRevisions.FirstOrDefaultAsync(
                x => x.Id == revisionId, ctx.RequestAborted);
            if (row is null) return Results.NotFound();
            var settings = await Core.Extras.Extras.GetOrCreateSettingsAsync(db);
            var current = settings.Data;
            settings.Data = row.Prev;
            await ConfigRevisionStore.RecordAsync(
                db, row.Area, ctx.User.Identity?.Name ?? "admin", row.Prev, current,
                ctx.RequestAborted);
            return Results.Json(new { success = true, revisionId });
        });
    }
}

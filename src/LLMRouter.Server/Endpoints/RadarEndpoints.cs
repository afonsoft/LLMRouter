using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Radar;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-055: /api/radar — feeds, sync, sources, combos sugeridos.</summary>
public static class RadarEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Mapeia os endpoints do radar.</summary>
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/radar");

        foreach (var kind in RadarStore.Kinds)
            g.MapGet("/" + kind + "s", async (LlmRouterDbContext db) =>
            {
                var rows = await db.RadarItems.Where(x => x.Kind == kind)
                    .OrderByDescending(x => x.Id).Take(500).ToListAsync();
                return Results.Json(new { items = rows }, JsonOpts);
            });

        g.MapGet("/status", async (LlmRouterDbContext db) =>
            Results.Json(await RadarStore.StatusAsync(db), JsonOpts));

        g.MapPost("/sync", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var kind = b.TryGetProperty("kind", out var k) ? k.GetString() : null;
            if (kind is null || !RadarStore.Kinds.Contains(kind))
                return Results.Json(new { error = "kind required: " + string.Join("/", RadarStore.Kinds) }, JsonOpts, statusCode: 400);
            var sources = await RadarStore.SourcesAsync(db);
            var (added, error) = await RadarStore.SyncKindAsync(db, Http, kind,
                sources.TryGetValue(kind, out var u) ? u : []);
            return Results.Json(new { kind, added, error }, JsonOpts);
        });

        g.MapPost("/sync-all", async (LlmRouterDbContext db) =>
        {
            var sources = await RadarStore.SourcesAsync(db);
            var res = new Dictionary<string, object?>();
            foreach (var k in RadarStore.Kinds)
            {
                var (added, error) = await RadarStore.SyncKindAsync(db, Http, k,
                    sources.TryGetValue(k, out var u) ? u : []);
                res[k] = new { added, error };
            }
            return Results.Json(new { results = res }, JsonOpts);
        });

        g.MapGet("/settings", async (LlmRouterDbContext db) =>
            Results.Json(new { sources = await RadarStore.SourcesAsync(db) }, JsonOpts));
        g.MapPut("/settings", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<Dictionary<string, string[]>>(ctx.Request.Body) ?? [];
            await RadarStore.SaveSourcesAsync(db, b);
            return Results.Json(new { ok = true }, JsonOpts);
        });

        g.MapGet("/combos", async (LlmRouterDbContext db) =>
            Results.Json(new { suggestions = await RadarStore.SuggestCombosAsync(db) }, JsonOpts));
    }
}

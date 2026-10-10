using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

// SPEC-072: virtual auto/* combos — list, resolve pool, duplicate to static.
public static class AutoCombosEndpoints
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static string Now() => DateTime.UtcNow.ToString("o");

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // Known virtual combos + arbitrary auto/{name} filters.
        g.MapGet("/combos/auto", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry) =>
        {
            var pool = await AutoCombos.CandidatePoolAsync(db, registry, ctx.RequestAborted);
            var known = new List<object>();
            foreach (var vc in AutoCombos.All)
            {
                var cands = await AutoCombos.ResolveCandidatesAsync(db, registry, vc.Id, ctx.RequestAborted);
                known.Add(new { id = vc.Id, description = vc.Description, candidates = cands, count = cands.Count });
            }
            return Results.Json(new { combos = known, poolSize = pool.Count }, JsonOpts);
        });

        // Resolve any auto/* name into its candidate pool (incl. arbitrary filters).
        g.MapGet("/combos/auto/pool", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry) =>
        {
            var name = ctx.Request.Query["name"].FirstOrDefault() ?? "";
            if (!AutoCombos.IsAuto(name))
                return Results.Json(new { error = "name must start with auto/ or auto-" }, JsonOpts, statusCode: 400);
            var cands = await AutoCombos.ResolveCandidatesAsync(db, registry, name, ctx.RequestAborted);
            return Results.Json(new { name, candidates = cands, count = cands.Count }, JsonOpts);
        });

        // Materialize a virtual auto/* combo into a persisted static combo.
        g.MapPost("/combos/duplicate", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var autoId = b.TryGetProperty("autoId", out var av) ? av.GetString() ?? "" : "";
            var name = b.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "";
            if (!AutoCombos.IsAuto(autoId))
                return Results.Json(new { error = "autoId must start with auto/" }, JsonOpts, statusCode: 400);
            if (name.Length == 0)
                return Results.Json(new { error = "name required" }, JsonOpts, statusCode: 400);
            if (await db.Combos.AnyAsync(c => c.Name == name))
                return Results.Json(new { error = "combo name already exists" }, JsonOpts, statusCode: 409);
            var cands = await AutoCombos.ResolveCandidatesAsync(db, registry, autoId, ctx.RequestAborted);
            var c = new Combo
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = name,
                Kind = b.TryGetProperty("kind", out var kv) ? kv.GetString() ?? "fallback" : "fallback",
                Models = JsonSerializer.Serialize(cands),
                CreatedAt = Now(), UpdatedAt = Now(),
            };
            db.Combos.Add(c);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "combo.duplicate", $"{autoId} → {name}");
            return Results.Json(new { combo = c }, JsonOpts);
        });

        // Builder presets for the UI.
        g.MapGet("/combos/builder/options", () => Results.Json(new
        {
            virtualCombos = AutoCombos.All.Select(v => new { v.Id, v.Description }),
            strategies = new[] { "fallback", "priority", "least-used", "round-robin" },
            filters = new[] { "best", "coding", "fast", "free", "<substring>" },
        }, JsonOpts));
    }
}

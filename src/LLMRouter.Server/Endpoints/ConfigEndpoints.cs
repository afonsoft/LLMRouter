using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Config;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-104: config-as-code — export/validate/diff/import of the
/// routing config surface as one versionable `llmr-config` JSON document.</summary>
public static class ConfigEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/config/export", async (string? scope, LlmRouterDbContext db, HttpContext ctx) =>
            Results.Json(await ConfigAsCode.ExportAsync(db, scope ?? "all", ctx.RequestAborted), JsonOpts));

        g.MapPost("/config/validate", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry) =>
        {
            var doc = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            if (doc is null) return Results.Json(new { error = "document required" }, JsonOpts, statusCode: 400);
            var errors = await ConfigAsCode.ValidateAsync(doc, db, registry, ctx.RequestAborted);
            return Results.Json(new { ok = errors.Count == 0, errors }, JsonOpts);
        });

        g.MapPost("/config/diff", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var doc = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            if (doc is null) return Results.Json(new { error = "document required" }, JsonOpts, statusCode: 400);
            var diff = await ConfigAsCode.DiffAsync(doc, db, ctx.RequestAborted);
            return Results.Json(new { diff }, JsonOpts);
        });

        g.MapPost("/config/import", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var doc = body?["doc"] as JsonObject ?? body;
            if (doc is null) return Results.Json(new { error = "doc required" }, JsonOpts, statusCode: 400);
            var scopes = body?["scopes"] is JsonArray sa
                ? sa.Select(e => e?.GetValue<string>()).Where(x => x is not null).Cast<string>().ToArray() : null;
            var dryRun = body?["dryRun"]?.GetValue<bool>() ?? false;
            var redacted = body?["redactedSecrets"]?.GetValue<string>() ?? "keep";

            var result = await ConfigAsCode.ImportAsync(doc, scopes, dryRun, redacted, db, registry, ctx.RequestAborted);
            if (!dryRun && result.Errors.Count == 0)
                await LLMRouter.Core.Extras.Extras.AuditAsync(db, "config.import",
                    string.Join(" ", result.Counts.Select(kv => $"{kv.Key}:{kv.Value}")));
            return Results.Json(new
            {
                ok = result.Errors.Count == 0,
                result.Errors,
                result.Counts,
                dryRun,
            }, JsonOpts);
        });
    }
}

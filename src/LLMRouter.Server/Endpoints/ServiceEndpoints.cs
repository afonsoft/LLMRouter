using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Services;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-061: lifecycle de sidecar services — /api/services(+status),
/// install/start/stop/restart/update, logs, auto-start/auto-restart,
/// provider-expose; /api/local/redis/* espelha o serviço bundled.
/// </summary>
public static class ServiceEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia os endpoints de services.</summary>
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/services", async (LlmRouterDbContext db) =>
            Results.Json(new { services = await ServiceManager.StatusAllAsync(db) }, JsonOpts));

        g.MapGet("/services/{id}/status", async (string id, LlmRouterDbContext db) =>
        {
            var all = await ServiceManager.StatusAllAsync(db);
            var s = all.FirstOrDefault(x => x.GetType().GetProperty("id")!.GetValue(x) as string == id);
            return s is null ? Results.Json(new { error = "unknown service" }, JsonOpts, statusCode: 404)
                             : Results.Json(s, JsonOpts);
        });

        g.MapPost("/services/{id}/install", async (string id) =>
        {
            var (ok, detail) = await ServiceManager.InstallAsync(id);
            return Results.Json(new { ok, detail }, JsonOpts,
                statusCode: ok ? 200 : 400);
        });
        g.MapPost("/services/{id}/start", async (string id) =>
        {
            var (ok, detail) = await ServiceManager.StartAsync(id);
            return Results.Json(new { ok, detail }, JsonOpts, statusCode: ok ? 200 : 400);
        });
        g.MapPost("/services/{id}/stop", async (string id) =>
        {
            var (ok, detail) = await ServiceManager.StopAsync(id);
            return Results.Json(new { ok, detail }, JsonOpts);
        });
        g.MapPost("/services/{id}/restart", async (string id) =>
        {
            await ServiceManager.StopAsync(id);
            var (ok, detail) = await ServiceManager.StartAsync(id);
            return Results.Json(new { ok, detail }, JsonOpts, statusCode: ok ? 200 : 400);
        });
        g.MapPost("/services/{id}/update", async (string id) =>
        {
            // update = reinstall marker (binários reais atualizam re-baixando)
            var (ok, detail) = await ServiceManager.InstallAsync(id);
            return Results.Json(new { ok, detail, updated = ok }, JsonOpts, statusCode: ok ? 200 : 400);
        });

        g.MapGet("/services/{id}/logs", (string id, int? tail) =>
        {
            var logs = ServiceManager.Logs(id);
            return Results.Json(new { logs = logs.TakeLast(Math.Clamp(tail ?? 100, 1, 500)) }, JsonOpts);
        });

        g.MapPut("/services/{id}/auto-start", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            await ServiceManager.SetFlagAsync(db, id,
                b.TryGetProperty("enabled", out var e) && e.GetBoolean(), null);
            return Results.Json(new { ok = true }, JsonOpts);
        });
        g.MapPut("/services/{id}/auto-restart", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            await ServiceManager.SetFlagAsync(db, id, null,
                b.TryGetProperty("enabled", out var e) && e.GetBoolean());
            return Results.Json(new { ok = true }, JsonOpts);
        });
        g.MapPut("/services/{id}/auto-restart-adopted", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            await ServiceManager.SetFlagAsync(db, id, null,
                b.TryGetProperty("enabled", out var e) && e.GetBoolean());
            return Results.Json(new { ok = true, adopted = true }, JsonOpts);
        });

        g.MapPost("/services/{id}/provider-expose", async (string id, LlmRouterDbContext db) =>
        {
            var (ok, detail, connId) = await ServiceManager.ProviderExposeAsync(db, id);
            await Core.Extras.Extras.AuditAsync(db, "service.provider-expose", id);
            return Results.Json(new { ok, baseUrl = detail, connectionId = connId }, JsonOpts,
                statusCode: ok ? 200 : 400);
        });

        // ---- local/redis/* — atalhos do serviço bundled ----
        var redis = app.MapGroup("/api/local/redis");
        redis.MapGet("/status", async (LlmRouterDbContext db) =>
            Results.Json(new { running = await ServiceManager.RunningAsync("redis") }, JsonOpts));
        redis.MapPost("/start", async () =>
        {
            var (ok, detail) = await ServiceManager.StartAsync("redis");
            return Results.Json(new { ok, detail }, JsonOpts, statusCode: ok ? 200 : 400);
        });
        redis.MapPost("/stop", async () =>
        {
            var (ok, detail) = await ServiceManager.StopAsync("redis");
            return Results.Json(new { ok, detail }, JsonOpts);
        });
        redis.MapGet("/logs", () => Results.Json(new { logs = ServiceManager.Logs("redis").TakeLast(100) }, JsonOpts));
    }
}

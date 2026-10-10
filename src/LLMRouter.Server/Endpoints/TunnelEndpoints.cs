using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Tunnels;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-060: /api/tunnels — status, start/stop, logs e config por backend.</summary>
public static class TunnelEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia os endpoints de tunnels.</summary>
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/tunnels");

        g.MapGet("", async () => Results.Json(new { tunnels = await TunnelManager.StatusAllAsync() }, JsonOpts));
        g.MapGet("/{name}", async (string name) =>
            TunnelManager.Backends.Any(b => b.Id == name)
                ? Results.Json(await TunnelManager.StatusAsync(name), JsonOpts)
                : Results.Json(new { error = "unknown tunnel" }, JsonOpts, statusCode: 404));

        g.MapPost("/{name}/start", async (string name, LlmRouterDbContext db) =>
        {
            var (ok, detail) = await TunnelManager.StartAsync(name, await TunnelManager.ConfigAsync(db, name));
            await Core.Extras.Extras.AuditAsync(db, "tunnel.start", $"{name}:{ok}");
            return Results.Json(new { ok, detail }, JsonOpts, statusCode: ok ? 200 : 400);
        });
        g.MapPost("/{name}/stop", async (string name, LlmRouterDbContext db) =>
        {
            var (ok, detail) = await TunnelManager.StopAsync(name);
            await Core.Extras.Extras.AuditAsync(db, "tunnel.stop", name);
            return Results.Json(new { ok, detail }, JsonOpts);
        });
        g.MapGet("/{name}/logs", (string name, int? tail) =>
            Results.Json(new { logs = TunnelManager.Logs(name).TakeLast(Math.Clamp(tail ?? 100, 1, 400)) }, JsonOpts));

        g.MapGet("/{name}/config", async (string name, LlmRouterDbContext db) =>
            Results.Json(new { config = await TunnelManager.ConfigAsync(db, name) }, JsonOpts));
        g.MapPut("/{name}/config", async (string name, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var cfg = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(ctx.Request.Body) ?? [];
            await TunnelManager.SaveConfigAsync(db, name, cfg);
            return Results.Json(new { ok = true }, JsonOpts);
        });
    }
}

using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using LLMRouter.Core.Gamification;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-056: gamification — eventos, badges, invites, federação, notificações, SSE, anomalias, transfer.</summary>
public static class GamiEndpoints
{
    /// <summary>Mapeia /api/gamification.</summary>
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/gamification");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        MapScores(g, json);
        MapInvites(g, json);
        MapFederation(g, json);
        MapStream(g);
    }

    static void MapScores(RouteGroupBuilder g, JsonSerializerOptions json)
    {

        g.MapGet("/score/{actor}", async (LlmRouterDbContext db, string actor) =>
            Results.Json(new { actor, score = await GamiCore.ScoreAsync(db, actor) }, json));

        g.MapPost("/event", async (LlmRouterDbContext db, HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var actor = body.TryGetProperty("actor", out var a) ? a.GetString() ?? "dashboard" : "dashboard";
            var points = body.TryGetProperty("points", out var p) && p.TryGetInt64(out var v) ? v : 1;
            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var metric = body.TryGetProperty("metric", out var m) ? m.GetString() ?? "request" : "request";
            return Results.Json(await GamiCore.EventAsync(db, actor, points, reason, metric), json);
        });

        g.MapGet("/badges/{actor}", async (LlmRouterDbContext db, string actor) => Results.Json(await GamiCore.EarnedAsync(db, actor), json));
        g.MapGet("/notifications/{actor}", async (LlmRouterDbContext db, string actor, int limit = 50) => Results.Json(await GamiCore.NotificationsAsync(db, actor, limit), json));

        // SPEC-129: upstream GET /api/gamification/level?apiKeyId= (aggregate when absent)
        g.MapGet("/level", async (LlmRouterDbContext db, string? apiKeyId, CancellationToken ct) =>
            Results.Json(await GamiCore.LevelAsync(db, apiKeyId, ct), json));

        // SPEC-129: upstream GET /api/gamification/badges/earned?apiKeyId= (distinct when absent)
        g.MapGet("/badges/earned", async (LlmRouterDbContext db, string? apiKeyId, CancellationToken ct) =>
            Results.Json(await GamiCore.EarnedBadgesAsync(db, apiKeyId, ct), json));
    }

    static void MapInvites(RouteGroupBuilder g, JsonSerializerOptions json)
    {
        g.MapPost("/invites", async (LlmRouterDbContext db, HttpRequest req) =>
        {
            var actor = "dashboard";
            try { var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body); if (b.TryGetProperty("actor", out var a)) actor = a.GetString() ?? actor; } catch { /* corpo opcional */ }
            return Results.Json(new { code = await GamiCore.InviteCreateAsync(db, actor) }, json);
        });
        g.MapPost("/invites/redeem", async (LlmRouterDbContext db, HttpRequest req) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var code = b.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var actor = b.TryGetProperty("actor", out var a) ? a.GetString() ?? "dashboard" : "dashboard";
            return await GamiCore.InviteRedeemAsync(db, code, actor) ? Results.Json(new { ok = true }, json) : Results.BadRequest(new { error = "invite inválido" });
        });

    }

    static void MapFederation(RouteGroupBuilder g, JsonSerializerOptions json)
    {
        g.MapGet("/leaderboard", async (LlmRouterDbContext db, IHttpClientFactory hf, CancellationToken ct) => Results.Json(await GamiCore.LeaderboardAsync(db, hf.CreateClient("gami"), ct), json));
        g.MapPost("/servers", async (LlmRouterDbContext db, HttpRequest req) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var url = b.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var key = b.TryGetProperty("apiKey", out var k) ? k.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) return Results.BadRequest(new { error = "url" });
            await GamiCore.ServerAddAsync(db, url, key);
            await Extras.AuditAsync(db, "gami.server.add", url);
            return Results.Json(new { ok = true }, json);
        });

        // SPEC-129: federation surface — remote peers auth via Bearer (pbkdf2 keyHash)
        g.MapGet("/federation/leaderboard", async (HttpContext ctx, LlmRouterDbContext db, CancellationToken ct) =>
        {
            if (ctx.Request.Headers.Authorization.FirstOrDefault() is not { } auth || !auth.StartsWith("Bearer "))
                return Results.Json(new { error = "Missing authorization" }, json, statusCode: 401);
            if (await GamiCore.FederationAuthAsync(db, auth, ct) is null)
                return Results.Json(new { error = "Invalid or unauthorized token" }, json, statusCode: 403);
            var scope = ctx.Request.Query["scope"].FirstOrDefault() ?? "global";
            var raw = ctx.Request.Query["limit"].FirstOrDefault();
            var limit = raw is null ? 100 : (int.TryParse(raw, out var n) ? n : -1);
            if (limit < 1 || limit > GamiCore.LeaderboardMaxLimit)
                return Results.Json(new { error = "'limit' must be an integer between 1 and 200" }, json, statusCode: 400);
            return Results.Json(new { entries = await GamiCore.TopNAsync(db, scope, limit, 0, ct) }, json);
        });
        g.MapPost("/federation/score", async (HttpContext ctx, LlmRouterDbContext db, CancellationToken ct) =>
        {
            if (ctx.Request.Headers.Authorization.FirstOrDefault() is not { } auth || !auth.StartsWith("Bearer "))
                return Results.Json(new { error = "Missing authorization" }, json, statusCode: 401);
            if (await GamiCore.FederationAuthAsync(db, auth, ct) is null)
                return Results.Json(new { error = "Invalid or unauthorized token" }, json, statusCode: 403);
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, json, ct);
            var apiKeyId = b.TryGetProperty("apiKeyId", out var k) ? k.GetString() ?? "" : "";
            var scope = b.TryGetProperty("scope", out var s) ? s.GetString() ?? "global" : "global";
            var points = b.TryGetProperty("points", out var p) && p.TryGetInt64(out var v) ? v : 0;
            if (string.IsNullOrWhiteSpace(apiKeyId)) return Results.BadRequest(new { error = "apiKeyId" });
            await GamiCore.UpdateScoreAsync(db, apiKeyId, scope, points, ct);
            return Results.Json(new { success = true }, json);
        });
        g.MapPost("/rotate", async (LlmRouterDbContext db) =>
        {
            var id = GamiCore.RotateFederationId();
            await Extras.AuditAsync(db, "gami.federation.rotate", id);
            return Results.Json(new { federationId = id }, json);
        });
        g.MapPost("/transfer", async (LlmRouterDbContext db, IHttpClientFactory hf, HttpRequest req, CancellationToken ct) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var actor = b.TryGetProperty("actor", out var a) ? a.GetString() ?? "dashboard" : "dashboard";
            var srv = b.TryGetProperty("server", out var s) ? s.GetString() ?? "" : "";
            var to = b.TryGetProperty("toActor", out var t) ? t.GetString() ?? "" : "";
            var pts = b.TryGetProperty("points", out var p) && p.TryGetInt64(out var v) ? v : 0;
            return await GamiCore.TransferAsync(db, hf.CreateClient("gami"), actor, srv, to, pts, ct)
                ? Results.Json(new { ok = true }, json) : Results.BadRequest(new { error = "transfer falhou" });
        });
        g.MapGet("/anomalies", async (LlmRouterDbContext db) => Results.Json(await GamiCore.AnomaliesAsync(db), json));
    }

    // SSE stream de eventos recentes (poll 2s, 30 frames)
    static void MapStream(RouteGroupBuilder g)
    {
        g.MapGet("/stream", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            long lastId = 0;
            for (var i = 0; i < 30 && !ctx.RequestAborted.IsCancellationRequested; i++)
            {
                var evs = await db.GamiItems.Where(x => x.Id > lastId && (x.Kind == "scoreEvent" || x.Kind == "earned" || x.Kind == "notification"))
                    .OrderBy(x => x.Id).Take(20).ToListAsync(ctx.RequestAborted);
                foreach (var e in evs)
                {
                    lastId = e.Id;
                    await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { e.Id, e.Kind, e.Actor, e.Points, e.Data, e.At })}\n\n", ctx.RequestAborted);
                }
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                await Task.Delay(2000, ctx.RequestAborted);
            }
        });
    }
}

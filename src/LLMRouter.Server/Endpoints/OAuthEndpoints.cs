using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.OAuth;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-011: /api/oauth start/poll/callback + /api/token-health (list/refresh).
/// </summary>
public static class OAuthEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task StoreTokens(LlmRouterDbContext db, ProviderConnection? existing, string providerId, JsonElement tokens)
    {
        var conn = existing ?? new ProviderConnection
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Provider = providerId,
            Name = $"{providerId}-oauth",
            IsActive = true,
            Data = "{}",
        };
        var data = JsonNode.Parse(conn.Data)?.AsObject() ?? new JsonObject();
        foreach (var kv in OAuthService.TokenData(tokens))
            data[kv.Key] = kv.Value is null ? null : JsonValue.Create(kv.Value);
        data["oauthFlow"] = "auto";
        conn.Data = data.ToJsonString();
        if (existing is null) db.ProviderConnections.Add(conn);
        await db.SaveChangesAsync();
    }

    public static void MapOAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapPost("/oauth/{provider}/start", async (string provider, HttpContext ctx,
            ProviderRegistry registry, IHttpClientFactory hf) =>
        {
            var p = registry.GetProvider(provider);
            if (p is null) return Results.NotFound(new { error = "unknown provider" });
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            try
            {
                var s = await OAuthService.StartAsync(p, baseUrl, hf.CreateClient("upstream"));
                return Results.Json(new
                {
                    s.State, s.Flow, authUrl = s.AuthUrl, userCode = s.UserCode,
                    intervalSec = s.IntervalSec, poll = $"/api/oauth/poll/{s.State}",
                }, JsonOpts);
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapGet("/oauth/poll/{state}", async (string state, LlmRouterDbContext db,
            ProviderRegistry registry, IHttpClientFactory hf) =>
        {
            if (!OAuthService.Sessions.TryGetValue(state, out var s))
                return Results.NotFound(new { error = "session expired" });
            var p = registry.GetProvider(s.Provider)!;
            var tokens = await OAuthService.CompleteAsync(s, p, null, hf.CreateClient("upstream"));
            if (tokens is null) return Results.Json(new { status = "pending" }, JsonOpts);
            if (tokens.Value.TryGetProperty("error", out var err))
                return Results.Json(new { status = "error", error = err.GetString() }, JsonOpts);
            await StoreTokens(db, null, s.Provider, tokens.Value);
            OAuthService.Sessions.TryRemove(state, out _);
            return Results.Json(new { status = "done" }, JsonOpts);
        });

        // PKCE/browser callback — unauthenticated (state is the secret)
        app.MapGet("/api/oauth/callback", async (HttpContext ctx, LlmRouterDbContext db,
            ProviderRegistry registry, IHttpClientFactory hf) =>
        {
            var state = ctx.Request.Query["state"].FirstOrDefault();
            var code = ctx.Request.Query["code"].FirstOrDefault();
            if (state is null || !OAuthService.Sessions.TryGetValue(state, out var s))
                return Results.BadRequest("session expired");
            var p = registry.GetProvider(s.Provider)!;
            var tokens = await OAuthService.CompleteAsync(s, p, code, hf.CreateClient("upstream"));
            if (tokens is null)
                return Results.BadRequest("oauth failed");
            if (tokens.Value.TryGetProperty("error", out var err))
                return Results.BadRequest($"oauth failed: {err.GetString()}");
            await StoreTokens(db, null, s.Provider, tokens.Value);
            OAuthService.Sessions.TryRemove(state, out _);
            return Results.Content("<html><body><h3>Connected — close this tab.</h3></body></html>", "text/html");
        });

        g.MapGet("/token-health", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections.Where(c => c.IsActive).ToListAsync();
            var list = conns.Select(c => new
            {
                c.Id, c.Name, c.Provider,
                status = OAuthService.HealthOf(c),
            }).Where(x => x.status != "none");
            return Results.Json(new { connections = list }, JsonOpts);
        });

        g.MapPost("/token-health/{connId}/refresh", async (string connId, LlmRouterDbContext db,
            ProviderRegistry registry, IHttpClientFactory hf) =>
        {
            var conn = await db.ProviderConnections.FindAsync(connId);
            if (conn is null) return Results.NotFound();
            var p = registry.GetProvider(conn.Provider) ?? await NodeResolver.ResolveAsync(db, conn.Provider);
            if (p is null) return Results.BadRequest(new { error = "unknown provider" });
            var ok = await OAuthService.RefreshAsync(conn, p, hf.CreateClient("upstream"));
            if (ok) await db.SaveChangesAsync();
            return Results.Json(new { ok, status = OAuthService.HealthOf(conn) }, JsonOpts);
        });
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-096: settings sub-endpoints (kv "settings" rows), forgot/reset password,
/// and misc leftovers (upstream-proxy, cursor-cli, codex connect, cli tokens,
/// version-manager, vnc-session, middleware hooks).
/// </summary>
public static class SettingsExtrasEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Sub-settings live as keys inside the main settings.data blob — same
    // shape upstream uses, and what SPEC-047's ipFilter middleware reads.
    private const string AdminScope = "admin";

    private static async Task<IResult> GetAsync(LlmRouterDbContext db, string key, CancellationToken ct)
    {
        var s = await db.Settings.FirstOrDefaultAsync(ct);
        var d = AuthEndpoints.Parse(s?.Data).Deserialize<Dictionary<string, JsonElement>>() ?? [];
        return Results.Json(d.TryGetValue(key, out var v) ? v : (JsonElement?)null, JsonOpts);
    }

    private static async Task<IResult> PutAsync(HttpContext ctx, LlmRouterDbContext db, string key)
    {
        var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
        var s = await db.Settings.FirstOrDefaultAsync(ctx.RequestAborted)
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var d = AuthEndpoints.Parse(s.Data).Deserialize<Dictionary<string, JsonElement>>() ?? [];
        d[key] = b;
        s.Data = JsonSerializer.Serialize(d);
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.Json(new { ok = true }, JsonOpts);
    }

    // NOTE: generic GET/PUT /api/settings/{seg} (SettingsRoutingEndpoints)
    // already covers ip-filter, feature-flags, system-prompt, thinking-budget,
    // tier-config, task-routing, reasoning-routing-rules, auto-disable-accounts,
    // background-degradation, cache-config, database — added to its segment list.
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();
        MapSettingsOps(g);
        MapAuthReset(g);
        MapMisc(app, g);
    }

    private static void MapSettingsOps(RouteGroupBuilder g)
    {
        g.MapPost("/settings/purge-cache", async () =>
        {
            Core.Gateway.HotCache.Default.InvalidateAll();
            return Results.Json(new { ok = true, purged = "cache" }, JsonOpts);
        });
        g.MapPost("/settings/purge-logs", async (LlmRouterDbContext db) =>
        {
            db.AuditEvents.RemoveRange(db.AuditEvents);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, purged = "logs" }, JsonOpts);
        });
        g.MapPost("/settings/purge-sessions", async (LlmRouterDbContext db) =>
        {
            db.PoolSessions.RemoveRange(db.PoolSessions);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, purged = "sessions" }, JsonOpts);
        });

        g.MapPost("/settings/database/vacuum", async (LlmRouterDbContext db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlRawAsync("VACUUM", ct);
            return Results.Json(new { ok = true }, JsonOpts);
        });

        g.MapGet("/settings/cache-metrics", () => Results.Json(new
        {
            hits = Core.Gateway.HotCache.Default.Hits,
            misses = Core.Gateway.HotCache.Default.Misses,
        }, JsonOpts));

        g.MapPost("/settings/favicon", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return Results.Json(new { error = "empty body" }, JsonOpts, statusCode: 400);
            var row = await db.Kv.FindAsync("assets", "favicon")
                ?? db.Kv.Add(new KvEntry { Scope = "assets", Key = "favicon" }).Entity;
            row.Value = Convert.ToBase64String(bytes);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, bytes = bytes.Length }, JsonOpts);
        });

        g.MapGet("/settings/authz-inventory", () => Results.Json(new
        {
            routes = new[]
            {
                new { path = "/api/settings", scope = AdminScope },
                new { path = "/api/providers", scope = AdminScope },
                new { path = "/api/keys", scope = AdminScope },
                new { path = "/v1/*", scope = "apiKey" },
                new { path = "/mcp", scope = "apiKey" },
                new { path = "/healthz", scope = "anonymous" },
            },
        }, JsonOpts));

        g.MapGet("/settings/lkgp-cache", async (LlmRouterDbContext db) =>
        {
            var rows = await db.Kv.Where(k => k.Scope == "lkgp").ToListAsync();
            return Results.Json(new { entries = rows.Select(r => new { r.Key, value = JsonNode.Parse(r.Value) }) }, JsonOpts);
        });
        g.MapPost("/settings/lkgp-cache/clear", async (LlmRouterDbContext db) =>
        {
            db.Kv.RemoveRange(db.Kv.Where(k => k.Scope == "lkgp"));
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        g.MapGet("/settings/qdrant/{section}", async (string section, LlmRouterDbContext db, HttpContext ctx) =>
            await GetAsync(db, $"qdrant.{section}", ctx.RequestAborted));
        g.MapPut("/settings/qdrant/{section}", async (string section, HttpContext ctx, LlmRouterDbContext db) =>
            await PutAsync(ctx, db, $"qdrant.{section}"));

        g.MapGet("/settings/cc-discovery-metrics", async (LlmRouterDbContext db) =>
        {
            var rows = await db.Kv.Where(k => k.Scope == "ccDiscovery").ToListAsync();
            return Results.Json(new { metrics = rows.Select(r => new { r.Key, value = JsonNode.Parse(r.Value) }) }, JsonOpts);
        });
    }

    private static void MapAuthReset(RouteGroupBuilder g)
    {
        // ---- forgot/reset password (token row + log; no SMTP in docker) ----
        g.MapPost("/auth/forgot-password", async (HttpContext ctx, LlmRouterDbContext db, ILoggerFactory lf) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var token = Guid.NewGuid().ToString("N");
            db.Kv.Add(new KvEntry
            {
                Scope = "passwordResets", Key = token,
                Value = JsonSerializer.Serialize(new
                {
                    user = b.TryGetProperty("email", out var e) ? e.GetString() : AdminScope,
                    expires = DateTimeOffset.UtcNow.AddHours(1).ToString("o"),
                }),
            });
            await db.SaveChangesAsync();
            lf.CreateLogger("auth").LogWarning("Password reset token issued: {Token} (no SMTP — surfaced in logs)", token);
            return Results.Json(new { sent = true }, JsonOpts);
        }).AllowAnonymous();

        g.MapPost("/auth/reset-password", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var token = b.TryGetProperty("token", out var t) ? t.GetString() : null;
            var password = b.TryGetProperty("password", out var p) ? p.GetString() : null;
            if (token is null || password is null)
                return Results.Json(new { error = "token and password required" }, JsonOpts, statusCode: 400);
            var row = await db.Kv.FindAsync("passwordResets", token);
            if (row is null)
                return Results.Json(new { error = "invalid token" }, JsonOpts, statusCode: 400);
            db.Kv.Remove(row);
            var s = await db.Settings.FirstOrDefaultAsync() ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
            var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                password, salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
            d["adminPasswordHash"] = JsonSerializer.SerializeToElement(
                $"pbkdf2$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
            s.Data = JsonSerializer.Serialize(d);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        }).AllowAnonymous();
    }

    private static void MapMisc(WebApplication app, RouteGroupBuilder g)
    {
        // ---- misc leftovers ----
        app.MapMethods("/api/upstream-proxy/{**path}", new[] { "GET", "POST", "PUT", "DELETE" },
            (HttpContext ctx, string? path, IHttpClientFactory hf) =>
        {
            // loopback-admin only: proxy to a URL the caller asks for, guarded to loopback
            var target = ctx.Request.Query["url"].FirstOrDefault();
            if (target is null || !Uri.TryCreate(target, UriKind.Absolute, out var uri) || !uri.IsLoopback)
                return Task.FromResult(Results.Json(new { error = "loopback url required" }, JsonOpts, statusCode: 400));
            return ProxyOnce(ctx, hf, uri);
        }).RequireAuthorization();

        // cursor-cli catch-all already exists (BotsEndpoints — real passthrough
        // to settings.data.cursorCli.baseUrl), so none added here.
        g.MapPost("/codex/connect/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var conn = await db.ProviderConnections.FindAsync(id);
            if (conn is null) return Results.Json(new { error = "connection not found" }, JsonOpts, statusCode: 404);
            var row = await db.Kv.FindAsync("codexConnections", id)
                ?? db.Kv.Add(new KvEntry { Scope = "codexConnections", Key = id }).Entity;
            row.Value = JsonSerializer.Serialize(new { connectionId = id, attached = DateTimeOffset.UtcNow.ToString("o") });
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, id, provider = conn.Provider }, JsonOpts);
        });

        g.MapGet("/cli/tokens", async (LlmRouterDbContext db) =>
        {
            var rows = await db.Kv.Where(k => k.Scope == "cliTokens").ToListAsync();
            return Results.Json(new { tokens = rows.Select(r => JsonNode.Parse(r.Value)) }, JsonOpts);
        });

        app.MapGet("/api/internal/codex-responses-ws", () =>
            Results.Json(new { available = false, note = "codex responses WS not enabled" }, JsonOpts))
            .RequireAuthorization();

        g.MapPost("/version-manager/{op}", async (string op, LlmRouterDbContext db) =>
        {
            if (op is not ("start" or "stop"))
                return Results.Json(new { error = "op must be start|stop" }, JsonOpts, statusCode: 400);
            var row = await db.Kv.FindAsync("jobs", "versionManager")
                ?? db.Kv.Add(new KvEntry { Scope = "jobs", Key = "versionManager" }).Entity;
            row.Value = JsonSerializer.Serialize(new { running = op == "start", at = DateTimeOffset.UtcNow.ToString("o") });
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, running = op == "start" }, JsonOpts);
        });

        // vnc-session start/status/stop (BotsEndpoints) and middleware/hooks
        // get/put (AdminOpsEndpoints) already exist — no catch-alls needed.
    }

    private static async Task<IResult> ProxyOnce(HttpContext ctx, IHttpClientFactory hf, Uri uri)
    {
        var http = hf.CreateClient("upstream");
        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), uri);
        if (ctx.Request.ContentLength > 0)
            req.Content = new StreamContent(ctx.Request.Body);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        var body = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        return Results.Text(body, resp.Content.Headers.ContentType?.ToString() ?? "text/plain",
            statusCode: (int)resp.StatusCode);
    }
}

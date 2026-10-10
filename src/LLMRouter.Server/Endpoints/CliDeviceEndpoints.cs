using System.Security.Cryptography;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-050: CLI device login — a CLI calls POST /api/cli/connect (no auth),
/// opens /connect/{token} in the owner's browser where a logged-in user
/// approves it, and polls GET /api/cli/tokens/{token} for the minted apiKey.
/// Tokens expire 10 minutes after creation.
/// </summary>
public static class CliDeviceEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(10);

    private static bool Expired(CliToken t) =>
        DateTime.TryParse(t.CreatedAt, out var c) && DateTime.UtcNow - c > TokenTtl;

    public static void Map(WebApplication app)
    {
        // unauthenticated: the CLI starts the flow here
        app.MapPost("/api/cli/connect", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            JsonElement req = default;
            if (ctx.Request.ContentLength > 0)
                try { req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); } catch { }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            var t = new CliToken
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Token = token,
                DeviceName = req.ValueKind == JsonValueKind.Object
                    && req.TryGetProperty("deviceName", out var dn) ? dn.GetString() : null,
                CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            db.CliTokens.Add(t);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "cli.connect", token);
            return Results.Json(new
            {
                token,
                url = $"/connect/{token}",
                expiresAt = DateTime.UtcNow.Add(TokenTtl).ToString("o"),
            }, JsonOpts);
        });

        // unauthenticated: the CLI polls until approved
        app.MapGet("/api/cli/tokens/{token}", async (string token, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var t = await db.CliTokens.FirstOrDefaultAsync(x => x.Token == token);
            if (t is null)
                return Results.Json(new { error = "unknown token" }, JsonOpts, statusCode: 404);
            if (t.State == "revoked" || (t.State == "pending" && Expired(t)))
                return Results.Json(new { state = "expired" }, JsonOpts, statusCode: 410);
            if (t.State == "pending")
            {
                ctx.Response.StatusCode = 202;
                return Results.Json(new { state = "pending" }, JsonOpts);
            }
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Json(new
            { state = "approved", apiKey = t.ApiKey, baseUrl }, JsonOpts);
        });

        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapPost("/cli/tokens/{token}/approve", async (string token, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var t = await db.CliTokens.FirstOrDefaultAsync(x => x.Token == token);
            if (t is null)
                return Results.Json(new { error = "unknown token" }, JsonOpts, statusCode: 404);
            if (t.State != "pending" || Expired(t))
                return Results.Json(new { error = "token expired or already handled" },
                    JsonOpts, statusCode: 410);
            var req = ctx.Request.ContentLength > 0
                ? await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body)
                : (JsonElement?)null;
            if (req is { } r && r.TryGetProperty("deviceName", out var dn)) t.DeviceName = dn.GetString();
            var key = "sk-cli-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            db.ApiKeys.Add(new ApiKey
            {
                Id = t.Id,
                Key = key,
                Name = $"cli:{t.DeviceName ?? token}",
                IsActive = true,
                CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            t.State = "approved";
            t.ApiKey = key;
            t.ApprovedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "cli.approve", token);
            return Results.Json(new { state = "approved", apiKey = key }, JsonOpts);
        });

        g.MapPost("/cli/tokens/{token}/revoke", async (string token, LlmRouterDbContext db) =>
        {
            var t = await db.CliTokens.FirstOrDefaultAsync(x => x.Token == token);
            if (t is null)
                return Results.Json(new { error = "unknown token" }, JsonOpts, statusCode: 404);
            t.State = "revoked";
            if (t.ApiKey is { } k && await db.ApiKeys.FirstOrDefaultAsync(x => x.Key == k) is { } row)
                row.IsActive = false;
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "cli.revoke", token);
            return Results.Json(new { state = "revoked" }, JsonOpts);
        });

        // identity check for a CLI holding an apiKey (Bearer or ?key=)
        app.MapGet("/api/cli/whoami", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var key = ctx.Request.Headers.Authorization.FirstOrDefault() is { } a && a.StartsWith("Bearer ")
                ? a[7..].Trim()
                : ctx.Request.Headers["x-api-key"].FirstOrDefault()
                  ?? ctx.Request.Query["key"].FirstOrDefault();
            var row = key is null ? null : await db.ApiKeys.FirstOrDefaultAsync(k => k.Key == key && k.IsActive);
            if (row is null)
                return Results.Json(new { error = "invalid api key" }, JsonOpts, statusCode: 401);
            return Results.Json(new
            {
                key = row.Key.Length > 8 ? row.Key[..8] + "…" : "…",
                row.Name,
                authenticated = true,
                restricted = row.AccessRestricted,
                allow = row.AccessAllow,
            }, JsonOpts);
        });
    }
}

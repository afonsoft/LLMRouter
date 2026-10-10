using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-048: relay service — per-client tokens that hit the normal gateway
/// pipeline without a dashboard account or apiKeys entry.
/// POST /v1/relay/chat/completions accepts "Authorization: Bearer rl-…".
/// Usage is attributed as ApiKey="relay:{id}"; quota is requests/day +
/// tokens/day counted from usageHistory.
/// </summary>
public static class RelayEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static object View(RelayToken t) => new
    {
        t.Id, t.Name,
        token = t.Token.Length > 8 ? t.Token[..8] + "…" : "…",
        allowedModels = JsonSerializer.Deserialize<string[]>(t.AllowedModels) ?? [],
        quotaRequests = t.QuotaRequests,
        quotaTokens = t.QuotaTokens,
        t.ExpiresAt, active = t.IsActive, t.CreatedAt,
    };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/relay/tokens", async (LlmRouterDbContext db) =>
            Results.Json(new
            { tokens = (await db.RelayTokens.OrderByDescending(t => t.CreatedAt).ToListAsync()).Select(View) },
            JsonOpts));

        g.MapPost("/relay/tokens", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var t = new RelayToken
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Token = "rl-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                Name = req.TryGetProperty("name", out var n) ? n.GetString() : null,
                CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            ApplyPatch(t, req);
            db.RelayTokens.Add(t);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "relay.token.create", t.Id);
            // full token returned only here — list/get always mask it
            return Results.Json(new { token = t.Token, view = View(t) }, JsonOpts);
        });

        g.MapPut("/relay/tokens/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var t = await db.RelayTokens.FindAsync(id);
            if (t is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (req.TryGetProperty("name", out var n)) t.Name = n.ValueKind == JsonValueKind.Null ? null : n.GetString();
            if (req.TryGetProperty("active", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False)
                t.IsActive = a.GetBoolean();
            ApplyPatch(t, req);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "relay.token.update", id);
            return Results.Json(View(t), JsonOpts);
        });

        g.MapDelete("/relay/tokens/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var t = await db.RelayTokens.FindAsync(id);
            if (t is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            db.RelayTokens.Remove(t);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "relay.token.delete", id);
            return Results.Json(new { success = true }, JsonOpts);
        });

        g.MapGet("/relay/tokens/{id}/usage", async (string id, LlmRouterDbContext db) =>
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var ak = $"relay:{id}";
            var all = await db.UsageHistory.Where(u => u.ApiKey == ak).ToListAsync();
            var todays = all.Where(u => string.CompareOrdinal(u.Timestamp, today) >= 0).ToList();
            return Results.Json(new
            {
                requestsToday = todays.Count,
                tokensToday = todays.Sum(u => u.PromptTokens + u.CompletionTokens),
                totalRequests = all.Count,
                totalTokens = all.Sum(u => u.PromptTokens + u.CompletionTokens),
            }, JsonOpts);
        });

        // relayed gateway access — relay token auth, then the normal pipeline
        foreach (var prefix in new[] { "/v1", "/api/v1" })
            app.MapMethods($"{prefix}/relay/chat/completions", ["POST"], RelayChat);
    }

    private static void ApplyPatch(RelayToken t, JsonElement req)
    {
        if (req.TryGetProperty("allowedModels", out var am) && am.ValueKind == JsonValueKind.Array)
            t.AllowedModels = am.GetRawText();
        if (req.TryGetProperty("quotaRequests", out var qr) && qr.ValueKind == JsonValueKind.Number)
            t.QuotaRequests = qr.GetInt32();
        if (req.TryGetProperty("quotaTokens", out var qt) && qt.ValueKind == JsonValueKind.Number)
            t.QuotaTokens = qt.GetInt64();
        if (req.TryGetProperty("expiresAt", out var ex))
            t.ExpiresAt = ex.ValueKind == JsonValueKind.Null ? null : ex.GetString();
    }

    private static async Task RelayChat(HttpContext ctx)
    {
        async Task Reject(int status, string type, string msg)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(
                JsonSerializer.Serialize(new { error = new { message = msg, type, code = type } }));
        }

        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var key = ctx.Request.Headers.Authorization.FirstOrDefault() is { } a && a.StartsWith("Bearer ")
            ? a[7..].Trim() : null;
        if (string.IsNullOrEmpty(key))
        {
            await Reject(401, "invalid_api_key", "Missing relay token.");
            return;
        }
        var tok = await db.RelayTokens.FirstOrDefaultAsync(t => t.Token == key, ctx.RequestAborted);
        if (tok is null || !tok.IsActive)
        {
            await Reject(401, "invalid_api_key", "Invalid or revoked relay token.");
            return;
        }
        if (tok.ExpiresAt is { } exp
            && DateTime.TryParse(exp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var expAt) && expAt < DateTime.UtcNow)
        {
            await Reject(401, "invalid_api_key", "Relay token expired.");
            return;
        }

        // buffer the body: Chat re-reads it after our model check
        var raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var model = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("model", out var m)) model = m.GetString() ?? "";
        }
        catch { /* malformed — Chat returns its own 400 */ }

        var allow = JsonSerializer.Deserialize<string[]>(tok.AllowedModels) ?? [];
        if (allow.Length > 0 && !allow.Any(a => a.EndsWith('*')
            ? model.StartsWith(a[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, model, StringComparison.OrdinalIgnoreCase)))
        {
            await Reject(403, "model_not_allowed", $"Relay token is not allowed to use '{model}'.");
            return;
        }

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var ak = $"relay:{tok.Id}";
        var reqsToday = await db.UsageHistory
            .CountAsync(u => u.ApiKey == ak && u.Timestamp.CompareTo(today) >= 0);
        if (tok.QuotaRequests > 0 && reqsToday >= tok.QuotaRequests)
        {
            await Reject(429, "rate_limit", "Relay token daily request quota exhausted.");
            return;
        }
        if (tok.QuotaTokens > 0)
        {
            var toksToday = await db.UsageHistory
                .Where(u => u.ApiKey == ak && u.Timestamp.CompareTo(today) >= 0)
                .SumAsync(u => (long?)(u.PromptTokens + u.CompletionTokens)) ?? 0;
            if (toksToday >= tok.QuotaTokens)
            {
                await Reject(429, "rate_limit", "Relay token daily token quota exhausted.");
                return;
            }
        }

        await GatewayEndpoints.Chat(ctx, "openai", ak);
    }
}

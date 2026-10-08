using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>Management API consumed by the dashboard (cookie-authenticated).</summary>
public static class ManagementEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapManagementEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- providers catalog ----
        g.MapGet("/providers", (ProviderRegistry r) => Results.Json(new
        {
            categories = r.UiProviders()
                .GroupBy(t => t.Category)
                .Select(grp => new
                {
                    kind = grp.Key,
                    providers = grp.Select(t => t.Entry),
                }),
        }, JsonOpts));

        g.MapGet("/providers/{id}", (string id, ProviderRegistry r) =>
            r.GetProvider(id) is { } p
                ? Results.Json(new { provider = p, ui = r.GetUiProvider(id).Entry }, JsonOpts)
                : Results.NotFound());

        // ---- provider connections ----
        g.MapGet("/provider-connections", async (LlmRouterDbContext db) =>
            Results.Json(new { connections = await db.ProviderConnections.OrderBy(c => c.Provider).ThenBy(c => c.Priority).ToListAsync() }, JsonOpts));

        g.MapPost("/provider-connections", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry r) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var provider = b.GetProperty("provider").GetString()!;
            if (r.GetProvider(provider) is null) return Results.BadRequest(new { error = "unknown provider" });
            var c = new ProviderConnection
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Provider = provider,
                AuthType = Get(b, "authType") ?? r.GetProvider(provider)!.AuthType,
                Name = Get(b, "name") ?? provider,
                Email = Get(b, "email"),
                Priority = b.TryGetProperty("priority", out var pr) && pr.TryGetInt32(out var pi) ? pi : 0,
                IsActive = true,
                Data = b.TryGetProperty("data", out var d) ? d.GetRawText() : "{}",
                CreatedAt = Now(), UpdatedAt = Now(),
            };
            db.ProviderConnections.Add(c);
            await db.SaveChangesAsync();
            return Results.Json(new { connection = c }, JsonOpts);
        });

        g.MapPut("/provider-connections/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound();
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out _)) c.Name = Get(b, "name") ?? c.Name;
            if (b.TryGetProperty("email", out _)) c.Email = Get(b, "email");
            if (b.TryGetProperty("priority", out var pr) && pr.TryGetInt32(out var pi)) c.Priority = pi;
            if (b.TryGetProperty("isActive", out var ia)) c.IsActive = ia.GetBoolean();
            if (b.TryGetProperty("data", out var d)) c.Data = d.GetRawText();
            c.UpdatedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(new { connection = c }, JsonOpts);
        });

        g.MapDelete("/provider-connections/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound();
            db.ProviderConnections.Remove(c);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true });
        });

        g.MapPost("/provider-connections/{id}/test", async (string id, LlmRouterDbContext db, ProviderRegistry r, IHttpClientFactory hf) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound();
            var p = r.GetProvider(c.Provider);
            if (p is null) return Results.BadRequest(new { error = "unknown provider" });
            var modelsUrl = r.GetModelsUrl(p) ?? $"{p.BaseUrl?.TrimEnd('/')}/v1/models";
            var secret = Core.Gateway.GatewayEngine.ConnectionSecret(c);
            var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            if (secret is not null)
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
            try
            {
                var resp = await hf.CreateClient("upstream").SendAsync(req);
                var ok = resp.IsSuccessStatusCode;
                var text = await resp.Content.ReadAsStringAsync();
                return Results.Json(new { ok, status = (int)resp.StatusCode, body = text[..Math.Min(2000, text.Length)] });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        // ---- api keys ----
        g.MapGet("/keys", async (LlmRouterDbContext db) =>
            Results.Json(new { keys = await db.ApiKeys.ToListAsync() }, JsonOpts));

        g.MapPost("/keys", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var k = new ApiKey
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Key = $"sk-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}",
                Name = Get(b, "name") ?? "default",
                IsActive = true,
                CreatedAt = Now(),
            };
            db.ApiKeys.Add(k);
            await db.SaveChangesAsync();
            return Results.Json(new { key = k }, JsonOpts);
        });

        g.MapDelete("/keys/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var k = await db.ApiKeys.FindAsync(id);
            if (k is null) return Results.NotFound();
            db.ApiKeys.Remove(k);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true });
        });

        g.MapPut("/keys/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var k = await db.ApiKeys.FindAsync(id);
            if (k is null) return Results.NotFound();
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("isActive", out var ia)) k.IsActive = ia.GetBoolean();
            if (b.TryGetProperty("name", out _)) k.Name = Get(b, "name") ?? k.Name;
            await db.SaveChangesAsync();
            return Results.Json(new { key = k }, JsonOpts);
        });

        // ---- combos ----
        g.MapGet("/combos", async (LlmRouterDbContext db) =>
            Results.Json(new { combos = await db.Combos.ToListAsync() }, JsonOpts));

        g.MapPost("/combos", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var c = new Combo
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = b.GetProperty("name").GetString()!,
                Kind = Get(b, "kind") ?? "fallback",
                Models = b.TryGetProperty("models", out var m) ? m.GetRawText() : "[]",
                CreatedAt = Now(), UpdatedAt = Now(),
            };
            db.Combos.Add(c);
            await db.SaveChangesAsync();
            return Results.Json(new { combo = c }, JsonOpts);
        });

        g.MapPut("/combos/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var c = await db.Combos.FindAsync(id);
            if (c is null) return Results.NotFound();
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out _)) c.Name = Get(b, "name") ?? c.Name;
            if (b.TryGetProperty("kind", out _)) c.Kind = Get(b, "kind");
            if (b.TryGetProperty("models", out var m)) c.Models = m.GetRawText();
            c.UpdatedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(new { combo = c }, JsonOpts);
        });

        g.MapDelete("/combos/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var c = await db.Combos.FindAsync(id);
            if (c is null) return Results.NotFound();
            db.Combos.Remove(c);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true });
        });

        // ---- usage & logs ----
        g.MapGet("/usage", async (LlmRouterDbContext db, string? range) =>
        {
            var since = range == "today" ? DateTime.UtcNow.Date
                : range == "week" ? DateTime.UtcNow.Date.AddDays(-7)
                : range == "month" ? DateTime.UtcNow.Date.AddDays(-30)
                : DateTime.MinValue;
            var s = since.ToString("yyyy-MM-dd HH:mm:ss");
            var rows = await db.UsageHistory.Where(u => string.Compare(u.Timestamp, s) >= 0).ToListAsync();
            static long Tok(UsageRecord r) => long.TryParse(r.Tokens, out var t) ? t : r.PromptTokens + r.CompletionTokens;
            return Results.Json(new
            {
                totalRequests = rows.Count,
                totalTokens = rows.Sum(Tok),
                promptTokens = rows.Sum(r => r.PromptTokens),
                completionTokens = rows.Sum(r => r.CompletionTokens),
                byProvider = rows.GroupBy(r => r.Provider).Select(x => new { provider = x.Key, requests = x.Count(), tokens = x.Sum(Tok) }),
                byModel = rows.GroupBy(r => r.Model).Select(x => new { model = x.Key, requests = x.Count(), tokens = x.Sum(Tok) }),
                errors = rows.Count(r => r.Status != "200"),
            });
        });

        g.MapGet("/usage/daily", async (LlmRouterDbContext db) =>
            Results.Json(new { days = await db.UsageDaily.OrderBy(x => x.DateKey).ToListAsync() }, JsonOpts));

        g.MapGet("/logs", async (LlmRouterDbContext db, int? limit, int? offset, string? status) =>
        {
            var q = db.RequestDetails.AsQueryable();
            if (status is not null) q = q.Where(x => x.Status == status);
            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(x => x.Timestamp)
                .Skip(offset ?? 0).Take(Math.Min(limit ?? 50, 200)).ToListAsync();
            return Results.Json(new { total, logs = rows }, JsonOpts);
        });

        g.MapGet("/logs/{id}", async (string id, LlmRouterDbContext db) =>
            await db.RequestDetails.FindAsync(id) is { } r
                ? Results.Json(new { log = r }, JsonOpts) : Results.NotFound());

        // ---- models (aggregated catalog) ----
        g.MapGet("/models", async (LlmRouterDbContext db, ProviderRegistry r) =>
        {
            var conns = await db.ProviderConnections.Where(c => c.IsActive).ToListAsync();
            var models = conns
                .Select(c => (conn: c, p: r.GetProvider(c.Provider)))
                .Where(t => t.p?.Models is not null)
                .SelectMany(t => t.p!.Models!.Select(m => new
                {
                    id = $"{t.p.Id}/{m.Id}",
                    provider = t.p.Id,
                    model = m.Id,
                    name = m.Name,
                    contextLength = m.ContextLength,
                    capabilities = m.Capabilities,
                    connection = t.conn.Name,
                }));
            return Results.Json(new { models }, JsonOpts);
        });

        // ---- settings ----
        g.MapGet("/settings", async (LlmRouterDbContext db) =>
        {
            var s = await db.Settings.FirstOrDefaultAsync();
            return Results.Json(new { data = AuthEndpoints.Parse(s?.Data) });
        });

        g.MapPut("/settings", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var s = await db.Settings.FirstOrDefaultAsync()
                ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
            var d = AuthEndpoints.Parse(s.Data).Deserialize<Dictionary<string, JsonElement>>() ?? [];
            foreach (var kv in b.EnumerateObject())
                d[kv.Name] = kv.Value.Clone();
            s.Data = JsonSerializer.Serialize(d);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true });
        });

        // ---- endpoint info (for /dashboard/endpoint page) ----
        g.MapGet("/endpoint", (HttpContext ctx) =>
        {
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Json(new
            {
                baseUrl,
                openai = $"{baseUrl}/v1/chat/completions",
                claude = $"{baseUrl}/v1/messages",
                gemini = $"{baseUrl}/v1beta/models",
                responses = $"{baseUrl}/v1/responses",
                models = $"{baseUrl}/v1/models",
            });
        });
    }

    private static string? Get(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

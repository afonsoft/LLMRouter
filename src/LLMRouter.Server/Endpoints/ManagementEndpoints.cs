using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LLMRouter.Server.Endpoints;

/// <summary>Management API consumed by the dashboard (cookie-authenticated).</summary>
public static class ManagementEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapManagementEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- providers catalog ----
        g.MapGet("/providers", async (ProviderRegistry r, LlmRouterDbContext db) =>
        {
            var counts = await db.ProviderConnections
                .GroupBy(c => c.Provider)
                .Select(x => new { Provider = x.Key, Count = x.Count(), Active = x.Count(c => c.IsActive) })
                .ToListAsync();
            var countBy = counts.ToDictionary(x => x.Provider, StringComparer.OrdinalIgnoreCase);
            return Results.Json(new
            {
                categories = r.UiProviders()
                    .GroupBy(t => t.Category)
                    .Select(grp => new
                    {
                        kind = grp.Key,
                        providers = grp.Select(t => new
                        {
                            t.Entry.Id, t.Entry.Alias, t.Entry.Name, t.Entry.Icon, t.Entry.Color,
                            t.Entry.TextIcon, t.Entry.Website, t.Entry.ServiceKinds, t.Entry.Extra,
                            backend = r.GetProvider(t.Entry.Id) is not null,
                            connectionCount = countBy.TryGetValue(t.Entry.Id, out var cc) ? cc.Count : 0,
                            connected = countBy.TryGetValue(t.Entry.Id, out var ca) && ca.Active > 0,
                        }),
                    }),
            }, JsonOpts);
        });

        g.MapGet("/providers/{id}", async (string id, ProviderRegistry r, LlmRouterDbContext db) =>
        {
            var p = r.GetProvider(id);
            if (p is null && NodeResolver.IsNodeProviderId(id))
                p = await NodeResolver.ResolveAsync(db, id);
            if (p is null) return Results.NotFound();
            var conns = await db.ProviderConnections
                .Where(c => c.Provider == p.Id || c.Provider == id)
                .OrderBy(c => c.Priority).ThenBy(c => c.Name).ToListAsync();
            return Results.Json(new { provider = p, ui = r.GetUiProvider(id).Entry, connections = conns }, JsonOpts);
        });

        // Embedded services (UI catalog entries with serviceKinds)
        g.MapGet("/providers/services", (ProviderRegistry r) => Results.Json(new
        {
            services = r.UiProviders()
                .Where(t => t.Entry.ServiceKinds is { Length: > 0 })
                .Select(t => new { t.Entry.Id, t.Entry.Name, t.Entry.Icon, t.Entry.Color, t.Entry.ServiceKinds, category = t.Category }),
        }, JsonOpts));

        // ---- provider nodes (custom endpoints) ----
        g.MapGet("/provider-nodes", async (LlmRouterDbContext db) =>
            Results.Json(new { nodes = await db.ProviderNodes.ToListAsync() }, JsonOpts));

        g.MapPost("/provider-nodes", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var kind = Get(b, "kind") ?? "openai-compatible-chat"; // openai-compatible-{chat|responses} | anthropic-compatible | claude-code
            var uuid = Guid.NewGuid().ToString("N")[..12];
            var id = $"{kind}-{uuid}";
            var n = new ProviderNode
            {
                Id = id,
                Type = kind,
                Name = Get(b, "name") ?? id,
                Data = b.TryGetProperty("data", out var d) ? d.GetRawText() : "{}",
                CreatedAt = Now(), UpdatedAt = Now(),
            };
            db.ProviderNodes.Add(n);
            await db.SaveChangesAsync();
            return Results.Json(new { node = n }, JsonOpts);
        });

        g.MapPut("/provider-nodes/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var n = await db.ProviderNodes.FindAsync(id);
            if (n is null) return Results.NotFound();
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out _)) n.Name = Get(b, "name") ?? n.Name;
            if (b.TryGetProperty("data", out var d)) n.Data = d.GetRawText();
            n.UpdatedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(new { node = n }, JsonOpts);
        });

        g.MapDelete("/provider-nodes/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var n = await db.ProviderNodes.FindAsync(id);
            if (n is null) return Results.NotFound();
            db.ProviderNodes.Remove(n);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true });
        });

        // ---- provider models: registry list + live fetch (5-min cache) ----
        g.MapGet("/provider-models", async (string provider, ProviderRegistry r, LlmRouterDbContext db,
            IHttpClientFactory hf, IMemoryCache cache) =>
        {
            var key = $"provider-models:{provider}";
            if (cache.TryGetValue(key, out object? cached))
                return Results.Json(new { models = cached, cached = true }, JsonOpts);

            var p = r.GetProvider(provider) ?? await NodeResolver.ResolveAsync(db, provider);
            if (p is null) return Results.NotFound();

            var registryModels = (p.Models ?? []).Select(m => new
            {
                id = m.Id, name = m.Name, contextLength = m.ContextLength,
                capabilities = m.Capabilities, source = "registry",
            }).Cast<object>().ToList();

            var modelsUrl = r.GetModelsUrl(p);
            if (modelsUrl is null)
                return Results.Json(new { models = registryModels, live = false }, JsonOpts);

            try
            {
                var baseUrl = (p.BaseUrl ?? "").TrimEnd('/');
                var url = modelsUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? modelsUrl : $"{baseUrl}/{modelsUrl.TrimStart('/')}";
                // first active connection supplies the credential
                var conn = await db.ProviderConnections
                    .Where(c => c.Provider == p.Id && c.IsActive)
                    .OrderBy(c => c.Priority).ThenBy(c => c.Name)
                    .FirstOrDefaultAsync();
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                var secret = conn is null ? null : Core.Gateway.GatewayEngine.ConnectionSecret(conn);
                if (secret is not null)
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
                var resp = await hf.CreateClient("upstream").SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                    return Results.Json(new { models = registryModels, live = false, status = (int)resp.StatusCode }, JsonOpts);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var live = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                    ? data.EnumerateArray().Select(x => (object)new
                    {
                        id = x.TryGetProperty("id", out var i) ? i.GetString() : null,
                        name = x.TryGetProperty("name", out var n) ? n.GetString() : null,
                        source = "live",
                    }).ToList()
                    : [];
                var models = live.Count > 0 ? live : registryModels;
                cache.Set(key, models, TimeSpan.FromMinutes(5));
                return Results.Json(new { models, live = live.Count > 0 }, JsonOpts);
            }
            catch
            {
                return Results.Json(new { models = registryModels, live = false }, JsonOpts);
            }
        });

        // ---- provider connections ----
        g.MapGet("/provider-connections", async (LlmRouterDbContext db) =>
            Results.Json(new { connections = await db.ProviderConnections.OrderBy(c => c.Provider).ThenBy(c => c.Priority).ToListAsync() }, JsonOpts));

        g.MapPost("/provider-connections", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry r) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var provider = b.GetProperty("provider").GetString()!;
            var p = r.GetProvider(provider)
                ?? (NodeResolver.IsNodeProviderId(provider) ? await NodeResolver.ResolveAsync(db, provider) : null);
            if (p is null) return Results.BadRequest(new { error = "unknown provider" });
            var c = new ProviderConnection
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Provider = provider,
                AuthType = Get(b, "authType") ?? p.AuthType,
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
            var p = r.GetProvider(c.Provider) ?? await NodeResolver.ResolveAsync(db, c.Provider);
            if (p is null) return Results.BadRequest(new { error = "unknown provider" });
            var connBase = c.Data.Contains("baseUrl")
                ? JsonDocument.Parse(c.Data).RootElement.GetProperty("baseUrl").GetString() : null;
            var baseUrl = (connBase ?? p.BaseUrl ?? "https://api.openai.com").TrimEnd('/');
            var rawModels = r.GetModelsUrl(p);
            // connection-level baseUrl override wins over the provider's default
            var modelsUrl = connBase is not null
                ? $"{baseUrl}/{(rawModels is { } rp && !rp.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? rp.TrimStart('/') : "v1/models")}"
                : rawModels is { } mu
                    ? (mu.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? mu : $"{baseUrl}/{mu.TrimStart('/')}")
                    : $"{baseUrl}/v1/models";
            var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            if (Core.Gateway.GatewayEngine.ConnectionSecret(c) is { } secret)
            {
                var authHeaders = new Dictionary<string, string>();
                Core.Gateway.GatewayEngine.ApplyAuth(authHeaders, p, secret);
                foreach (var kv in authHeaders) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string status; string? errBody = null; int httpStatus = 0;
            try
            {
                var resp = await hf.CreateClient("upstream").SendAsync(req);
                httpStatus = (int)resp.StatusCode;
                status = resp.IsSuccessStatusCode ? "ok"
                    : httpStatus is 401 or 403 ? "auth-fail" : "error";
                var text = await resp.Content.ReadAsStringAsync();
                errBody = text[..Math.Min(2000, text.Length)];
            }
            catch (Exception ex)
            {
                status = "network";
                errBody = ex.Message;
            }
            sw.Stop();
            // persist the last test result on the connection row
            var dataEl = AuthEndpoints.Parse(c.Data).Deserialize<Dictionary<string, JsonElement>>() ?? [];
            dataEl["testStatus"] = JsonSerializer.SerializeToElement(status);
            dataEl["latencyMs"] = JsonSerializer.SerializeToElement(sw.ElapsedMilliseconds);
            dataEl["lastTestAt"] = JsonSerializer.SerializeToElement(Now());
            if (errBody is not null) dataEl["lastError"] = JsonSerializer.SerializeToElement(errBody[..Math.Min(500, errBody.Length)]);
            c.Data = JsonSerializer.Serialize(dataEl);
            c.UpdatedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = status == "ok", status, httpStatus, latencyMs = sw.ElapsedMilliseconds, error = errBody }, JsonOpts);
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
        static object ComboDto(Combo c) => new
        {
            c.Id, c.Name, c.Kind, c.CreatedAt, c.UpdatedAt,
            models = JsonDocument.Parse(c.Models).RootElement.Clone(),
        };
        g.MapGet("/combos", async (LlmRouterDbContext db) =>
            Results.Json(new { combos = (await db.Combos.ToListAsync()).Select(ComboDto) }, JsonOpts));

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

        // ---- models (aggregated catalog; ?live=true fetches upstream model lists) ----
        g.MapGet("/models", async (LlmRouterDbContext db, ProviderRegistry r, bool live,
            IHttpClientFactory hf, IMemoryCache cache) =>
        {
            var conns = await db.ProviderConnections.Where(c => c.IsActive).ToListAsync();
            var models = new List<object>();
            foreach (var conn in conns)
            {
                var p = r.GetProvider(conn.Provider) ?? await NodeResolver.ResolveAsync(db, conn.Provider);
                if (p is null) continue;
                List<(string id, string? name, int? ctx, string[]? caps)> list = (p.Models ?? [])
                    .Select(m => (m.Id, m.Name, m.ContextLength, m.Capabilities)).ToList();
                if (live && r.GetModelsUrl(p) is { } murl)
                {
                    var cacheKey = $"live-models:{conn.Id}";
                    if (cache.TryGetValue(cacheKey, out object? cl) && cl is List<(string, string?, int?, string[]?)> cachedList)
                    {
                        list = cachedList;
                    }
                    else try
                    {
                        var url = murl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? murl : $"{(conn.Data.Contains("baseUrl") ? JsonDocument.Parse(conn.Data).RootElement.GetProperty("baseUrl").GetString() : p.BaseUrl)?.TrimEnd('/')}/{murl.TrimStart('/')}";
                        var req = new HttpRequestMessage(HttpMethod.Get, url);
                        if (Core.Gateway.GatewayEngine.ConnectionSecret(conn) is { } s)
                            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {s}");
                        var resp = await hf.CreateClient("upstream").SendAsync(req);
                        if (resp.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                            if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                            {
                                list = d.EnumerateArray()
                                    .Select(x => ((string)(x.TryGetProperty("id", out var i) ? i.GetString() ?? "" : ""), (string?)null, (int?)null, (string[]?)null))
                                    .Where(t => t.Item1.Length > 0).ToList();
                                cache.Set(cacheKey, list, TimeSpan.FromMinutes(5));
                            }
                        }
                    }
                    catch { }
                }
                foreach (var m in list)
                    models.Add(new
                    {
                        id = $"{p.Id}/{m.id}", provider = p.Id, model = m.id,
                        name = m.name, contextLength = m.ctx, capabilities = m.caps,
                        connection = conn.Name,
                    });
            }
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

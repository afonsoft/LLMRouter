using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Usage;
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
            await Core.Extras.Extras.AuditAsync(db, "connection.create", $"{c.Provider} ({c.Name})");
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
            await Core.Extras.Extras.AuditAsync(db, "connection.update", $"{c.Provider} ({c.Id})");
            return Results.Json(new { connection = c }, JsonOpts);
        });

        g.MapDelete("/provider-connections/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var c = await db.ProviderConnections.FindAsync(id);
            if (c is null) return Results.NotFound();
            db.ProviderConnections.Remove(c);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "connection.delete", $"{c.Provider} ({c.Id})");
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
            await Core.Extras.Extras.AuditAsync(db, "apikey.create", k.Name ?? k.Id);
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
        /// <summary>Push settings.data.resilience into the static CooldownTracker (SPEC-007).</summary>
        static void ApplyResilienceSettings(Dictionary<string, JsonElement> d)
        {
            if (!d.TryGetValue("resilience", out var r) || r.ValueKind != JsonValueKind.Object) return;
            int? th = r.TryGetProperty("failureThreshold", out var t1) && t1.TryGetInt32(out var i1) ? i1 : null;
            double? bs = r.TryGetProperty("cooldownBaseSeconds", out var t2) && t2.TryGetDouble(out var d2) ? d2 : null;
            double? ms = r.TryGetProperty("cooldownMaxSeconds", out var t3) && t3.TryGetDouble(out var d3) ? d3 : null;
            LLMRouter.Core.Resilience.CooldownTracker.Configure(th, bs, ms);
        }

        static object ComboDto(Combo c) => new
        {
            c.Id, c.Name, c.Kind, c.StickyLimit, c.CreatedAt, c.UpdatedAt,
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
                StickyLimit = b.TryGetProperty("stickyLimit", out var sl) && sl.ValueKind == JsonValueKind.Number
                    ? Math.Max(1, sl.GetInt32()) : 1,
                Models = b.TryGetProperty("models", out var m) ? m.GetRawText() : "[]",
                CreatedAt = Now(), UpdatedAt = Now(),
            };
            db.Combos.Add(c);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "combo.create", c.Name);
            return Results.Json(new { combo = c }, JsonOpts);
        });

        g.MapPut("/combos/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var c = await db.Combos.FindAsync(id);
            if (c is null) return Results.NotFound();
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out _)) c.Name = Get(b, "name") ?? c.Name;
            if (b.TryGetProperty("kind", out _)) c.Kind = Get(b, "kind");
            if (b.TryGetProperty("stickyLimit", out var sl) && sl.ValueKind == JsonValueKind.Number)
                c.StickyLimit = Math.Max(1, sl.GetInt32());
            if (b.TryGetProperty("models", out var m)) c.Models = m.GetRawText();
            // strategy change resets the rotation cursor
            ComboPlanner.ResetRotation(c.Name);
            c.UpdatedAt = Now();
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "combo.update", c.Name);
            return Results.Json(new { combo = c }, JsonOpts);
        });

        g.MapDelete("/combos/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var c = await db.Combos.FindAsync(id);
            if (c is null) return Results.NotFound();
            db.Combos.Remove(c);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "combo.delete", c.Name);
            return Results.Json(new { success = true });
        });

        // combo live stats (Combo Studio): which model served recent requests,
        // rotation index, per-model success rate — from usageHistory/requestDetails
        g.MapGet("/combos/{id}/live", async (string id, LlmRouterDbContext db) =>
        {
            var c = await db.Combos.FindAsync(id);
            if (c is null) return Results.NotFound();
            var models = JsonSerializer.Deserialize<List<string>>(c.Models) ?? [];
            var modelIds = models.Select(m => m.Contains('/') ? m[(m.IndexOf('/') + 1)..] : m).ToHashSet();
            var rows = await db.UsageHistory
                .Where(u => u.Model != null && modelIds.Contains(u.Model))
                .OrderByDescending(u => u.Timestamp).Take(200).ToListAsync();
            var perModel = models.Select(m => new
            {
                model = m,
                requests = rows.Count(r => (r.Provider + "/" + r.Model) == m || r.Model == m),
                successes = rows.Count(r => ((r.Provider + "/" + r.Model) == m || r.Model == m)
                    && r.Status == "200"),
                lastUsed = rows.Where(r => (r.Provider + "/" + r.Model) == m || r.Model == m)
                    .Select(r => r.Timestamp).FirstOrDefault(),
            });
            var recent = rows.Take(20).Select(r => new
            {
                r.Timestamp, provider = r.Provider, r.Model, r.Status,
            });
            return Results.Json(new { combo = c, perModel, recent }, JsonOpts);
        });

        // ---- model aliases (settings.data.modelAliases) ----
        g.MapGet("/model-aliases", async (LlmRouterDbContext db) =>
        {
            var s = await db.Settings.FirstOrDefaultAsync();
            var map = new Dictionary<string, string>();
            if (s is not null)
            {
                var el = JsonDocument.Parse(s.Data).RootElement;
                if (el.TryGetProperty("modelAliases", out var a))
                    foreach (var kv in a.EnumerateObject())
                        map[kv.Name] = kv.Value.GetString() ?? "";
            }
            return Results.Json(new { aliases = map }, JsonOpts);
        });

        g.MapPut("/model-aliases", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var s = await db.Settings.FirstOrDefaultAsync();
            if (s is null) { s = new SettingRow { Data = "{}" }; db.Settings.Add(s); }
            var data = JsonNode.Parse(s.Data)!.AsObject();
            var aliases = new JsonObject();
            if (b.TryGetProperty("aliases", out var a))
                foreach (var kv in a.EnumerateObject())
                    aliases[kv.Name] = kv.Value.GetString();
            data["modelAliases"] = aliases;
            s.Data = data.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { success = true });
        });

        // ---- model-combo mappings (kv scope modelComboMappings) ----
        g.MapGet("/model-combo-mappings", async (LlmRouterDbContext db) =>
        {
            var rows = await db.Kv.Where(k => k.Scope == "modelComboMappings").ToListAsync();
            return Results.Json(new { mappings = rows.ToDictionary(k => k.Key, k => k.Value) }, JsonOpts);
        });

        g.MapPost("/model-combo-mappings", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var model = b.GetProperty("model").GetString()!;
            var combo = b.GetProperty("combo").GetString()!;
            var existing = await db.Kv.FindAsync("modelComboMappings", model);
            if (existing is null)
                db.Kv.Add(new KvEntry { Scope = "modelComboMappings", Key = model, Value = combo });
            else
                existing.Value = combo;
            await db.SaveChangesAsync();
            return Results.Json(new { success = true });
        });

        g.MapDelete("/model-combo-mappings/{model}", async (string model, LlmRouterDbContext db) =>
        {
            var e = await db.Kv.FindAsync("modelComboMappings", model);
            if (e is not null) { db.Kv.Remove(e); await db.SaveChangesAsync(); }
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

        // SPEC-005: timeseries (per-hour today / per-day week+month+all)
        g.MapGet("/usage/timeseries", async (LlmRouterDbContext db, string? range) =>
        {
            var (since, hourly) = range == "today"
                ? (DateTime.UtcNow.Date, true)
                : (range == "week" ? DateTime.UtcNow.Date.AddDays(-7)
                    : range == "month" ? DateTime.UtcNow.Date.AddDays(-30)
                    : DateTime.MinValue, false);
            var s = since.ToString("yyyy-MM-dd HH:mm:ss");
            var rows = await db.UsageHistory.Where(u => string.Compare(u.Timestamp, s) >= 0).ToListAsync();
            var buckets = rows
                .GroupBy(r => hourly ? r.Timestamp[..13] : r.Timestamp[..10])
                .OrderBy(x => x.Key)
                .Select(x => new
                {
                    bucket = x.Key,
                    requests = x.Count(),
                    tokens = x.Sum(r => long.TryParse(r.Tokens, out var t) ? t : r.PromptTokens + r.CompletionTokens),
                    cost = x.Sum(r => r.Cost),
                    errors = x.Count(r => r.Status != "200"),
                });
            return Results.Json(new { hourly, buckets }, JsonOpts);
        });

        // SPEC-005: per-provider stats incl. p50/p95 latency and error rate
        g.MapGet("/provider-stats", async (LlmRouterDbContext db, string? range) =>
        {
            var since = range == "today" ? DateTime.UtcNow.Date
                : range == "week" ? DateTime.UtcNow.Date.AddDays(-7)
                : range == "month" ? DateTime.UtcNow.Date.AddDays(-30)
                : DateTime.MinValue;
            var s = since.ToString("yyyy-MM-dd HH:mm:ss");
            var rows = await db.UsageHistory.Where(u => string.Compare(u.Timestamp, s) >= 0).ToListAsync();
            static double P(List<long> v, double q) => v.Count == 0 ? 0
                : v.Order().ElementAt(Math.Min(v.Count - 1, (int)Math.Ceiling(q * v.Count) - 1));
            return Results.Json(new
            {
                providers = rows.GroupBy(r => r.Provider ?? "?").Select(x =>
                {
                    var lat = x.Select(r => r.LatencyMs).Where(l => l > 0).ToList();
                    var errs = x.Count(r => r.Status != "200");
                    return new
                    {
                        provider = x.Key,
                        requests = x.Count(),
                        errors = errs,
                        errorRate = x.Count() == 0 ? 0 : Math.Round(100.0 * errs / x.Count(), 1),
                        p50 = P(lat, 0.5), p95 = P(lat, 0.95),
                        avgLatencyMs = lat.Count == 0 ? 0 : Math.Round(lat.Average()),
                        cost = x.Sum(r => r.Cost),
                    };
                }),
            }, JsonOpts);
        });

        // SPEC-005: pricing catalog (registry + user overrides)
        g.MapGet("/pricing", async (LlmRouterDbContext db, ProviderRegistry r) =>
        {
            var sd = await PricingService.SettingsDataAsync(db);
            var overrides = new Dictionary<string, object>();
            if (sd.ValueKind == JsonValueKind.Object
                && sd.TryGetProperty(PricingService.OverridesKey, out var ov)
                && ov.ValueKind == JsonValueKind.Object)
                foreach (var kv in ov.EnumerateObject())
                    overrides[kv.Name] = kv.Value;
            var catalog = r.UiProviders().SelectMany(t =>
                (r.GetProvider(t.Entry.Id)?.Models ?? []).Select(m => new
                {
                    provider = t.Entry.Id, model = m.Id,
                    price = m.Pricing is null ? null : m.Pricing,
                    hasOverride = overrides.ContainsKey($"{t.Entry.Id}/{m.Id}"),
                }));
            return Results.Json(new { catalog, overrides }, JsonOpts);
        });

        g.MapPut("/pricing/{provider}/{model}", async (string provider, string model,
            HttpContext ctx, LlmRouterDbContext db) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var sd = await PricingService.SettingsDataAsync(db);
            var root = sd.ValueKind == JsonValueKind.Object
                ? sd.EnumerateObject().ToDictionary(k => k.Name, k => k.Value.Clone())
                : new Dictionary<string, JsonElement>();
            var ov = root.TryGetValue(PricingService.OverridesKey, out var o) && o.ValueKind == JsonValueKind.Object
                ? o.EnumerateObject().ToDictionary(k => k.Name, k => k.Value.Clone())
                : new Dictionary<string, JsonElement>();
            var key = $"{provider}/{model}";
            if (body.ValueKind == JsonValueKind.Null || (body.ValueKind == JsonValueKind.Object && !body.EnumerateObject().Any()))
                ov.Remove(key); // empty body = clear override
            else
                ov[key] = body.Clone();
            root[PricingService.OverridesKey] = JsonSerializer.SerializeToElement(ov);
            var row = await db.Settings.FindAsync(1) ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
            row.Data = JsonSerializer.Serialize(root);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true, key });
        });

        // SPEC-005: costs summary + monthly budget
        g.MapGet("/costs", async (LlmRouterDbContext db) =>
        {
            var monthStart = DateTime.UtcNow.Date.AddDays(1 - DateTime.UtcNow.Day).ToString("yyyy-MM-dd");
            var rows = await db.UsageHistory.Where(u => string.Compare(u.Timestamp, monthStart) >= 0).ToListAsync();
            var sd = await PricingService.SettingsDataAsync(db);
            double budget = sd.ValueKind == JsonValueKind.Object
                && sd.TryGetProperty("monthlyBudget", out var b) && b.ValueKind == JsonValueKind.Number
                ? b.GetDouble() : 0;
            return Results.Json(new
            {
                monthCost = rows.Sum(r => r.Cost),
                monthTokens = rows.Sum(r => r.PromptTokens + r.CompletionTokens),
                monthRequests = rows.Count,
                byProvider = rows.GroupBy(r => r.Provider).Select(x => new
                { provider = x.Key, cost = x.Sum(r => r.Cost), requests = x.Count() }),
                byModel = rows.GroupBy(r => (r.Provider ?? "") + "/" + (r.Model ?? "")).Select(x => new
                { model = x.Key, cost = x.Sum(r => r.Cost), requests = x.Count() })
                    .OrderByDescending(x => x.cost).Take(20),
                budget,
            }, JsonOpts);
        });

        g.MapPut("/costs/budget", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var sd = await PricingService.SettingsDataAsync(db);
            var root = sd.ValueKind == JsonValueKind.Object
                ? sd.EnumerateObject().ToDictionary(k => k.Name, k => k.Value.Clone())
                : new Dictionary<string, JsonElement>();
            root["monthlyBudget"] = body.TryGetProperty("budget", out var b) ? b.Clone() : JsonSerializer.SerializeToElement(0);
            var row = await db.Settings.FindAsync(1) ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
            row.Data = JsonSerializer.Serialize(root);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true });
        });

        // SPEC-005: activity feed = recent requestDetails
        g.MapGet("/activity", async (LlmRouterDbContext db, int? limit) =>
        {
            var rows = await db.RequestDetails.OrderByDescending(x => x.Timestamp)
                .Take(Math.Min(limit ?? 50, 200)).ToListAsync();
            return Results.Json(new { events = rows }, JsonOpts);
        });

        g.MapGet("/logs", async (LlmRouterDbContext db, int? limit, int? offset,
            string? status, string? provider, string? model, string? since) =>
        {
            var q = db.RequestDetails.AsQueryable();
            if (status is not null) q = q.Where(x => x.Status == status);
            if (provider is not null) q = q.Where(x => x.Provider == provider);
            if (model is not null) q = q.Where(x => x.Model == model);
            if (since is not null) q = q.Where(x => string.Compare(x.Timestamp, since) >= 0);
            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(x => x.Timestamp)
                .Skip(offset ?? 0).Take(Math.Min(limit ?? 50, 200)).ToListAsync();
            return Results.Json(new { total, logs = rows }, JsonOpts);
        });

        g.MapGet("/logs/{id}", async (string id, LlmRouterDbContext db) =>
            await db.RequestDetails.FindAsync(id) is { } r
                ? Results.Json(new { log = r }, JsonOpts) : Results.NotFound());

        // SPEC-006: console ring buffer + SSE live tail
        g.MapGet("/console", (int? tail) =>
            Results.Json(new { lines = Services.ConsoleLogBuffer.Instance.Recent(tail ?? 200) }, JsonOpts));

        g.MapGet("/logs/stream", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var reader = Services.ConsoleLogBuffer.Instance.Stream;
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                if (await reader.WaitToReadAsync(ctx.RequestAborted))
                    while (reader.TryRead(out var line))
                        await ctx.Response.WriteAsync($"data: {line}\n\n", ctx.RequestAborted);
            }
        });

        // SPEC-006: log export (jsonl | csv)
        g.MapGet("/log-export", async (LlmRouterDbContext db, string? fmt, int? limit) =>
        {
            var rows = await db.RequestDetails.OrderByDescending(x => x.Timestamp)
                .Take(Math.Min(limit ?? 1000, 10000)).ToListAsync();
            if (fmt == "csv")
            {
                var sb = new System.Text.StringBuilder("id,timestamp,provider,model,status\n");
                foreach (var r in rows)
                    sb.AppendLine($"{r.Id},{r.Timestamp},{r.Provider},{r.Model},{r.Status}");
                return Results.Text(sb.ToString(), "text/csv");
            }
            var lines = string.Join('\n', rows.Select(r => JsonSerializer.Serialize(r, JsonOpts)));
            return Results.Text(lines, "application/x-ndjson");
        });

        // SPEC-006: per-connection health (last test + cooldown state)
        g.MapGet("/health/connections", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections.ToListAsync();
            var cooling = Core.Resilience.CooldownTracker.Snapshot()
                .ToDictionary(x => x.ConnectionId, x => x);
            return Results.Json(new
            {
                connections = conns.Select(c =>
                {
                    var data = JsonDocument.Parse(c.Data).RootElement;
                    cooling.TryGetValue(c.Id, out var cd);
                    return new
                    {
                        c.Id, c.Provider, c.Name, c.IsActive,
                        testStatus = data.TryGetProperty("testStatus", out var ts) ? ts.GetString() : null,
                        lastError = data.TryGetProperty("lastError", out var le) ? le.GetString() : null,
                        cooldown = cd,
                    };
                }),
            }, JsonOpts);
        });

        // SPEC-006: runtime info
        g.MapGet("/runtime", (LlmRouterDbContext db, IWebHostEnvironment env) =>
        {
            var dbPath = db.Database.GetDbConnection().DataSource;
            return Results.Json(new
            {
                version = "0.1.0",
                started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o"),
                uptimeSeconds = (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                dbSizeBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0,
                gcMemoryMb = Math.Round(GC.GetTotalMemory(false) / 1e6, 1),
                environment = env.EnvironmentName,
            }, JsonOpts);
        });

        // SPEC-006: resilience — cooldown list + clear
        g.MapGet("/resilience/cooldowns", () =>
            Results.Json(new { cooldowns = Core.Resilience.CooldownTracker.Snapshot() }, JsonOpts));

        // SPEC-021: provider circuit breakers + model lockouts
        g.MapGet("/resilience/breakers", () =>
            Results.Json(new { breakers = Core.Resilience.ProviderBreaker.Snapshot() }, JsonOpts));
        g.MapDelete("/resilience/breakers/{id}", (string id) =>
        {
            Core.Resilience.ProviderBreaker.Clear(id);
            return Results.Json(new { ok = true });
        });
        g.MapGet("/resilience/lockouts", () =>
            Results.Json(new { lockouts = Core.Resilience.ModelLockout.Snapshot() }, JsonOpts));

        g.MapDelete("/resilience/cooldowns/{id}", (string id) =>
        {
            Core.Resilience.CooldownTracker.Clear(id);
            return Results.Json(new { ok = true });
        });

        // SPEC-006: retention — prune requestDetails beyond cap
        g.MapPost("/logs/prune", async (LlmRouterDbContext db, int? keep) =>
        {
            var keepN = keep ?? 5000;
            var stale = await db.RequestDetails.OrderByDescending(x => x.Timestamp)
                .Skip(keepN).ToListAsync();
            db.RequestDetails.RemoveRange(stale);
            var staleUsage = await db.UsageHistory.OrderByDescending(x => x.Timestamp)
                .Skip(keepN * 4).ToListAsync();
            db.UsageHistory.RemoveRange(staleUsage);
            await db.SaveChangesAsync();
            return Results.Json(new { removed = stale.Count + staleUsage.Count });
        });

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
            ApplyResilienceSettings(d);
            await Core.Extras.Extras.AuditAsync(db, "settings.update", string.Join(",", d.Keys));
            return Results.Json(new { success = true });
        });

        // SPEC-007: dangerous zone — wipe all settings.data
        g.MapPost("/settings/reset", async (LlmRouterDbContext db) =>
        {
            var s = await db.Settings.FirstOrDefaultAsync();
            if (s is not null) { s.Data = "{}"; await db.SaveChangesAsync(); }
            return Results.Json(new { success = true });
        });

        // SPEC-007: export settings.data as downloadable JSON
        g.MapGet("/settings/export", async (LlmRouterDbContext db) =>
        {
            var s = await db.Settings.FirstOrDefaultAsync();
            return Results.Text(s?.Data ?? "{}", "application/json");
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

    /// <summary>SPEC-008: quota, proxy pools, token-saver stats.</summary>
    public static void MapQuotaProxyEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/quota", async (LlmRouterDbContext db) =>
        {
            var list = new List<object>();
            foreach (var c in await db.ProviderConnections.Where(x => x.IsActive).ToListAsync())
            {
                var st = await Core.Routing.QuotaTracker.StateAsync(db, c);
                list.Add(new
                {
                    connectionId = c.Id, c.Provider, c.Name,
                    daily = new { used = st.DailyUsed, limit = st.DailyLimit, exhausted = st.DailyExhausted },
                    monthly = new { used = st.MonthlyUsed, limit = st.MonthlyLimit, exhausted = st.MonthlyExhausted },
                });
            }
            return Results.Json(new { quota = list }, JsonOpts);
        });

        // ---- proxy pools ----
        g.MapGet("/proxy-pools", async (LlmRouterDbContext db) =>
        {
            var pools = await db.ProxyPools.ToListAsync();
            return Results.Json(new
            {
                pools = pools.Select(p => new
                {
                    p.Id, p.IsActive, p.TestStatus,
                    data = JsonDocument.Parse(p.Data).RootElement,
                    proxies = Core.Routing.ProxyPoolService.Proxies(p),
                    p.CreatedAt, p.UpdatedAt,
                }),
            }, JsonOpts);
        });

        g.MapPost("/proxy-pools", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var data = b.TryGetProperty("data", out var d) ? d.GetRawText()
                : JsonSerializer.Serialize(new { name = Get(b, "name") ?? "pool", proxies = b.TryGetProperty("proxies", out var px) ? px : JsonSerializer.SerializeToElement(Array.Empty<object>()) });
            var p = new ProxyPool { Id = Guid.NewGuid().ToString("N")[..12], Data = data, CreatedAt = Now(), UpdatedAt = Now() };
            db.ProxyPools.Add(p);
            await db.SaveChangesAsync();
            return Results.Json(new { pool = p }, JsonOpts);
        });

        g.MapPut("/proxy-pools/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var p = await db.ProxyPools.FindAsync(id);
            if (p is null) return Results.NotFound();
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("isActive", out var a)) p.IsActive = a.GetBoolean();
            if (b.TryGetProperty("data", out var d)) p.Data = d.GetRawText();
            if (b.TryGetProperty("testStatus", out var ts)) p.TestStatus = ts.GetString();
            p.UpdatedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(new { pool = p }, JsonOpts);
        });

        g.MapDelete("/proxy-pools/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var p = await db.ProxyPools.FindAsync(id);
            if (p is null) return Results.NotFound();
            db.ProxyPools.Remove(p);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true });
        });

        // test each proxy in the pool against a target URL, store latency/status
        g.MapPost("/proxy-pools/{id}/test", async (string id, string? target, LlmRouterDbContext db) =>
        {
            var p = await db.ProxyPools.FindAsync(id);
            if (p is null) return Results.NotFound();
            var url = target ?? "https://api.ipify.org?format=json";
            var results = new List<object>();
            var proxies = Core.Routing.ProxyPoolService.Proxies(p).ToList();
            var updated = false;
            foreach (var px in proxies)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string status;
                try
                {
                    var handler = new SocketsHttpHandler { Proxy = new System.Net.WebProxy(px.Url), UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(10) };
                    using var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                    using var r = await c.GetAsync(url);
                    status = r.IsSuccessStatusCode ? "ok" : $"http {(int)r.StatusCode}";
                }
                catch (Exception ex) { status = ex.GetType().Name; }
                sw.Stop();
                updated = true;
                results.Add(new { proxy = px, latencyMs = sw.ElapsedMilliseconds, status });
            }
            if (updated)
            {
                // persist latencies back into data.proxies
                var d = JsonDocument.Parse(p.Data).RootElement;
                var obj = d.EnumerateObject().ToDictionary(k => k.Name, k => k.Value.Clone());
                var arr = proxies.Select(px =>
                {
                    var r = results.First(x => ((Core.Routing.ProxyPoolService.PoolProxy)x.GetType().GetProperty("proxy")!.GetValue(x)!).Id == px.Id);
                    return new Dictionary<string, object?>
                    {
                        ["id"] = px.Id, ["url"] = px.Url, ["active"] = px.Active,
                        ["failCount"] = px.FailCount, ["latencyMs"] = r.GetType().GetProperty("latencyMs")!.GetValue(r),
                        ["status"] = r.GetType().GetProperty("status")!.GetValue(r),
                    };
                }).ToList();
                obj["proxies"] = JsonSerializer.SerializeToElement(arr);
                p.Data = JsonSerializer.Serialize(obj);
                p.TestStatus = results.All(r => (string)r.GetType().GetProperty("status")!.GetValue(r)! == "ok") ? "ok" : "degraded";
                p.UpdatedAt = Now();
                await db.SaveChangesAsync();
            }
            return Results.Json(new { results }, JsonOpts);
        });

        // ---- token saver stats ----
        g.MapGet("/token-saver/stats", async (LlmRouterDbContext db) =>
        {
            var saved = 0; var requests = 0;
            foreach (var u in await db.UsageHistory.Where(x => x.Meta != null).ToListAsync())
            {
                try
                {
                    var m = JsonDocument.Parse(u.Meta!).RootElement;
                    if (m.TryGetProperty("tokensSaved", out var t) && t.TryGetInt32(out var n))
                    { saved += n; requests++; }
                }
                catch { }
            }
            return Results.Json(new { tokensSaved = saved, requestsCompressed = requests }, JsonOpts);
        });
    }

    private static string? Get(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

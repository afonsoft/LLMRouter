using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-089: /v1 leftovers — responses extras, agents mirror, relay bifrost,
/// registered-keys detail, batches housekeeping, vscode raw/combos mirrors,
/// management proxies + proxy-subscriptions.
/// </summary>
public static class V1LeftoversEndpoints
{
    private const string Json = "application/json";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        foreach (var prefix in new[] { "/v1", "/api/v1" })
        {
            var g = app.MapGroup(prefix);
            // local preflight — never forwarded upstream (upstream parity).
            g.MapMethods("/responses/input_tokens", ["POST"], GatewayEndpoints.CountTokens);
            g.MapMethods("/responses/{*path}", ["GET", "POST", "PUT", "DELETE", "PATCH"],
                (HttpContext c, string path) => GatewayEndpoints.Passthrough(c, upstreamPath: $"/responses/{path}"));
            g.MapMethods("/relay/chat/completions/bifrost", ["POST"], BifrostRelay);

            g.MapGet("/agents/health", AgentsHealth);
            g.MapGet("/agents/tasks", AgentsTasks);
            g.MapGet("/agents/tasks/{id}", AgentsTaskItem);
            g.MapGet("/agents/credentials", AgentsCredentials);

            g.MapGet("/registered-keys/{providerId}", RegisteredKeyDetail);
            g.MapMethods("/registered-keys/{providerId}/revoke", ["POST"], RegisteredKeyRevoke);

            g.MapMethods("/batches/delete-completed", ["POST"], BatchesDeleteCompleted);

            g.MapGet("/management/proxies/assignments", ProxyAssignmentsGet);
            g.MapMethods("/management/proxies/assignments", ["POST"], ProxyAssignmentsSet);
            g.MapMethods("/management/proxies/bulk-assign", ["POST"], ProxyBulkAssign);
            g.MapGet("/management/proxies/health", ProxyHealth);
            g.MapGet("/management/proxy-subscriptions/{p}", ProxySubscriptionGet);
            g.MapMethods("/management/proxy-subscriptions/{p}", ["PUT"], ProxySubscriptionPut);
            g.MapGet("/management/proxy-subscriptions/{p}/nodes", ProxySubscriptionNodes);
            g.MapMethods("/management/proxy-subscriptions/{p}/refresh", ["POST"], ProxySubscriptionRefresh);
        }

        // vscode mirrors — raw skips combo expansion; combos forces it (existing ScopedChat).
        app.MapMethods("/v1/vscode/raw/{token}/chat/completions", ["POST"],
            (HttpContext c, string token) => VscodeEndpoints.ScopedChat(c, token, "openai", rawModel: true));
        app.MapMethods("/v1/vscode/combos/{token}/chat/completions", ["POST"],
            (HttpContext c, string token) => VscodeEndpoints.ScopedChat(c, token, "openai", rawModel: false));
        app.MapGet("/v1/models/{*path}", ModelCard);
        app.MapGet("/api/v1/models/{*path}", ModelCard);
    }

    private static async Task<JsonElement?> Body(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static async Task WriteJson(HttpContext ctx, object body, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = Json;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOpts), ctx.RequestAborted);
    }

    private static async Task<string?> ApiKeyOr401(HttpContext ctx, LlmRouterDbContext db)
    {
        var (key, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return null; }
        return key;
    }

    // ---- relay bifrost: same auth as relay, verbatim proxy to the Go sidecar ----

    private static async Task BifrostRelay(HttpContext ctx, LlmRouterDbContext db,
        IHttpClientFactory hf, IConfiguration cfg)
    {
        var ct = ctx.RequestAborted;
        var baseUrl = cfg["BIFROST_BASE_URL"] ?? cfg["Bifrost:BaseUrl"];
        var key = ctx.Request.Headers.Authorization.FirstOrDefault() is { } a
            && a.StartsWith("Bearer ", StringComparison.Ordinal) ? a[7..].Trim() : null;
        if (string.IsNullOrEmpty(key)) { await WriteJson(ctx, new { error = new { message = "Missing relay token.", type = "invalid_api_key" } }, 401); return; }
        var tok = await db.RelayTokens.FirstOrDefaultAsync(t => t.Token == key, ct);
        if (tok is null || !tok.IsActive)
        { await WriteJson(ctx, new { error = new { message = "Invalid or revoked relay token.", type = "invalid_api_key" } }, 401); return; }
        if (string.IsNullOrEmpty(baseUrl))
        {
            ctx.Response.Headers["X-Bifrost-Fallback"] = "/api/v1/relay/chat/completions";
            await WriteJson(ctx, new { error = new { message = "Bifrost sidecar not configured.", type = "bifrost_unavailable" } }, 503);
            return;
        }

        var raw = await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/chat/completions")
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(raw)) };
        req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
            ctx.Request.ContentType ?? Json);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            using var resp = await hf.CreateClient("upstream").SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? Json;
            if (resp.Headers.TryGetValues("x-bifrost-fallback", out var fb))
                ctx.Response.Headers["X-Bifrost-Fallback"] = fb.First();
            await resp.Content.CopyToAsync(ctx.Response.Body, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { ctx.Response.StatusCode = 504; }
        catch (HttpRequestException)
        {
            ctx.Response.Headers["X-Bifrost-Fallback"] = "/api/v1/relay/chat/completions";
            ctx.Response.StatusCode = 502;
        }
    }

    // ---- agents mirror ----

    private static async Task AgentsHealth(HttpContext ctx, LlmRouterDbContext db)
    {
        if (await ApiKeyOr401(ctx, db) is null) return;
        var ct = ctx.RequestAborted;
        var agents = await db.A2aTasks
            .Where(t => t.Agent != null)
            .Select(t => new { t.Agent, t.State })
            .ToListAsync(ct);
        var running = await db.ConductorTasks.CountAsync(t => t.State == "running", ct);
        var byAgent = agents
            .GroupBy(a => a.Agent!)
            .Select(g2 => new { id = g2.Key, status = g2.Any(x => x.State == "running") ? "running" : "idle" })
            .ToList();
        await WriteJson(ctx, new { ok = true, conductor = running > 0 ? "running" : "idle", agents = byAgent });
    }

    private static async Task AgentsTasks(HttpContext ctx, LlmRouterDbContext db)
    {
        if (await ApiKeyOr401(ctx, db) is null) return;
        var rows = await db.A2aTasks.OrderByDescending(t => t.CreatedAt).Take(200).ToListAsync(ctx.RequestAborted);
        await WriteJson(ctx, new
        { tasks = rows.Select(t => new { t.Id, t.Agent, t.State, t.CreatedAt, t.StartedAt, t.FinishedAt }) });
    }

    private static async Task AgentsTaskItem(HttpContext ctx, LlmRouterDbContext db, string id)
    {
        if (await ApiKeyOr401(ctx, db) is null) return;
        var t = await db.A2aTasks.FindAsync([id], ctx.RequestAborted);
        if (t is null) { await WriteJson(ctx, new { error = "not found" }, 404); return; }
        await WriteJson(ctx, new
        { t.Id, t.Agent, t.State, t.Payload, t.Result, t.Error, t.CreatedAt, t.StartedAt, t.FinishedAt });
    }

    private static async Task AgentsCredentials(HttpContext ctx, LlmRouterDbContext db)
    {
        if (await ApiKeyOr401(ctx, db) is null) return;
        var rows = await db.ApiKeys.Where(k => k.IsActive).ToListAsync(ctx.RequestAborted);
        await WriteJson(ctx, new
        {
            credentials = rows.Select(k => new
            {
                k.Id, k.Name,
                masked = k.Key.Length > 4 ? "…" + k.Key[^4..] : "…",
                scopes = k.AccessRestricted ? "restricted" : "full",
            }),
        });
    }

    // ---- registered-keys detail + revoke ----

    private static async Task RegisteredKeyDetail(HttpContext ctx, LlmRouterDbContext db, string providerId)
    {
        if (await ApiKeyOr401(ctx, db) is null) return;
        var row = await db.ApiKeys.FindAsync([providerId], ctx.RequestAborted);
        if (row is null) { await WriteJson(ctx, new { error = "not found" }, 404); return; }
        var revoked = await db.Kv.FindAsync(["apiKeyRevoked", providerId], ctx.RequestAborted);
        await WriteJson(ctx, new
        {
            row.Id, row.Name,
            masked = row.Key.Length > 4 ? "…" + row.Key[^4..] : "…",
            row.IsActive, row.AccessRestricted, row.CreatedAt,
            revokedAt = revoked?.Value,
        });
    }

    private static async Task RegisteredKeyRevoke(HttpContext ctx, LlmRouterDbContext db, string providerId)
    {
        var ct = ctx.RequestAborted;
        if (await ApiKeyOr401(ctx, db) is null) return;
        var row = await db.ApiKeys.FindAsync([providerId], ct);
        if (row is null) { await WriteJson(ctx, new { error = "not found" }, 404); return; }
        row.IsActive = false;
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var kv = await db.Kv.FindAsync(["apiKeyRevoked", providerId], ct);
        if (kv is null) db.Kv.Add(new KvEntry { Scope = "apiKeyRevoked", Key = providerId, Value = now });
        else kv.Value = now;
        await db.SaveChangesAsync(ct);
        await WriteJson(ctx, new { providerId, revoked = true, revokedAt = now });
    }

    // ---- batches housekeeping ----

    private static async Task BatchesDeleteCompleted(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        if (await ApiKeyOr401(ctx, db) is null) return;
        if (!int.TryParse(ctx.Request.Query["olderThanDays"].FirstOrDefault(), out var days) || days < 0) days = 0;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var rows = await db.Kv.Where(k => k.Scope == "v1batches").ToListAsync(ct);
        var deleted = 0;
        foreach (var r in rows)
        {
            var terminal = false;
            string? created = null;
            try
            {
                using var doc = JsonDocument.Parse(r.Value);
                var st = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                terminal = st is "completed" or "failed" or "cancelled" or "expired";
                if (doc.RootElement.TryGetProperty("created_at", out var ca) && ca.TryGetInt64(out var ts))
                    created = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.ToString("o");
            }
            catch (JsonException) { continue; }
            if (!terminal) continue;
            if (created is { } c && DateTime.TryParse(c, out var at) && at > cutoff) continue;
            db.Kv.Remove(r);
            deleted++;
        }
        if (deleted > 0) await db.SaveChangesAsync(ct);
        await WriteJson(ctx, new { deleted });
    }

    // ---- management proxies ----

    private static async Task<JsonObject> LoadAssignments(LlmRouterDbContext db, CancellationToken ct)
    {
        var row = await db.Kv.FindAsync(["proxyAssignments", "map"], ct);
        if (row?.Value is null) return new JsonObject();
        try { return JsonNode.Parse(row.Value) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static async Task SaveAssignments(LlmRouterDbContext db, JsonObject map, CancellationToken ct)
    {
        var row = await db.Kv.FindAsync(["proxyAssignments", "map"], ct);
        var val = map.ToJsonString();
        if (row is null) db.Kv.Add(new KvEntry { Scope = "proxyAssignments", Key = "map", Value = val });
        else row.Value = val;
        await db.SaveChangesAsync(ct);
    }

    private static async Task ProxyAssignmentsGet(HttpContext ctx, LlmRouterDbContext db)
    {
        if (await ApiKeyOr401(ctx, db) is null) return;
        await WriteJson(ctx, new { assignments = await LoadAssignments(db, ctx.RequestAborted) });
    }

    private static async Task ProxyAssignmentsSet(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        if (await ApiKeyOr401(ctx, db) is null) return;
        var body = await Body(ctx);
        if (body is null || body.Value.ValueKind != JsonValueKind.Object
            || !body.Value.TryGetProperty("proxyId", out var pid) || !body.Value.TryGetProperty("providerId", out var vid))
        { await WriteJson(ctx, new { error = new { message = "Body must include proxyId and providerId" } }, 400); return; }
        var map = await LoadAssignments(db, ct);
        map[pid.GetString()!] = vid.GetString()!;
        await SaveAssignments(db, map, ct);
        await WriteJson(ctx, new { assignments = map });
    }

    private static async Task ProxyBulkAssign(HttpContext ctx, LlmRouterDbContext db)
    {
        var ct = ctx.RequestAborted;
        if (await ApiKeyOr401(ctx, db) is null) return;
        var body = await Body(ctx);
        if (body is null || body.Value.ValueKind != JsonValueKind.Array)
        { await WriteJson(ctx, new { error = new { message = "Body must be an array of {proxyId,providerId}" } }, 400); return; }
        var map = await LoadAssignments(db, ct);
        foreach (var item in body.Value.EnumerateArray())
        {
            if (item.TryGetProperty("proxyId", out var pid) && item.TryGetProperty("providerId", out var vid))
                map[pid.GetString()!] = vid.GetString()!;
        }
        await SaveAssignments(db, map, ct);
        await WriteJson(ctx, new { assignments = map });
    }

    private static async Task ProxyHealth(HttpContext ctx, LlmRouterDbContext db)
    {
        if (await ApiKeyOr401(ctx, db) is null) return;
        var pools = await db.ProxyPools.Where(p => p.IsActive).ToListAsync(ctx.RequestAborted);
        var assignments = await LoadAssignments(db, ctx.RequestAborted);
        await WriteJson(ctx, new
        {
            proxies = pools.Select(p =>
            {
                JsonNode? data = null;
                try { data = JsonNode.Parse(p.Data); } catch (JsonException) { /* malformed row */ }
                return new
                {
                    p.Id, p.TestStatus, p.UpdatedAt,
                    latencyMs = data?["lastLatencyMs"]?.GetValue<long>(),
                    failStreak = data?["failStreak"]?.GetValue<int>() ?? 0,
                    providerId = assignments[p.Id]?.GetValue<string>(),
                };
            }),
        });
    }

    // ---- proxy subscriptions ----

    private const string ProxySubScope = "proxySubscriptions";

    private static async Task ProxySubscriptionGet(HttpContext ctx, LlmRouterDbContext db, string p)
    {
        if (await ApiKeyOr401(ctx, db) is null) return;
        var row = await db.Kv.FindAsync([ProxySubScope, p], ctx.RequestAborted);
        if (row is null) { await WriteJson(ctx, new { error = "not found" }, 404); return; }
        ctx.Response.ContentType = Json;
        await ctx.Response.WriteAsync($"{{\"provider\":{JsonSerializer.Serialize(p, JsonOpts)},\"subscription\":{row.Value}}}", ctx.RequestAborted);
    }

    private static async Task ProxySubscriptionPut(HttpContext ctx, LlmRouterDbContext db, string p)
    {
        var ct = ctx.RequestAborted;
        if (await ApiKeyOr401(ctx, db) is null) return;
        var raw = await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync(ct);
        try { JsonDocument.Parse(raw); }
        catch (JsonException) { await WriteJson(ctx, new { error = new { message = "Invalid JSON body" } }, 400); return; }
        var row = await db.Kv.FindAsync([ProxySubScope, p], ct);
        if (row is null) db.Kv.Add(new KvEntry { Scope = ProxySubScope, Key = p, Value = raw });
        else row.Value = raw;
        await db.SaveChangesAsync(ct);
        await WriteJson(ctx, new { provider = p, saved = true });
    }

    private static async Task ProxySubscriptionNodes(HttpContext ctx, LlmRouterDbContext db, string p)
    {
        if (await ApiKeyOr401(ctx, db) is null) return;
        var row = await db.Kv.FindAsync([ProxySubScope, p], ctx.RequestAborted);
        if (row is null) { await WriteJson(ctx, new { error = "not found" }, 404); return; }
        JsonNode? nodes = null;
        try { nodes = JsonNode.Parse(row.Value)?["nodes"]; } catch (JsonException) { /* malformed row */ }
        await WriteJson(ctx, new { provider = p, nodes = nodes ?? new JsonArray() });
    }

    private static async Task ProxySubscriptionRefresh(HttpContext ctx, LlmRouterDbContext db, string p)
    {
        var ct = ctx.RequestAborted;
        if (await ApiKeyOr401(ctx, db) is null) return;
        var row = await db.Kv.FindAsync([ProxySubScope, p], ct);
        if (row is null) { await WriteJson(ctx, new { error = "not found" }, 404); return; }
        var obj = JsonNode.Parse(row.Value) as JsonObject ?? new JsonObject();
        var now = DateTime.UtcNow;
        // SPEC-097: a repeat inside the subscription's throttle window is recorded
        // on the row without raising a standing failure warning (upstream #16067).
        var throttleSec = obj["throttleSeconds"]?.GetValue<double>() ?? 0;
        if (throttleSec > 0
            && obj["lastRefresh"]?.GetValue<string>() is { } last
            && DateTime.TryParse(last, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastAt)
            && now - lastAt < TimeSpan.FromSeconds(throttleSec))
        {
            obj["lastThrottledAt"] = now.ToString("o");
            obj["throttleCount"] = (obj["throttleCount"]?.GetValue<int>() ?? 0) + 1;
            row.Value = obj.ToJsonString();
            await db.SaveChangesAsync(ct);
            await WriteJson(ctx, new
            {
                provider = p, refreshed = false, throttled = true,
                nextEligibleAt = (lastAt + TimeSpan.FromSeconds(throttleSec)).ToString("o"),
            });
            return;
        }
        obj["lastRefresh"] = now.ToString("o");
        row.Value = obj.ToJsonString();
        await db.SaveChangesAsync(ct);
        var count = obj["nodes"] is JsonArray arr ? arr.Count : 0;
        await WriteJson(ctx, new { provider = p, refreshed = true, nodes = count });
    }

    // ---- single-model card ----

    private static async Task ModelCard(HttpContext ctx, LlmRouterDbContext db,
        Core.Registry.ProviderRegistry registry, string path)
    {
        var ct = ctx.RequestAborted;
        var id = Uri.UnescapeDataString(path);
        var disabled = await Core.Gateway.HotReads.DisabledModelsAsync(db);
        foreach (var c in await db.ProviderConnections.Where(x => x.IsActive).ToListAsync(ct))
        {
            var entry = registry.GetProvider(c.Provider);
            if (entry?.Models is null) continue;
            foreach (var m in entry.Models)
            {
                if (Core.Routing.DisabledModels.IsDisabled(disabled, entry.Id, m.Id)) continue;
                if (id == $"{entry.Id}/{m.Id}" || id == $"{entry.Alias}/{m.Id}")
                {
                    await WriteJson(ctx, new
                    { id = $"{entry.Id}/{m.Id}", @object = "model", created = 0, owned_by = entry.Alias ?? entry.Id });
                    return;
                }
            }
        }
        var combo = await db.Combos.FirstOrDefaultAsync(x => x.Name == id, ct);
        if (combo is not null)
        {
            await WriteJson(ctx, new { id = combo.Name, @object = "model", created = 0, owned_by = "combo" });
            return;
        }
        await WriteJson(ctx, new { error = new { message = $"The model '{id}' does not exist", type = "invalid_request_error" } }, 404);
    }
}

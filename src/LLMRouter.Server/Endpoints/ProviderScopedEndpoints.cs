using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-088: container probes + upstream /v1/providers/{p}/* scoped calls
/// (chat, embeddings, images, models, voices, limits, suggested-models).
/// </summary>
public static class ProviderScopedEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly Regex VoiceIdRe = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    public static void Map(WebApplication app)
    {
        // Container probes — anonymous, work in headless mode (endpoints, not UI).
        app.MapGet("/healthz", () => Results.Json(new { ok = true }));
        app.MapGet("/livez", () => Results.Json(new { ok = true }));
        app.MapGet("/readyz", async (LlmRouterDbContext db) =>
            await db.Database.CanConnectAsync()
                ? Results.Json(new { ok = true })
                : Results.Json(new { ok = false, error = "db_unreachable" }, statusCode: 503));
        app.MapGroup("/api").RequireAuthorization()
            .MapGet("/health/ping", () => Results.Json(new { pong = true }));

        foreach (var prefix in new[] { "/v1", "/api/v1" })
        {
            var g = app.MapGroup(prefix);
            g.MapMethods("/providers/{p}/chat/completions", ["POST"],
                (HttpContext c, string p) => ScopedCall(c, p, chat: true));
            g.MapMethods("/providers/{p}/embeddings", ["POST"],
                (HttpContext c, string p) => ScopedCall(c, p, chat: false, upstreamPath: "/embeddings"));
            g.MapMethods("/providers/{p}/images/generations", ["POST"],
                (HttpContext c, string p) => ScopedCall(c, p, chat: false, upstreamPath: "/images/generations"));
            g.MapGet("/providers/{p}/models", ProviderModels);
            g.MapGet("/providers/{p}/limits", GetLimits);
            g.MapMethods("/providers/{p}/limits", ["PUT"], PutLimits);
            g.MapMethods("/providers/{p}/voices", ["GET", "POST"], Voices);
            g.MapMethods("/providers/{p}/voices/{voiceId}", ["GET", "PUT", "DELETE"], VoiceItem);
            g.MapGet("/providers/suggested-models", SuggestedModels);
        }
    }

    /// <summary>Resolve {p} to a registry entry (alias-aware) or an active custom connection.</summary>
    private static async Task<(ProviderEntry? Entry, string Alias)> ResolveProvider(
        LlmRouterDbContext db, ProviderRegistry registry, string p)
    {
        var e = registry.GetProvider(p);
        if (e is not null) return (e, e.Alias ?? e.Id);
        var node = await Core.Routing.NodeResolver.ResolveAsync(db, p);
        if (node is not null) return (node, node.Id);
        if (await db.ProviderConnections.AnyAsync(c => c.Provider == p && c.IsActive))
            return (new ProviderEntry { Id = p }, p);
        return (null, p);
    }

    private static async Task BadRequest(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = 400;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(
            new { error = new { message } }, JsonOpts));
    }

    /// <summary>
    /// Force-scope a call to provider {p}: rewrites body.model to {alias}/{model}
    /// when unprefixed (upstream parity), 400s when the prefix mismatches, then
    /// delegates to the full Chat/Passthrough pipeline.
    /// </summary>
    private static async Task ScopedCall(HttpContext ctx, string p, bool chat, string? upstreamPath = null)
    {
        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var registry = ctx.RequestServices.GetRequiredService<ProviderRegistry>();
        var (entry, alias) = await ResolveProvider(db, registry, p);
        if (entry is null)
        {
            await BadRequest(ctx, $"Unknown provider: {p}");
            return;
        }

        if (ctx.Request.Method == "POST")
        {
            var raw = await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync();
            JsonNode? node = null;
            try { node = JsonNode.Parse(raw); } catch { /* let downstream report malformed */ }
            if (node is JsonObject obj)
            {
                var model = obj["model"]?.GetValue<string>();
                if (model is { Length: > 0 })
                {
                    var idx = model.IndexOf('/');
                    if (idx >= 0)
                    {
                        var mp = model[..idx];
                        if (mp != alias && mp != p && mp != entry.Id)
                        {
                            await BadRequest(ctx,
                                $"Model \"{model}\" does not belong to provider \"{p}\". Expected prefix: {alias}/");
                            return;
                        }
                    }
                    else
                    {
                        obj["model"] = $"{alias}/{model}";
                    }
                }
                var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
                ctx.Request.Body = new MemoryStream(bytes);
                ctx.Request.ContentLength = bytes.Length;
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(raw);
                ctx.Request.Body = new MemoryStream(bytes);
                ctx.Request.ContentLength = bytes.Length;
            }
        }

        if (chat) await GatewayEndpoints.Chat(ctx, "openai");
        else await GatewayEndpoints.Passthrough(ctx, upstreamPath: upstreamPath);
    }

    /// <summary>GET /v1/providers/{p}/models — registry + synced models, unprefixed ids.</summary>
    private static async Task ProviderModels(HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry, string p)
    {
        var (key, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return; }
        var (entry, _) = await ResolveProvider(db, registry, p);
        if (entry is null) { await BadRequest(ctx, $"Unknown provider: {p}"); return; }

        var models = new List<JsonObject>();
        if (entry.Models is { } rm)
            models.AddRange(rm.Where(m => m.Id.Length > 0).Select(m => new JsonObject
            {
                ["id"] = m.Id, ["object"] = "model", ["owned_by"] = entry.Id, ["parent"] = null,
            }));
        var synced = await db.SyncedModels
            .Where(s => s.Provider == entry.Id && s.Available)
            .Select(s => s.Model).ToListAsync();
        var seen = new HashSet<string>(models.Select(m => m["id"]!.GetValue<string>()));
        foreach (var s in synced.Where(s => seen.Add(s)))
            models.Add(new JsonObject { ["id"] = s, ["object"] = "model", ["owned_by"] = entry.Id, ["parent"] = null });
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(
            new JsonObject { ["object"] = "list", ["data"] = new JsonArray(models.ToArray<JsonNode>()) }, JsonOpts));
    }

    /// <summary>GET /v1/providers/{p}/limits — registered-key issuance limits.</summary>
    private static async Task GetLimits(HttpContext ctx, LlmRouterDbContext db, string p)
    {
        var (_, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return; }
        var row = await db.Kv.FindAsync("providerKeyLimits", p);
        JsonElement? limits = null;
        if (row?.Value is { } v)
        {
            try { limits = JsonDocument.Parse(v).RootElement; } catch { }
        }
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(limits is null
            ? JsonSerializer.Serialize(new { provider = p, limits = (object?)null }, JsonOpts)
            : $"{{\"provider\":{JsonSerializer.Serialize(p, JsonOpts)},\"limits\":{limits.Value.GetRawText()}}}");
    }

    /// <summary>PUT /v1/providers/{p}/limits — set issuance limits {maxActiveKeys,dailyIssueLimit,hourlyIssueLimit}.</summary>
    private static async Task PutLimits(HttpContext ctx, LlmRouterDbContext db, string p)
    {
        var (_, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return; }
        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
        catch { await BadRequest(ctx, "Invalid JSON body"); return; }
        var limits = new JsonObject();
        foreach (var k in new[] { "maxActiveKeys", "dailyIssueLimit", "hourlyIssueLimit" })
        {
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.Null) limits[k] = null;
                else if (v.TryGetInt32(out var n) && n > 0) limits[k] = n;
                else { await BadRequest(ctx, $"{k} must be a positive integer or null"); return; }
            }
        }
        var row = await db.Kv.FindAsync("providerKeyLimits", p);
        var val = limits.ToJsonString();
        if (row is null) db.Kv.Add(new KvEntry { Scope = "providerKeyLimits", Key = p, Value = val });
        else row.Value = val;
        await db.SaveChangesAsync();
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync($"{{\"provider\":{JsonSerializer.Serialize(p, JsonOpts)},\"limits\":{val}}}");
    }

    /// <summary>GET/POST /v1/providers/{p}/voices — Fish Audio voice models (upstream parity).</summary>
    private static async Task Voices(HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf, string p)
        => await FishAudioProxy(ctx, db, hf, p, "/model", null);

    /// <summary>GET/PUT/DELETE /v1/providers/{p}/voices/{voiceId}.</summary>
    private static async Task VoiceItem(HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf, string p, string voiceId)
    {
        if (!VoiceIdRe.IsMatch(voiceId))
        {
            await BadRequest(ctx, "Invalid Fish Audio voice ID");
            return;
        }
        await FishAudioProxy(ctx, db, hf, p, $"/model/{voiceId}", voiceId);
    }

    /// <summary>Proxy a request to the Fish Audio /model* API using the fishaudio connection.</summary>
    private static async Task FishAudioProxy(HttpContext ctx, LlmRouterDbContext db,
        IHttpClientFactory hf, string p, string upstreamPath, string? _)
    {
        if (p != "fishaudio")
        {
            await BadRequest(ctx, $"Voice-model management is not supported for provider: {p}");
            return;
        }
        var (_, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return; }

        var conn = await db.ProviderConnections
            .FirstOrDefaultAsync(c => c.Provider == "fishaudio" && c.IsActive);
        if (conn is null)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"error\":{\"message\":\"No credentials for provider: fishaudio\"}}");
            return;
        }
        var secret = Core.Gateway.GatewayEngine.ConnectionSecret(conn);
        var baseUrl = Core.Gateway.GatewayEngine.ConnectionBaseUrl(conn, new ProviderEntry { Id = "fishaudio", BaseUrl = "https://api.fish.audio" });
        if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://api.fish.audio";

        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), baseUrl.TrimEnd('/') + upstreamPath);
        if (secret is not null) req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
        if (ctx.Request.Method is "POST" or "PUT")
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            req.Content = new ByteArrayContent(ms.ToArray());
            if (ctx.Request.ContentType is { } ct)
                req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ct);
        }
        try
        {
            var client = hf.CreateClient("upstream");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (Exception)
        {
            ctx.Response.StatusCode = 502;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(
                new { error = new { message = "Fish Audio upstream error" } }, JsonOpts));
        }
    }

    /// <summary>GET /v1/providers/suggested-models — server-side HuggingFace Hub proxy.</summary>
    private static async Task SuggestedModels(HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf)
    {
        var (_, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return; }

        var q = ctx.Request.Query;
        var type = q["type"].FirstOrDefault() ?? "image";
        var sortBy = q["sortBy"].FirstOrDefault() ?? "downloads";
        if (!int.TryParse(q["limit"].FirstOrDefault(), out var limit) || limit < 1) limit = 20;
        if (limit > 50) limit = 50;
        if (type != "image") { await BadRequest(ctx, $"Unsupported suggested-models type: {type}"); return; }
        if (sortBy != "downloads" && sortBy != "likes") { await BadRequest(ctx, "sortBy must be downloads|likes"); return; }
        const string pipelineTag = "text-to-image";

        try
        {
            var url = $"https://huggingface.co/api/models?inference_provider=hf-inference&pipeline_tag={pipelineTag}&limit=100";
            var client = hf.CreateClient("upstream");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            cts.CancelAfter(8000);
            var data = await client.GetFromJsonAsync<JsonElement>(url, cts.Token);
            var items = data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray()
                    .Where(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(m => new
                    {
                        id = m.GetProperty("id").GetString()!,
                        likes = m.TryGetProperty("likes", out var l) && l.TryGetInt64(out var ln) ? ln : 0,
                        downloads = m.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var dn) ? dn : 0,
                    })
                    .OrderByDescending(m => sortBy == "likes" ? m.likes : m.downloads)
                    .Take(limit).ToList()
                : [];
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                @object = "list",
                type,
                pipeline_tag = pipelineTag,
                data = items,
            }, JsonOpts));
        }
        catch (Exception)
        {
            ctx.Response.StatusCode = 502;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"error\":{\"message\":\"HuggingFace Hub API unavailable\"}}");
        }
    }
}

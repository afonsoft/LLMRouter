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
    private const string Json = "application/json";
    private const string Model = "model";
    private const string FishAudioDefaultBase = "https://api.fish.audio";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly Regex VoiceIdRe = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static void Map(WebApplication app)
    {
        // Container probes — anonymous, work in headless mode (endpoints, not UI).
        app.MapGet("/healthz", () => Results.Json(new { ok = true }));
        app.MapGet("/livez", () => Results.Json(new { ok = true }));
        app.MapGet("/readyz", async (HttpContext ctx, LlmRouterDbContext db) =>
            await db.Database.CanConnectAsync(ctx.RequestAborted)
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
        LlmRouterDbContext db, ProviderRegistry registry, string p, CancellationToken ct)
    {
        var e = registry.GetProvider(p);
        if (e is not null) return (e, e.Alias ?? e.Id);
        var node = await Core.Routing.NodeResolver.ResolveAsync(db, p);
        if (node is not null) return (node, node.Id);
        if (await db.ProviderConnections.AnyAsync(c => c.Provider == p && c.IsActive, ct))
            return (new ProviderEntry { Id = p }, p);
        return (null, p);
    }

    private static async Task BadRequest(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = 400;
        ctx.Response.ContentType = Json;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(
            new { error = new { message } }, JsonOpts), ctx.RequestAborted);
    }

    /// <summary>
    /// Force-scope a call to provider {p}: rewrites body.model to {alias}/{model}
    /// when unprefixed (upstream parity), 400s when the prefix mismatches, then
    /// delegates to the full Chat/Passthrough pipeline.
    /// </summary>
    private static async Task ScopedCall(HttpContext ctx, string p, bool chat, string? upstreamPath = null)
    {
        var ct = ctx.RequestAborted;
        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var registry = ctx.RequestServices.GetRequiredService<ProviderRegistry>();
        var (entry, alias) = await ResolveProvider(db, registry, p, ct);
        if (entry is null)
        {
            await BadRequest(ctx, $"Unknown provider: {p}");
            return;
        }
        if (ctx.Request.Method == "POST" && !await RewriteBodyModel(ctx, entry, p, alias, ct))
            return;

        if (chat) await GatewayEndpoints.Chat(ctx, "openai");
        else await GatewayEndpoints.Passthrough(ctx, upstreamPath: upstreamPath);
    }

    /// <summary>Prefix-validate/rewrite body.model; false when a 400 was written.</summary>
    private static async Task<bool> RewriteBodyModel(
        HttpContext ctx, ProviderEntry entry, string p, string alias, CancellationToken ct)
    {
        var raw = await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync(ct);
        JsonNode? node = null;
        try { node = JsonNode.Parse(raw); } catch (JsonException) { /* let downstream report malformed */ }
        var bytes = node is JsonObject obj ? RewriteModel(obj, entry, p) : Encoding.UTF8.GetBytes(raw);
        if (bytes is null)
        {
            var model = (node as JsonObject)?[Model]?.GetValue<string>() ?? string.Empty;
            await BadRequest(ctx,
                $"Model \"{model}\" does not belong to provider \"{p}\". Expected prefix: {alias}/");
            return false;
        }
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        return true;
    }

    /// <summary>Returns rewritten bytes, or null on prefix mismatch.</summary>
    private static byte[]? RewriteModel(JsonObject obj, ProviderEntry entry, string p)
    {
        var model = obj[Model]?.GetValue<string>();
        if (model is { Length: > 0 })
        {
            var alias = entry.Alias ?? entry.Id;
            var idx = model.IndexOf('/');
            if (idx < 0) obj[Model] = $"{alias}/{model}";
            else if (model[..idx] != alias && model[..idx] != p && model[..idx] != entry.Id) return null;
        }
        return JsonSerializer.SerializeToUtf8Bytes(obj);
    }

    /// <summary>GET /v1/providers/{p}/models — registry + synced models, unprefixed ids.</summary>
    private static async Task ProviderModels(HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry, string p)
    {
        var ct = ctx.RequestAborted;
        var (_, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return; }
        var (entry, _) = await ResolveProvider(db, registry, p, ct);
        if (entry is null) { await BadRequest(ctx, $"Unknown provider: {p}"); return; }

        var models = new List<JsonObject>();
        if (entry.Models is { } rm)
            models.AddRange(rm.Where(m => m.Id.Length > 0).Select(m => new JsonObject
            {
                ["id"] = m.Id, ["object"] = Model, ["owned_by"] = entry.Id, ["parent"] = null,
            }));
        var synced = await db.SyncedModels
            .Where(s => s.Provider == entry.Id && s.Available)
            .Select(s => s.Model).ToListAsync(ct);
        var seen = new HashSet<string>(models.Select(m => m["id"]!.GetValue<string>()));
        foreach (var s in synced.Where(s => seen.Add(s)))
            models.Add(new JsonObject { ["id"] = s, ["object"] = Model, ["owned_by"] = entry.Id, ["parent"] = null });
        ctx.Response.ContentType = Json;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(
            new JsonObject { ["object"] = "list", ["data"] = new JsonArray(models.ToArray<JsonNode>()) }, JsonOpts), ct);
    }

    /// <summary>GET /v1/providers/{p}/limits — registered-key issuance limits.</summary>
    private static async Task GetLimits(HttpContext ctx, LlmRouterDbContext db, string p)
    {
        var ct = ctx.RequestAborted;
        var (_, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return; }
        var row = await db.Kv.FindAsync(["providerKeyLimits", p], ct);
        string? raw = null;
        if (row?.Value is { } v)
        {
            try { raw = JsonDocument.Parse(v).RootElement.GetRawText(); }
            catch (JsonException) { /* malformed kv row — report limits:null */ }
        }
        ctx.Response.ContentType = Json;
        await ctx.Response.WriteAsync(raw is null
            ? JsonSerializer.Serialize(new { provider = p, limits = (object?)null }, JsonOpts)
            : $"{{\"provider\":{JsonSerializer.Serialize(p, JsonOpts)},\"limits\":{raw}}}", ct);
    }

    /// <summary>PUT /v1/providers/{p}/limits — set issuance limits {maxActiveKeys,dailyIssueLimit,hourlyIssueLimit}.</summary>
    private static async Task PutLimits(HttpContext ctx, LlmRouterDbContext db, string p)
    {
        var ct = ctx.RequestAborted;
        var (_, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return; }
        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, JsonOpts, ct); }
        catch (JsonException) { await BadRequest(ctx, "Invalid JSON body"); return; }
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
        var row = await db.Kv.FindAsync(["providerKeyLimits", p], ct);
        var val = limits.ToJsonString();
        if (row is null) db.Kv.Add(new KvEntry { Scope = "providerKeyLimits", Key = p, Value = val });
        else row.Value = val;
        await db.SaveChangesAsync(ct);
        ctx.Response.ContentType = Json;
        await ctx.Response.WriteAsync($"{{\"provider\":{JsonSerializer.Serialize(p, JsonOpts)},\"limits\":{val}}}", ct);
    }

    /// <summary>GET/POST /v1/providers/{p}/voices — Fish Audio voice models (upstream parity).</summary>
    private static async Task Voices(HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf, string p)
        => await FishAudioProxy(ctx, db, hf, p, "/model");

    /// <summary>GET/PUT/DELETE /v1/providers/{p}/voices/{voiceId}.</summary>
    private static async Task VoiceItem(HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf, string p, string voiceId)
    {
        if (!VoiceIdRe.IsMatch(voiceId))
        {
            await BadRequest(ctx, "Invalid Fish Audio voice ID");
            return;
        }
        await FishAudioProxy(ctx, db, hf, p, $"/model/{voiceId}");
    }

    /// <summary>Proxy a request to the Fish Audio /model* API using the fishaudio connection.</summary>
    private static async Task FishAudioProxy(HttpContext ctx, LlmRouterDbContext db,
        IHttpClientFactory hf, string p, string upstreamPath)
    {
        var ct = ctx.RequestAborted;
        if (p != "fishaudio")
        {
            await BadRequest(ctx, $"Voice-model management is not supported for provider: {p}");
            return;
        }
        var (_, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return; }

        var conn = await db.ProviderConnections
            .FirstOrDefaultAsync(c => c.Provider == "fishaudio" && c.IsActive, ct);
        if (conn is null)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = Json;
            await ctx.Response.WriteAsync("{\"error\":{\"message\":\"No credentials for provider: fishaudio\"}}", ct);
            return;
        }
        var secret = Core.Gateway.GatewayEngine.ConnectionSecret(conn);
        var baseUrl = Core.Gateway.GatewayEngine.ConnectionBaseUrl(
            conn, new ProviderEntry { Id = "fishaudio", BaseUrl = FishAudioDefaultBase });
        if (string.IsNullOrEmpty(baseUrl)) baseUrl = FishAudioDefaultBase;

        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), baseUrl.TrimEnd('/') + upstreamPath);
        if (secret is not null) req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
        if (ctx.Request.Method is "POST" or "PUT")
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            req.Content = new ByteArrayContent(ms.ToArray());
            if (ctx.Request.ContentType is { } contentType)
                req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }
        try
        {
            var client = hf.CreateClient("upstream");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? Json;
            await resp.Content.CopyToAsync(ctx.Response.Body, ct);
        }
        catch (Exception)
        {
            ctx.Response.StatusCode = 502;
            ctx.Response.ContentType = Json;
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(
                new { error = new { message = "Fish Audio upstream error" } }, JsonOpts), ct);
        }
    }

    /// <summary>GET /v1/providers/suggested-models — server-side HuggingFace Hub proxy.</summary>
    private static async Task SuggestedModels(HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf)
    {
        var ct = ctx.RequestAborted;
        var (_, authed) = await GatewayEndpoints.AuthenticatedKey(ctx, db);
        if (!authed) { ctx.Response.StatusCode = 401; return; }
        var q = ctx.Request.Query;
        var type = q["type"].FirstOrDefault() ?? "image";
        var sortBy = q["sortBy"].FirstOrDefault() ?? "downloads";
        if (!int.TryParse(q["limit"].FirstOrDefault(), out var limit) || limit < 1) limit = 20;
        if (limit > 50) limit = 50;
        if (type != "image") { await BadRequest(ctx, $"Unsupported suggested-models type: {type}"); return; }
        if (sortBy != "downloads" && sortBy != "likes") { await BadRequest(ctx, "sortBy must be downloads|likes"); return; }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(8000);
            var items = await FetchHubItems(hf, sortBy, limit, cts.Token);
            ctx.Response.ContentType = Json;
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                @object = "list",
                type,
                pipeline_tag = "text-to-image",
                data = items,
            }, JsonOpts), ct);
        }
        catch (Exception)
        {
            ctx.Response.StatusCode = 502;
            ctx.Response.ContentType = Json;
            await ctx.Response.WriteAsync("{\"error\":{\"message\":\"HuggingFace Hub API unavailable\"}}", ct);
        }
    }

    private static async Task<List<object>> FetchHubItems(
        IHttpClientFactory hf, string sortBy, int limit, CancellationToken ct)
    {
        const string url =
            "https://huggingface.co/api/models?inference_provider=hf-inference&pipeline_tag=text-to-image&limit=100";
        var data = await hf.CreateClient("upstream").GetFromJsonAsync<JsonElement>(url, ct);
        if (data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray()
            .Where(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(m => new
            {
                id = m.GetProperty("id").GetString()!,
                likes = m.TryGetProperty("likes", out var l) && l.TryGetInt64(out var ln) ? ln : 0,
                downloads = m.TryGetProperty("downloads", out var d) && d.TryGetInt64(out var dn) ? dn : 0,
            })
            .OrderByDescending(m => sortBy == "likes" ? m.likes : m.downloads)
            .Take(limit)
            .Select(m => (object)m)
            .ToList();
    }
}

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Translation;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// OpenAI/Claude/Gemini-compatible gateway. Mounted under /v1/* and /api/v1/*
/// like upstream. Auth: Bearer or x-api-key against the apiKeys table.
/// </summary>
public static class GatewayEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapGatewayEndpoints(this WebApplication app)
    {
        foreach (var prefix in new[] { "/v1", "/api/v1" })
        {
            var g = app.MapGroup(prefix);
            g.MapMethods("/chat/completions", ["POST"], (HttpContext c) => Chat(c, "openai"));
            g.MapMethods("/messages", ["POST"], (HttpContext c) => Chat(c, "claude"));
            g.MapMethods("/messages/count_tokens", ["POST"], CountTokens);
            g.MapMethods("/responses", ["POST"], (HttpContext c) => Chat(c, "responsesApi"));
            g.MapMethods("/responses/compact", ["POST"], (HttpContext c) => Chat(c, "responsesApi"));
            g.MapGet("/models", Models);
            g.MapGet("/models/info", ModelInfo);
            g.MapGet("/models/{*path}", Models);
            // non-chat passthrough: same resolution + cascade, no translation
            foreach (var p in new[] { "/embeddings", "/images/generations", "/audio/speech",
                "/audio/transcriptions", "/audio/voices", "/search", "/web/fetch",
                "/moderations", "/files", "/batches" })
                g.MapMethods(p, ["POST", "GET"], (HttpContext c) => Passthrough(c));
            g.MapMethods("/{*path}", ["OPTIONS"], () => Results.Ok());
        }
        // Gemini inbound
        app.MapMethods("/v1beta/models/{model}:generateContent", ["POST"], (HttpContext c) => Chat(c, "gemini"));
        app.MapMethods("/v1beta/models/{model}:streamGenerateContent", ["POST"], (HttpContext c) => Chat(c, "gemini"));
        app.MapMethods("/v1beta/models/{model}:countTokens", ["POST"], CountTokens);
        app.MapGet("/v1beta/models", Models);
        app.MapGet("/v1beta/models/{*path}", Models);
    }

    /// <summary>Model list with per-connection detail (upstream /v1/models/{id} + /info).</summary>
    private static async Task ModelInfo(HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry)
    {
        if (!await Authorized(ctx, db)) { ctx.Response.StatusCode = 401; return; }
        var id = ctx.Request.Query["id"].FirstOrDefault() ?? "";
        var slash = id.IndexOf('/');
        var providerId = slash > 0 ? id[..slash] : id;
        var modelId = slash > 0 ? id[(slash + 1)..] : null;
        var p = registry.GetProvider(providerId);
        var conns = await db.ProviderConnections
            .Where(c => c.Provider == providerId && c.IsActive).ToListAsync();
        var result = new
        {
            id,
            provider = p is null ? null : new
            {
                p.Id, p.Alias, p.Format, p.AuthType,
                models = p.Models?.Select(m => m.Id) ?? [],
            },
            model = modelId,
            connections = conns.Select(c => new { c.Id, c.Name, c.Priority }),
        };
        await ctx.Response.WriteAsJsonAsync(result);
    }

    /// <summary>
    /// Approximate token counting (upstream does a chars/4 heuristic for claude
    /// when no provider tokenizer is configured). Returns claude-shaped
    /// {input_tokens} for /messages/count_tokens, gemini-shaped for :countTokens.
    /// </summary>
    private static async Task CountTokens(HttpContext ctx, LlmRouterDbContext db)
    {
        if (!await Authorized(ctx, db)) { ctx.Response.StatusCode = 401; return; }
        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
        catch { ctx.Response.StatusCode = 400; return; }
        var raw = body.GetRawText();
        var chars = 0;
        // sum only the content-bearing strings, not JSON overhead
        try
        {
            foreach (var el in EnumerateStrings(body)) chars += el.Length;
        }
        catch { chars = raw.Length; }
        var tokens = Math.Max(1, (int)Math.Ceiling(chars / 4.0));
        var isGemini = ctx.Request.Path.Value?.Contains(":countTokens") == true;
        await ctx.Response.WriteAsync(isGemini
            ? JsonSerializer.Serialize(new { totalTokens = tokens })
            : JsonSerializer.Serialize(new { input_tokens = tokens }));
        ctx.Response.ContentType = "application/json";
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                yield return el.GetString() ?? "";
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    foreach (var s in EnumerateStrings(item)) yield return s;
                break;
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    if (prop.Name is "text" or "content" or "input" or "instructions")
                        foreach (var s in EnumerateStrings(prop.Value)) yield return s;
                break;
        }
    }

    /// <summary>
    /// Non-chat endpoints (embeddings, images, audio, search, web/fetch):
    /// resolve a provider connection for the requested model, forward the body
    /// verbatim to the provider's endpoint for that service, stream the
    /// response back (may be binary/multipart). No format translation.
    /// </summary>
    private static async Task Passthrough(HttpContext ctx)
    {
        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var engine = ctx.RequestServices.GetRequiredService<GatewayEngine>();
        var httpFactory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();
        var sw = Stopwatch.StartNew();

        var (apiKey, authed) = await AuthenticatedKey(ctx, db);
        if (!authed)
        {
            ctx.Response.StatusCode = 401;
            await WriteError(ctx, "openai", "invalid_api_key", "Invalid or missing API key.");
            return;
        }

        string model = "";
        JsonElement body = default;
        byte[]? rawBody = null;
        if (ctx.Request.Method == "POST")
        {
            rawBody = await GetRawBody(ctx);
            try
            {
                body = JsonDocument.Parse(rawBody).RootElement;
                if (body.TryGetProperty("model", out var m)) model = m.GetString() ?? "";
            }
            catch { /* multipart etc — model may be in form fields */ }
        }
        model = string.IsNullOrEmpty(model) ? ctx.Request.Query["model"].FirstOrDefault() ?? "" : model;

        var targets = await engine.ResolveAsync(model, body);
        if (targets.Count == 0)
        {
            ctx.Response.StatusCode = 400;
            await WriteError(ctx, "openai", "model_not_found",
                $"No active provider connection can serve '{model}'.");
            return;
        }

        var client = httpFactory.CreateClient("upstream");
        var path = ctx.Request.Path.Value ?? "";
        var suffix = path[(path.IndexOf("/v1", StringComparison.Ordinal) + 3)..];
        foreach (var target in targets)
        {
            try
            {
                var baseUrl = GatewayEngine.ConnectionBaseUrl(target.Connection, target.Provider);
                var url = baseUrl + suffix + (ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : "");
                var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);
                var secret = GatewayEngine.ConnectionSecret(target.Connection);
                if (secret is not null)
                {
                    var header = target.Provider.AuthHeader ?? "authorization";
                    var value = target.Provider.AuthPrefix is { Length: > 0 } p
                        ? $"{p}{secret}"
                        : header.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                            ? $"Bearer {secret}" : secret;
                    req.Headers.TryAddWithoutValidation(header, value);
                }
                if (target.Provider.Headers is { } extra)
                    foreach (var kv in extra) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                if (rawBody is not null)
                {
                    req.Content = new ByteArrayContent(rawBody);
                    req.Content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue(
                            ctx.Request.ContentType ?? "application/json");
                }
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                if (!resp.IsSuccessStatusCode && ShouldCascade(resp.StatusCode) && target != targets[^1])
                    continue;
                ctx.Response.StatusCode = (int)resp.StatusCode;
                ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
                await resp.Content.CopyToAsync(ctx.Response.Body);
                if (!resp.IsSuccessStatusCode)
                    await engine.LogUsageAsync(target.Provider.Id, target.UpstreamModel,
                        target.Connection.Id, apiKey, path, 0, 0,
                        ((int)resp.StatusCode).ToString(), null, sw.ElapsedMilliseconds);
                return;
            }
            catch (Exception) when (target != targets[^1]) { }
        }
        ctx.Response.StatusCode = 502;
        await WriteError(ctx, "openai", "upstream_unavailable", "All upstream targets failed.");
    }

    private static async Task<byte[]?> GetRawBody(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static async Task Models(HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry)
    {
        if (!await Authorized(ctx, db)) { ctx.Response.StatusCode = 401; return; }
        var models = new List<object>();
        foreach (var c in await db.ProviderConnections.Where(x => x.IsActive).ToListAsync())
        {
            var p = registry.GetProvider(c.Provider);
            if (p?.Models is null) continue;
            foreach (var m in p.Models)
                models.Add(new
                {
                    id = $"{p.Id}/{m.Id}",
                    @object = "model",
                    created = 0,
                    owned_by = p.Alias ?? p.Id,
                });
        }
        foreach (var combo in await db.Combos.ToListAsync())
            models.Add(new { id = combo.Name, @object = "model", created = 0, owned_by = "combo" });
        await ctx.Response.WriteAsJsonAsync(new { @object = "list", data = models });
    }

    private static async Task Chat(HttpContext ctx, string inbound)
    {
        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var engine = ctx.RequestServices.GetRequiredService<GatewayEngine>();
        var httpFactory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();
        var registry = ctx.RequestServices.GetRequiredService<ProviderRegistry>();
        var sw = Stopwatch.StartNew();

        var (apiKey, authed) = await AuthenticatedKey(ctx, db);
        if (!authed)
        {
            ctx.Response.StatusCode = 401;
            await WriteError(ctx, inbound, "invalid_api_key", "Invalid or missing API key.");
            return;
        }

        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
        catch { ctx.Response.StatusCode = 400; await WriteError(ctx, inbound, "invalid_request", "Malformed JSON body."); return; }

        var model = body.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        if (inbound == "gemini" && string.IsNullOrEmpty(model))
        {
            var routeModel = ctx.Request.RouteValues["model"]?.ToString() ?? "";
            model = routeModel.Contains(':') ? routeModel[..routeModel.IndexOf(':')] : routeModel;
        }
        var stream = body.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        if (inbound == "gemini" && ctx.Request.Path.Value?.Contains("streamGenerateContent") == true)
            stream = true;

        // vision-adapter combo: stage-1 imageToText call rewrites the image into
        // a text description, then the remaining combo models serve the request
        var comboEntity = await db.Combos.FirstOrDefaultAsync(c => c.Name == model);
        if (comboEntity?.Kind == "vision-adapter"
            && ComboPlanner.DetectRequiredCapabilities(body).Contains("vision"))
        {
            var rewritten = await VisionAdapterAsync(ctx, engine, httpFactory, comboEntity, body, inbound, model);
            if (rewritten is { } rw) body = rw;
        }

        var targets = await engine.ResolveAsync(model, body);
        if (comboEntity?.Kind == "vision-adapter")
        {
            var models = JsonSerializer.Deserialize<List<string>>(comboEntity.Models) ?? [];
            if (models.Count > 0)
                targets = targets.Where(t => $"{t.Provider.Id}/{t.UpstreamModel}" != models[0]).ToList();
        }
        if (targets.Count == 0)
        {
            ctx.Response.StatusCode = 400;
            await WriteError(ctx, inbound, "model_not_found",
                $"No active provider connection can serve '{model}'.");
            return;
        }

        var client = httpFactory.CreateClient("upstream");
        Exception? lastError = null;
        foreach (var target in targets)
        {
            try
            {
                var call = engine.BuildCall(target, inbound, body, stream);
                var req = new HttpRequestMessage(HttpMethod.Post, call.Url);
                foreach (var (k, v) in call.Headers)
                    req.Headers.TryAddWithoutValidation(k, v);
                // gemini streaming needs alt=sse
                if (call.OutboundFormat == "gemini" && stream)
                    req.RequestUri = new Uri(call.Url.Replace(":generateContent", ":streamGenerateContent") +
                        (call.Url.Contains('?') ? "&" : "?") + "alt=sse");
                req.Content = new StringContent(call.Body, Encoding.UTF8, "application/json");

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                if (!resp.IsSuccessStatusCode)
                {
                    Core.Resilience.CooldownTracker.ReportFailure(target.Connection.Id);
                    var errBody = await resp.Content.ReadAsStringAsync();
                    if (ShouldCascade(resp.StatusCode) && target != targets[^1])
                    {
                        lastError = new HttpRequestException($"upstream {(int)resp.StatusCode}: {errBody[..Math.Min(200, errBody.Length)]}");
                        continue; // cascade to next target
                    }
                    ctx.Response.StatusCode = (int)resp.StatusCode;
                    await WriteError(ctx, inbound, "upstream_error", errBody);
                    await engine.LogUsageAsync(target.Provider.Id, target.UpstreamModel,
                        target.Connection.Id, apiKey, inbound, 0, 0,
                        ((int)resp.StatusCode).ToString(), errBody[..Math.Min(500, errBody.Length)], sw.ElapsedMilliseconds);
                    return;
                }

                if (stream)
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.Headers.CacheControl = "no-cache";
                    ctx.Response.Headers["X-Accel-Buffering"] = "no";
                    var (pt, ct) = await StreamThrough(ctx, resp, call.OutboundFormat, inbound, model);
                    Core.Resilience.CooldownTracker.ReportSuccess(target.Connection.Id);
                    await engine.LogUsageAsync(target.Provider.Id, target.UpstreamModel,
                        target.Connection.Id, apiKey, inbound, pt, ct, "200", null, sw.ElapsedMilliseconds);
                }
                else
                {
                    var upstream = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ctx.RequestAborted);
                    var translated = Translators.TranslateResponse(upstream, call.OutboundFormat, inbound, model);
                    var (pt, ct) = ExtractUsage(translated, inbound);
                    ctx.Response.ContentType = "application/json";
                    Core.Resilience.CooldownTracker.ReportSuccess(target.Connection.Id);
                    await ctx.Response.WriteAsync(translated.ToJsonString(JsonOpts));
                    await engine.LogUsageAsync(target.Provider.Id, target.UpstreamModel,
                        target.Connection.Id, apiKey, inbound, pt, ct, "200", null, sw.ElapsedMilliseconds);
                }
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ctx.RequestAborted.IsCancellationRequested)
            {
                lastError = ex;
                if (target == targets[^1]) break;
            }
        }

        ctx.Response.StatusCode = 502;
        await WriteError(ctx, inbound, "upstream_unavailable",
            lastError?.Message ?? "All upstream targets failed.");
        await engine.LogUsageAsync(model, model, null, apiKey, inbound, 0, 0, "502",
            lastError?.Message, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Vision-adapter stage 1: send the image-bearing request to the combo's
    /// first model (imageToText), get a text description back, rewrite the body
    /// replacing image content with the description.
    /// </summary>
    private static async Task<JsonElement?> VisionAdapterAsync(
        HttpContext ctx, GatewayEngine engine, IHttpClientFactory factory,
        Combo combo, JsonElement body, string inbound, string model)
    {
        var models = JsonSerializer.Deserialize<List<string>>(combo.Models) ?? [];
        if (models.Count < 2) return null;
        var stage1 = await engine.ResolveAsync(models[0], body);
        var t = stage1.FirstOrDefault();
        if (t is null) return null;
        try
        {
            var call = engine.BuildCall(t, inbound, body, stream: false);
            var client = factory.CreateClient("upstream");
            var req = new HttpRequestMessage(HttpMethod.Post, call.Url)
            {
                Content = new StringContent(call.Body, Encoding.UTF8, "application/json"),
            };
            foreach (var (k, v) in call.Headers) req.Headers.TryAddWithoutValidation(k, v);
            using var resp = await client.SendAsync(req, ctx.RequestAborted);
            if (!resp.IsSuccessStatusCode) return null;
            var up = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ctx.RequestAborted);
            var translated = Translators.TranslateResponse(up, call.OutboundFormat, inbound, model);
            var desc = ResponseText(translated, inbound);
            if (string.IsNullOrEmpty(desc)) return null;
            return InjectImageDescription(body, desc);
        }
        catch { return null; }
    }

    private static string? ResponseText(System.Text.Json.Nodes.JsonNode node, string inbound)
    {
        var el = JsonDocument.Parse(node.ToJsonString()).RootElement;
        if (inbound == "claude")
            return el.TryGetProperty("content", out var c)
                ? string.Concat(c.EnumerateArray()
                    .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
                    .Select(b => b.TryGetProperty("text", out var t2) ? t2.GetString() : "")) : null;
        if (inbound == "gemini")
            return el.TryGetProperty("candidates", out var cands)
                ? string.Concat(cands.EnumerateArray().SelectMany(cd =>
                    cd.TryGetProperty("content", out var cc) && cc.TryGetProperty("parts", out var pp)
                        ? pp.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : "")
                        : [])) : null;
        return el.TryGetProperty("choices", out var ch)
            ? ch.EnumerateArray().FirstOrDefault().TryGetProperty("message", out var m)
                && m.TryGetProperty("content", out var ct) ? ct.GetString() : null
            : el.TryGetProperty("output_text", out var ot) ? ot.GetString() : null;
    }

    /// <summary>Replace image content blocks with the extracted text description.</summary>
    private static JsonElement InjectImageDescription(JsonElement body, string description)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();
        bool IsImageBlock(System.Text.Json.Nodes.JsonNode? b)
            => b?["type"]?.GetValue<string>() is "image_url" or "image" or "input_image"
               || b?["inlineData"] is not null || b?["fileData"] is not null;
        var descBlock = new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = $"[Image description]: {description}" };

        // openai/claude messages[] / responses input[] / gemini contents[]
        foreach (var prop in new[] { "messages", "input", "contents" })
        {
            if (doc[prop] is not System.Text.Json.Nodes.JsonArray arr) continue;
            System.Text.Json.Nodes.JsonObject? lastUser = null;
            foreach (var m in arr)
            {
                var role = m?["role"]?.GetValue<string>();
                if (role is "user") lastUser = m as System.Text.Json.Nodes.JsonObject;
                var contentProp = prop == "contents" ? "parts" : "content";
                if (m?[contentProp] is System.Text.Json.Nodes.JsonArray blocks)
                    for (var i = blocks.Count - 1; i >= 0; i--)
                        if (IsImageBlock(blocks[i])) blocks.RemoveAt(i);
            }
            if (lastUser is not null)
            {
                var contentProp = prop == "contents" ? "parts" : "content";
                if (lastUser[contentProp] is System.Text.Json.Nodes.JsonArray ub)
                    ub.Add(descBlock.DeepClone());
                else if (lastUser[contentProp] is System.Text.Json.Nodes.JsonValue v
                         && v.TryGetValue<string>(out var s))
                    lastUser[contentProp] = s + $"\n[Image description]: {description}";
            }
        }
        return JsonDocument.Parse(doc.ToJsonString()).RootElement;
    }

    private static bool ShouldCascade(HttpStatusCode s) =>
        s == HttpStatusCode.TooManyRequests || (int)s >= 500 ||
        s is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    /// <summary>Pipe upstream SSE to the client, translating events; returns (promptTokens, completionTokens).</summary>
    private static async Task<(int, int)> StreamThrough(
        HttpContext ctx, HttpResponseMessage resp, string outbound, string inbound, string model)
    {
        int pt = 0, ct = 0;
        var state = new Translators.SseState();
        var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (!line.StartsWith("data:"))
            {
                if (line.StartsWith("event:"))
                    continue; // upstream event names recomputed below
                continue;
            }
            var data = line[5..].TrimStart();
            if (data == "[DONE]")
            {
                if (inbound is "openai" or "responsesApi")
                    await WriteSseLine(ctx, null, "[DONE]");
                break;
            }
            foreach (var (ev, payload) in Translators.TranslateSse(data, outbound, inbound, model, state))
            {
                if (payload == "[DONE]")
                {
                    if (inbound is "openai" or "responsesApi")
                        await WriteSseLine(ctx, null, "[DONE]");
                    continue;
                }
                TrackUsage(payload, inbound, ref pt, ref ct);
                await WriteSseLine(ctx, ev, payload);
            }
        }
        return (pt, ct);
    }

    private static async Task WriteSseLine(HttpContext ctx, string? ev, string data)
    {
        if (ev is not null)
            await ctx.Response.WriteAsync($"event: {ev}\n");
        await ctx.Response.WriteAsync($"data: {data}\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    private static void TrackUsage(string payload, string inbound, ref int pt, ref int ct)
    {
        try
        {
            var el = JsonDocument.Parse(payload).RootElement;
            if (inbound == "claude")
            {
                var usage = el.TryGetProperty("usage", out var u) ? u
                    : el.TryGetProperty("message", out var m) && m.TryGetProperty("usage", out u) ? u
                    : default;
                if (usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("input_tokens", out var i)) pt = i.GetInt32();
                    if (usage.TryGetProperty("output_tokens", out var o)) ct = o.GetInt32();
                }
            }
            else if (inbound == "gemini")
            {
                if (el.TryGetProperty("usageMetadata", out var u))
                {
                    if (u.TryGetProperty("promptTokenCount", out var i)) pt = i.GetInt32();
                    if (u.TryGetProperty("candidatesTokenCount", out var o)) ct = o.GetInt32();
                }
            }
            else if (el.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("prompt_tokens", out var i)) pt = i.GetInt32();
                if (u.TryGetProperty("completion_tokens", out var o)) ct = o.GetInt32();
            }
        }
        catch { }
    }

    private static (int, int) ExtractUsage(JsonNodeOrElement response, string inbound)
    {
        try
        {
            var el = response.Element;
            if (inbound == "claude" && el.TryGetProperty("usage", out var u))
                return (u.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0,
                        u.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0);
            if (inbound == "gemini" && el.TryGetProperty("usageMetadata", out var g))
                return (g.TryGetProperty("promptTokenCount", out var i) ? i.GetInt32() : 0,
                        g.TryGetProperty("candidatesTokenCount", out var o) ? o.GetInt32() : 0);
            if (el.TryGetProperty("usage", out var ou))
                return (ou.TryGetProperty("prompt_tokens", out var i) ? i.GetInt32() : 0,
                        ou.TryGetProperty("completion_tokens", out var o) ? o.GetInt32() : 0);
        }
        catch { }
        return (0, 0);
    }

    private readonly struct JsonNodeOrElement
    {
        public required JsonElement Element { get; init; }
        public static implicit operator JsonNodeOrElement(System.Text.Json.Nodes.JsonNode n) =>
            new() { Element = n is null ? default : JsonDocument.Parse(n.ToJsonString()).RootElement.Clone() };
    }

    private static async Task<bool> Authorized(HttpContext ctx, LlmRouterDbContext db) =>
        (await AuthenticatedKey(ctx, db)).Authed;

    private static async Task<(string Key, bool Authed)> AuthenticatedKey(HttpContext ctx, LlmRouterDbContext db)
    {
        // dashboard session → allow
        if (ctx.User.Identity?.IsAuthenticated == true) return ("dashboard", true);
        var key = ctx.Request.Headers.Authorization.FirstOrDefault() is { } a && a.StartsWith("Bearer ")
            ? a[7..].Trim()
            : ctx.Request.Headers["x-api-key"].FirstOrDefault()
              ?? ctx.Request.Query["key"].FirstOrDefault();
        if (string.IsNullOrEmpty(key)) return ("", false);
        var found = await db.ApiKeys.FirstOrDefaultAsync(k => k.Key == key && k.IsActive);
        return (key, found is not null);
    }

    private static async Task WriteError(HttpContext ctx, string inbound, string type, string message)
    {
        ctx.Response.ContentType = "application/json";
        var payload = inbound switch
        {
            "claude" => JsonSerializer.Serialize(new { type = "error", error = new { type, message } }),
            "gemini" => JsonSerializer.Serialize(new { error = new { code = 400, message, status = "INVALID_ARGUMENT" } }),
            _ => JsonSerializer.Serialize(new { error = new { message, type, code = type } }),
        };
        await ctx.Response.WriteAsync(payload);
    }
}

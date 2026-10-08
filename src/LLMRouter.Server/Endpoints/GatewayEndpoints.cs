using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;
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
            g.MapMethods("/responses", ["POST"], (HttpContext c) => Chat(c, "responsesApi"));
            g.MapGet("/models", Models);
            g.MapGet("/models/{*path}", Models);
            g.MapMethods("/{*path}", ["OPTIONS"], () => Results.Ok());
        }
        // Gemini inbound
        app.MapMethods("/v1beta/models/{model}:generateContent", ["POST"], (HttpContext c) => Chat(c, "gemini"));
        app.MapMethods("/v1beta/models/{model}:streamGenerateContent", ["POST"], (HttpContext c) => Chat(c, "gemini"));
        app.MapGet("/v1beta/models", Models);
        app.MapGet("/v1beta/models/{*path}", Models);
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

        var targets = await engine.ResolveAsync(model, body);
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
                    await engine.LogUsageAsync(target.Provider.Id, target.UpstreamModel,
                        target.Connection.Id, apiKey, inbound, pt, ct, "200", null, sw.ElapsedMilliseconds);
                }
                else
                {
                    var upstream = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ctx.RequestAborted);
                    var translated = Translators.TranslateResponse(upstream, call.OutboundFormat, inbound, model);
                    var (pt, ct) = ExtractUsage(translated, inbound);
                    ctx.Response.ContentType = "application/json";
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

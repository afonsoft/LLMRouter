using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        // SPEC-039: rate limiting — apiKey/model scopes pre-resolution; provider
        // scope is enforced per-target inside the dispatch loop below.
        var rateLimiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
        var rlRules = await db.RateLimits.Where(r => r.Enabled).ToListAsync();
        if (rlRules.Count > 0 && rateLimiter.CheckAndConsume(rlRules, apiKey, null, model) is { } ptVio)
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.Headers.RetryAfter = ptVio.RetryAfterSec.ToString();
            await WriteError(ctx, "openai", "rate_limit",
                $"Rate limit exceeded ({ptVio.Kind} {ptVio.Limit}/min on {ptVio.Scope} '{ptVio.ScopeValue}').");
            return;
        }

        // SPEC-031: plugin onRequest hooks (setModel/addHeaders) before resolution
        if (body.ValueKind == JsonValueKind.Object && rawBody is not null)
        {
            var bj = JsonNode.Parse(rawBody)!.AsObject();
            var plugins = await Core.Extras.PluginHooks.RegisteredAsync(db);
            var pluginHeaders = await Core.Extras.PluginHooks.ApplyRequestAsync(db, bj, plugins);
            if (pluginHeaders.Count > 0) ctx.Items["pluginHeaders"] = pluginHeaders;
            ctx.Items["plugins"] = plugins;
            var newRaw = JsonSerializer.SerializeToUtf8Bytes(bj);
            if (!newRaw.SequenceEqual(rawBody))
            {
                rawBody = newRaw;
                body = JsonDocument.Parse(newRaw).RootElement;
                if (body.TryGetProperty("model", out var pm)) model = pm.GetString() ?? model;
            }
        }

        // SPEC-015: chaos fault injection (latency + random 5xx, rules per route)
        if (await Core.Extras.Extras.ChaosDelayAsync(db, model) is { } chaosStatus)
        {
            ctx.Response.StatusCode = chaosStatus;
            await WriteError(ctx, "openai", "chaos", "Injected fault (chaos mode).");
            return;
        }

        var targets = await engine.ResolveAsync(model, body);
        targets = Core.Routing.MediaKinds.Filter(targets,
            Core.Routing.MediaKinds.KindForPath(ctx.Request.Path.Value ?? ""), t => t.Connection);
        if (targets.Count == 0)
        {
            ctx.Response.StatusCode = 400;
            await WriteError(ctx, "openai", "model_not_found",
                $"No active provider connection can serve '{model}'.");
            return;
        }

        var path = ctx.Request.Path.Value ?? "";
        var suffix = path[(path.IndexOf("/v1", StringComparison.Ordinal) + 3)..];
        var providerLimited = false;
        foreach (var target in targets)
        {
            // SPEC-039: provider-scope rate limits — skip this target, try the next
            if (rlRules.Count > 0 && rateLimiter.CheckAndConsume(rlRules, apiKey,
                    target.Provider.Id, model, providerOnly: true) is not null)
            {
                providerLimited = true;
                continue;
            }
            try
            {
                var client = await Core.Routing.ProxyPoolService.ClientForAsync(db, target.Connection)
                    ?? httpFactory.CreateClient("upstream");
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
                MergePluginHeaders(ctx, req);
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
                var scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
                var hookClient = httpFactory.CreateClient("webhook");
                var st = (int)resp.StatusCode; var msDone = sw.ElapsedMilliseconds; var mdl = model;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var sc = scopeFactory.CreateAsyncScope();
                        await Core.Extras.Extras.WebhooksDispatchAsync(
                            sc.ServiceProvider.GetRequiredService<Core.Data.LlmRouterDbContext>(),
                            hookClient, "request",
                            new { model = mdl, status = st, ms = msDone });
                    }
                    catch { }
                });
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
        // SPEC-039: every target was skipped by a provider-scope limit → 429
        if (providerLimited)
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.Headers.RetryAfter = "60";
            await WriteError(ctx, "openai", "rate_limit", "Rate limit exceeded on provider scope.");
            return;
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

        // SPEC-023: body validation — model + a message container are required
        var hasMsgs = body.TryGetProperty("messages", out var mm) && mm.ValueKind == JsonValueKind.Array && mm.GetArrayLength() > 0
            || body.TryGetProperty("input", out var ii) && ii.ValueKind is JsonValueKind.Array or JsonValueKind.String
            || body.TryGetProperty("contents", out var cc) && cc.ValueKind == JsonValueKind.Array;
        if (!hasMsgs && inbound != "gemini")
        {
            ctx.Response.StatusCode = 400;
            await WriteError(ctx, inbound, "invalid_request", "Request must include messages/input/contents.");
            return;
        }

        // SPEC-023: prompt-injection guard — settings.guardrails or env:
        // INPUT_SANITIZER_ENABLED (default true), _MODE (block|warn|log), _BLOCK_THRESHOLD (low|medium|high)
        var sdata = await Core.Usage.PricingService.SettingsDataAsync(db);
        string? S(string k) => sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("guardrails", out var g) && g.ValueKind == JsonValueKind.Object
            && g.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var gmode = S("mode")
            ?? Environment.GetEnvironmentVariable("INPUT_SANITIZER_MODE")
            ?? Environment.GetEnvironmentVariable("INJECTION_GUARD_MODE") ?? "warn";
        var genabled = (S("enabled") ?? "") is not "false"
            && Environment.GetEnvironmentVariable("INPUT_SANITIZER_ENABLED") is not "0" and not "false";
        if (genabled)
        {
            var hits = Core.Guardrails.PromptGuard.Detect(Core.Guardrails.PromptGuard.ScanText(body));
            if (hits.Count > 0)
            {
                var threshold = S("threshold")
                    ?? Environment.GetEnvironmentVariable("INPUT_SANITIZER_BLOCK_THRESHOLD") ?? "high";
                var block = gmode == "block"
                    && Core.Guardrails.PromptGuard.ShouldBlock(hits, threshold);
                _ = Core.Extras.Extras.AuditAsync(db, block ? "guardrail.block" : "guardrail.flag",
                    $"{inbound}: {string.Join(",", hits.Select(h => h.Name))}");
                if (block)
                {
                    ctx.Response.StatusCode = 400;
                    await WriteError(ctx, inbound, "prompt_injection_blocked",
                        $"Blocked by prompt-injection guard: {string.Join(", ", hits.Select(h => h.Name))}");
                    return;
                }
            }
        }

        // SPEC-008 token saver: compress prompt text when enabled
        var savedDetail = (JsonElement?)null;
        if (sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("tokenSaver", out var tsv)
            && tsv.ValueKind == JsonValueKind.Object
            && tsv.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True)
        {
            // SPEC-033: RTK filter config — when a filters array is configured,
            // run the named-filter pipeline; otherwise the legacy dedup path.
            if (tsv.TryGetProperty("filters", out _) || tsv.TryGetProperty("skipRules", out _)
                || tsv.TryGetProperty("preservePatterns", out _) || tsv.TryGetProperty("compressRoles", out _))
            {
                var rtk = Core.Extras.RtkFilters.Parse(sdata);
                var rr = Core.Extras.RtkFilters.ApplyToBody(body, rtk);
                body = rr.Body;
                var savedTokens = Core.Gateway.TokenSaver.SavedTokens(rr.SavedChars);
                if (savedTokens > 0)
                    savedDetail = JsonSerializer.SerializeToElement(new { tokensSaved = savedTokens });
            }
            else
            {
                var dedup = !tsv.TryGetProperty("dedup", out var dp) || dp.ValueKind != JsonValueKind.False;
                var r = Core.Gateway.TokenSaver.Apply(body, dedup);
                body = r.Body;
                var savedTokens = Core.Gateway.TokenSaver.SavedTokens(r.SavedChars);
                if (savedTokens > 0)
                    savedDetail = JsonSerializer.SerializeToElement(new { tokensSaved = savedTokens });
            }
        }

        // SPEC-026: context compression — settings.contextCompression {enabled,maxTokens}
        if (sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("contextCompression", out var ccs)
            && ccs.ValueKind == JsonValueKind.Object
            && ccs.TryGetProperty("enabled", out var ce) && ce.ValueKind == JsonValueKind.True)
        {
            var maxTok = ccs.TryGetProperty("maxTokens", out var mt) && mt.ValueKind == JsonValueKind.Number
                ? mt.GetInt32() : 8000;
            var cr = Core.Gateway.ContextCompressor.Apply(body, maxTok);
            if (cr.Compressed)
            {
                body = cr.Body;
                _ = Core.Extras.Extras.AuditAsync(db, "context.compress", $"{inbound}: dropped {cr.Dropped} message(s)");
            }
        }

        // SPEC-026: enabled skills injected as system-prompt context
        // (settings.skillsInjection.enabled — default on when skills exist)
        var injectSkills = !(sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("skillsInjection", out var si)
            && si.ValueKind == JsonValueKind.Object
            && si.TryGetProperty("enabled", out var se) && se.ValueKind == JsonValueKind.False);
        if (injectSkills)
        {
            var env = ctx.RequestServices.GetService<IWebHostEnvironment>();
            if (env is not null)
            {
                var skillText = await Endpoints.ToolsEndpoints.SkillPromptTextAsync(db, env);
                if (skillText is not null)
                    body = Core.Gateway.ContextCompressor.InjectSystem(body, skillText);
            }
        }

        var model = body.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
        if (inbound == "gemini" && string.IsNullOrEmpty(model))
        {
            var routeModel = ctx.Request.RouteValues["model"]?.ToString() ?? "";
            model = routeModel.Contains(':') ? routeModel[..routeModel.IndexOf(':')] : routeModel;
        }
        // SPEC-023: API key policy — restricted keys only reach AccessAllow providers/models
        if (model.Length > 0 && apiKey != "dashboard")
        {
            var keyRow = await db.ApiKeys.FirstOrDefaultAsync(k => k.Key == apiKey);
            if (keyRow is { AccessRestricted: true })
            {
                var allow = (keyRow.AccessAllow ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var ok = allow.Any(a => a.EndsWith('*')
                    ? model.StartsWith(a[..^1], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(a, model, StringComparison.OrdinalIgnoreCase));
                if (!ok)
                {
                    ctx.Response.StatusCode = 403;
                    await WriteError(ctx, inbound, "model_not_allowed",
                        $"API key is not allowed to use '{model}'.");
                    return;
                }
            }
        }

        var stream = body.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        if (inbound == "gemini" && ctx.Request.Path.Value?.Contains("streamGenerateContent") == true)
            stream = true;

        // SPEC-039: rate limiting — apiKey/model scopes pre-resolution; provider
        // scope is enforced per-target inside the dispatch loop below.
        var rateLimiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
        var rlRules = await db.RateLimits.Where(r => r.Enabled).ToListAsync();
        if (rlRules.Count > 0 && rateLimiter.CheckAndConsume(rlRules, apiKey, null, model) is { } rlVio)
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.Headers.RetryAfter = rlVio.RetryAfterSec.ToString();
            await WriteError(ctx, inbound, "rate_limit",
                $"Rate limit exceeded ({rlVio.Kind} {rlVio.Limit}/min on {rlVio.Scope} '{rlVio.ScopeValue}').");
            return;
        }

        // SPEC-015/023: chaos fault injection (rules match provider/model)
        if (await Core.Extras.Extras.ChaosDelayAsync(db, model) is { } chatChaos)
        {
            ctx.Response.StatusCode = chatChaos;
            await WriteError(ctx, inbound, "chaos", "Injected fault (chaos mode).");
            return;
        }

        // vision-adapter combo: stage-1 imageToText call rewrites the image into
        // a text description, then the remaining combo models serve the request
        var comboEntity = await db.Combos.FirstOrDefaultAsync(c => c.Name == model);
        if (comboEntity?.Kind == "vision-adapter"
            && ComboPlanner.DetectRequiredCapabilities(body).Contains("vision"))
        {
            var rewritten = await VisionAdapterAsync(ctx, engine, httpFactory, comboEntity, body, inbound, model);
            if (rewritten is { } rw) body = rw;
        }

        // SPEC-034: compression engine pipeline — combo-assigned pipeline >
        // settings.compression.stackedPipeline > comboOverrides > defaultMode.
        if (sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("compression", out var cv) && cv.ValueKind == JsonValueKind.Object)
        {
            var compNode = JsonNode.Parse(cv.GetRawText()) as JsonObject;
            var routingComboId = comboEntity?.Id ?? model;
            var assigned = routingComboId is null ? null
                : await db.CompressionComboAssignments.FirstOrDefaultAsync(a => a.RoutingComboId == routingComboId);
            JsonObject? comboPipeline = null;
            if (assigned is not null
                && await db.CompressionCombos.FindAsync(assigned.CompressionComboId) is { } comboRow)
                comboPipeline = JsonNode.Parse(comboRow.Pipeline) as JsonObject;

            var steps = Core.Compression.CompressionPipeline.ResolvePlan(compNode, routingComboId, comboPipeline);
            if (steps.Count > 0 && body.ValueKind == JsonValueKind.Object)
            {
                var bodyNode = JsonNode.Parse(body.GetRawText()) as JsonObject;
                if (bodyNode is not null)
                {
                    var engineCfgs = compNode?["engineConfigs"] as JsonObject;
                    var (outBody, runs) = Core.Compression.CompressionPipeline.Run(bodyNode, steps,
                        new Core.Compression.EngineOptions(
                            Model: model,
                            PrincipalId: apiKey),
                        engineCfgs);
                    if (runs.Count > 0)
                    {
                        body = JsonSerializer.SerializeToElement(outBody);
                        var now = DateTime.UtcNow.ToString("o");
                        foreach (var r in runs.Where(r => r.SavedChars > 0))
                            db.CompressionRuns.Add(new Core.Data.CompressionRun
                            {
                                Id = Guid.NewGuid().ToString("n")[..12],
                                Timestamp = now,
                                PrincipalId = apiKey,
                                Model = model,
                                EngineId = r.Engine,
                                BeforeChars = r.BeforeChars,
                                AfterChars = r.AfterChars,
                                ComboId = assigned?.CompressionComboId,
                            });
                        await db.SaveChangesAsync();
                    }
                }
            }
        }

        // SPEC-020: fusion (parallel fan-out + judge) and pipeline (sequential
        // chain) execute at the endpoint level, non-stream only
        if (comboEntity?.Kind == "fusion" && !stream)
        {
            await FusionAsync(ctx, engine, httpFactory, db, comboEntity, body, inbound, model, apiKey, sw);
            return;
        }
        if (comboEntity?.Kind == "pipeline" && !stream)
        {
            await PipelineAsync(ctx, engine, httpFactory, db, comboEntity, body, inbound, model, apiKey, sw);
            return;
        }

        // SPEC-031: plugin onRequest hooks before resolution
        if (body.ValueKind == JsonValueKind.Object)
        {
            var bj = JsonNode.Parse(body.GetRawText())!.AsObject();
            var plugins = await Core.Extras.PluginHooks.RegisteredAsync(db);
            var pluginHeaders = await Core.Extras.PluginHooks.ApplyRequestAsync(db, bj, plugins);
            if (pluginHeaders.Count > 0) ctx.Items["pluginHeaders"] = pluginHeaders;
            ctx.Items["plugins"] = plugins;
            body = JsonDocument.Parse(bj.ToJsonString()).RootElement;
            if (body.TryGetProperty("model", out var pm)) model = pm.GetString() ?? model;
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

        Exception? lastError = null;
        foreach (var target in targets)
        {
            // SPEC-039: provider-scope rate limits — skip this target, try the next
            if (rlRules.Count > 0 && rateLimiter.CheckAndConsume(rlRules, apiKey,
                    target.Provider.Id, model, providerOnly: true) is { } pvio)
            {
                lastError = new HttpRequestException(
                    $"rate limited ({pvio.Scope} '{pvio.ScopeValue}' {pvio.Kind} {pvio.Limit}/min)");
                continue;
            }
            try
            {
                var client = await Core.Routing.ProxyPoolService.ClientForAsync(db, target.Connection)
                    ?? httpFactory.CreateClient("upstream");
                var call = engine.BuildCall(target, inbound, body, stream);
                var req = new HttpRequestMessage(HttpMethod.Post, call.Url);
                foreach (var (k, v) in call.Headers)
                    req.Headers.TryAddWithoutValidation(k, v);
                MergePluginHeaders(ctx, req);
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
                    // SPEC-021: provider breaker (408/5xx) + model lockout (404/model-429)
                    Core.Resilience.ProviderBreaker.ReportStatus(target.Provider.Id,
                        (int)resp.StatusCode, target.Provider.AuthType);
                    if (Core.Resilience.ModelLockout.IsModelScoped((int)resp.StatusCode, errBody))
                        Core.Resilience.ModelLockout.Lock(target.Provider.Id,
                            target.Connection.Id, target.UpstreamModel);
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
                    Core.Resilience.ProviderBreaker.ReportSuccess(target.Provider.Id);
                    Core.Resilience.ModelLockout.Unlock(target.Provider.Id,
                        target.Connection.Id, target.UpstreamModel);
                    RecordStrategiesSuccess(target, body);
                    await engine.LogUsageAsync(target.Provider.Id, target.UpstreamModel,
                        target.Connection.Id, apiKey, inbound, pt, ct, "200", null, sw.ElapsedMilliseconds,
                        savedDetail);
                }
                else
                {
                    var upstream = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ctx.RequestAborted);
                    var translated = Translators.TranslateResponse(upstream, call.OutboundFormat, inbound, model);
                    if (ctx.Items["plugins"] is System.Text.Json.Nodes.JsonArray plArr
                        && translated is JsonObject tj)
                        Core.Extras.PluginHooks.ApplyResponse(plArr, tj);
                    var (pt, ct) = ExtractUsage(translated, inbound);
                    ctx.Response.ContentType = "application/json";
                    Core.Resilience.CooldownTracker.ReportSuccess(target.Connection.Id);
                    Core.Resilience.ProviderBreaker.ReportSuccess(target.Provider.Id);
                    Core.Resilience.ModelLockout.Unlock(target.Provider.Id,
                        target.Connection.Id, target.UpstreamModel);
                    RecordStrategiesSuccess(target, body);
                    await ctx.Response.WriteAsync(translated.ToJsonString(JsonOpts));
                    await engine.LogUsageAsync(target.Provider.Id, target.UpstreamModel,
                        target.Connection.Id, apiKey, inbound, pt, ct, "200", null, sw.ElapsedMilliseconds,
                        savedDetail);
                }
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ctx.RequestAborted.IsCancellationRequested)
            {
                lastError = ex;
                if (target == targets[^1]) break;
            }
        }

        // SPEC-039: every target was skipped by a provider-scope limit → 429
        if (lastError?.Message.StartsWith("rate limited", StringComparison.Ordinal) == true)
        {
            ctx.Response.StatusCode = 429;
            ctx.Response.Headers.RetryAfter = "60";
            await WriteError(ctx, inbound, "rate_limit", lastError.Message);
            return;
        }
        ctx.Response.StatusCode = 502;
        await WriteError(ctx, inbound, "upstream_unavailable",
            lastError?.Message ?? "All upstream targets failed.");
        await engine.LogUsageAsync(model, model, null, apiKey, inbound, 0, 0, "502",
            lastError?.Message, sw.ElapsedMilliseconds);
    }

    /// <summary>SPEC-020: remember the serving model for lkgp / cache-optimized.</summary>
    private static void RecordStrategiesSuccess(Core.Gateway.ResolvedTarget target, JsonElement body)
    {
        var modelRef = $"{target.Provider.Id}/{target.UpstreamModel}";
        Core.Routing.ComboStrategies.RecordSuccess(target.ComboName, modelRef);
        var hash = Core.Routing.ComboStrategies.PromptHash(body);
        if (hash.Length > 0) Core.Routing.ComboStrategies.RecordCacheHit(target.ComboName, hash, modelRef);
    }

    /// <summary>Non-stream upstream call; returns translated response text.</summary>
    private static async Task<(bool Ok, string? Text, JsonElement Raw)> CallTargetJsonAsync(
        HttpContext ctx, Core.Gateway.GatewayEngine engine, IHttpClientFactory httpFactory,
        LlmRouterDbContext db, Core.Gateway.ResolvedTarget target, string inbound, JsonElement body)
    {
        var client = await Core.Routing.ProxyPoolService.ClientForAsync(db, target.Connection)
            ?? httpFactory.CreateClient("upstream");
        var call = engine.BuildCall(target, inbound, body, false);
        var req = new HttpRequestMessage(HttpMethod.Post, call.Url);
        foreach (var (k, v) in call.Headers) req.Headers.TryAddWithoutValidation(k, v);
        req.Content = new StringContent(call.Body, Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req, ctx.RequestAborted);
        if (!resp.IsSuccessStatusCode) return (false, null, default);
        var upstream = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ctx.RequestAborted);
        var asOpenAi = Translators.TranslateResponse(upstream, call.OutboundFormat, "openai", "m");
        var text = asOpenAi["choices"] is JsonArray ch && ch.Count > 0
            && ch[0]?["message"]?["content"] is JsonValue jv && jv.TryGetValue<string>(out var st2)
            ? st2 : null;
        return (true, text, upstream);
    }

    private static JsonElement ChatResponseJson(string model, string content) =>
        JsonSerializer.SerializeToElement(new
        {
            id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24],
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" },
            },
            usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 },
        });

    private static async Task WriteSynthesizedResponse(HttpContext ctx, GatewayEngine engine,
        string inbound, string model, string apiKey, string content, long ms)
    {
        var final = Translators.TranslateResponse(ChatResponseJson(model, content), "openai", inbound, model);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(final.ToJsonString(JsonOpts));
        await engine.LogUsageAsync("combo", model, null, apiKey, inbound, 0, 0, "200", null, ms);
    }

    /// <summary>
    /// SPEC-020 fusion: fan out to panel members in parallel, then a judge model
    /// synthesizes one answer. Judge = member prefixed "judge:", else first member.
    /// </summary>
    private static async Task FusionAsync(HttpContext ctx, GatewayEngine engine,
        IHttpClientFactory httpFactory, LlmRouterDbContext db, Core.Data.Combo combo,
        JsonElement body, string inbound, string model, string apiKey, Stopwatch sw)
    {
        var members = JsonSerializer.Deserialize<List<string>>(combo.Models) ?? [];
        var judge = members.FirstOrDefault(x => x.StartsWith("judge:", StringComparison.Ordinal))?[6..];
        var panel = members.Where(x => !x.StartsWith("judge:", StringComparison.Ordinal)).ToList();
        var judgeModel = judge ?? panel.FirstOrDefault();

        var calls = panel.Select(async m2 =>
        {
            var ts = await engine.ResolveAsync(m2, body, ctx.RequestAborted);
            if (ts.Count == 0) return (Ok: false, Text: (string?)null, Model: m2);
            var r = await CallTargetJsonAsync(ctx, engine, httpFactory, db, ts[0], inbound, body);
            return (r.Ok, r.Text, Model: m2);
        }).ToList();
        var results = (await Task.WhenAll(calls)).Where(r => r.Ok).ToList();
        if (results.Count == 0 || judgeModel is null)
        {
            ctx.Response.StatusCode = 502;
            await WriteError(ctx, inbound, "upstream_unavailable", "Fusion panel: all members failed.");
            await engine.LogUsageAsync("combo", model, null, apiKey, inbound, 0, 0, "502", null, sw.ElapsedMilliseconds);
            return;
        }
        var joined = string.Join("\n\n---\n\n",
            results.Select(r => $"[{r.Model}]\n{r.Text}"));
        var judgeBody = JsonSerializer.SerializeToElement(new
        {
            model = judgeModel,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = $"You are a judge model. Synthesize the following {results.Count} candidate answers into the single best answer.\n\n{joined}",
                },
            },
        });
        var jt = await engine.ResolveAsync(judgeModel, judgeBody, ctx.RequestAborted);
        var synthesis = jt.Count > 0
            ? (await CallTargetJsonAsync(ctx, engine, httpFactory, db, jt[0], "openai", judgeBody)).Text
            : null;
        await WriteSynthesizedResponse(ctx, engine, inbound, model, apiKey,
            synthesis ?? results[0].Text ?? "", sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// SPEC-020 pipeline: members run sequentially — each model's output is the
    /// next one's input (mapped to the conductor chain semantics upstream).
    /// </summary>
    private static async Task PipelineAsync(HttpContext ctx, GatewayEngine engine,
        IHttpClientFactory httpFactory, LlmRouterDbContext db, Core.Data.Combo combo,
        JsonElement body, string inbound, string model, string apiKey, Stopwatch sw)
    {
        var members = JsonSerializer.Deserialize<List<string>>(combo.Models) ?? [];
        if (members.Count == 0)
        {
            ctx.Response.StatusCode = 400;
            await WriteError(ctx, inbound, "invalid_request", "Pipeline combo has no members.");
            return;
        }
        // initial input: last user message text in openai shape
        var openaiBody = Translators.Translate(body, inbound, "openai", model, false);
        var msgs = openaiBody["messages"]?.AsArray();
        var current = msgs?.LastOrDefault(m => m?["role"]?.GetValue<string>() == "user")?["content"];
        var currentText = current is JsonNode cv && cv is JsonValue v && v.TryGetValue<string>(out var s)
            ? s : current?.ToJsonString() ?? "";
        foreach (var member in members)
        {
            var stepBody = JsonSerializer.SerializeToElement(new
            {
                model = member,
                messages = new[] { new { role = "user", content = currentText } },
            });
            var ts = await engine.ResolveAsync(member, stepBody, ctx.RequestAborted);
            if (ts.Count == 0)
            {
                ctx.Response.StatusCode = 502;
                await WriteError(ctx, inbound, "upstream_unavailable",
                    $"Pipeline stage '{member}' has no available connection.");
                return;
            }
            var r = await CallTargetJsonAsync(ctx, engine, httpFactory, db, ts[0], "openai", stepBody);
            if (!r.Ok || r.Text is null)
            {
                ctx.Response.StatusCode = 502;
                await WriteError(ctx, inbound, "upstream_unavailable",
                    $"Pipeline stage '{member}' failed.");
                await engine.LogUsageAsync("combo", model, null, apiKey, inbound, 0, 0, "502", null, sw.ElapsedMilliseconds);
                return;
            }
            currentText = r.Text;
        }
        await WriteSynthesizedResponse(ctx, engine, inbound, model, apiKey, currentText, sw.ElapsedMilliseconds);
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
            var cdb = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
            var client = await Core.Routing.ProxyPoolService.ClientForAsync(cdb, t.Connection)
                ?? factory.CreateClient("upstream");
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

    /// <summary>SPEC-031: merge plugin addHeaders into the upstream request.</summary>
    private static void MergePluginHeaders(HttpContext ctx, HttpRequestMessage req)
    {
        if (ctx.Items["pluginHeaders"] is Dictionary<string, string> h)
            foreach (var kv in h) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
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

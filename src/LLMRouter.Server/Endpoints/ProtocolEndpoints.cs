using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Orchestration;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-012: MCP JSON-RPC server (/mcp), A2A agent card + tasks (/a2a),
/// conductor workflows, saved conversations, external MCP server registry.
/// </summary>
public static class ProtocolEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Call the local gateway chat endpoint with the caller's auth.</summary>
    private static async Task<string> ChatAsync(HttpContext ctx, IHttpClientFactory hf,
        string model, string prompt)
    {
        var c = hf.CreateClient("upstream");
        var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/v1/chat/completions";
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (ctx.Request.Headers.Authorization.FirstOrDefault() is { } a)
            req.Headers.TryAddWithoutValidation("Authorization", a);
        req.Content = JsonContent.Create(new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
            stream = false,
        });
        var resp = await c.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        try
        {
            return JsonDocument.Parse(json).RootElement
                .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? json;
        }
        catch { return json; }
    }

    private static JsonObject RpcResult(object id, object result) => new()
    { ["jsonrpc"] = "2.0", ["id"] = JsonValue.Create(id), ["result"] = JsonSerializer.SerializeToNode(result) };

    private static JsonObject RpcError(object? id, int code, string msg) => new()
    { ["jsonrpc"] = "2.0", ["id"] = id is null ? null : JsonValue.Create(id), ["error"] = new JsonObject { ["code"] = code, ["message"] = msg } };

    public static void MapProtocolEndpoints(this WebApplication app)
    {
        // ---- MCP server (JSON-RPC 2.0) — expose gateway models as tools ----
        MapA2aEndpoints(app);
        app.MapPost("/mcp", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf, LLMRouter.Core.Registry.ProviderRegistry registry) =>
        {
            JsonElement rpc;
            try { rpc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { return Results.Json(RpcError(null, -32700, "parse error")); }
            return await HandleRpcAsync(ctx, rpc, db, hf, registry);
        });

        // SPEC-025: SSE transport — GET /mcp/sse emits the POST endpoint, then
        // streams `event: message` responses; POST /mcp/sse/message?sessionId
        // enqueues a JSON-RPC request onto that session's stream.
        var sseSessions = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Channels.Channel<string>>();
        app.MapGet("/mcp/sse", async (HttpContext ctx) =>
        {
            var sid = Guid.NewGuid().ToString("N");
            var ch = System.Threading.Channels.Channel.CreateUnbounded<string>();
            sseSessions[sid] = ch;
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            await ctx.Response.WriteAsync($"event: endpoint\ndata: /mcp/sse/message?sessionId={sid}\n\n");
            await ctx.Response.Body.FlushAsync();
            try
            {
                await foreach (var msg in ch.Reader.ReadAllAsync(ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync($"event: message\ndata: {msg}\n\n");
                    await ctx.Response.Body.FlushAsync();
                }
            }
            catch (OperationCanceledException) { }
            finally { sseSessions.TryRemove(sid, out _); }
        });
        app.MapPost("/mcp/sse/message", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf, LLMRouter.Core.Registry.ProviderRegistry registry) =>
        {
            var sid = ctx.Request.Query["sessionId"].ToString();
            if (sid == "" || !sseSessions.TryGetValue(sid, out var ch))
                return Results.Json(RpcError(null, -32000, "unknown sessionId"), statusCode: 404);
            JsonElement rpc;
            try { rpc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { ch.Writer.TryWrite(RpcError(null, -32700, "parse error").ToJsonString()); return Results.Accepted(); }
            var res = await HandleRpcAsync(ctx, rpc, db, hf, registry);
            if (res is IValueHttpResult vr && vr.Value is not null)
                ch.Writer.TryWrite(JsonSerializer.Serialize(vr.Value, JsonOpts));
            return Results.Accepted();
        });
    }

    private static async Task<IResult> HandleRpcAsync(HttpContext ctx, JsonElement rpc,
        LlmRouterDbContext db, IHttpClientFactory hf, LLMRouter.Core.Registry.ProviderRegistry registry)
    {
            var method = rpc.TryGetProperty("method", out var m) ? m.GetString() : "";
            var id = rpc.TryGetProperty("id", out var i) ? (object)i.Clone() : null;

            switch (method)
            {
                case "initialize":
                    return Results.Json(RpcResult(id!, new
                    {
                        protocolVersion = "2025-06-18",
                        serverInfo = new { name = "llmrouter", version = "0.1" },
                        capabilities = new { tools = new { } },
                    }));
                case "notifications/initialized":
                case "ping":
                    return Results.Ok();
                case "tools/list":
                {
                    // SPEC-019: canonical tools + dynamic chat__<model> tools
                    var models = await db.Combos.Select(c => c.Name).ToListAsync();
                    var dynamic = models.Select(mn => (object)new
                    {
                        name = $"chat__{mn.Replace('-', '_').Replace('/', '_')}",
                        description = $"Chat via combo/model '{mn}'",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new { prompt = new { type = "string" } },
                            required = new[] { "prompt" },
                        },
                    });
                    var scopes = await ToolScopesAsync(ctx, db);
                    var tools = Mcp.McpTools.ListTools().Concat(dynamic);
                    if (scopes is not null)
                        tools = tools.Where(t => ScopeAllows(scopes, ((JsonElement)JsonSerializer.SerializeToElement(t)).GetProperty("name").GetString()!));
                    return Results.Json(RpcResult(id!, new
                    {
                        tools,
                    }));
                }
                case "tools/call":
                {
                    var prms = rpc.GetProperty("params");
                    var toolName = prms.GetProperty("name").GetString()!;
                    var scopes2 = await ToolScopesAsync(ctx, db);
                    if (scopes2 is not null && !ScopeAllows(scopes2, toolName))
                        return Results.Json(RpcError(id, -32602, $"tool not permitted by key scopes: {toolName}"));
                    var callArgs = prms.TryGetProperty("arguments", out var ca) ? ca.Clone() : default;
                    string text;
                    if (toolName.StartsWith("chat__"))
                    {
                        var model = toolName["chat__".Length..].Replace('_', '-');
                        var prompt = callArgs.ValueKind == JsonValueKind.Object
                            && callArgs.TryGetProperty("prompt", out var pr) ? pr.GetString() ?? "" : "";
                        text = await ChatAsync(ctx, hf, model, prompt);
                    }
                    else
                    {
                        var result = await Mcp.McpTools.DispatchAsync(toolName, callArgs, ctx, db, registry, hf,
                            (m, p) => ChatAsync(ctx, hf, m, p));
                        if (result is null)
                            return Results.Json(RpcError(id, -32602, $"unknown tool or missing args: {toolName}"));
                        text = JsonSerializer.Serialize(result, JsonOpts);
                    }
                    return Results.Json(RpcResult(id!, new
                    {
                        content = new[] { new { type = "text", text } },
                    }));
                }
                default:
                    return Results.Json(RpcError(id, -32601, "method not found"));
            }
    }

    /// <summary>SPEC-025: tool scopes — when the caller's API key is access
    /// restricted, its AccessAllow globs also gate MCP tools (absent = open).
    /// Denied tools are filtered out of tools/list and rejected on tools/call.</summary>
    private static async Task<string[]?> ToolScopesAsync(HttpContext ctx, LlmRouterDbContext db)
    {
        var raw = ctx.Request.Headers["x-api-key"].FirstOrDefault()
            ?? ctx.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
        if (string.IsNullOrEmpty(raw)) return null;
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Key == raw && k.IsActive);
        if (key is null || !key.AccessRestricted || string.IsNullOrWhiteSpace(key.AccessAllow)) return null;
        return key.AccessAllow.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool ScopeAllows(string[] scopes, string tool) => scopes.Any(sc =>
        sc == "*" || sc == tool ||
        (sc.EndsWith('*') && tool.StartsWith(sc[..^1], StringComparison.Ordinal)));

    private static void MapA2aEndpoints(WebApplication app)
    {
        // ---- A2A agent card + JSON-RPC tasks ----
        app.MapGet("/.well-known/agent.json", (HttpContext ctx) => Results.Json(new
        {
            name = "LLMRouter",
            description = "Unified LLM router — send tasks with a model field to route to any provider or combo.",
            url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/a2a",
            version = "0.1",
            capabilities = new { streaming = false, pushNotifications = false },
            skills = new[] { new { id = "chat", name = "Chat completion via any configured model/combo" } },
        }, JsonOpts));

        app.MapPost("/a2a", async (HttpContext ctx, IHttpClientFactory hf) =>
        {
            var rpc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = rpc.TryGetProperty("method", out var m) ? m.GetString() : "";
            var id = rpc.TryGetProperty("id", out var i) ? (object)i.Clone() : null;
            if (method is not ("tasks/send" or "message/send"))
                return Results.Json(RpcError(id, -32601, "method not found"));
            var msg = rpc.GetProperty("params");
            var text = msg.TryGetProperty("message", out var mm) && mm.TryGetProperty("parts", out var parts)
                ? parts[0].GetProperty("text").GetString() ?? ""
                : msg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var model = msg.TryGetProperty("model", out var mo) ? mo.GetString() ?? "" : "";
            var output = await ChatAsync(ctx, hf, model, text);
            return Results.Json(RpcResult(id!, new
            {
                id = Guid.NewGuid().ToString("N"),
                status = new { state = "completed" },
                artifacts = new[] { new { parts = new[] { new { type = "text", text = output } } } },
            }));
        });

        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- conversations (playground save/load) ----
        g.MapGet("/conversations", async (LlmRouterDbContext db) =>
        {
            var rows = await db.Kv.Where(k => k.Scope == "conversations").ToListAsync();
            return Results.Json(new
            {
                conversations = rows.Select(r => new
                {
                    id = r.Key,
                    title = JsonNode.Parse(r.Value)?["title"]?.GetValue<string>(),
                    updatedAt = JsonNode.Parse(r.Value)?["updatedAt"]?.GetValue<string>(),
                }),
            }, JsonOpts);
        });
        g.MapGet("/conversations/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var r = await db.Kv.FindAsync("conversations", id);
            return r is null ? Results.NotFound() : Results.Content(r.Value, "application/json");
        });
        g.MapPut("/conversations/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            var node = JsonNode.Parse(body.GetRawText())!.AsObject();
            node["updatedAt"] = DateTimeOffset.UtcNow.ToString("o");
            var row = await db.Kv.FindAsync("conversations", id)
                ?? db.Kv.Add(new KvEntry { Scope = "conversations", Key = id }).Entity;
            row.Value = node.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });
        g.MapDelete("/conversations/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var r = await db.Kv.FindAsync("conversations", id);
            if (r is not null) { db.Kv.Remove(r); await db.SaveChangesAsync(); }
            return Results.Ok();
        });

        // ---- conductor workflows ----
        g.MapGet("/conductor/workflows", async (LlmRouterDbContext db) =>
        {
            var rows = await db.Kv.Where(k => k.Scope == "conductorWorkflows").ToListAsync();
            return Results.Json(new { workflows = rows.Select(r => JsonNode.Parse(r.Value)) }, JsonOpts);
        });
        g.MapPut("/conductor/workflows/{name}", async (string name, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            var row = await db.Kv.FindAsync("conductorWorkflows", name)
                ?? db.Kv.Add(new KvEntry { Scope = "conductorWorkflows", Key = name }).Entity;
            row.Value = body;
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });
        g.MapPost("/conductor/run", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf) =>
        {
            var b = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            var wf = b.TryGetProperty("workflow", out var w) ? w.GetRawText()
                : (await db.Kv.FindAsync("conductorWorkflows",
                    b.GetProperty("name").GetString()))?.Value;
            if (wf is null) return Results.BadRequest(new { error = "workflow required" });
            var input = b.TryGetProperty("input", out var i) ? i.GetString() ?? "" : "";
            var steps = Conductor.Parse(wf);
            var result = await Conductor.RunAsync(steps, input,
                (model, prompt) => ChatAsync(ctx, hf, model, prompt));
            return Results.Json(new
            {
                final = result.Final,
                steps = result.Steps.Select(s => new { s.Name, s.Model, s.Ms, output = s.Output }),
            }, JsonOpts);
        });

        // ---- external MCP server registry (stdio/sse/http records) ----
        g.MapGet("/mcp-servers", async (LlmRouterDbContext db) =>
        {
            var rows = await db.Kv.Where(k => k.Scope == "mcpServers").ToListAsync();
            return Results.Json(new { servers = rows.Select(r => JsonNode.Parse(r.Value)) }, JsonOpts);
        });
        g.MapPut("/mcp-servers/{name}", async (string name, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            var node = JsonNode.Parse(body)!.AsObject();
            node["name"] = name;
            var row = await db.Kv.FindAsync("mcpServers", name)
                ?? db.Kv.Add(new KvEntry { Scope = "mcpServers", Key = name }).Entity;
            row.Value = node.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });
        g.MapDelete("/mcp-servers/{name}", async (string name, LlmRouterDbContext db) =>
        {
            var r = await db.Kv.FindAsync("mcpServers", name);
            if (r is not null) { db.Kv.Remove(r); await db.SaveChangesAsync(); }
            return Results.Ok();
        });
    }
}

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
    { ["jsonrpc"] = "2.0", ["id"] = JsonValue.Create(id), ["result"] = JsonValue.Create(JsonSerializer.SerializeToNode(result)) };

    private static JsonObject RpcError(object? id, int code, string msg) => new()
    { ["jsonrpc"] = "2.0", ["id"] = id is null ? null : JsonValue.Create(id), ["error"] = new JsonObject { ["code"] = code, ["message"] = msg } };

    public static void MapProtocolEndpoints(this WebApplication app)
    {
        // ---- MCP server (JSON-RPC 2.0) — expose gateway models as tools ----
        app.MapPost("/mcp", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf) =>
        {
            JsonElement rpc;
            try { rpc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { return Results.Json(RpcError(null, -32700, "parse error")); }
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
                    var models = await db.Combos.Select(c => c.Name).ToListAsync();
                    var tools = models.Select(mn => (object)new
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
                    return Results.Json(RpcResult(id!, new { tools }));
                }
                case "tools/call":
                {
                    var args = rpc.GetProperty("params");
                    var name = args.GetProperty("name").GetString()!.Replace("chat__", "").Replace('_', '-');
                    var prompt = args.GetProperty("arguments").GetProperty("prompt").GetString() ?? "";
                    var text = await ChatAsync(ctx, hf, name, prompt);
                    return Results.Json(RpcResult(id!, new
                    {
                        content = new[] { new { type = "text", text } },
                    }));
                }
                default:
                    return Results.Json(RpcError(id, -32601, "method not found"));
            }
        });

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

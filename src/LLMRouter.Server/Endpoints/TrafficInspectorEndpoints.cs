using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Server.Services.TrafficInspector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-127: /api/tools/traffic-inspector/* — verbatim port of the upstream
/// OmniRoute traffic-inspector route tree (16 routes).
/// </summary>
public static class TrafficInspectorEndpoints
{
    private const int AnnotationMaxChars = 10_000;
    private const string InternalTokenEnv = "LLMR_INSPECTOR_INTERNAL_INGEST_TOKEN";
    private const int HttpProxyDefaultPort = 9080;
    private const string ReplaySourceHeader = "x-llmrouter-source";
    private const string Base = "/api/tools/traffic-inspector";

    private static readonly string InternalToken
        = Environment.GetEnvironmentVariable(InternalTokenEnv) is { Length: >= 16 } t
            ? t : Guid.NewGuid().ToString("N");

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup(Base);
        MapRequests(g);
        MapWs(app);
        MapSessions(g);
        MapExport(g);
        MapCaptureModes(g);
        MapHosts(g);
        MapInternalIngest(g);
    }

    /// <summary>Map routes + wire the agent-bridge capture sink into the shared buffer.</summary>
    public static void MapAndWire(WebApplication app)
    {
        Map(app);
        var ti = app.Services.GetRequiredService<TrafficInspectorService>();
        var ab = app.Services.GetRequiredService<Services.AgentBridge.AgentBridgeService>();
        ab.Proxy.SetCapture(r =>
        {
            if (ti.Buffer.Get(r.Id) is null) ti.Buffer.Push(r); else ti.Buffer.Update(r.Id, r);
        });
    }

    private static void MapRequests(RouteGroupBuilder g)
    {
        g.MapGet("/requests", (HttpContext ctx, TrafficInspectorService svc) =>
        {
            var q = ctx.Request.Query;
            var filters = new ListFilters(
                q["profile"].Count > 0 ? q["profile"].ToString() : null,
                q["host"].Count > 0 ? q["host"].ToString() : null,
                q["agent"].Count > 0 ? q["agent"].ToString() : null,
                q["status"].Count > 0 ? q["status"].ToString() : null,
                q["source"].Count > 0 ? q["source"].ToString() : null,
                q["sessionId"].Count > 0 ? q["sessionId"].ToString() : null);
            return Results.Ok(new { requests = svc.Buffer.List(filters) });
        });
        g.MapDelete("/requests", (TrafficInspectorService svc) =>
        {
            svc.Buffer.Clear();
            return Results.NoContent();
        });
        g.MapGet("/requests/{id}", (string id, TrafficInspectorService svc) =>
            svc.Buffer.Get(id) is { } r ? Results.Ok(r) : Results.NotFound());
        g.MapPut("/requests/{id}/annotation", async (string id, HttpContext ctx, TrafficInspectorService svc) =>
        {
            var req = svc.Buffer.Get(id);
            if (req is null) return Results.NotFound();
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var val = body?["annotation"]?.GetValue<string>() ?? body?["note"]?.GetValue<string>();
            if (val is not null && val.Length > AnnotationMaxChars)
                return Results.BadRequest(new { error = "annotation exceeds 10000 chars" });
            req.Annotation = val;
            svc.Buffer.Update(id, req);
            return Results.Ok(new { ok = true });
        });
        g.MapPost("/requests/{id}/replay", async (string id, TrafficInspectorService svc) =>
            await Replay(id, svc));
    }

    private static void MapWs(WebApplication app)
    {
        app.Map(Base + "/ws", async (HttpContext ctx, TrafficInspectorService svc) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var snapshot = new JsonObject { ["type"] = "snapshot", ["data"] = JsonSerializer.SerializeToNode(svc.Buffer.List()) };
            await Send(ws, snapshot, ctx.RequestAborted);
            var sendQ = new Queue<JsonObject>();
            var unsub = svc.Buffer.Subscribe(e => { lock (sendQ) sendQ.Enqueue(e); });
            try
            {
                while (ws.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
                {
                    JsonObject? ev;
                    lock (sendQ) ev = sendQ.Count > 0 ? sendQ.Dequeue() : null;
                    if (ev is null) { await Task.Delay(50, ctx.RequestAborted); continue; }
                    await Send(ws, ev, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally { unsub(); }
        });
    }

    private static void MapSessions(RouteGroupBuilder g)
    {
        g.MapGet("/sessions", async (TrafficInspectorService svc) => Results.Ok(new { sessions = await svc.ListSessions() }));
        g.MapPost("/sessions", async (HttpContext ctx, TrafficInspectorService svc) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var created = await svc.CreateSession(body?["name"]?.GetValue<string>());
            return Results.Json(created, statusCode: 201);
        });
        g.MapGet("/sessions/{id}", async (string id, TrafficInspectorService svc) =>
            await svc.GetSession(id) is { } s
                ? Results.Ok(new
                {
                    id = s.Id, name = s.Name, started_at = s.StartedAt, ended_at = s.EndedAt,
                    request_count = s.RequestCount, profile = s.Profile,
                    requests = (await svc.GetSessionRequests(id))
                        .Select(p => JsonNode.Parse(p)!).ToList(),
                })
                : Results.NotFound());
        g.MapPatch("/sessions/{id}", async (string id, HttpContext ctx, TrafficInspectorService svc) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var action = body?["action"]?.GetValue<string>();
            if (action is not ("stop" or "rename")) return Results.BadRequest(new { error = "action must be stop|rename" });
            return await svc.PatchSession(id, action, body!["name"]?.GetValue<string>()) is { } s
                ? Results.Ok(s) : Results.NotFound();
        });
        g.MapDelete("/sessions/{id}", async (string id, TrafficInspectorService svc) =>
            await svc.DeleteSession(id) ? Results.NoContent() : Results.NotFound());
        g.MapPost("/sessions/{id}/requests", async (string id, HttpContext ctx, TrafficInspectorService svc) =>
        {
            var payload = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            if (payload is null) return Results.BadRequest(new { error = "request payload required" });
            var seq = await svc.AppendSessionRequest(id, payload.ToJsonString());
            return seq is null ? Results.BadRequest(new { error = "payload too large or session missing" })
                : Results.Ok(new { seq });
        });
        g.MapGet("/sessions/{id}/export.har", async (string id, HttpContext ctx, TrafficInspectorService svc) =>
        {
            var session = await svc.GetSession(id);
            if (session is null) return Results.NotFound();
            var reqs = (await svc.GetSessionRequests(id))
                .Select(p => JsonNode.Parse(p)!.Deserialize<InterceptedRequest>()!).ToList();
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{(session.Name ?? "traffic")}.har\"";
            return Results.Json(HarExport.ToHar(reqs));
        });
    }

    private static void MapExport(RouteGroupBuilder g)
    {
        g.MapGet("/export.har", (HttpContext ctx, TrafficInspectorService svc) =>
        {
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"traffic.har\"";
            return Results.Json(HarExport.ToHar(svc.Buffer.List()));
        });
    }

    private static void MapCaptureModes(RouteGroupBuilder g)
    {
        g.MapGet("/capture-modes", async (HttpContext ctx, TrafficInspectorService svc,
            IServiceScopeFactory scopes) =>
        {
            var hosts = await svc.ListHosts();
            var mitm = await LLMRouter.Server.Mitm.MitmPolicy.ReadAsync(scopes, ctx.RequestAborted);
            return Results.Ok(new
            {
                agentBridge = new { enabled = true },
                customHosts = new { count = hosts.Count, enabledCount = hosts.Count(h => h.Enabled) },
                httpProxy = new { running = svc.HttpProxy.Running, port = svc.HttpProxy.Running ? svc.HttpProxy.Port : (int?)null },
                systemProxy = new { applied = svc.SystemProxyApplied, guardUntil = svc.SystemProxyGuardUntil, port = svc.SystemProxyPort },
                tlsIntercept = new { enabled = mitm.TlsIntercept },
            });
        });
        g.MapPost("/capture-modes/http-proxy", async (HttpContext ctx, TrafficInspectorService svc) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var action = body?["action"]?.GetValue<string>();
            if (action is not ("start" or "stop")) return Results.BadRequest(new { error = "action must be start|stop" });
            var port = body!["port"]?.GetValue<int>() ?? HttpProxyDefaultPort;
            var result = await svc.HttpProxyAction(action, port);
            return action == "start" ? Results.Json(result, statusCode: 201) : Results.Ok(result);
        });
        g.MapPost("/capture-modes/system-proxy", async (HttpContext ctx, TrafficInspectorService svc) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var action = body?["action"]?.GetValue<string>();
            if (action is not ("apply" or "revert")) return Results.BadRequest(new { error = "action must be apply|revert" });
            var r = await svc.SystemProxyAction(action, body!["port"]?.GetValue<int>(), body["guardMinutes"]?.GetValue<int>());
            return Results.Ok(r);
        });
        g.MapPost("/capture-modes/tls-intercept", async (HttpContext ctx, TrafficInspectorService svc) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var enabled = body?["enabled"]?.GetValue<bool>() ?? false;
            return Results.Ok(await svc.SetTlsIntercept(enabled));
        });
    }

    private static void MapHosts(RouteGroupBuilder g)
    {
        g.MapGet("/hosts", async (TrafficInspectorService svc) => Results.Ok(new { hosts = await svc.ListHosts() }));
        g.MapPost("/hosts", async (HttpContext ctx, TrafficInspectorService svc) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var host = body?["host"]?.GetValue<string>();
            if (string.IsNullOrEmpty(host)) return Results.BadRequest(new { error = "host required" });
            await svc.AddHost(host, body!["kind"]?.GetValue<string>() ?? "custom", body["label"]?.GetValue<string>());
            return Results.Json(new { ok = true }, statusCode: 201);
        });
        g.MapPatch("/hosts/{host}", async (string host, HttpContext ctx, TrafficInspectorService svc) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var enabled = body?["enabled"]?.GetValue<bool>() ?? false;
            return await svc.PatchHost(host, enabled) ? Results.Ok(new { ok = true }) : Results.NotFound();
        });
        g.MapDelete("/hosts/{host}", async (string host, TrafficInspectorService svc) =>
        {
            await svc.RemoveHost(host);
            return Results.NoContent();
        });
    }

    private static void MapInternalIngest(RouteGroupBuilder g)
    {
        g.MapPost("/internal/ingest", async (HttpContext ctx, TrafficInspectorService svc) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : "";
            var exp = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(InternalToken));
            var got = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token));
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(exp, got))
                return Results.Unauthorized();
            var body = await ctx.Request.ReadFromJsonAsync<InterceptedRequest>();
            if (body is null || body.Id.Length == 0 || body.Host.Length == 0 || body.Source.Length == 0)
                return Results.BadRequest(new { error = "missing required fields" });
            svc.Buffer.Push(body);
            return Results.Ok(new { ok = true });
        });
    }

    private static async Task Send(WebSocket ws, JsonObject o, CancellationToken ct)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()),
            WebSocketMessageType.Text, true, ct);

    /// <summary>POST /requests/{id}/replay — forwards via local base with the replay marker.</summary>
    private static async Task<IResult> Replay(string id, TrafficInspectorService svc)
    {
        var req = svc.Buffer.Get(id);
        if (req is null) return Results.NotFound();
        var defaultBase = new UriBuilder("http", "127.0.0.1", 5000).Uri.ToString().TrimEnd('/');
        var baseUrl = Environment.GetEnvironmentVariable("LLMROUTER_BASE_URL") ?? defaultBase;
        var url = $"{baseUrl.TrimEnd('/')}{req.Path}";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var msg = new HttpRequestMessage(new HttpMethod(req.Method), url);
        foreach (var (k, v) in req.RequestHeaders)
        {
            if (k is "host" or "connection" or "content-length") continue;
            if (v.Contains("***") || v.Contains("[REDACTED]")) continue;
            msg.Headers.TryAddWithoutValidation(k, v);
        }
        msg.Headers.TryAddWithoutValidation(ReplaySourceHeader, "inspector-replay");
        if (req.RequestBody is not null) msg.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(req.RequestBody));
        try
        {
            using var resp = await http.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();
            return Results.Ok(new
            {
                status = (int)resp.StatusCode,
                responseBody = body,
                replayedUrl = url,
            });
        }
        catch (Exception e) { return Results.BadRequest(new { error = MaskSecrets.Mask(e.Message) }); }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-044: generated OpenAPI doc + server-side "try it" dispatch.</summary>
public static class OpenApiExplorerEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static JsonObject? _specCache;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/openapi/spec", (EndpointDataSource eps) =>
        {
            _specCache ??= BuildSpec(eps);
            return Results.Json(_specCache, JsonOpts);
        });

        // self-dispatch through the app's own pipeline via the named "batches" client
        g.MapPost("/openapi/try", async (HttpContext ctx, IHttpClientFactory hf) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var path = b.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            var method = (b.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET").ToUpperInvariant();
            if (!path.StartsWith('/'))
                return Results.Json(new { error = "path must start with /" }, JsonOpts, statusCode: 400);
            if (method == "TRACE" || method == "CONNECT")
                return Results.Json(new { error = "method not allowed" }, JsonOpts, statusCode: 400);

            var http = hf.CreateClient("batches");
            http.BaseAddress = new Uri($"{ctx.Request.Scheme}://{ctx.Request.Host}");
            var req = new HttpRequestMessage(new HttpMethod(method), path);
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", auth);
            var cookie = ctx.Request.Headers.Cookie.ToString();
            if (cookie.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            if (b.TryGetProperty("body", out var body) && method is "POST" or "PUT" or "PATCH" or "DELETE")
                req.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");

            var sw = Stopwatch.StartNew();
            var resp = await http.SendAsync(req);
            sw.Stop();
            var text = await resp.Content.ReadAsStringAsync();
            return Results.Json(new { status = (int)resp.StatusCode, ms = sw.ElapsedMilliseconds, body = text }, JsonOpts);
        });
    }

    private static JsonObject BuildSpec(EndpointDataSource eps)
    {
        var paths = new JsonObject();
        foreach (var ep in eps.Endpoints.OfType<RouteEndpoint>().OrderBy(e => e.RoutePattern.RawText))
        {
            var path = "/" + (ep.RoutePattern.RawText?.TrimStart('/') ?? "");
            var methods = ep.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods;
            if (methods is null || methods.Count == 0) continue;
            var name = ep.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName
                       ?? ep.DisplayName ?? "";

            foreach (var method in methods)
            {
                if (paths[path] is not JsonObject ops) { ops = new JsonObject(); paths[path] = ops; }
                ops[method.ToLowerInvariant()] = new JsonObject
                {
                    ["summary"] = name.Length > 0 ? name : $"{method} {path}",
                    ["tags"] = new JsonArray(path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "root"),
                };
                if (method is "POST" or "PUT" or "PATCH" or "DELETE")
                    ops[method.ToLowerInvariant()]!["requestBody"] = new JsonObject
                    {
                        ["required"] = false,
                        ["content"] = new JsonObject
                        {
                            ["application/json"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object" } },
                        },
                    };
            }
        }

        // hand-written schemas for the /v1 compat surface
        if (paths["/v1/chat/completions"] is JsonObject chatOps && chatOps["post"] is JsonObject chatPost)
        {
            chatPost["summary"] = "Chat completions (OpenAI-compatible)";
            chatPost["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject
                    {
                        ["schema"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["required"] = new JsonArray("model", "messages"),
                            ["properties"] = new JsonObject
                            {
                                ["model"] = new JsonObject { ["type"] = "string" },
                                ["messages"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
                                ["stream"] = new JsonObject { ["type"] = "boolean" },
                            },
                        },
                    },
                },
            };
        }

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject { ["title"] = "LLMRouter", ["version"] = "1.0.0" },
            ["paths"] = paths,
        };
    }
}

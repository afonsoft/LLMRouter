using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-051: VSCode token surface — /v1/vscode/{token}/* serves an
/// Ollama-style API (api/chat, api/show, api/tags, api/version) plus the
/// OpenAI surface, so extensions like Continue/Cline can point at LLMRouter
/// via their Ollama/OpenAI provider settings without a dashboard key.
/// Usage is attributed to "vscode:{id}"; allowedCombos scopes which combos
/// the token may dispatch.
/// </summary>
public static class VscodeEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static async Task<VscodeToken?> Auth(HttpContext ctx, LlmRouterDbContext db, string token)
    {
        var t = await db.VscodeTokens.FirstOrDefaultAsync(x => x.Token == token && x.IsActive);
        if (t is null)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(
                """{"error":{"message":"Invalid or revoked vscode token.","type":"invalid_api_key"}}""");
        }
        return t;
    }

    private static string[] Allowed(VscodeToken t) =>
        JsonSerializer.Deserialize<string[]>(t.AllowedCombos) ?? [];

    private static object TokenView(VscodeToken t) => new
    {
        t.Id, t.Name,
        token = t.Token.Length > 8 ? t.Token[..8] + "…" : "…",
        t.DefaultCombo,
        allowedCombos = Allowed(t),
        active = t.IsActive, t.CreatedAt,
    };

    public static void Map(WebApplication app)
    {
        // ---- scoped gateway surface (token in path) ----
        app.MapGet("/v1/vscode/{token}/api/version", () =>
            Results.Json(new { version = "0.5.0" }));

        app.MapGet("/v1/vscode/{token}/api/tags", Tags);
        app.MapMethods("/v1/vscode/{token}/api/show", ["POST"], Show);
        app.MapGet("/v1/vscode/{token}/api/show/{*m}", (HttpContext c, string token, string m) =>
            Show(c, token, m));
        app.MapMethods("/v1/vscode/{token}/api/chat", ["POST"], OllamaChat);
        app.MapMethods("/v1/vscode/{token}/chat/completions", ["POST"],
            (HttpContext c, string token) => ScopedChat(c, token, "openai"));
        app.MapMethods("/v1/vscode/{token}/responses", ["POST"],
            (HttpContext c, string token) => ScopedChat(c, token, "responsesApi"));
        app.MapGet("/v1/vscode/{token}/models", ScopedModels);

        // ---- dashboard CRUD ----
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/vscode-tokens", async (LlmRouterDbContext db) =>
            Results.Json(new
            { tokens = (await db.VscodeTokens.OrderByDescending(t => t.CreatedAt).ToListAsync()).Select(TokenView) },
            JsonOpts));

        g.MapPost("/vscode-tokens", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var t = new VscodeToken
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Token = "vsc-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
                Name = req.TryGetProperty("name", out var n) ? n.GetString() : null,
                CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            ApplyPatch(t, req);
            db.VscodeTokens.Add(t);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "vscode.token.create", t.Id);
            return Results.Json(new { token = t.Token, view = TokenView(t) }, JsonOpts);
        });

        g.MapPut("/vscode-tokens/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var t = await db.VscodeTokens.FindAsync(id);
            if (t is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (req.TryGetProperty("name", out var n)) t.Name = n.ValueKind == JsonValueKind.Null ? null : n.GetString();
            if (req.TryGetProperty("active", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False)
                t.IsActive = a.GetBoolean();
            ApplyPatch(t, req);
            await db.SaveChangesAsync();
            return Results.Json(TokenView(t), JsonOpts);
        });

        g.MapDelete("/vscode-tokens/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var t = await db.VscodeTokens.FindAsync(id);
            if (t is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            db.VscodeTokens.Remove(t);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "vscode.token.delete", id);
            return Results.Json(new { success = true }, JsonOpts);
        });
    }

    private static void ApplyPatch(VscodeToken t, JsonElement req)
    {
        if (req.TryGetProperty("defaultCombo", out var dc))
            t.DefaultCombo = dc.ValueKind == JsonValueKind.Null ? null : dc.GetString();
        if (req.TryGetProperty("allowedCombos", out var ac) && ac.ValueKind == JsonValueKind.Array)
            t.AllowedCombos = ac.GetRawText();
    }

    /// <summary>Rewrites the request body model against the token's combo scope, then Chat.</summary>
    private static async Task ScopedChat(HttpContext ctx, string token, string inbound)
    {
        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var t = await Auth(ctx, db, token);
        if (t is null) return;

        var raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        var allowed = Allowed(t);
        if (allowed.Length > 0 || !string.IsNullOrEmpty(t.DefaultCombo))
        {
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
                var model = node["model"]?.GetValue<string>() ?? "";
                if (model.Length == 0 && !string.IsNullOrEmpty(t.DefaultCombo))
                    model = t.DefaultCombo;
                if (model.Length > 0) node["model"] = model;
                if (allowed.Length > 0 && model.Length > 0 && !allowed.Contains(model)
                    && await db.Combos.AnyAsync(c => c.Name == model))
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(
                        """{"error":{"message":"Combo not allowed for this token.","type":"model_not_allowed"}}""");
                    return;
                }
                raw = node.ToJsonString();
            }
            catch { /* let Chat handle malformed */ }
        }
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        await GatewayEndpoints.Chat(ctx, inbound, $"vscode:{t.Id}");
    }

    /// <summary>Ollama api/chat → translate to OpenAI chat, dispatch, translate back.</summary>
    private static async Task OllamaChat(HttpContext ctx, string token)
    {
        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var t = await Auth(ctx, db, token);
        if (t is null) return;

        var raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        string model = "";
        try
        {
            var ob = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
            model = ob["model"]?.GetValue<string>() ?? "";
            if (model.Length == 0 && !string.IsNullOrEmpty(t.DefaultCombo))
                model = t.DefaultCombo;
            var allowed = Allowed(t);
            if (allowed.Length > 0 && model.Length > 0 && !allowed.Contains(model)
                && await db.Combos.AnyAsync(c => c.Name == model))
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    """{"error":{"message":"Combo not allowed for this token.","type":"model_not_allowed"}}""");
                return;
            }
            // ollama options → openai params
            var oa = new System.Text.Json.Nodes.JsonObject
            {
                ["model"] = model,
                ["messages"] = ob["messages"]?.DeepClone(),
                ["stream"] = false,
            };
            if (ob["options"] is System.Text.Json.Nodes.JsonObject opt)
            {
                if (opt["temperature"] is { } temp) oa["temperature"] = temp.DeepClone();
                if (opt["num_predict"] is { } np) oa["max_tokens"] = np.DeepClone();
            }
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(oa.ToJsonString()));
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"error":"malformed ollama chat body"}""");
            return;
        }

        // capture Chat's response so we can translate the shape
        var orig = ctx.Response.Body;
        await using var capture = new MemoryStream();
        ctx.Response.Body = capture;
        await GatewayEndpoints.Chat(ctx, "openai", $"vscode:{t.Id}");
        ctx.Response.Body = orig;
        capture.Position = 0;
        var captured = Encoding.UTF8.GetString(capture.ToArray());

        if (ctx.Response.StatusCode != 200)
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(captured);
            return;
        }
        try
        {
            var r = JsonDocument.Parse(captured).RootElement;
            var content = r.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
                && ch[0].TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var pt = r.TryGetProperty("usage", out var u) && u.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
            var ct = r.TryGetProperty("usage", out var u2) && u2.TryGetProperty("completion_tokens", out var cc) ? cc.GetInt32() : 0;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                model,
                created_at = DateTime.UtcNow.ToString("o"),
                message = new { role = "assistant", content },
                done = true,
                done_reason = "stop",
                prompt_eval_count = pt,
                eval_count = ct,
            }, JsonOpts));
        }
        catch
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(captured);
        }
    }

    /// <summary>Ollama api/tags — combos + provider models as Ollama model list.</summary>
    private static async Task Tags(HttpContext ctx, string token, LlmRouterDbContext db)
    {
        var t = await Auth(ctx, db, token);
        if (t is null) return;
        var models = await ScopedModelsList(ctx, t, db);
        await ctx.Response.WriteAsJsonAsync(new
        {
            models = models.Select(m => new
            {
                name = m.Id,
                model = m.Id,
                modified_at = DateTime.UtcNow.ToString("o"),
                size = 0,
                digest = m.Id,
                details = new { family = m.OwnedBy, format = "api", families = new[] { m.OwnedBy } },
            })
        }, JsonOpts);
    }

    private static async Task Show(HttpContext ctx, string token, string? model = null)
    {
        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var t = await Auth(ctx, db, token);
        if (t is null) return;
        if (model is null && ctx.Request.Method == "POST")
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            model = req.TryGetProperty("name", out var n) ? n.GetString() : null
                ?? (req.TryGetProperty("model", out var mm) ? mm.GetString() : null);
        }
        var combo = model is null ? null : await db.Combos.FirstOrDefaultAsync(c => c.Name == model);
        await ctx.Response.WriteAsJsonAsync(new
        {
            modelfile = combo is null ? "" : $"# combo {combo.Name}\n{combo.Models}",
            parameters = "",
            template = "",
            details = new { family = combo is null ? "model" : "combo", format = "api" },
            model_info = new { },
        }, JsonOpts);
    }

    private static async Task ScopedModels(HttpContext ctx, string token)
    {
        var db = ctx.RequestServices.GetRequiredService<LlmRouterDbContext>();
        var t = await Auth(ctx, db, token);
        if (t is null) return;
        var models = await ScopedModelsList(ctx, t, db);
        await ctx.Response.WriteAsJsonAsync(new
        {
            @object = "list",
            data = models.Select(m => new { id = m.Id, @object = "model", created = 0, owned_by = m.OwnedBy })
        }, JsonOpts);
    }

    private record ModelEntry(string Id, string OwnedBy);

    private static async Task<List<ModelEntry>> ScopedModelsList(HttpContext ctx, VscodeToken t, LlmRouterDbContext db)
    {
        var registry = ctx.RequestServices.GetRequiredService<ProviderRegistry>();
        var list = new List<ModelEntry>();
        var allowed = Allowed(t);
        foreach (var combo in await db.Combos.ToListAsync())
            if (allowed.Length == 0 || allowed.Contains(combo.Name))
                list.Add(new ModelEntry(combo.Name, "combo"));
        foreach (var c in await db.ProviderConnections.Where(x => x.IsActive).ToListAsync())
        {
            var p = registry.GetProvider(c.Provider);
            if (p?.Models is null) continue;
            foreach (var m in p.Models)
                list.Add(new ModelEntry($"{p.Id}/{m.Id}", p.Alias ?? p.Id));
        }
        return list;
    }
}

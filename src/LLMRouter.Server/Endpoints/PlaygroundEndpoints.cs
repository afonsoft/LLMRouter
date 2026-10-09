using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-043: playground extras — improve-prompt, presets, simulate-route.</summary>
public static class PlaygroundEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private const string ImproveSystem =
        "You are a prompt engineer. Rewrite the user's prompt to be clearer, more specific, " +
        "and better structured for an LLM. Return only the improved prompt — no preamble, no quotes.";

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- improve prompt: loop through the gateway via the "batches" client
        g.MapPost("/playground/improve-prompt", async (HttpContext ctx, IHttpClientFactory hf, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var prompt = b.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
            if (prompt.Length == 0)
                return Results.Json(new { error = "prompt required" }, JsonOpts, statusCode: 400);
            var model = b.TryGetProperty("model", out var m) ? m.GetString() : null;
            if (string.IsNullOrEmpty(model))
            {
                // default: first combo, else first provider/model guess from a connection
                var combo = await db.Combos.Select(c => c.Name).FirstOrDefaultAsync();
                if (combo is not null) model = combo;
                else
                {
                    var conn = await db.ProviderConnections.FirstOrDefaultAsync(c => c.IsActive);
                    model = conn is null ? null : $"{conn.Provider}/default";
                }
            }
            if (model is null)
                return Results.Json(new { error = "no model configured" }, JsonOpts, statusCode: 400);

            var http = hf.CreateClient("batches");
            http.BaseAddress = new Uri($"{ctx.Request.Scheme}://{ctx.Request.Host}");
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.Length > 0) http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
            var resp = await http.PostAsJsonAsync("/v1/chat/completions", new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = ImproveSystem },
                    new { role = "user", content = prompt },
                },
                stream = false,
            });
            if (!resp.IsSuccessStatusCode)
                return Results.Json(new { error = $"upstream {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}" }, JsonOpts, statusCode: 502);
            var el = await resp.Content.ReadFromJsonAsync<JsonElement>();
            var improved = el.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return Results.Json(new { improved }, JsonOpts);
        });

        // ---- presets CRUD ----
        g.MapGet("/playground/presets", async (LlmRouterDbContext db) =>
            Results.Json(new { presets = await db.PlaygroundPresets.OrderBy(p => p.Name).ToListAsync() }, JsonOpts));

        g.MapPost("/playground/presets", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var p = new PlaygroundPreset
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "preset" : "preset",
                Model = b.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
                ParamsJson = b.TryGetProperty("params", out var pa) ? pa.GetRawText() : "{}",
                CreatedAt = Now(),
            };
            db.PlaygroundPresets.Add(p);
            await db.SaveChangesAsync();
            return Results.Json(new { preset = p }, JsonOpts);
        });

        g.MapPut("/playground/presets/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var p = await db.PlaygroundPresets.FindAsync(id);
            if (p is null) return NotFound("preset");
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out var n)) p.Name = n.GetString() ?? p.Name;
            if (b.TryGetProperty("model", out var m)) p.Model = m.GetString() ?? p.Model;
            if (b.TryGetProperty("params", out var pa)) p.ParamsJson = pa.GetRawText();
            await db.SaveChangesAsync();
            return Results.Json(new { preset = p }, JsonOpts);
        });

        g.MapDelete("/playground/presets/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var p = await db.PlaygroundPresets.FindAsync(id);
            if (p is null) return NotFound("preset");
            db.PlaygroundPresets.Remove(p);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        // ---- simulate route: resolve targets without calling upstream ----
        g.MapPost("/playground/simulate-route", async (HttpContext ctx, LlmRouterDbContext db, GatewayEngine engine) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var model = b.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            if (model.Length == 0)
                return Results.Json(new { error = "model required" }, JsonOpts, statusCode: 400);
            JsonElement? reqBody = b.TryGetProperty("body", out var rb) && rb.ValueKind == JsonValueKind.Object ? rb : null;

            var targets = await engine.ResolveAsync(model, reqBody);
            var steps = targets.Select((t, i) => new
            {
                order = i + 1,
                provider = t.Provider.Id,
                providerName = t.Provider.Alias ?? t.Provider.Id,
                connectionId = t.Connection.Id,
                connection = t.Connection.Name,
                upstreamModel = t.UpstreamModel,
                combo = t.ComboName,
            }).ToList();

            object? wouldBeCall = null;
            if (targets.Count > 0 && reqBody is { } bodyEl)
            {
                var call = engine.BuildCall(targets[0], "openai", bodyEl, stream: false);
                // mask auth header values — this is a dry run, secrets never leave
                var headers = call.Headers.ToDictionary(h => h.Key,
                    h => h.Key.Contains("key", StringComparison.OrdinalIgnoreCase)
                      || h.Key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
                        ? "***" : h.Value);
                wouldBeCall = new { call.Url, call.OutboundFormat, headers };
            }
            return Results.Json(new { model, targets = steps, wouldBeCall }, JsonOpts);
        });
    }

    private static IResult NotFound(string what) =>
        Results.Json(new { error = $"{what} not found" }, JsonOpts, statusCode: 404);
}

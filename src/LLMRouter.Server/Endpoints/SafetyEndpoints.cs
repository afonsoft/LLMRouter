using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-147: /api/safety* — eventos de guardrails revisáveis + stats + dry-run.</summary>
public static class SafetyEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/safety/events", async (LlmRouterDbContext db,
            string? kind, string? action, string? guardrail, string? keyId, string? model,
            bool? reviewed, int? limit) =>
        {
            var q = db.SafetyEvents.AsQueryable();
            if (!string.IsNullOrEmpty(kind)) q = q.Where(e => e.Kind == kind);
            if (!string.IsNullOrEmpty(action)) q = q.Where(e => e.Action == action);
            if (!string.IsNullOrEmpty(guardrail)) q = q.Where(e => e.Guardrail.Contains(guardrail));
            if (!string.IsNullOrEmpty(keyId)) q = q.Where(e => e.KeyId == keyId);
            if (!string.IsNullOrEmpty(model)) q = q.Where(e => e.Model == model);
            if (reviewed is { } rv) q = q.Where(e => e.Reviewed == rv);
            var rows = await q.OrderByDescending(e => e.Id)
                .Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync();
            return Results.Json(new { events = rows }, JsonOpts);
        });

        g.MapPost("/safety/events/{id}/reviewed", async (long id, JsonElement body, LlmRouterDbContext db) =>
        {
            var e = await db.SafetyEvents.FindAsync(id);
            if (e is null) return Results.NotFound(new { error = "not_found" });
            e.Reviewed = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("reviewed", out var r) ? r.GetBoolean() : true;
            await db.SaveChangesAsync();
            return Results.Json(e, JsonOpts);
        });

        g.MapGet("/safety/stats", async (LlmRouterDbContext db) =>
            Results.Json(await Core.Guardrails.SafetyEvents.StatsAsync(db), JsonOpts));

        // dry-run de um texto contra regras + builtins — útil p/ validar config sem tráfego real
        g.MapPost("/guardrails/test", async (JsonElement body, LlmRouterDbContext db) =>
        {
            var text = body.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (text.Length == 0) return Results.BadRequest(new { error = "text_required" });
            var results = new List<object>();
            // builtins
            var pHits = Core.Guardrails.PromptGuard.Detect(text);
            foreach (var h in pHits)
                results.Add(new { guardrail = h.Name, kind = "prompt_injection", h.Severity, match = h.Match });
            var sdata = await Core.Gateway.HotReads.SettingsDataAsync(db);
            var rg = Core.Guardrails.ResponseGuard.Read(sdata);
            foreach (var d in Core.Guardrails.ResponseGuard.Detect(text, rg))
                results.Add(new { guardrail = d.Name, kind = "response_pii", d.Severity, match = (string?)$"x{d.Count}" });
            // custom rules — reusa o mesmo matching do pipeline
            var fakeBody = JsonSerializer.SerializeToElement(new { text });
            var rules = await db.Kv.Where(k => k.Scope == "guardrails").ToListAsync();
            foreach (var row in rules)
            {
                try
                {
                    var d = JsonDocument.Parse(row.Value).RootElement;
                    if (d.TryGetProperty("enabled", out var en) && !en.GetBoolean()) continue;
                    var kind = d.TryGetProperty("kind", out var k2) ? k2.GetString() ?? "regex" : "regex";
                    var pattern = d.TryGetProperty("pattern", out var p) ? p.GetString() ?? "" : "";
                    var name = d.TryGetProperty("name", out var n) ? n.GetString() ?? row.Key : row.Key;
                    var act = d.TryGetProperty("action", out var a) ? a.GetString() ?? "block" : "block";
                    var matched = kind switch
                    {
                        "keyword" => text.Contains(pattern, StringComparison.OrdinalIgnoreCase),
                        "length" => int.TryParse(pattern, out var mx) && text.Length > mx,
                        "json_schema" => MgmtBatchEndpoints.JsonSchemaViolates(pattern, text),
                        "tool_permission" => false, // sem tools[] num texto solto
                        _ => System.Text.RegularExpressions.Regex.IsMatch(text, pattern,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                            TimeSpan.FromMilliseconds(500)),
                    };
                    if (matched)
                        results.Add(new { guardrail = name, kind, severity = "medium", match = pattern, action = act });
                }
                catch { }
            }
            return Results.Json(new { matches = results, count = results.Count }, JsonOpts);
        });
    }
}

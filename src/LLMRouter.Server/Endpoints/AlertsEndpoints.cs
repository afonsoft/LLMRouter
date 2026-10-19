using System.Text.Json;
using LLMRouter.Core.Alerts;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-146: /api/alerts* — regras, canais, instâncias (ciclo de vida) e deliveries.</summary>
public static class AlertsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly string[] States = ["firing", "acked", "snoozed", "escalated", "resolved"];

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- rules CRUD ----
        g.MapGet("/alerts/rules", async (LlmRouterDbContext db) =>
        {
            var rows = await db.AlertRules.OrderBy(r => r.Name).ToListAsync();
            return Results.Json(new { rules = rows }, JsonOpts);
        });
        g.MapPost("/alerts/rules", async (AlertRule body, LlmRouterDbContext db) =>
        {
            var err = ValidateRule(body);
            if (err is not null) return Results.BadRequest(new { error = err });
            body.Id = string.IsNullOrEmpty(body.Id) ? $"ar-{Guid.NewGuid():N}"[..12] : body.Id;
            var now = Iso();
            body.CreatedAt = now; body.UpdatedAt = now;
            db.AlertRules.Add(body);
            await db.SaveChangesAsync();
            return Results.Json(body, JsonOpts);
        });
        g.MapPut("/alerts/rules/{id}", async (string id, AlertRule body, LlmRouterDbContext db) =>
        {
            var r = await db.AlertRules.FindAsync(id);
            if (r is null) return Results.NotFound(new { error = "not_found" });
            var err = ValidateRule(body);
            if (err is not null) return Results.BadRequest(new { error = err });
            r.Name = body.Name; r.Kind = body.Kind; r.Expr = body.Expr;
            r.Severity = body.Severity; r.ChannelIds = body.ChannelIds;
            r.Enabled = body.Enabled; r.CooldownMin = body.CooldownMin;
            r.UpdatedAt = Iso();
            await db.SaveChangesAsync();
            return Results.Json(r, JsonOpts);
        });
        g.MapDelete("/alerts/rules/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var r = await db.AlertRules.FindAsync(id);
            if (r is null) return Results.NotFound(new { error = "not_found" });
            db.AlertRules.Remove(r);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });
        g.MapPost("/alerts/rules/{id}/dry", async (string id, LlmRouterDbContext db) =>
        {
            var r = await db.AlertRules.FindAsync(id);
            if (r is null) return Results.NotFound(new { error = "not_found" });
            var (breached, detail) = await AlertEngine.CheckRuleAsync(db, r, DateTime.UtcNow);
            return Results.Json(new { breached, detail = JsonDocument.Parse(detail).RootElement }, JsonOpts);
        });

        // ---- channels CRUD + test ----
        g.MapGet("/alerts/channels", async (LlmRouterDbContext db) =>
        {
            var rows = await db.AlertChannels.OrderBy(c => c.Name).ToListAsync();
            return Results.Json(new { channels = rows.Select(Redact) }, JsonOpts);
        });
        g.MapPost("/alerts/channels", async (AlertChannel body, LlmRouterDbContext db) =>
        {
            var err = ValidateChannel(body);
            if (err is not null) return Results.BadRequest(new { error = err });
            body.Id = string.IsNullOrEmpty(body.Id) ? $"ac-{Guid.NewGuid():N}"[..12] : body.Id;
            var now = Iso();
            body.CreatedAt = now; body.UpdatedAt = now;
            db.AlertChannels.Add(body);
            await db.SaveChangesAsync();
            return Results.Json(Redact(body), JsonOpts);
        });
        g.MapPut("/alerts/channels/{id}", async (string id, AlertChannel body, LlmRouterDbContext db) =>
        {
            var c = await db.AlertChannels.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "not_found" });
            c.Name = body.Name; c.Enabled = body.Enabled;
            if (!string.IsNullOrEmpty(body.Config) && body.Config != "{}") c.Config = body.Config;
            c.UpdatedAt = Iso();
            await db.SaveChangesAsync();
            return Results.Json(Redact(c), JsonOpts);
        });
        g.MapDelete("/alerts/channels/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var c = await db.AlertChannels.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "not_found" });
            db.AlertChannels.Remove(c);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });
        g.MapPost("/alerts/channels/{id}/test", async (string id, LlmRouterDbContext db, IHttpClientFactory hf) =>
        {
            var c = await db.AlertChannels.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "not_found" });
            try
            {
                var smtp = await AlertEngine.SmtpConfigAsync(db);
                await AlertEngine.SendAsync(hf, c, new { rule = "test-alert", severity = "info", state = "firing", at = Iso() }, smtp);
                return Results.Json(new { ok = true }, JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, JsonOpts, statusCode: 502);
            }
        });

        // ---- instances + lifecycle ----
        g.MapGet("/alerts/instances", async (LlmRouterDbContext db,
            string? state, string? ruleId, int? limit) =>
        {
            var q = db.AlertInstances.AsQueryable();
            if (!string.IsNullOrEmpty(state) && state != "all") q = q.Where(i => i.State == state);
            if (!string.IsNullOrEmpty(ruleId)) q = q.Where(i => i.RuleId == ruleId);
            var rows = await q.OrderByDescending(i => i.Id)
                .Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync();
            return Results.Json(new { instances = rows }, JsonOpts);
        });
        g.MapPost("/alerts/instances/{id}/transition", async (long id, JsonElement body, LlmRouterDbContext db, HttpContext ctx) =>
        {
            var i = await db.AlertInstances.FindAsync(id);
            if (i is null) return Results.NotFound(new { error = "not_found" });
            var to = body.TryGetProperty("to", out var t) ? t.GetString() ?? "" : "";
            if (to != "resolved" && i.State == "resolved")
                return Results.Conflict(new { error = "already_resolved" });
            var err = to switch
            {
                "acked" or "escalated" or "resolved" => null,
                "snoozed" => body.TryGetProperty("minutes", out var m) && m.TryGetInt32(out var mv) && mv > 0 ? null : "minutes_required",
                _ => "invalid_state",
            };
            if (err is not null) return Results.BadRequest(new { error = err });
            i.State = to;
            i.UpdatedAt = Iso();
            i.Actor = ctx.User?.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString();
            if (to == "resolved") i.ResolvedAt = Iso();
            if (to == "snoozed" && body.TryGetProperty("minutes", out var mm) && mm.TryGetInt32(out var mins))
                i.SnoozedUntil = Iso(DateTime.UtcNow.AddMinutes(mins));
            if (body.TryGetProperty("note", out var n)) i.Note = n.GetString();
            await db.SaveChangesAsync();
            return Results.Json(i, JsonOpts);
        });

        // ---- deliveries ----
        g.MapGet("/alerts/deliveries", async (LlmRouterDbContext db, long? instanceId, int? limit) =>
        {
            var q = db.AlertDeliveries.AsQueryable();
            if (instanceId is { } iid) q = q.Where(d => d.InstanceId == iid);
            var rows = await q.OrderByDescending(d => d.Id)
                .Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync();
            return Results.Json(new { deliveries = rows }, JsonOpts);
        });
        g.MapPost("/alerts/deliveries/{id}/redeliver", async (long id, LlmRouterDbContext db) =>
        {
            var d = await db.AlertDeliveries.FindAsync(id);
            if (d is null) return Results.NotFound(new { error = "not_found" });
            d.Status = "pending"; d.Attempts = 0; d.LastError = null; d.At = Iso();
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- inbox summary p/ badge ----
        g.MapGet("/alerts/summary", async (LlmRouterDbContext db) =>
        {
            var firing = await db.AlertInstances.CountAsync(i => i.State == "firing" || i.State == "escalated");
            var rules = await db.AlertRules.CountAsync(r => r.Enabled);
            return Results.Json(new { firing, enabledRules = rules }, JsonOpts);
        });
    }

    private static string? ValidateRule(AlertRule r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "name_required";
        if (r.Kind is not ("metric" or "event")) return "kind_invalid";
        try
        {
            var e = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.Expr) ? "{}" : r.Expr).RootElement;
            if (r.Kind == "metric" && !(e.TryGetProperty("metric", out var mp) && mp.GetString() is { Length: > 0 }))
                return "expr_metric_required";
            if (r.Kind == "event" && !(e.TryGetProperty("match", out var em) && em.GetString() is { Length: > 0 }))
                return "expr_match_required";
        }
        catch { return "expr_invalid_json"; }
        try { JsonSerializer.Deserialize<List<string>>(r.ChannelIds); } catch { return "channelIds_invalid"; }
        return null;
    }

    private static string? ValidateChannel(AlertChannel c)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) return "name_required";
        if (c.Type is not ("webhook" or "slack" or "email")) return "type_invalid";
        try
        {
            var e = JsonDocument.Parse(string.IsNullOrWhiteSpace(c.Config) ? "{}" : c.Config).RootElement;
            if (c.Type is "webhook" or "slack" && !(e.TryGetProperty("url", out var u) || e.TryGetProperty("webhookUrl", out u)))
                return "config_url_required";
            if (c.Type == "email" && !(e.TryGetProperty("to", out var t) && t.GetString() is { Length: > 0 }))
                return "config_to_required";
        }
        catch { return "config_invalid_json"; }
        return null;
    }

    private static object Redact(AlertChannel c)
    {
        string config = c.Config;
        try
        {
            var e = JsonDocument.Parse(c.Config).RootElement;
            var dict = new Dictionary<string, JsonElement>();
            foreach (var p in e.EnumerateObject())
                dict[p.Name] = p.Name is "secret" or "pass" or "token"
                    ? JsonSerializer.SerializeToElement("***")
                    : p.Value;
            config = JsonSerializer.Serialize(dict, JsonOpts);
        }
        catch { }
        return new { c.Id, c.Name, c.Type, config = JsonDocument.Parse(config).RootElement, c.Enabled, c.CreatedAt, c.UpdatedAt };
    }

    private static string Iso(DateTime? t = null) =>
        (t ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

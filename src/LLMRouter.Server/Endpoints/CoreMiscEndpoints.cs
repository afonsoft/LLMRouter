using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using LLMRouter.Core.Data;
using LLMRouter.Core.Translation;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-073 — core-misc endpoints (upstream parity): provider quota windows,
/// credential expiration tracking, generic tags, routing policies,
/// chat sessions, translator detect/formats/history.
/// </summary>
public static class CoreMiscEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---------- quota windows ----------
        g.MapGet("/quota-windows", async (LlmRouterDbContext db) =>
            Results.Ok(await db.QuotaWindows.OrderBy(w => w.Provider).ToListAsync()));

        g.MapPost("/quota-windows", async (LlmRouterDbContext db, JsonElement body) =>
        {
            var provider = body.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "";
            var name = body.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var minutes = body.TryGetProperty("windowMinutes", out var wm) && wm.TryGetInt32(out var m) ? m : 0;
            if (string.IsNullOrWhiteSpace(provider) || minutes <= 0)
                return Results.BadRequest(new { error = "provider and windowMinutes > 0 required" });
            var w = new QuotaWindow
            {
                Id = Guid.NewGuid().ToString("N"),
                Provider = provider,
                Name = string.IsNullOrWhiteSpace(name) ? $"{provider} {minutes}m" : name,
                WindowMinutes = minutes,
                MaxTokens = body.TryGetProperty("maxTokens", out var mt) && mt.TryGetInt64(out var t) ? t : 0,
                MaxRequests = body.TryGetProperty("maxRequests", out var mr) && mr.TryGetInt64(out var r) ? r : 0,
                CreatedAt = DateTime.UtcNow.ToString("o"),
            };
            db.QuotaWindows.Add(w);
            await db.SaveChangesAsync();
            return Results.Ok(w);
        });

        g.MapDelete("/quota-windows/{id}", async (LlmRouterDbContext db, string id) =>
        {
            var w = await db.QuotaWindows.FindAsync(id);
            if (w is null) return Results.Json(new { error = "window not found" }, statusCode: 404);
            db.QuotaWindows.Remove(w);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // ---------- credential expiration ----------
        g.MapGet("/credentials/expiration", async (LlmRouterDbContext db) =>
            Results.Ok(await db.CredentialExpirations.ToListAsync()));

        g.MapPost("/credentials/expiration", async (LlmRouterDbContext db, JsonElement body) =>
        {
            var connId = body.TryGetProperty("connectionId", out var c) ? c.GetString() ?? "" : "";
            var expiresAt = body.TryGetProperty("expiresAt", out var e) ? e.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(connId) || string.IsNullOrWhiteSpace(expiresAt))
                return Results.BadRequest(new { error = "connectionId and expiresAt required" });
            var row = await db.CredentialExpirations.FirstOrDefaultAsync(x => x.ConnectionId == connId);
            if (row is null)
            {
                row = new CredentialExpiration { Id = Guid.NewGuid().ToString("N"), ConnectionId = connId };
                db.CredentialExpirations.Add(row);
            }
            row.ExpiresAt = expiresAt;
            if (body.TryGetProperty("warnDays", out var wd) && wd.TryGetInt32(out var d)) row.WarnDays = d;
            await db.SaveChangesAsync();
            return Results.Ok(row);
        });

        g.MapDelete("/credentials/expiration/{id}", async (LlmRouterDbContext db, string id) =>
        {
            var x = await db.CredentialExpirations.FindAsync(id);
            if (x is null) return Results.Json(new { error = "not found" }, statusCode: 404);
            db.CredentialExpirations.Remove(x);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        g.MapGet("/credentials/expiring", async (LlmRouterDbContext db) =>
        {
            var all = await db.CredentialExpirations.ToListAsync();
            var now = DateTime.UtcNow;
            var expiring = all
                .Where(x => DateTime.TryParse(x.ExpiresAt, out var e)
                    && e <= now.AddDays(Math.Max(x.WarnDays, 0)))
                .Select(x => new
                {
                    x.Id, x.ConnectionId, x.ExpiresAt, x.WarnDays,
                    expired = DateTime.TryParse(x.ExpiresAt, out var e2) && e2 <= now,
                    connection = db.ProviderConnections.Where(c => c.Id == x.ConnectionId)
                        .Select(c => new { c.Name, c.Provider }).FirstOrDefault(),
                }).ToList();
            return Results.Ok(new { expiring });
        });

        // ---------- tags ----------
        g.MapGet("/tags", async (LlmRouterDbContext db, string? targetType, string? targetId) =>
        {
            var q = db.Tags.AsQueryable();
            if (!string.IsNullOrEmpty(targetType)) q = q.Where(t => t.TargetType == targetType);
            if (!string.IsNullOrEmpty(targetId)) q = q.Where(t => t.TargetId == targetId);
            return Results.Ok(await q.ToListAsync());
        });

        g.MapPost("/tags", async (LlmRouterDbContext db, JsonElement body) =>
        {
            var tt = body.TryGetProperty("targetType", out var a) ? a.GetString() ?? "" : "";
            var ti = body.TryGetProperty("targetId", out var b) ? b.GetString() ?? "" : "";
            var val = body.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(tt) || string.IsNullOrWhiteSpace(ti) || string.IsNullOrWhiteSpace(val))
                return Results.BadRequest(new { error = "targetType, targetId and value required" });
            var existing = await db.Tags.FirstOrDefaultAsync(t =>
                t.TargetType == tt && t.TargetId == ti && t.Value == val);
            if (existing is not null) return Results.Ok(existing);
            var tag = new Tag
            {
                Id = Guid.NewGuid().ToString("N"),
                TargetType = tt, TargetId = ti, Value = val,
            };
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
            return Results.Ok(tag);
        });

        g.MapDelete("/tags/{id}", async (LlmRouterDbContext db, string id) =>
        {
            var t = await db.Tags.FindAsync(id);
            if (t is null) return Results.Json(new { error = "not found" }, statusCode: 404);
            db.Tags.Remove(t);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // ---------- policies ----------
        g.MapGet("/policies", async (LlmRouterDbContext db) =>
            Results.Ok(await db.Policies.OrderByDescending(p => p.Priority).ToListAsync()));

        g.MapPost("/policies", async (LlmRouterDbContext db, JsonElement body) =>
        {
            var name = body.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "name required" });
            var p = new Policy
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Priority = body.TryGetProperty("priority", out var pr) && pr.TryGetInt32(out var pv) ? pv : 0,
                Rule = body.TryGetProperty("rule", out var r) ? r.GetRawText() : "{}",
                Enabled = !body.TryGetProperty("enabled", out var en) || en.GetBoolean(),
            };
            db.Policies.Add(p);
            await db.SaveChangesAsync();
            return Results.Ok(p);
        });

        g.MapPut("/policies/{id}", async (LlmRouterDbContext db, string id, JsonElement body) =>
        {
            var p = await db.Policies.FindAsync(id);
            if (p is null) return Results.Json(new { error = "not found" }, statusCode: 404);
            if (body.TryGetProperty("name", out var n)) p.Name = n.GetString() ?? p.Name;
            if (body.TryGetProperty("priority", out var pr) && pr.TryGetInt32(out var pv)) p.Priority = pv;
            if (body.TryGetProperty("rule", out var r)) p.Rule = r.GetRawText();
            if (body.TryGetProperty("enabled", out var en)) p.Enabled = en.GetBoolean();
            await db.SaveChangesAsync();
            return Results.Ok(p);
        });

        g.MapDelete("/policies/{id}", async (LlmRouterDbContext db, string id) =>
        {
            var p = await db.Policies.FindAsync(id);
            if (p is null) return Results.Json(new { error = "not found" }, statusCode: 404);
            db.Policies.Remove(p);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // Dry-run: which enabled policies match a request context.
        // Rule shape: {"match": {"provider": "openai", "model": "gpt-*"}, "action": "deny|allow"}
        g.MapPost("/policies/evaluate", async (LlmRouterDbContext db, JsonElement body) =>
        {
            var policies = await db.Policies.Where(p => p.Enabled)
                .OrderByDescending(p => p.Priority).ToListAsync();
            var matches = new List<object>();
            foreach (var p in policies)
            {
                JsonObject? rule;
                try { rule = JsonNode.Parse(p.Rule)?.AsObject(); }
                catch { continue; }
                var match = rule?["match"] as JsonObject;
                if (match is null || match.Count == 0) continue;
                var ok = true;
                foreach (var kv in match)
                {
                    var want = kv.Value?.GetValue<string>() ?? "";
                    var got = body.TryGetProperty(kv.Key, out var cv) ? cv.ToString() : "";
                    ok &= want == "*" || string.Equals(want, got, StringComparison.OrdinalIgnoreCase)
                        || (want.EndsWith('*') && got.StartsWith(want[..^1], StringComparison.OrdinalIgnoreCase));
                }
                if (ok)
                    matches.Add(new
                    {
                        p.Id, p.Name, p.Priority,
                        action = rule?["action"]?.GetValue<string>() ?? "allow",
                    });
            }
            return Results.Ok(new { matches });
        });

        // ---------- chat sessions ----------
        g.MapGet("/sessions", async (LlmRouterDbContext db) =>
            Results.Ok(await db.ChatSessions.OrderByDescending(s => s.LastSeenAt).ToListAsync()));

        g.MapDelete("/sessions/{id}", async (LlmRouterDbContext db, string id) =>
        {
            var s = await db.ChatSessions.FindAsync(id);
            if (s is null) return Results.Json(new { error = "not found" }, statusCode: 404);
            db.ChatSessions.Remove(s);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // ---------- translator ----------
        g.MapGet("/translator/formats", () => Results.Ok(new
        {
            formats = new[] { "openai", "claude", "gemini", "responsesApi", "ollama" },
        }));

        g.MapPost("/translator/detect", (JsonElement body) =>
        {
            var detected = "unknown";
            if (body.ValueKind == JsonValueKind.Object)
            {
                if (body.TryGetProperty("contents", out _)) detected = "gemini";
                else if (body.TryGetProperty("input", out _) || body.TryGetProperty("instructions", out _))
                    detected = "responsesApi";
                else if (body.TryGetProperty("max_tokens", out _) && body.TryGetProperty("messages", out _))
                    detected = "claude";
                else if (body.TryGetProperty("messages", out _)) detected = "openai";
            }
            return Results.Ok(new { format = detected });
        });

        g.MapGet("/translator/history", () =>
            Results.Ok(new { history = Translators.Recent.Reverse() }));
    }
}

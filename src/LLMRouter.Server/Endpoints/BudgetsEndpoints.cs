using System.Text.Json;
using LLMRouter.Core.Budgets;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-150: /api/budgets* — CRUD + counters, overage requests
/// (approve/deny) e grants (list/revoke) do workflow de estouro.</summary>
public static class BudgetsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Scopes = ["gateway", "user", "key", "model"];
    private static readonly HashSet<string> Periods = ["daily", "weekly", "monthly"];
    private static readonly HashSet<string> Modes = ["soft", "hard", "held"];

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/budgets", async (LlmRouterDbContext db) =>
        {
            var now = DateTime.UtcNow;
            var rows = await db.Budgets.AsNoTracking().OrderBy(b => b.Name).ToListAsync();
            var list = new List<object>();
            foreach (var b in rows)
            {
                var since = BudgetGate.PeriodStart(b.Period, now);
                var (usd, tokens) = await BudgetGate.SpendAsync(db, b, since);
                var grant = await BudgetGate.ActiveGrantUsdAsync(db, b.Id, now.ToString("O"));
                list.Add(new
                {
                    b.Id, b.Name, b.Scope, b.OwnerId, b.LimitUsd, b.LimitTokens,
                    b.Period, b.Mode, b.Enabled, b.CreatedAt,
                    spentUsd = usd, spentTokens = tokens, grantUsd = grant,
                });
            }
            return Results.Json(new { budgets = list }, JsonOpts);
        });

        g.MapPost("/budgets", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<Budget>(ctx.Request.Body, JsonOpts);
            if (b is null || !Scopes.Contains(b.Scope) || !Periods.Contains(b.Period) || !Modes.Contains(b.Mode))
                return Results.BadRequest(new { error = "invalid scope/period/mode" });
            if (b.Scope != "gateway" && string.IsNullOrWhiteSpace(b.OwnerId))
                return Results.BadRequest(new { error = "ownerId required for scope" });
            if (b.LimitUsd is null && b.LimitTokens is null)
                return Results.BadRequest(new { error = "limitUsd or limitTokens required" });
            b.Id = Guid.NewGuid().ToString("N")[..12];
            b.CreatedAt = DateTime.UtcNow.ToString("O");
            b.Enabled = true;
            db.Budgets.Add(b);
            await db.SaveChangesAsync();
            return Results.Json(b, JsonOpts);
        });

        g.MapPut("/budgets/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await db.Budgets.FindAsync(id);
            if (b is null) return Results.NotFound(new { error = "budget not found" });
            var p = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, JsonOpts);
            string? S(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (S("name") is { } name) b.Name = name;
            if (S("ownerId") is { } oid) b.OwnerId = oid;
            if (S("period") is { } per && Periods.Contains(per)) b.Period = per;
            if (S("mode") is { } mode && Modes.Contains(mode)) b.Mode = mode;
            if (p.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False)
                b.Enabled = en.GetBoolean();
            if (p.TryGetProperty("limitUsd", out var lu))
                b.LimitUsd = lu.ValueKind == JsonValueKind.Null ? null : lu.GetDouble();
            if (p.TryGetProperty("limitTokens", out var lt))
                b.LimitTokens = lt.ValueKind == JsonValueKind.Null ? null : lt.GetInt64();
            await db.SaveChangesAsync();
            return Results.Json(b, JsonOpts);
        });

        g.MapDelete("/budgets/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var b = await db.Budgets.FindAsync(id);
            if (b is null) return Results.NotFound(new { error = "budget not found" });
            db.Budgets.Remove(b);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- overage requests (inbox) ----
        g.MapGet("/budgets/requests", async (LlmRouterDbContext db, string? state) =>
        {
            var q = db.OverageRequests.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(state)) q = q.Where(r => r.State == state);
            var rows = await q.OrderByDescending(r => r.Id).Take(500).ToListAsync();
            var names = await db.Budgets.AsNoTracking()
                .ToDictionaryAsync(b => b.Id, b => b.Name);
            return Results.Json(new
            {
                requests = rows.Select(r => new
                {
                    r.Id, r.BudgetId, budget = names.GetValueOrDefault(r.BudgetId),
                    r.Requester, r.Amount, r.Reason, r.State, r.Decider, r.DecidedAt, r.CreatedAt,
                }),
            }, JsonOpts);
        });

        g.MapPost("/budgets/requests/{id}/approve", async (long id, HttpContext ctx,
            LlmRouterDbContext db, double? amount, int? ttlMinutes) =>
        {
            var r = await db.OverageRequests.FindAsync(id);
            if (r is null) return Results.NotFound(new { error = "request not found" });
            if (r.State != "pending") return Results.BadRequest(new { error = $"already {r.State}" });
            var now = DateTime.UtcNow;
            r.State = "approved";
            r.DecidedAt = now.ToString("O");
            r.Decider = ctx.User.Identity?.Name ?? "admin";
            var grant = new OverageGrant
            {
                BudgetId = r.BudgetId,
                Amount = amount ?? (r.Amount > 0 ? r.Amount : 10),
                ExpiresAt = now.AddMinutes(ttlMinutes ?? 60).ToString("O"),
                CreatedAt = now.ToString("O"),
            };
            db.OverageGrants.Add(grant);
            await db.SaveChangesAsync();
            return Results.Json(new { request = r, grant }, JsonOpts);
        });

        g.MapPost("/budgets/requests/{id}/deny", async (long id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var r = await db.OverageRequests.FindAsync(id);
            if (r is null) return Results.NotFound(new { error = "request not found" });
            if (r.State != "pending") return Results.BadRequest(new { error = $"already {r.State}" });
            r.State = "denied";
            r.DecidedAt = DateTime.UtcNow.ToString("O");
            r.Decider = ctx.User.Identity?.Name ?? "admin";
            await db.SaveChangesAsync();
            return Results.Json(r, JsonOpts);
        });

        // ---- grants ----
        g.MapGet("/budgets/grants", async (LlmRouterDbContext db) =>
        {
            var rows = await db.OverageGrants.AsNoTracking().OrderByDescending(g2 => g2.Id).Take(500).ToListAsync();
            var now = DateTime.UtcNow.ToString("O");
            return Results.Json(new
            {
                grants = rows.Select(g2 => new
                {
                    g2.Id, g2.BudgetId, g2.Amount, g2.ExpiresAt, g2.CreatedAt,
                    g2.Revoked, active = !g2.Revoked && string.Compare(g2.ExpiresAt, now) > 0,
                }),
            }, JsonOpts);
        });

        g.MapDelete("/budgets/grants/{id}", async (long id, LlmRouterDbContext db) =>
        {
            var gr = await db.OverageGrants.FindAsync(id);
            if (gr is null) return Results.NotFound(new { error = "grant not found" });
            gr.Revoked = true;
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });
    }
}

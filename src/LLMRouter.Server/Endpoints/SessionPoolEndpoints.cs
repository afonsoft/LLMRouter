using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-049: session-pool CRUD + sessions listing + drain/refresh.</summary>
public static class SessionPoolEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static object PoolView(SessionPoolRow p, List<PoolSession> sessions) => new
    {
        p.Id, p.Name, p.Provider, p.Strategy, p.MinSize, p.MaxSize, p.LeaseSeconds,
        active = p.IsActive, p.CreatedAt,
        sessions = new
        {
            total = sessions.Count,
            idle = sessions.Count(s => s.State == "idle"),
            busy = sessions.Count(s => s.State == "busy"),
            cooldown = sessions.Count(s => s.State == "cooldown"),
            dead = sessions.Count(s => s.Health == "dead"),
        },
        requests = sessions.Sum(s => s.TotalRequests),
        successes = sessions.Sum(s => s.SuccessfulRequests),
    };

    private static object SessionView(PoolSession s) => new
    {
        s.Id, s.PoolId, s.ConnectionId, s.State, s.Health,
        s.BusyUntil, s.CooldownUntil, s.LastUsedAt,
        s.TotalRequests, s.SuccessfulRequests, s.ConsecutiveFails, s.CreatedAt,
    };

    private static void ApplyPatch(SessionPoolRow p, JsonElement req)
    {
        if (req.TryGetProperty("name", out var n)) p.Name = n.GetString() ?? p.Name;
        if (req.TryGetProperty("provider", out var pr)) p.Provider = pr.GetString() ?? p.Provider;
        if (req.TryGetProperty("strategy", out var st) && st.ValueKind == JsonValueKind.String)
            p.Strategy = st.GetString() is "least-used" ? "least-used" : "round-robin";
        if (req.TryGetProperty("minSize", out var mi) && mi.ValueKind == JsonValueKind.Number)
            p.MinSize = Math.Max(0, mi.GetInt32());
        if (req.TryGetProperty("maxSize", out var ma) && ma.ValueKind == JsonValueKind.Number)
            p.MaxSize = Math.Max(1, ma.GetInt32());
        if (req.TryGetProperty("leaseSeconds", out var ls) && ls.ValueKind == JsonValueKind.Number)
            p.LeaseSeconds = Math.Clamp(ls.GetInt32(), 5, 3600);
        if (req.TryGetProperty("active", out var ac) && ac.ValueKind is JsonValueKind.True or JsonValueKind.False)
            p.IsActive = ac.GetBoolean();
        p.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/session-pools", async (LlmRouterDbContext db) =>
        {
            var pools = await db.SessionPools.ToListAsync();
            var sessions = await db.PoolSessions.ToListAsync();
            return Results.Json(new
            {
                pools = pools.Select(p =>
                    PoolView(p, sessions.Where(s => s.PoolId == p.Id).ToList()))
            }, JsonOpts);
        });

        g.MapPost("/session-pools", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var provider = req.TryGetProperty("provider", out var pv) ? pv.GetString() : null;
            if (string.IsNullOrWhiteSpace(provider))
                return Results.Json(new { error = "provider required" }, JsonOpts, statusCode: 400);
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            var p = new SessionPoolRow
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = req.TryGetProperty("name", out var n) ? n.GetString() ?? provider : provider,
                Provider = provider,
                CreatedAt = now, UpdatedAt = now,
            };
            ApplyPatch(p, req);
            db.SessionPools.Add(p);
            await db.SaveChangesAsync();
            await SessionPoolOps.EnsureMinAsync(db, p);
            await Core.Extras.Extras.AuditAsync(db, "sessionPool.create", p.Id);
            var sessions = await db.PoolSessions.Where(s => s.PoolId == p.Id).ToListAsync();
            return Results.Json(PoolView(p, sessions), JsonOpts);
        });

        g.MapPut("/session-pools/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var p = await db.SessionPools.FindAsync(id);
            if (p is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            ApplyPatch(p, req);
            await db.SaveChangesAsync();
            await SessionPoolOps.EnsureMinAsync(db, p);
            var sessions = await db.PoolSessions.Where(s => s.PoolId == p.Id).ToListAsync();
            return Results.Json(PoolView(p, sessions), JsonOpts);
        });

        g.MapDelete("/session-pools/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var p = await db.SessionPools.FindAsync(id);
            if (p is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            db.PoolSessions.RemoveRange(db.PoolSessions.Where(s => s.PoolId == id));
            db.SessionPools.Remove(p);
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "sessionPool.delete", id);
            return Results.Json(new { success = true }, JsonOpts);
        });

        g.MapGet("/session-pools/{id}/sessions", async (string id, LlmRouterDbContext db) =>
        {
            var sessions = await db.PoolSessions.Where(s => s.PoolId == id)
                .OrderBy(s => s.CreatedAt).ToListAsync();
            return Results.Json(new { sessions = sessions.Select(SessionView) }, JsonOpts);
        });

        g.MapPost("/session-pools/{id}/drain", async (string id, LlmRouterDbContext db) =>
        {
            var p = await db.SessionPools.FindAsync(id);
            if (p is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            await SessionPoolOps.DrainAsync(db, p);
            await Core.Extras.Extras.AuditAsync(db, "sessionPool.drain", id);
            var sessions = await db.PoolSessions.Where(s => s.PoolId == id).ToListAsync();
            return Results.Json(PoolView(p, sessions), JsonOpts);
        });

        g.MapPost("/session-pools/{id}/refresh", async (string id, LlmRouterDbContext db) =>
        {
            var p = await db.SessionPools.FindAsync(id);
            if (p is null)
                return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            await SessionPoolOps.RefreshAsync(db, p);
            var sessions = await db.PoolSessions.Where(s => s.PoolId == id).ToListAsync();
            return Results.Json(PoolView(p, sessions), JsonOpts);
        });
    }
}

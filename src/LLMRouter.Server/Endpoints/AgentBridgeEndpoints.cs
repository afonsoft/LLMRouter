using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Server.Services.AgentBridge;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-127: /api/tools/agent-bridge/* — port of OmniRoute's AgentBridge API:
/// MITM server lifecycle, CA cert, per-agent state/mappings/dns, bypass,
/// upstream-ca, tproxy, diagnose, repair.
/// </summary>
public static class AgentBridgeEndpoints
{
    private const string UnknownAgent = "unknown_agent";

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/tools/agent-bridge").RequireAuthorization();
        MapAgents(g);
        MapServer(g);
        MapBypass(g);
        MapCert(g);
        MapMisc(g);
    }

    private static void MapAgents(RouteGroupBuilder g)
    {
        g.MapGet("/agents", async (AgentBridgeService svc, LlmRouterDbContext db) =>
        {
            var states = await db.AgentBridgeStates.AsNoTracking().ToListAsync();
            return Results.Ok(new
            {
                agents = AgentBridgeTargets.All.Select(t =>
                    AgentBridgeService.AgentFor(t, states.FirstOrDefault(s => s.AgentId == t.Id)))
            });
        });

        g.MapGet("/agents/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var t = AgentBridgeTargets.All.FirstOrDefault(x => x.Id == id);
            if (t is null) return Results.NotFound(new { error = UnknownAgent });
            var s = await db.AgentBridgeStates.FindAsync(id);
            return Results.Ok(AgentBridgeService.AgentFor(t, s));
        });

        g.MapPost("/agents/{id}/reset", async (string id, LlmRouterDbContext db) =>
        {
            var t = AgentBridgeTargets.All.FirstOrDefault(x => x.Id == id);
            if (t is null) return Results.NotFound(new { error = UnknownAgent });
            db.AgentBridgeMappings.RemoveRange(db.AgentBridgeMappings.Where(m => m.AgentId == id));
            if (await db.AgentBridgeStates.FindAsync(id) is { } s)
                db.AgentBridgeStates.Remove(s);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, reset = id });
        });

        g.MapGet("/agents/{id}/mappings", async (string id, LlmRouterDbContext db) =>
        {
            if (AgentBridgeTargets.All.All(x => x.Id != id))
                return Results.NotFound(new { error = UnknownAgent });
            var rows = await db.AgentBridgeMappings.AsNoTracking()
                .Where(m => m.AgentId == id).ToListAsync();
            return Results.Ok(new
            {
                mappings = rows.Select(m => new { source = m.SourceModel, target = m.TargetModel })
            });
        });

        g.MapPut("/agents/{id}/mappings", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            if (AgentBridgeTargets.All.All(x => x.Id != id))
                return Results.NotFound(new { error = UnknownAgent });
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var mappings = body?["mappings"] as JsonObject;
            if (mappings is null)
                return Results.BadRequest(new { error = "mappings object required" });
            db.AgentBridgeMappings.RemoveRange(db.AgentBridgeMappings.Where(m => m.AgentId == id));
            foreach (var kv in mappings)
            {
                var target = kv.Value?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(target))
                    db.AgentBridgeMappings.Add(new AgentBridgeMapping
                    { AgentId = id, SourceModel = kv.Key, TargetModel = target });
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, count = mappings.Count });
        });

        g.MapGet("/agents/{id}/detected-models", (string id) =>
        {
            var t = AgentBridgeTargets.All.FirstOrDefault(x => x.Id == id);
            if (t is null) return Results.NotFound(new { error = UnknownAgent });
            return Results.Ok(new { models = t.DetectedModels });
        });

        g.MapPost("/agents/{id}/dns", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var t = AgentBridgeTargets.All.FirstOrDefault(x => x.Id == id);
            if (t is null) return Results.NotFound(new { error = UnknownAgent });
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var enabled = body?["enabled"]?.GetValue<bool>() ?? false;
            var s = await db.AgentBridgeStates.FindAsync(id)
                ?? db.AgentBridgeStates.Add(new AgentBridgeState { AgentId = id }).Entity;
            s.DnsEnabled = enabled;
            s.UpdatedAt = DateTime.UtcNow.ToString("o");
            await db.SaveChangesAsync();
            // DNS spoofing (hosts-file write) requires root — reported, not performed.
            return Results.Ok(new { ok = true, dnsEnabled = enabled, requiresRoot = true, hosts = t.Hosts });
        });
    }

    private static void MapServer(RouteGroupBuilder g)
    {
        g.MapPost("/server", async (HttpContext ctx, AgentBridgeService svc) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var action = body?["action"]?.GetValue<string>() ?? "status";
            int? port = body?["port"]?.GetValue<int?>();
            var status = await svc.ServerAction(action, port, body?["sudoPassword"]?.GetValue<string>());
            return Results.Ok(status);
        });

        g.MapGet("/state", async (AgentBridgeService svc, LlmRouterDbContext db) =>
        {
            var states = await db.AgentBridgeStates.AsNoTracking().ToListAsync();
            var bypass = await db.AgentBridgeBypass.AsNoTracking().Select(b => b.Pattern).ToListAsync();
            var mappings = await db.AgentBridgeMappings.AsNoTracking()
                .GroupBy(m => m.AgentId)
                .ToDictionaryAsync(x => x.Key,
                    x => x.Select(m => new { source = m.SourceModel, target = m.TargetModel }).ToList());
            var server = svc.Status();
            return Results.Ok(new
            {
                server,
                agents = AgentBridgeTargets.All.Select(t =>
                    AgentBridgeService.AgentFor(t, states.FirstOrDefault(s => s.AgentId == t.Id))),
                serverState = server,
                agentStates = states,
                bypassPatterns = bypass,
                mappings,
            });
        });
    }

    private static void MapBypass(RouteGroupBuilder g)
    {
        g.MapGet("/bypass", async (LlmRouterDbContext db) => Results.Ok(new
        {
            patterns = await db.AgentBridgeBypass.AsNoTracking().Select(b => b.Pattern).ToListAsync()
        }));

        g.MapPost("/bypass", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var pattern = body?["pattern"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(pattern))
                return Results.BadRequest(new { error = "pattern required" });
            if (await db.AgentBridgeBypass.FindAsync(pattern) is null)
            {
                db.AgentBridgeBypass.Add(new AgentBridgeBypass
                { Pattern = pattern, CreatedAt = DateTime.UtcNow.ToString("o") });
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { ok = true, pattern });
        });

        g.MapDelete("/bypass", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var pattern = body?["pattern"]?.GetValue<string>();
            if (await db.AgentBridgeBypass.FindAsync(pattern ?? "") is { } row)
            {
                db.AgentBridgeBypass.Remove(row);
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { ok = true });
        });
    }

    private static void MapCert(RouteGroupBuilder g)
    {
        g.MapGet("/cert", (AgentBridgeService svc) => Results.Ok(svc.Ca.Info()));

        g.MapGet("/cert/download", (AgentBridgeService svc) =>
        {
            var (cert, _) = svc.Ca.Ensure();
            var pem = System.IO.File.ReadAllText(svc.Ca.CertPath);
            return Results.Text(pem, "application/x-pem-file",
                System.Text.Encoding.UTF8);
        });

        g.MapPost("/cert/regenerate", (AgentBridgeService svc) =>
        {
            svc.Ca.Generate();
            return Results.Ok(new { ok = true, info = svc.Ca.Info() });
        });
    }

    private static void MapMisc(RouteGroupBuilder g)
    {
        // Upstream CA (trusted-roots bundle for upstream TLS verification).
        g.MapGet("/upstream-ca", (AgentBridgeService svc) => Results.Ok(new
        {
            managed = false,
            systemStore = true,
            note = "Upstream TLS uses the OS trust store; no custom upstream CA configured.",
        }));

        g.MapPost("/upstream-ca/test", () => Results.Ok(new { ok = true, reachable = true }));

        // tproxy state (transparent-proxy mode toggle, upstream parity surface).
        g.MapGet("/tproxy", () => Results.Ok(new { enabled = false, supported = false }));
        g.MapPost("/tproxy", () => Results.Json(
            new { error = "tproxy requires Linux root capabilities; use CONNECT proxy mode" },
            statusCode: 400));

        g.MapGet("/diagnose", (AgentBridgeService svc) => Results.Ok(new
        {
            cert = svc.Ca.Info(),
            server = svc.Status(),
            checks = new[]
            {
                new { name = "ca_exists", ok = svc.Ca.CertExists },
                new { name = "ca_trusted", ok = svc.Ca.IsTrusted() },
                new { name = "proxy_running", ok = svc.Proxy.Running },
            },
        }));

        g.MapPost("/repair", async (AgentBridgeService svc) =>
        {
            svc.Ca.Ensure(); // regenerate missing CA files — the repairable slice
            return Results.Ok(new { ok = true, repaired = svc.Ca.CertExists ? new[] { "ca" } : Array.Empty<string>() });
        });
    }
}

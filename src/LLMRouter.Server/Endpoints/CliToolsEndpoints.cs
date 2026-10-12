using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Server.Services.CliTools;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// Port of OmniRoute's /api/cli-tools/* surface (SPEC-126): per-tool settings
/// read/write, aggregate status, apply, backups, detect, runtime, logs, keys.
/// </summary>
public static class CliToolsEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/cli-tools").RequireAuthorization();

        // GET /api/cli-tools/all-statuses — runtime + config status for every tool.
        g.MapGet("/all-statuses", (CliToolService svc) =>
        {
            var statuses = CliToolService.Tools.ToDictionary(
                t => t.Id, t => svc.StatusFor(t));
            return Results.Ok(new { statuses });
        });

        // GET /api/cli-tools/status[?tool=] — one tool's status or the full batch.
        g.MapGet("/status", (HttpContext ctx, CliToolService svc) =>
        {
            var tool = ctx.Request.Query["tool"].FirstOrDefault();
            if (!string.IsNullOrEmpty(tool))
            {
                var def = CliToolService.Find(tool);
                if (def is null) return Results.NotFound(new { error = "unknown_tool" });
                return Results.Ok(new { tool = def.Id, status = svc.StatusFor(def) });
            }
            return Results.Ok(new
            {
                statuses = CliToolService.Tools.ToDictionary(t => t.Id, t => svc.StatusFor(t))
            });
        });

        // GET /api/cli-tools/detect — which tool binaries are on PATH.
        g.MapGet("/detect", (CliToolService svc) => Results.Ok(new
        {
            detected = CliToolService.Tools.Select(t => new
            {
                tool = t.Id,
                command = t.DefaultCommand,
                path = svc.LocateBinary(t),
                detected = svc.LocateBinary(t) is not null,
            }).Where(x => x.detected || x.command is not null),
        }));

        // GET /api/cli-tools/runtime — host runtime info for the CLI tools page.
        g.MapGet("/runtime", (CliToolService svc) => Results.Ok(new
        {
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            dotnet = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            configHome = svc.ConfigHome,
            gatewayBaseUrl = svc.GatewayBaseUrl,
            writesAllowed = CliToolService.WritesAllowed,
            containerized = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
        }));

        // GET /api/cli-tools/config — write-guard + resolved paths the UI needs.
        g.MapGet("/config", (CliToolService svc) => Results.Ok(new
        {
            writesAllowed = CliToolService.WritesAllowed,
            configHome = svc.ConfigHome,
            baseUrl = svc.GatewayBaseUrl,
            tools = CliToolService.Tools.Select(t => new { t.Id, t.DefaultCommand, configPath = svc.ConfigPathFor(t) }),
        }));

        // GET /api/cli-tools/keys — API keys usable when wiring a CLI (id + alias only).
        g.MapGet("/keys", async (LlmRouterDbContext db) => Results.Ok(new
        {
            keys = await db.ApiKeys.AsNoTracking()
                .Where(k => k.IsActive)
                .OrderByDescending(k => k.CreatedAt)
                .Select(k => new { k.Id, name = k.Name ?? k.Id, prefix = k.Key.Length > 7 ? k.Key.Substring(0, 7) + "…" : null })
                .ToListAsync(),
        }));

        // GET /api/cli-tools/logs[?lines=N] — tail of tool log files under config home.
        g.MapGet("/logs", (HttpContext ctx, CliToolService svc) =>
        {
            var lines = int.TryParse(ctx.Request.Query["lines"], out var n) ? Math.Clamp(n, 1, 500) : 100;
            var logs = new List<object>();
            if (Directory.Exists(svc.ConfigHome))
            {
                foreach (var tool in CliToolService.Tools)
                {
                    var dir = Path.GetDirectoryName(svc.ConfigPathFor(tool))!;
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, "*.log"))
                    {
                        var tail = ReadTail(f, lines);
                        if (tail is not null)
                            logs.Add(new { tool = tool.Id, file = f, lines = tail });
                    }
                }
            }
            return Results.Ok(new { logs });
        });

        // GET /api/cli-tools/backups — every .llmrouter.bak snapshot.
        g.MapGet("/backups", (CliToolService svc) => Results.Ok(new { backups = svc.ListBackups() }));

        // POST /api/cli-tools/backups/restore {backupPath} — restore a snapshot.
        g.MapPost("/backups/restore", async (HttpContext ctx, CliToolService svc) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var backupPath = body?["backupPath"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
                return Results.NotFound(new { error = "backup_not_found" });
            var target = Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(backupPath))!,
                Path.GetFileNameWithoutExtension(backupPath));
            if (!backupPath.StartsWith(svc.ConfigHome, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "path_outside_config_home" });
            if (!CliToolService.WritesAllowed)
                return Results.Json(new { error = "CLI config writes are disabled" }, statusCode: 403);
            File.Copy(backupPath, target, overwrite: true);
            return Results.Ok(new { ok = true, restoredTo = target });
        });

        // POST /api/cli-tools/apply {toolId, baseUrl?, apiKey?, model?, dryRun?}
        g.MapPost("/apply", async (HttpContext ctx, CliToolService svc) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var toolId = body?["toolId"]?.GetValue<string>();
            var def = toolId is null ? null : CliToolService.Find(toolId);
            if (def is null) return Results.BadRequest(new { error = "unknown_tool" });
            var (status, result) = svc.Apply(def,
                body?["apiKey"]?.GetValue<string>(),
                body?["model"]?.GetValue<string>(),
                body?["dryRun"]?.GetValue<bool>() ?? false);
            return Results.Json(result, statusCode: status);
        });

        // GET/POST /api/cli-tools/{tool}-settings — port of the per-tool routes.
        g.MapGet("/{toolId}-settings", (string toolId, CliToolService svc) =>
        {
            var def = CliToolService.Find(toolId);
            if (def is null) return Results.NotFound(new { error = "unknown_tool" });
            return Results.Ok(svc.ReadSettings(def));
        });

        g.MapPost("/{toolId}-settings", async (string toolId, HttpContext ctx,
            CliToolService svc, LlmRouterDbContext db) =>
        {
            var def = CliToolService.Find(toolId);
            if (def is null) return Results.NotFound(new { error = "unknown_tool" });
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var env = body?["env"] as JsonObject;
            if (env is null) return Results.BadRequest(new { error = "env object required" });

            // keyId resolves the real key from the DB (upstream #523/#526).
            if (body?["keyId"]?.GetValue<string>() is { } keyId)
            {
                var record = await db.ApiKeys.AsNoTracking()
                    .FirstOrDefaultAsync(k => k.Id == keyId && k.IsActive);
                if (record?.Key is { } realKey)
                {
                    foreach (var varName in def.EnvTemplate.Skip(1))
                        env[varName] = realKey;
                }
            }
            var (status, result) = svc.ApplyEnv(def, env);
            return Results.Json(result, statusCode: status);
        });
    }

    private static string[]? ReadTail(string path, int lines)
    {
        try
        {
            var all = File.ReadAllLines(path);
            return all.Skip(Math.Max(0, all.Length - lines)).ToArray();
        }
        catch { return null; }
    }
}

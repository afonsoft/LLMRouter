using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Versioning;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-059: /api/version-manager/* + /api/{restart,shutdown}.</summary>
public static class VersionManagerEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Mapeia os endpoints de version-manager e power.</summary>
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/version-manager");

        g.MapGet("/status", () => Results.Json(new
        {
            version = VersionManager.CurrentVersion(),
            commitSha = VersionManager.CommitSha(),
            channel = Environment.GetEnvironmentVariable("LLMROUTER_CHANNEL") ?? "stable",
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        }, JsonOpts));

        g.MapGet("/check-update", async () => Results.Json(await VersionManager.CheckUpdateAsync(Http), JsonOpts));

        g.MapPost("/install", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var url = b.TryGetProperty("assetUrl", out var u) ? u.GetString() : null;
            var sha = b.TryGetProperty("sha256", out var s) ? s.GetString() : null;
            if (url is null) return Results.Json(new { error = "assetUrl required" }, JsonOpts, statusCode: 400);
            var (ok, detail) = await VersionManager.InstallAsync(db, Http, url, sha);
            return Results.Json(new { ok, detail }, JsonOpts, statusCode: ok ? 200 : 400);
        });

        var power = app.MapGroup("/api");
        power.MapPost("/restart", async (LlmRouterDbContext db) =>
        {
            await Core.Extras.Extras.AuditAsync(db, "power.restart", "user request");
            VersionManager.RequestExit(0, "restart");
            return Results.Json(new { ok = true, action = "restart" }, JsonOpts);
        });
        power.MapPost("/shutdown", async (LlmRouterDbContext db) =>
        {
            await Core.Extras.Extras.AuditAsync(db, "power.shutdown", "user request");
            VersionManager.RequestExit(0, "shutdown");
            return Results.Json(new { ok = true, action = "shutdown" }, JsonOpts);
        });
    }
}

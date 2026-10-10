using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Skills;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-058: /api/skills extras — executions, collect, marketplace, skillssh.</summary>
public static class SkillsExtrasEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string StoreDir(IWebHostEnvironment env) =>
        Path.Combine(env.ContentRootPath, "skills");

    /// <summary>Mapeia os endpoints extras de skills.</summary>
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/skills");

        g.MapGet("/executions", async (string? skill, int? limit, LlmRouterDbContext db) =>
        {
            var q = db.SkillExecutions.AsQueryable();
            if (skill is not null) q = q.Where(e => e.Skill == skill);
            return Results.Json(new { executions = await q.OrderByDescending(e => e.Id).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync() }, JsonOpts);
        });

        g.MapPost("/{id}/run", async (string id, LlmRouterDbContext db, IWebHostEnvironment env) =>
        {
            var (ok, detail) = await SkillsExtras.RunAsync(db, StoreDir(env), id);
            await Core.Extras.Extras.AuditAsync(db, "skill.run", $"{id}:{ok}");
            return Results.Json(new { ok, detail }, JsonOpts, statusCode: ok ? 200 : 400);
        });

        g.MapPost("/collect/detect", async (HttpContext ctx, LlmRouterDbContext db, IWebHostEnvironment env) =>
        {
            string[]? extra = null;
            if (ctx.Request.ContentLength > 0)
            {
                var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
                if (b.TryGetProperty("roots", out var r)) extra = JsonSerializer.Deserialize<string[]>(r.GetRawText());
            }
            return Results.Json(new { detected = await SkillsExtras.DetectAsync(db, StoreDir(env), extra) }, JsonOpts);
        });

        g.MapPost("/collect", async (HttpContext ctx, IWebHostEnvironment env) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var dir = b.TryGetProperty("dir", out var d) ? d.GetString() : null;
            if (dir is null) return Results.Json(new { error = "dir required" }, JsonOpts, statusCode: 400);
            var (ok, detail) = SkillsExtras.Collect(dir, StoreDir(env));
            return Results.Json(new { ok, detail }, JsonOpts, statusCode: ok ? 200 : 400);
        });

        g.MapGet("/collect/chaos", (IWebHostEnvironment env) =>
            Results.Json(new { missing = SkillsExtras.Chaos(StoreDir(env)) }, JsonOpts));

        g.MapGet("/marketplace", async (LlmRouterDbContext db) =>
            Results.Json(new { skills = await SkillsExtras.MarketplaceAsync(db, Http) }, JsonOpts));

        g.MapPost("/marketplace/install", async (HttpContext ctx, IWebHostEnvironment env) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var id = b.TryGetProperty("id", out var i) ? i.GetString() : null;
            var url = b.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (id is null || url is null)
                return Results.Json(new { error = "id and url required" }, JsonOpts, statusCode: 400);
            var (ok, detail) = await SkillsExtras.MarketInstallAsync(Http, StoreDir(env), id, url);
            return Results.Json(new { ok, detail }, JsonOpts, statusCode: ok ? 200 : 400);
        });

        // skillssh-style: install direto por URL de SKILL.md
        g.MapPost("/skillssh/install", async (HttpContext ctx, IWebHostEnvironment env) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var url = b.TryGetProperty("url", out var u) ? u.GetString() : null;
            var id = b.TryGetProperty("id", out var i) ? i.GetString()
                : url is null ? null : Path.GetFileName(new Uri(url).LocalPath).Replace("SKILL.md", "").Trim('-', '_');
            if (url is null || id is null or "")
                return Results.Json(new { error = "url (and optional id) required" }, JsonOpts, statusCode: 400);
            var (ok, detail) = await SkillsExtras.MarketInstallAsync(Http, StoreDir(env), id, url);
            return Results.Json(new { ok, detail }, JsonOpts, statusCode: ok ? 200 : 400);
        });
    }
}

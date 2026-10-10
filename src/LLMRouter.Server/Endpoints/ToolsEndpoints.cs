using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Translation;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-009: /api/cli-tools (manifest + detection + config generation),
/// /api/translator (format converter), /api/skills (bundled/user SKILL.md).
/// </summary>
public static class ToolsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string SkillsDir(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "skills");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Parse SKILL.md frontmatter (name/description) — upstream agentSkills lib shape.</summary>
    public static (string Name, string Desc) ParseSkill(string path)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(path) ?? path);
        var desc = "";
        try
        {
            var lines = File.ReadLines(path).Take(30).ToList();
            if (lines.FirstOrDefault() == "---")
                foreach (var l in lines.Skip(1))
                {
                    if (l == "---") break;
                    if (l.StartsWith("name:")) name = l[5..].Trim().Trim('"', '\'');
                    else if (l.StartsWith("description:")) desc = l[12..].Trim().Trim('"', '\'');
                }
        }
        catch { /* best-effort: failure is non-fatal */ }
        return (name, desc);
    }

    /// <summary>SPEC-026: system-prompt fragment listing every enabled skill
    /// (skills/disabled kv holds opt-outs), capped per skill.</summary>
    public static async Task<string?> SkillPromptTextAsync(LlmRouterDbContext db, IWebHostEnvironment env)
    {
        var disabled = (await db.Kv.FindAsync("skills", "disabled"))?.Value;
        var dis = disabled is null ? new HashSet<string>()
            : JsonSerializer.Deserialize<string[]>(disabled)?.ToHashSet() ?? [];
        var dir = SkillsDir(env);
        var lines = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories))
        {
            var (name, desc) = ParseSkill(f);
            if (dis.Contains(name)) continue;
            var d = desc.Length > 160 ? desc[..160] + "…" : desc;
            lines.Add(d.Length > 0 ? $"- {name}: {d}" : $"- {name}");
        }
        return lines.Count == 0 ? null
            : "Active skills:\n" + string.Join("\n", lines);
    }

    public static void MapToolsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- cli tools ----
        g.MapGet("/cli-tools", (HttpContext ctx) =>
        {
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Json(new
            {
                tools = CliToolsManifest.Tools.Select(t => new
                {
                    t.Id, t.Name, t.Binary, t.InstallHint, t.Inbound,
                    detected = CliToolsManifest.Detected(t),
                    env = CliToolsManifest.EnvFor(t, baseUrl, "<your-api-key>"),
                }),
            }, JsonOpts);
        });

        g.MapGet("/cli-tools/{id}/config", (string id, HttpContext ctx, string? key) =>
        {
            var t = CliToolsManifest.Tools.FirstOrDefault(x => x.Id == id);
            if (t is null) return Results.NotFound();
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var apiKey = string.IsNullOrEmpty(key) ? "<your-api-key>" : key;
            return Results.Json(new
            {
                t.Id, t.Name,
                envBlock = CliToolsManifest.ExportBlock(t, baseUrl, apiKey),
                detected = CliToolsManifest.Detected(t),
            }, JsonOpts);
        });

        // ---- translator playground ----
        g.MapPost("/translator", async (HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var from = b.TryGetProperty("from", out var f) ? f.GetString() ?? "openai" : "openai";
            var to = b.TryGetProperty("to", out var t) ? t.GetString() ?? "claude" : "claude";
            var stream = b.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
            var model = b.TryGetProperty("model", out var m) ? m.GetString() ?? "model" : "model";
            if (!b.TryGetProperty("body", out var body))
                return Results.BadRequest(new { error = "body required" });
            try
            {
                var kind = b.TryGetProperty("kind", out var k) ? k.GetString() ?? "request" : "request";
                var result = kind == "response"
                    ? Translators.TranslateResponse(body, from, to, model)
                    : Translators.Translate(body, from, to, model, stream);
                return Results.Text(result.ToJsonString(), "application/json");
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // ---- skills ----
        g.MapGet("/skills", async (LlmRouterDbContext db, IWebHostEnvironment env) =>
        {
            var disabled = (await db.Kv.FindAsync("skills", "disabled"))?.Value;
            var dis = disabled is null ? new HashSet<string>()
                : JsonSerializer.Deserialize<string[]>(disabled)?.ToHashSet() ?? [];
            var skills = new List<object>();
            var dir = SkillsDir(env);
            foreach (var f in Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories))
            {
                var (name, desc) = ParseSkill(f);
                var rel = Path.GetRelativePath(dir, f);
                skills.Add(new
                {
                    id = Path.GetDirectoryName(rel) ?? name,
                    name, description = desc,
                    enabled = !dis.Contains(name),
                    source = "bundled",
                });
            }
            return Results.Json(new { skills }, JsonOpts);
        });

        g.MapPost("/skills/{name}/toggle", async (string name, LlmRouterDbContext db) =>
        {
            var row = await db.Kv.FindAsync("skills", "disabled")
                ?? db.Kv.Add(new KvEntry { Scope = "skills", Key = "disabled", Value = "[]" }).Entity;
            var dis = (JsonSerializer.Deserialize<string[]>(row.Value) ?? []).ToHashSet();
            var enabled = dis.Remove(name) || dis.Add(name) is false;
            row.Value = JsonSerializer.Serialize(dis);
            await db.SaveChangesAsync();
            return Results.Json(new { name, enabled }, JsonOpts);
        });

        // install a skill from a GitHub SKILL.md URL (blob or raw)
        g.MapPost("/skills/install", async (HttpContext ctx, LlmRouterDbContext db, IWebHostEnvironment env, IHttpClientFactory hf) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var url = b.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url)) return Results.BadRequest(new { error = "url required" });
            var raw = url
                .Replace("github.com/", "raw.githubusercontent.com/")
                .Replace("/blob/", "/");
            var c = hf.CreateClient("upstream");
            string content;
            try { content = await c.GetStringAsync(raw); }
            catch (Exception ex) { return Results.BadRequest(new { error = $"fetch failed: {ex.Message}" }); }
            // extract name from frontmatter
            var name = "skill-" + Guid.NewGuid().ToString("N")[..6];
            var tmp = Path.Combine(SkillsDir(env), name, "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            await File.WriteAllTextAsync(tmp, content);
            var (n, desc) = ParseSkill(tmp);
            if (n != name)
            {
                var dest = Path.Combine(SkillsDir(env), n);
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                Directory.Move(Path.GetDirectoryName(tmp)!, dest);
            }
            return Results.Json(new { success = true, name = n, description = desc }, JsonOpts);
        });

        // ---- media providers (SPEC-010): connections grouped by declared media kind ----
        g.MapGet("/media-providers", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections.Where(c => c.IsActive).ToListAsync();
            var byKind = MediaKinds.Visible.ToDictionary(k => k, _ => new List<object>());
            foreach (var c in conns)
                foreach (var k in MediaKinds.KindsOf(c))
                    if (byKind.TryGetValue(k, out var list))
                        list.Add(new { c.Id, c.Name, c.Provider, kinds = MediaKinds.KindsOf(c).ToArray() });
            return Results.Json(new { kinds = MediaKinds.Visible, connections = byKind }, JsonOpts);
        });
    }
}

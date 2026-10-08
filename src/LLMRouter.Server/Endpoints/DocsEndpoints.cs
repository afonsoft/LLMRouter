using System.Text.Json;
using LLMRouter.Core.Data;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-013: /api/docs (markdown files from repo docs/), /api/status (public).
/// </summary>
public static class DocsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly DateTime Started = DateTime.UtcNow;

    /// <summary>Walk up from content root to find the repo's docs/ dir.</summary>
    public static string? DocsRoot(IWebHostEnvironment env)
    {
        var dir = new DirectoryInfo(env.ContentRootPath);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, "docs");
            if (Directory.Exists(cand)) return cand;
        }
        return null;
    }

    /// <summary>Minimal markdown → html (headers, lists, code fences, bold/links, tables passthrough).</summary>
    public static string MdToHtml(string md)
    {
        var sb = new System.Text.StringBuilder();
        var inCode = false;
        foreach (var raw in md.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```")) { sb.Append(inCode ? "</code></pre>" : "<pre class='code-block'><code>"); inCode = !inCode; continue; }
            if (inCode) { sb.Append(System.Net.WebUtility.HtmlEncode(line)).Append('\n'); continue; }
            var e = System.Net.WebUtility.HtmlEncode(line);
            e = System.Text.RegularExpressions.Regex.Replace(e, @"\*\*([^*]+)\*\*", "<b>$1</b>");
            e = System.Text.RegularExpressions.Regex.Replace(e, @"`([^`]+)`", "<code>$1</code>");
            e = System.Text.RegularExpressions.Regex.Replace(e, @"\[([^\]]+)\]\(([^)]+)\)", "<a href='$2'>$1</a>");
            if (line.StartsWith("####")) sb.Append($"<h4>{e[4..].TrimStart()}</h4>");
            else if (line.StartsWith("###")) sb.Append($"<h3>{e[3..].TrimStart()}</h3>");
            else if (line.StartsWith("##")) sb.Append($"<h2>{e[2..].TrimStart()}</h2>");
            else if (line.StartsWith("#")) sb.Append($"<h1>{e[1..].TrimStart()}</h1>");
            else if (line.TrimStart().StartsWith("- ")) sb.Append($"<li>{e[(e.IndexOf('-') + 1)..].TrimStart()}</li>");
            else if (string.IsNullOrWhiteSpace(line)) sb.Append("<br/>");
            else sb.Append($"<p>{e}</p>");
        }
        if (inCode) sb.Append("</code></pre>");
        return sb.ToString();
    }

    public static void MapDocsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/status", (LlmRouterDbContext db) => Results.Json(new
        {
            status = "ok",
            version = "0.1.0",
            uptimeSec = (int)(DateTime.UtcNow - Started).TotalSeconds,
            providers = db.ProviderConnections.Count(c => c.IsActive),
        }, JsonOpts)).AllowAnonymous();

        g.MapGet("/docs", (IWebHostEnvironment env) =>
        {
            var root = DocsRoot(env);
            if (root is null) return Results.Json(new { files = Array.Empty<string>() }, JsonOpts);
            var files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'));
            return Results.Json(new { files }, JsonOpts);
        });

        g.MapGet("/docs/{*path}", (string path, IWebHostEnvironment env) =>
        {
            var root = DocsRoot(env);
            if (root is null) return Results.NotFound();
            var full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(root) || !File.Exists(full)) return Results.NotFound();
            var md = File.ReadAllText(full);
            return Results.Json(new { path, markdown = md, html = MdToHtml(md) }, JsonOpts);
        });
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// SPEC-024: markdown → html — GFM tables, nested lists (2-space indent),
    /// headings with id anchors + auto TOC block.
    /// </summary>
    public static string MdToHtml(string md)
    {
        var sb = new System.Text.StringBuilder();
        var toc = new List<(int Level, string Text, string Id)>();
        var inCode = false; var inTable = false; var listDepth = 0;
        string Slug(string t) => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(t.ToLowerInvariant(), @"[^a-z0-9\s-]", "", RegexOptions.None, TimeSpan.FromMilliseconds(500)), @"\s+", "-").Trim('-');
        string Enc(string t)
        {
            var e = System.Net.WebUtility.HtmlEncode(t);
            e = System.Text.RegularExpressions.Regex.Replace(e, @"\*\*([^*]+)\*\*", "<b>$1</b>", RegexOptions.None, TimeSpan.FromMilliseconds(500));
            e = System.Text.RegularExpressions.Regex.Replace(e, @"\*([^*]+)\*", "<i>$1</i>", RegexOptions.None, TimeSpan.FromMilliseconds(500));
            e = System.Text.RegularExpressions.Regex.Replace(e, @"`([^`]+)`", "<code>$1</code>", RegexOptions.None, TimeSpan.FromMilliseconds(500));
            e = System.Text.RegularExpressions.Regex.Replace(e, @"\[([^\]]+)\]\(([^)]+)\)", "<a href='$2'>$1</a>", RegexOptions.None, TimeSpan.FromMilliseconds(500));
            return e;
        }
        void CloseTable() { if (inTable) { sb.Append("</tbody></table>"); inTable = false; } }
        void CloseLists(int to = 0) { while (listDepth > to) { sb.Append("</ul>"); listDepth--; } }

        var lines = md.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i]; var line = raw.TrimEnd();
            if (line.StartsWith("```")) { CloseTable(); CloseLists(); sb.Append(inCode ? "</code></pre>" : "<pre class='code-block'><code>"); inCode = !inCode; continue; }
            if (inCode) { sb.Append(System.Net.WebUtility.HtmlEncode(line)).Append('\n'); continue; }

            // GFM table row
            var isRow = line.StartsWith('|') && line.EndsWith('|');
            var nextSep = i + 1 < lines.Length && System.Text.RegularExpressions.Regex.IsMatch(lines[i + 1].Trim(), @"^\|?[\s:|-]+\|[\s:|-]+\|?$", RegexOptions.None, TimeSpan.FromMilliseconds(500));
            if (isRow && nextSep)
            {
                CloseLists();
                sb.Append("<table class='tbl'><thead><tr>");
                foreach (var c in line.Trim('|').Split('|'))
                    sb.Append("<th>").Append(Enc(c.Trim())).Append("</th>");
                sb.Append("</tr></thead><tbody>");
                inTable = true; i++; // skip separator
                continue;
            }
            if (inTable)
            {
                if (isRow)
                {
                    sb.Append("<tr>");
                    foreach (var c in line.Trim('|').Split('|'))
                        sb.Append("<td>").Append(Enc(c.Trim())).Append("</td>");
                    sb.Append("</tr>");
                    continue;
                }
                CloseTable();
            }

            // headings with anchors for TOC
            var h = 0;
            while (h < line.Length && line[h] == '#') h++;
            if (h is > 0 and <= 4 && line.Length > h && line[h] == ' ')
            {
                CloseLists();
                var text = line[(h + 1)..].Trim();
                var id = Slug(text);
                toc.Add((h, text, id));
                sb.Append($"<h{h} id='{id}'>{Enc(text)}</h{h}>");
                continue;
            }

            // nested list item (2-space indents)
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^(\s*)[-*]\s+(.*)$", RegexOptions.None, TimeSpan.FromMilliseconds(500));
            if (m.Success)
            {
                CloseTable();
                var depth = m.Groups[1].Value.Length / 2;
                while (listDepth < depth + 1) { sb.Append("<ul>"); listDepth++; }
                CloseLists(depth + 1);
                sb.Append("<li>").Append(Enc(m.Groups[2].Value.TrimEnd())).Append("</li>");
                continue;
            }
            CloseLists();

            if (string.IsNullOrWhiteSpace(line)) { sb.Append("<br/>"); continue; }
            sb.Append("<p>").Append(Enc(line)).Append("</p>");
        }
        if (inCode) sb.Append("</code></pre>");
        CloseTable(); CloseLists();

        if (toc.Count(t => t.Level <= 2) >= 3)
        {
            var t2 = new System.Text.StringBuilder("<nav class='toc'><b>Contents</b><ul>");
            foreach (var (lv, text, id) in toc)
                if (lv <= 3) t2.Append($"<li style='margin-left:{(lv - 1) * 12}px'><a href='#{id}'>{System.Net.WebUtility.HtmlEncode(text)}</a></li>");
            t2.Append("</ul></nav>");
            return t2.ToString() + sb.ToString();
        }
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
            headless = Core.Security.HeadlessMode.Enabled,
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


public static class VersionEndpoints
{
    /// <summary>SPEC-016: running version + latest GitHub release tag (best-effort).</summary>
    public static void MapVersionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/version", async (IHttpClientFactory hf) =>
        {
            var current = typeof(VersionEndpoints).Assembly.GetName().Version?.ToString() ?? "0.1.0";
            string? latest = null;
            try
            {
                var c = hf.CreateClient("gh");
                c.DefaultRequestHeaders.UserAgent.ParseAdd("llmrouter");
                var r = await c.GetFromJsonAsync<System.Text.Json.JsonElement>(
                    "https://api.github.com/repos/afonsoft/LLMRouter/releases/latest");
                latest = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            }
            catch { }
            return Results.Json(new { version = current, latest, updateAvailable = latest is not null && latest.TrimStart('v') != current, headless = Core.Security.HeadlessMode.Enabled });
        }).AllowAnonymous();
    }
}

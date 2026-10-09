using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Logging;

/// <summary>
/// SPEC-042: log export — pulls filtered requestDetails, formats jsonl/csv and
/// ships to a destination (file, webhook, s3-compatible endpoint). Last-run
/// status is persisted on the destination row itself.
/// </summary>
public static class LogExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static string FormatRows(IEnumerable<RequestDetail> rows, string fmt)
    {
        var list = rows.ToList();
        if (fmt == "csv")
        {
            var sb = new StringBuilder("id,timestamp,provider,model,status\n");
            foreach (var r in list)
                sb.AppendLine($"{r.Id},{r.Timestamp},{r.Provider},{r.Model},{r.Status}");
            return sb.ToString();
        }
        return string.Join('\n', list.Select(r => JsonSerializer.Serialize(r, JsonOpts)));
    }

    /// <summary>Apply the destination's filters to a queryable.</summary>
    public static IQueryable<RequestDetail> ApplyFilters(IQueryable<RequestDetail> q, JsonElement filters)
    {
        if (filters.ValueKind == JsonValueKind.Object)
        {
            if (filters.TryGetProperty("provider", out var p) && p.GetString() is { Length: > 0 } pv)
                q = q.Where(r => r.Provider == pv);
            if (filters.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } mv)
                q = q.Where(r => r.Model == mv);
            if (filters.TryGetProperty("status", out var s) && s.GetString() is { Length: > 0 } sv)
                q = q.Where(r => r.Status == sv);
            if (filters.TryGetProperty("since", out var si) && si.GetString() is { Length: > 0 } sv2)
                q = q.Where(r => string.Compare(r.Timestamp, sv2) > 0);
        }
        return q;
    }

    /// <summary>Run one destination: select → format → deliver → persist status.</summary>
    public static async Task<string> RunAsync(LlmRouterDbContext db, LogExportDestination d,
        IHttpClientFactory hf, CancellationToken ct = default)
    {
        var cfg = JsonDocument.Parse(d.Config).RootElement;
        var filters = JsonDocument.Parse(d.Filters).RootElement;
        var fmt = cfg.TryGetProperty("fmt", out var f) && f.GetString() is { Length: > 0 } x ? x : "jsonl";
        var limit = cfg.TryGetProperty("limit", out var l) && l.TryGetInt32(out var ln) ? Math.Clamp(ln, 1, 50000) : 5000;

        var rows = await ApplyFilters(db.RequestDetails.OrderByDescending(r => r.Timestamp), filters)
            .Take(limit).ToListAsync(ct);
        var payload = FormatRows(rows, fmt);
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        try
        {
            string detail;
            switch (d.Type)
            {
                case "file":
                {
                    var path = cfg.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } pp
                        ? pp : Path.Combine("exports", $"{d.Name}.{fmt}");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    await File.AppendAllTextAsync(path, payload + "\n", ct);
                    detail = $"wrote {rows.Count} rows to {path}";
                    break;
                }
                case "webhook":
                case "s3-compatible":
                {
                    var url = cfg.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("config.url required");
                    var http = hf.CreateClient("logexport");
                    var content = new StringContent(payload, Encoding.UTF8,
                        fmt == "csv" ? "text/csv" : "application/x-ndjson");
                    if (cfg.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                        foreach (var hd in h.EnumerateObject())
                            content.Headers.TryAddWithoutValidation(hd.Name, hd.Value.GetString());
                    var resp = await http.PostAsync(url, content, ct);
                    detail = $"POST {url} -> {(int)resp.StatusCode} ({rows.Count} rows)";
                    if (!resp.IsSuccessStatusCode) throw new HttpRequestException(detail);
                    break;
                }
                default:
                    throw new InvalidOperationException($"unknown type '{d.Type}'");
            }
            d.LastRunAt = now; d.LastRunStatus = "ok"; d.LastRunDetail = detail;
        }
        catch (Exception ex)
        {
            d.LastRunAt = now; d.LastRunStatus = "error"; d.LastRunDetail = ex.Message;
        }
        d.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return d.LastRunDetail!;
    }
}

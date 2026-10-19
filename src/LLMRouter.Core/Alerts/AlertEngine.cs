using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LLMRouter.Core.Alerts;

/// <summary>
/// SPEC-146: alert engine — avalia regras metric/event sobre usageHistory +
/// auditEvents, mantém o ciclo de vida de alertInstances e despacha para
/// alertChannels (webhook/slack/email) com retry via alertDeliveries.
/// </summary>
public static class AlertEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    public const int MaxAttempts = 3;

    public sealed record EvalResult(int Fired, int Resolved, int Skipped);

    /// <summary>Avalia todas as regras habilitadas. Retorna contagens p/ job log.</summary>
    public static async Task<EvalResult> EvaluateAsync(LlmRouterDbContext db, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rules = await db.AlertRules.Where(r => r.Enabled).ToListAsync(ct);
        var fired = 0; var resolved = 0; var skipped = 0;
        foreach (var rule in rules)
        {
            try
            {
                var (breached, detail) = await CheckRuleAsync(db, rule, now, ct);
                var open = await db.AlertInstances
                    .Where(i => i.RuleId == rule.Id && i.State != "resolved")
                    .OrderByDescending(i => i.Id).FirstOrDefaultAsync(ct);
                if (breached)
                {
                    if (open is null)
                    {
                        if (await InCooldownAsync(db, rule, now, ct)) { skipped++; continue; }
                        var inst = new AlertInstance
                        {
                            RuleId = rule.Id,
                            State = "firing",
                            Detail = detail,
                            StartedAt = Iso(now),
                            UpdatedAt = Iso(now),
                        };
                        db.AlertInstances.Add(inst);
                        await db.SaveChangesAsync(ct);
                        EnqueueDeliveries(db, rule, inst.Id);
                        fired++;
                    }
                    else if (open.State == "snoozed" && open.SnoozedUntil is { } until
                        && DateTime.TryParse(until, null, System.Globalization.DateTimeStyles.RoundtripKind, out var u)
                        && u <= now)
                    {
                        open.State = "firing"; open.UpdatedAt = Iso(now);
                        EnqueueDeliveries(db, rule, open.Id);
                        fired++;
                    }
                    else skipped++;
                }
                else if (open is not null && open.State is "firing" or "escalated")
                {
                    open.State = "resolved";
                    open.ResolvedAt = Iso(now);
                    open.UpdatedAt = Iso(now);
                    resolved++;
                }
            }
            catch { skipped++; }
        }
        await db.SaveChangesAsync(ct);
        return new(fired, resolved, skipped);
    }

    /// <summary>Avalia uma regra. Breached → (true, detail JSON).</summary>
    public static async Task<(bool Breached, string Detail)> CheckRuleAsync(
        LlmRouterDbContext db, AlertRule rule, DateTime now, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rule.Expr) ? "{}" : rule.Expr);
        var e = doc.RootElement;
        var windowMin = e.TryGetProperty("windowMin", out var w) && w.TryGetInt32(out var wm) ? Math.Clamp(wm, 1, 1440) : 15;
        var since = now.AddMinutes(-windowMin);
        var sinceStr = since.ToString("yyyy-MM-dd HH:mm:ss");
        if (rule.Kind == "event")
        {
            var match = e.TryGetProperty("match", out var m) ? m.GetString() ?? "" : "";
            var count = await db.AuditEvents.Where(a => a.At.CompareTo(Iso(since)) > 0
                && a.Action.Contains(match)).CountAsync(ct);
            var threshold = e.TryGetProperty("threshold", out var t) && t.TryGetDouble(out var th) ? th : 1;
            var breached = count >= threshold;
            return (breached, JsonSerializer.Serialize(new
            {
                rule.Kind, @event = match, count, threshold, windowMin,
            }, JsonOpts));
        }
        var metric = e.TryGetProperty("metric", out var mm) ? mm.GetString() ?? "error_rate" : "error_rate";
        var thresholdMetric = e.TryGetProperty("threshold", out var tt) && tt.TryGetDouble(out var tv) ? tv : 0;
        var op = e.TryGetProperty("op", out var o) ? o.GetString() ?? "gt" : "gt";
        var modelFilter = e.TryGetProperty("model", out var mf) ? mf.GetString() : null;
        var q = db.UsageHistory.Where(u => u.Timestamp.CompareTo(sinceStr) > 0);
        if (!string.IsNullOrEmpty(modelFilter)) q = q.Where(u => u.Model == modelFilter);
        var rows = await q.Select(u => new { u.LatencyMs, u.Status, u.Cost })
            .ToListAsync(ct);
        var value = ComputeMetric(metric,
            rows.Select(r => r.LatencyMs).ToList(),
            rows.Select(r => r.Status).ToList(),
            rows.Select(r => r.Cost).ToList());
        var breached2 = op switch
        {
            "lt" => value < thresholdMetric,
            "gte" => value >= thresholdMetric,
            "lte" => value <= thresholdMetric,
            _ => value > thresholdMetric,
        };
        return (breached2, JsonSerializer.Serialize(new
        {
            rule.Kind, metric, op, threshold = thresholdMetric, windowMin,
            value, samples = rows.Count,
        }, JsonOpts));
    }

    /// <summary>Métricas suportadas: latency_p50, latency_p95, error_rate, spend, requests, errors.</summary>
    public static double ComputeMetric(string metric, List<long> lat, List<string?> status, List<double> cost)
    {
        if (lat.Count == 0) return 0;
        return metric switch
        {
            "latency_p50" => Percentile(lat, 0.50),
            "latency_p95" => Percentile(lat, 0.95),
            "error_rate" => status.Count(s => s is not null && s.StartsWith("5", StringComparison.Ordinal)) * 100.0 / status.Count,
            "fallback_rate" => status.Count(s => s == "fallback") * 100.0 / status.Count,
            "spend" => cost.Sum(),
            "requests" => status.Count,
            "errors" => status.Count(s => s is not null && s.StartsWith("5", StringComparison.Ordinal)),
            _ => 0,
        };
    }

    private static double Percentile(List<long> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    private static async Task<bool> InCooldownAsync(LlmRouterDbContext db, AlertRule rule, DateTime now, CancellationToken ct)
    {
        var last = await db.AlertInstances.Where(i => i.RuleId == rule.Id)
            .OrderByDescending(i => i.Id).Select(i => i.StartedAt).FirstOrDefaultAsync(ct);
        if (last is null) return false;
        return DateTime.TryParse(last, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            && now - t < TimeSpan.FromMinutes(Math.Max(1, rule.CooldownMin));
    }

    /// <summary>Cria deliveries pendentes para os canais da regra.</summary>
    public static void EnqueueDeliveries(LlmRouterDbContext db, AlertRule rule, long instanceId)
    {
        List<string> ids;
        try { ids = JsonSerializer.Deserialize<List<string>>(rule.ChannelIds) ?? []; }
        catch { ids = []; }
        var now = Iso(DateTime.UtcNow);
        foreach (var id in ids)
            db.AlertDeliveries.Add(new AlertDelivery
            {
                InstanceId = instanceId, ChannelId = id, Status = "pending", At = now,
            });
    }

    /// <summary>Despacha deliveries pendentes/falhas com retry.</summary>
    public static async Task<int> DeliverPendingAsync(LlmRouterDbContext db, IHttpClientFactory hf,
        ILogger? log = null, CancellationToken ct = default)
    {
        var pending = await db.AlertDeliveries
            .Where(d => d.Status == "pending" || (d.Status == "failed" && d.Attempts < MaxAttempts))
            .OrderBy(d => d.Id).Take(50).ToListAsync(ct);
        if (pending.Count == 0) return 0;
        var channelIds = pending.Select(d => d.ChannelId).Distinct().ToList();
        var channels = await db.AlertChannels.Where(c => channelIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var instanceIds = pending.Select(d => d.InstanceId).Distinct().ToList();
        var instances = await db.AlertInstances.Where(i => instanceIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, ct);
        var rules = await db.AlertRules.Where(r => instances.Values.Select(i => i.RuleId).Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct);
        var smtp = await SmtpConfigAsync(db, ct);
        var sent = 0;
        foreach (var d in pending)
        {
            d.Attempts++;
            try
            {
                if (!channels.TryGetValue(d.ChannelId, out var ch) || !ch.Enabled)
                    throw new InvalidOperationException("channel missing or disabled");
                instances.TryGetValue(d.InstanceId, out var inst);
                rules.TryGetValue(inst?.RuleId ?? "", out var rule);
                var payload = new
                {
                    instanceId = d.InstanceId,
                    rule = rule?.Name ?? inst?.RuleId,
                    rule?.Severity,
                    state = inst?.State,
                    detail = inst is null ? (JsonElement?)null : JsonDocument.Parse(inst.Detail).RootElement,
                    at = Iso(DateTime.UtcNow),
                };
                await SendAsync(hf, ch, payload, smtp, ct);
                d.Status = "sent"; d.DeliveredAt = Iso(DateTime.UtcNow); d.LastError = null;
                sent++;
            }
            catch (Exception ex)
            {
                d.LastError = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
                d.Status = d.Attempts >= MaxAttempts ? "failed" : "pending";
                log?.LogWarning("alert delivery {Id} attempt {N} failed: {Err}", d.Id, d.Attempts, d.LastError);
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>Envia o payload por canal. Testável via <paramref name="handler"/>.</summary>
    public static async Task SendAsync(IHttpClientFactory hf, AlertChannel ch, object payload,
        SmtpSettings? smtp = null, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        var cfg = JsonDocument.Parse(string.IsNullOrWhiteSpace(ch.Config) ? "{}" : ch.Config).RootElement;
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        switch (ch.Type)
        {
            case "slack":
            case "webhook":
            {
                var url = cfg.TryGetProperty("url", out var u1) ? u1.GetString()
                    : cfg.TryGetProperty("webhookUrl", out var u2) ? u2.GetString() : null;
                if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("channel url empty");
                var body = ch.Type == "slack"
                    ? JsonSerializer.Serialize(new { text = $"🚨 {JsonDocument.Parse(json).RootElement.GetProperty("rule").GetString()}: {json}" }, JsonOpts)
                    : json;
                var http = handler is null ? hf.CreateClient("alerts") : new HttpClient(handler);
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (ch.Type == "webhook" && cfg.TryGetProperty("secret", out var s) && s.GetString() is { Length: > 0 } secret)
                {
                    var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
                    req.Headers.Add("X-LLMR-Signature", $"sha256={sig}");
                }
                var res = await http.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                break;
            }
            case "email":
            {
                var to = cfg.TryGetProperty("to", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(to) || smtp is null || string.IsNullOrEmpty(smtp.Host))
                    throw new InvalidOperationException("smtp not configured");
                using var msg = new MailMessage(smtp.From ?? "alerts@llmrouter", to,
                    $"[LLMRouter] alert: {JsonDocument.Parse(json).RootElement.GetProperty("rule").GetString()}", json);
                using var client = new SmtpClient(smtp.Host, smtp.Port);
                if (!string.IsNullOrEmpty(smtp.User)) client.Credentials = new System.Net.NetworkCredential(smtp.User, smtp.Pass);
                client.EnableSsl = smtp.Ssl;
                await client.SendMailAsync(msg, ct);
                break;
            }
            default:
                throw new InvalidOperationException($"unknown channel type {ch.Type}");
        }
    }

    public sealed record SmtpSettings(string? Host, int Port, string? User, string? Pass, string? From, bool Ssl);

    /// <summary>Lê settings.smtp (JSON) → SmtpSettings, ou null se ausente.</summary>
    public static async Task<SmtpSettings?> SmtpConfigAsync(LlmRouterDbContext db, CancellationToken ct = default)
    {
        var row = await db.Settings.FindAsync([1], ct);
        if (row?.Data is not { Length: > 0 } raw) return null;
        try
        {
            var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("smtp", out var e)) return null;
            return new SmtpSettings(
                e.TryGetProperty("host", out var h) ? h.GetString() : null,
                e.TryGetProperty("port", out var p) && p.TryGetInt32(out var pv) ? pv : 587,
                e.TryGetProperty("user", out var u) ? u.GetString() : null,
                e.TryGetProperty("pass", out var pw) ? pw.GetString() : null,
                e.TryGetProperty("from", out var f) ? f.GetString() : null,
                e.TryGetProperty("ssl", out var ssl) && ssl.ValueKind == JsonValueKind.True);
        }
        catch { return null; }
    }

    private static string Iso(DateTime t) =>
        t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

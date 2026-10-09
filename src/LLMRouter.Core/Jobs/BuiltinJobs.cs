using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LLMRouter.Core.Jobs;

/// <summary>SPEC-038: built-in jobs.</summary>
public static class BuiltinJobs
{
    /// <summary>Probes every proxy of every pool (same logic as the
    /// /proxy-pools/{id}/check endpoint) and persists status.</summary>
    public sealed class ProxyPoolHealthJob : IJob
    {
        public string Id => "proxy-pool-health";
        public string Name => "Proxy pool health check";
        public TimeSpan Interval => TimeSpan.FromMinutes(15);
        public bool EnabledByDefault => true;

        public async Task<string> RunAsync(IServiceProvider services, CancellationToken ct)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            var pools = await db.ProxyPools.ToListAsync(ct);
            var ok = 0; var fail = 0;
            foreach (var p in pools)
            {
                var results = await CheckPoolAsync(db, p, ct);
                ok += results.Ok; fail += results.Fail;
            }
            return $"pools={pools.Count} proxies ok={ok} fail={fail}";
        }

        public static async Task<(int Ok, int Fail)> CheckPoolAsync(LlmRouterDbContext db, ProxyPool p, CancellationToken ct = default)
        {
            var data = JsonNode.Parse(p.Data)?.AsObject() ?? new JsonObject();
            var probeUrl = data["probeUrl"]?.GetValue<string>() ?? "https://api.ipify.org/?format=json";
            var ok = 0; var fail = 0;
            if (data["proxies"] is JsonArray arr)
            {
                foreach (var px in arr.OfType<JsonObject>())
                {
                    var url = px["url"]?.GetValue<string>() ?? "";
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    string status;
                    try
                    {
                        using var handler = new SocketsHttpHandler { Proxy = new System.Net.WebProxy(url), UseProxy = true };
                        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                        var res = await http.GetAsync(probeUrl, ct);
                        status = res.IsSuccessStatusCode ? "ok" : $"http{(int)res.StatusCode}";
                        px["failCount"] = 0;
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        status = "fail";
                        px["failCount"] = (px["failCount"]?.GetValue<int>() ?? 0) + 1;
                        px["lastError"] = ex.Message.Length > 120 ? ex.Message[..120] : ex.Message;
                        fail++;
                    }
                    px["latencyMs"] = sw.ElapsedMilliseconds;
                    px["lastStatus"] = status;
                    px["checkedAt"] = DateTime.UtcNow.ToString("o");
                }
            }
            p.Data = data.ToJsonString();
            p.TestStatus = fail == 0 && ok > 0 ? "ok" : ok > 0 ? "partial" : "fail";
            p.UpdatedAt = DateTime.UtcNow.ToString("o");
            await db.SaveChangesAsync(ct);
            return (ok, fail);
        }
    }

    /// <summary>Deletes usage/request history older than retention days
    /// (settings.retention.usageDays, default 30).</summary>
    public sealed class UsagePruneJob : IJob
    {
        public string Id => "usage-prune";
        public string Name => "Usage history retention";
        public TimeSpan Interval => TimeSpan.FromHours(24);
        public bool EnabledByDefault => true;

        public async Task<string> RunAsync(IServiceProvider services, CancellationToken ct)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            var days = 30;
            var s = await db.Settings.FirstOrDefaultAsync(ct);
            if (s is not null)
            {
                var node = JsonNode.Parse(s.Data)?["retention"]?["usageDays"];
                if (node is not null && int.TryParse(node.ToString(), out var d)) days = Math.Clamp(d, 1, 3650);
            }
            var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");
            var usage = await db.UsageHistory.Where(r => string.Compare(r.Timestamp, cutoff) < 0).ExecuteDeleteAsync(ct);
            var details = await db.RequestDetails.Where(r => string.Compare(r.Timestamp, cutoff) < 0).ExecuteDeleteAsync(ct);
            return $"older than {days}d: usage={usage} details={details}";
        }
    }

    // SPEC-041: quotaSchedules rows whose window elapsed get their daily-token
    // counters (kv scope "keyusage") reset; runs hourly and lets each schedule
    // decide if it's due (daily/weekly/monthly windows).
    public sealed class QuotaSchedulesJob : IJob
    {
        public string Id => "quota-schedules";
        public string Name => "Quota window resets";
        public TimeSpan Interval => TimeSpan.FromHours(1);
        public bool EnabledByDefault => true;

        public async Task<string> RunAsync(IServiceProvider services, CancellationToken ct)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            var now = DateTime.UtcNow;
            var ran = 0;
            foreach (var s in await db.QuotaSchedules.ToListAsync(ct))
            {
                var last = DateTime.TryParse(s.LastRunAt, out var l)
                    ? l : DateTime.MinValue;
                var due = s.Window switch
                {
                    "weekly" => now - last >= TimeSpan.FromDays(7),
                    "monthly" => now - last >= TimeSpan.FromDays(28),
                    _ => now - last >= TimeSpan.FromDays(1),
                };
                if (!due) continue;
                var cleared = s.Target == "all"
                    ? await KeyQuota.ResetDailyCountersAsync(db, ct)
                    : await ClearKeyAsync(db, s.Target, ct);
                s.LastRunAt = now.ToString("yyyy-MM-dd HH:mm:ss");
                ran++;
            }
            await db.SaveChangesAsync(ct);
            return $"ran {ran} schedule(s)";
        }

        private static async Task<int> ClearKeyAsync(LlmRouterDbContext db, string keyOrId, CancellationToken ct)
        {
            var key = await db.ApiKeys.FindAsync([keyOrId], ct) is { } k ? k.Key : keyOrId;
            var rows = await db.Kv.Where(r => r.Scope == KeyQuota.UsageScope && r.Key.StartsWith(key + ":")).ToListAsync(ct);
            db.Kv.RemoveRange(rows);
            return rows.Count;
        }
    }

    // SPEC-042: run enabled log-export destinations whose config.scheduleMinutes
    // elapsed since LastRunAt.
    public sealed class LogExportJob : IJob
    {
        public string Id => "log-export";
        public string Name => "Scheduled log exports";
        public TimeSpan Interval => TimeSpan.FromMinutes(5);
        public bool EnabledByDefault => true;

        public async Task<string> RunAsync(IServiceProvider services, CancellationToken ct)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            var hf = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var now = DateTime.UtcNow;
            var ran = 0; var failed = 0;
            foreach (var d in await db.LogExportDestinations.Where(x => x.Enabled).ToListAsync(ct))
            {
                var cfg = JsonDocument.Parse(d.Config).RootElement;
                if (!cfg.TryGetProperty("scheduleMinutes", out var sm) || !sm.TryGetInt32(out var mins) || mins <= 0)
                    continue;
                var last = DateTime.TryParse(d.LastRunAt, out var l) ? l : DateTime.MinValue;
                if (now - last < TimeSpan.FromMinutes(mins)) continue;
                await Logging.LogExporter.RunAsync(db, d, hf, ct);
                if (d.LastRunStatus == "ok") ran++; else failed++;
            }
            return $"ran {ran} ok, {failed} failed";
        }
    }

    // Writes a portable JSON export (SPEC-037 format) to
    // {dbDir}/backups/llmrouter-<ts>.json, keeping the last 14.
    public sealed class DbBackupJob : IJob
    {
        public string Id => "db-backup";
        public string Name => "JSON DB backup";
        public TimeSpan Interval => TimeSpan.FromHours(24);
        public bool EnabledByDefault => true;

        public async Task<string> RunAsync(IServiceProvider services, CancellationToken ct)
        {
            using var scope = services.CreateScope();
            var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var dbPath = cfg["Db:Path"]
                ?? Environment.GetEnvironmentVariable("LLMROUTER_DB_PATH")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLMRouter", "llmrouter.db");
            var dir = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"llmrouter-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

            var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            var conn = db.Database.GetDbConnection();
            var opened = conn.State != System.Data.ConnectionState.Open;
            if (opened) await conn.OpenAsync(ct);
            var tables = new Dictionary<string, List<Dictionary<string, object?>>>();
            try
            {
                var names = new List<string>();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct)) names.Add(rd.GetString(0));
                }
                foreach (var name in names)
                {
                    var rows = new List<Dictionary<string, object?>>();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT * FROM \"{name.Replace("\"", "\"\"")}\"";
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                    {
                        var row = new Dictionary<string, object?>();
                        for (var i = 0; i < rd.FieldCount; i++)
                            row[rd.GetName(i)] = rd.GetValue(i) is DBNull ? null : rd.GetValue(i);
                        rows.Add(row);
                    }
                    tables[name] = rows;
                }
            }
            finally { if (opened) conn.Close(); }
            await File.WriteAllTextAsync(file,
                JsonSerializer.Serialize(new { exportedAt = DateTime.UtcNow.ToString("o"), tables }), ct);
            foreach (var old in Directory.GetFiles(dir, "llmrouter-*.json").OrderByDescending(f => f).Skip(14))
                try { File.Delete(old); } catch { }
            return $"wrote {file} ({tables.Count} tables)";
        }
    }
}

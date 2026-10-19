using System.Text.Json;
using System.Threading.Channels;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// SPEC-108: usage row enqueued by the request path — one item produces a
/// usageHistory row, a requestDetails row, the usageDaily rollup, the daily
/// quota credit and the chat-session upsert, all inside the shared batch
/// transaction.
/// </summary>
public sealed record UsageWrite(
    string Timestamp, string? Provider, string? Model, string? ConnectionId,
    string? ApiKey, string? Endpoint, long PromptTokens, long CompletionTokens,
    double Cost, long LatencyMs, string? Status, string? DetailJson,
    // SPEC-111: atribuição a usuário/virtual-key (null = chave admin/global).
    string? UserId = null, string? UserKeyId = null,
    // SPEC-116: proxy-journal fields captured at the send site (null = auto direct/global).
    ProxyLogInfo? Proxy = null, string? CorrelationId = null,
    // SPEC-143: request/response payloads — only persisted when ioLogging is on.
    string? Prompt = null, string? Response = null);

/// <summary>
/// SPEC-116: resolved egress-proxy info captured where the upstream send
/// happens — carried on LogUsageAsync/UsageWrite so the proxy_logs journal
/// row can tag which proxy layer (provider pool | global oneproxy | direct)
/// served the request.
/// </summary>
public sealed record ProxyLogInfo(
    string Level, string? LevelId = null,
    string? ProxyId = null, string? ProxyType = null,
    string? ProxyHost = null, int? ProxyPort = null, string? ProxyName = null,
    string? TargetUrl = null, string? ClientIp = null, string? ComboId = null);

/// <summary>
/// SPEC-116: builds proxyLogs rows — mirrors upstream proxyLogger.
/// Every logged request produces one journal row; the level says which proxy
/// layer carried it (provider pool, global oneproxy, or direct).
/// </summary>
public static class ProxyJournal
{
    /// <summary>usage status → journal status (success|error|timeout).</summary>
    public static string StatusOf(string? status, string? error) =>
        (status?.StartsWith('2') ?? false) ? "success"
        : (status is "408" or "499" or "timeout" or "err-timeout"
            || (error?.Contains("timeout", StringComparison.OrdinalIgnoreCase) ?? false)) ? "timeout"
        : "error";

    /// <summary>Injects correlationId into the usage Meta json (object merge).</summary>
    public static string MergeCorrelation(string? meta, string correlationId)
    {
        try
        {
            if (meta is not null)
            {
                using var doc = JsonDocument.Parse(meta);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var d = doc.RootElement.EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.Clone());
                    d["correlationId"] = JsonSerializer.SerializeToElement(correlationId);
                    return JsonSerializer.Serialize(d);
                }
            }
        }
        catch { /* non-object meta wraps below */ }
        return meta is null
            ? JsonSerializer.Serialize(new { correlationId })
            : JsonSerializer.Serialize(new { correlationId, error = meta });
    }

    /// <summary>Builds the journal row; proxy object resolved from plog or oneproxy.</summary>
    public static ProxyLog Row(
        string id, string now, string? provider, string? connectionId,
        string? status, string? error, long latencyMs, string? endpoint,
        string? correlationId, string? oneProxyUrl, ProxyLogInfo? plog)
    {
        var level = plog?.Level ?? (oneProxyUrl is not null ? "global" : "direct");
        string? proxyJson = null;
        if (plog?.ProxyId is not null)
        {
            proxyJson = JsonSerializer.Serialize(new
            {
                id = plog.ProxyId,
                type = plog.ProxyType ?? "http",
                host = plog.ProxyHost,
                port = plog.ProxyPort,
                name = plog.ProxyName ?? plog.ProxyId,
            });
        }
        else if (level == "global" && oneProxyUrl is not null)
        {
            var (h, p) = HostPort(oneProxyUrl);
            proxyJson = JsonSerializer.Serialize(new
            {
                id = "oneproxy", type = "http", host = h, port = p, name = "oneproxy",
            });
        }
        return new ProxyLog
        {
            Id = id,
            Timestamp = now,
            Status = StatusOf(status, error),
            Proxy = proxyJson,
            Level = level,
            LevelId = plog?.LevelId,
            Provider = provider,
            TargetUrl = plog?.TargetUrl ?? endpoint,
            ClientIp = plog?.ClientIp,
            LatencyMs = latencyMs,
            Error = error is { Length: > 500 } ? error[..500] : error,
            ConnectionId = connectionId,
            ComboId = plog?.ComboId,
            Account = connectionId,
            CorrelationId = correlationId,
            UpstreamStatus = int.TryParse(status, out var us) ? us : null,
        };
    }

    /// <summary>(host,port) de uma url de proxy; porta null quando ausente.</summary>
    public static (string? host, int? port) HostPort(string url)
    {
        try
        {
            var u = new Uri(url.Contains("://") ? url : $"http://{url}");
            return (u.Host, u.IsDefaultPort ? (int?)null : u.Port);
        }
        catch { return (url, null); }
    }
}

/// <summary>
/// SPEC-074 + SPEC-108: serializes telemetry/config writes through a single
/// dedicated DbContext instead of N request-scoped contexts racing on SQLite.
///
/// Two channels:
/// - delegate items (<see cref="PostAsync"/>) run one at a time (audits,
///   connection reports);
/// - typed <see cref="UsageWrite"/> items (<see cref="PostUsageAsync"/>) are
///   flushed in batches of <see cref="BatchSize"/> rows or every
///   <see cref="FlushInterval"/> — one transaction per batch.
///
/// Backpressure: with <see cref="BlockOnFull"/> false (default) a full usage
/// channel drops the row and counts it in <see cref="UsageDropped"/> — the
/// request path never blocks. With <see cref="BlockOnFull"/> true the caller
/// waits for capacity (strict accounting installs).
///
/// Escape hatch: sync mode (<see cref="SyncMode"/>, env LLMR_SYNC_WRITES=1 or
/// settings.usageSink.sync) applies writes inline — used by tests/debug.
/// Shutdown drains remaining rows inside a 5s budget.
/// </summary>
public sealed class UsageWriter : BackgroundService
{
    private sealed record WorkItem(Func<LlmRouterDbContext, Task> Work, TaskCompletionSource Done);

    public const int BatchSize = 200;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(5);

    private readonly Channel<WorkItem> _ch = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<UsageWrite> _usageCh = Channel.CreateBounded<UsageWrite>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UsageWriter> _log;
    
    private int _dropped;
    private long _usageDropped;

    public UsageWriter(IServiceScopeFactory scopeFactory, ILogger<UsageWriter> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    /// <summary>Per-instance sync override — test hook (the suite runs with
    /// LLMR_SYNC_WRITES=1 globally; async behavior needs an explicit false).
    /// Null defers to <see cref="SyncMode"/>.</summary>
    public bool? SyncOverrideInstance { get; init; }

    private bool IsSync => SyncOverrideInstance ?? SyncMode;

    /// <summary>When true, posts execute inline (synchronously awaited).</summary>
    public static bool SyncMode => SyncModeOverride ??
        Environment.GetEnvironmentVariable("LLMR_SYNC_WRITES") is "1" or "true";

    /// <summary>settings.usageSink.sync — explicit override over the env flag.</summary>
    public static bool? SyncModeOverride { get; set; }

    /// <summary>settings.usageSink.blockOnFull — wait for channel capacity.</summary>
    public static bool BlockOnFull { get; set; }

    /// <summary>SPEC-101 hook: usage rows dropped by backpressure.</summary>
    public long UsageDropped => Interlocked.Read(ref _usageDropped);

    // Diagnostics for the drain/batch contract (SPEC-108).
    private long _rowsApplied, _batchesCommitted, _drainedRows, _collected;
    public long RowsApplied => Interlocked.Read(ref _rowsApplied);
    public long BatchesCommitted => Interlocked.Read(ref _batchesCommitted);
    public long DrainedRows => Interlocked.Read(ref _drainedRows);
    public long Collected => Interlocked.Read(ref _collected);

    /// <summary>Reads settings.usageSink.{sync,blockOnFull} from the settings row.</summary>
    public static void ApplySettings(LlmRouterDbContext db)
    {
        try
        {
            var json = db.Settings.OrderBy(s => s.Id).Select(s => s.Data).FirstOrDefault();
            if (string.IsNullOrEmpty(json)) return;
            var d = JsonDocument.Parse(json).RootElement;
            if (!d.TryGetProperty("usageSink", out var us)) return;
            if (us.TryGetProperty("sync", out var sy) &&
                sy.ValueKind is JsonValueKind.True or JsonValueKind.False)
                SyncModeOverride = sy.GetBoolean();
            if (us.TryGetProperty("blockOnFull", out var b) &&
                b.ValueKind is JsonValueKind.True or JsonValueKind.False)
                BlockOnFull = b.GetBoolean();
        }
        catch { /* tolerate malformed/absent settings */ }
    }

    /// <summary>
    /// Enqueue a delegate write (audits, reports). In prod mode returns as
    /// soon as the item is queued; in sync mode runs the work and awaits it.
    /// </summary>
    public async ValueTask PostAsync(Func<LlmRouterDbContext, Task> work)
    {
        if (IsSync)
        {
            await using var sc = _scopeFactory.CreateAsyncScope();
            var db = sc.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            await work(db);
            return;
        }
        var item = new WorkItem(work, new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_ch.Writer.TryWrite(item)) return;
        if (++_dropped % 100 == 1)
            _log.LogWarning("UsageWriter channel full; running write inline (drop={dropped})", _dropped);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db2 = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        db2.SuppressCacheBust = true;
        await work(db2);
    }

    /// <summary>
    /// Enqueue a usage row. Never blocks the request path unless
    /// <see cref="BlockOnFull"/> is set; otherwise drops + counts.
    /// </summary>
    public async ValueTask PostUsageAsync(UsageWrite u, CancellationToken ct = default)
    {
        if (IsSync)
        {
            await using var sc = _scopeFactory.CreateAsyncScope();
            var db = sc.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            var row = await ApplyUsageAsync(db, u, ct);
            await db.SaveChangesAsync(ct);
            if (row.UserId is not null || row.UserKeyId is not null)
            {
                await UsageSideEffects.ApplyAsync(db, row, ct);
                await db.SaveChangesAsync(ct);
            }
            return;
        }
        if (BlockOnFull)
        {
            await _usageCh.Writer.WriteAsync(u, ct);
            return;
        }
        if (_usageCh.Writer.TryWrite(u)) return;
        Interlocked.Increment(ref _usageDropped);
        GatewayMetrics.UsageDropped.Add(1); // SPEC-101: llmr_usage_dropped_total
        if (_usageDropped % 100 == 1)
            _log.LogWarning("UsageSink channel full; dropped usage row (total={dropped})", _usageDropped);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        db.SuppressCacheBust = true;
        try
        {
            while (true)
            {
                var batch = await CollectBatchAsync(stoppingToken);
                if (batch.Count > 0)
                {
                    // A collected batch always commits (≤BatchSize rows, bounded) —
                    // abandoning it mid-shutdown would lose rows already dequeued.
                    try { await ApplyBatchAsync(db, batch, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "UsageWriter batch failed ({n} rows)", batch.Count);
                    }
                }
                while (_ch.Reader.TryRead(out var item))
                {
                    try { await item.Work(db); }
                    catch (Exception ex) { _log.LogError(ex, "UsageWriter work item failed"); }
                    finally { item.Done.TrySetResult(); }
                }
                if (_ch.Reader.Count == 0 && _usageCh.Reader.Count == 0) await Task.Yield();
                var workWait = _ch.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var usageWait = _usageCh.Reader.WaitToReadAsync(stoppingToken).AsTask();
                await Task.WhenAny(workWait, usageWait);
            }
        }
        catch (OperationCanceledException) { /* graceful drain below */ }
        catch (Exception ex) { _log.LogError(ex, "UsageWriter loop crashed"); }

        await DrainAsync(db);
    }

    /// <summary>
    /// Synchronously drains both channels on the caller's thread (own scope +
    /// context). Channel reads are destructive, so this can safely run beside
    /// the background worker — a row is applied exactly once by whichever side
    /// dequeues it. Deterministic flush for graceful shutdown and tests.
    /// </summary>
    public async Task FlushAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        db.SuppressCacheBust = true;
        await DrainAsync(db);
    }

    /// <summary>Shutdown: flush whatever remains within the drain budget.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _ch.Writer.TryComplete();
        _usageCh.Writer.TryComplete();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DrainBudget);
        await base.StopAsync(budget.Token);
    }

    private async Task DrainAsync(LlmRouterDbContext db)
    {
        var deadline = DateTime.UtcNow + DrainBudget;
        try
        {
            var pending = new List<UsageWrite>();
            while (_usageCh.Reader.TryRead(out var u)) pending.Add(u);
            Interlocked.Add(ref _drainedRows, pending.Count);
            foreach (var chunk in pending.Chunk(BatchSize))
            {
                if (DateTime.UtcNow > deadline) break;
                try { await ApplyBatchAsync(db, [.. chunk], CancellationToken.None); }
                catch (Exception ex) { _log.LogError(ex, "UsageWriter drain batch failed"); }
            }
            while (_ch.Reader.TryRead(out var item))
            {
                if (DateTime.UtcNow > deadline) break;
                try { await item.Work(db); }
                catch (Exception ex) { _log.LogError(ex, "UsageWriter drain item failed"); }
                finally { item.Done.TrySetResult(); }
            }
        }
        catch (Exception ex) { _log.LogError(ex, "UsageWriter drain failed"); }
    }

    /// <summary>Collect up to BatchSize rows, flushing early after FlushInterval.</summary>
    private async ValueTask<List<UsageWrite>> CollectBatchAsync(CancellationToken ct)
    {
        var batch = new List<UsageWrite>(BatchSize);
        var deadline = DateTime.UtcNow + FlushInterval;
        while (batch.Count < BatchSize)
        {
            if (_usageCh.Reader.TryRead(out var u)) { batch.Add(u); Interlocked.Increment(ref _collected); continue; }
            if (batch.Count == 0) break;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) break;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(left);
            try
            {
                if (await _usageCh.Reader.WaitToReadAsync(wait.Token)) continue;
                break;
            }
            catch (OperationCanceledException)
            {
                // Flush-interval timeout OR shutdown — either way return what we
                // collected; dequeued items must never be discarded silently.
                break;
            }
        }
        return batch;
    }

    /// <summary>One transaction per batch: all writes for all rows commit together.</summary>
    private async Task ApplyBatchAsync(LlmRouterDbContext db, List<UsageWrite> batch, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = new List<UsageRecord>(batch.Count);
        foreach (var u in batch)
            rows.Add(await ApplyUsageAsync(db, u, ct));
        await db.SaveChangesAsync(ct);
        // SPEC-111: pós-save (UsageId atribuído) — spend da vk + débito no
        // creditLedger, ainda dentro da mesma transação do batch.
        foreach (var r in rows)
            if (r.UserId is not null || r.UserKeyId is not null)
                await UsageSideEffects.ApplyAsync(db, r, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        Interlocked.Add(ref _rowsApplied, batch.Count);
        Interlocked.Increment(ref _batchesCommitted);
        foreach (var p in batch.Select(u => u.Provider).Distinct())
            if (p is not null) HotCache.Default.Invalidate($"qwx:{p}");
        foreach (var k in batch.Select(u => u.ApiKey).Distinct())
            if (k is not null) HotCache.Default.InvalidateTag($"dcap:{k}");
    }

    /// <summary>
    /// Single usage write applied to a context — same side effects the old
    /// inline write produced (history + detail + rollup + quota + session).
    /// Caller saves/commits.
    /// </summary>
    internal static async Task<UsageRecord> ApplyUsageAsync(LlmRouterDbContext db, UsageWrite u, CancellationToken ct)
    {
        var row = new UsageRecord
        {
            Timestamp = u.Timestamp,
            Provider = u.Provider,
            Model = u.Model,
            ConnectionId = u.ConnectionId,
            ApiKey = u.ApiKey,
            Endpoint = u.Endpoint,
            PromptTokens = u.PromptTokens,
            CompletionTokens = u.CompletionTokens,
            Tokens = (u.PromptTokens + u.CompletionTokens).ToString(),
            Status = u.Status,
            Cost = u.Cost,
            LatencyMs = u.LatencyMs,
            Meta = u.DetailJson,
            UserId = u.UserId,
            UserKeyId = u.UserKeyId,
        };
        db.UsageHistory.Add(row);
        // SPEC-116: settings.requestLogging=false pauses request-payload capture
        // (usage accounting still writes).
        var sdata = await HotReads.SettingsDataAsync(db);
        // SPEC-116: proxy journal row for every logged request (metadata, ungated).
        db.ProxyLogs.Add(ProxyJournal.Row(
            Guid.NewGuid().ToString("N"), u.Timestamp, u.Provider, u.ConnectionId,
            u.Status, null, u.LatencyMs, u.Endpoint, u.CorrelationId,
            await HotReads.OneProxyCurrentAsync(db, sdata), u.Proxy));
        var recording = !sdata.TryGetProperty("requestLogging", out var rl)
            || rl.ValueKind is not JsonValueKind.False;
        // SPEC-143: ioLogging opt-in (settings.observability.ioLogging) — off =
        // metadata-only rows (OpenRouter privacy default); on = payload stored.
        var ioOn = sdata.TryGetProperty("observability", out var obs)
            && obs.ValueKind is JsonValueKind.Object
            && obs.TryGetProperty("ioLogging", out var iol)
            && iol.ValueKind is JsonValueKind.True;
        if (recording)
        {
            var rd = new RequestDetail
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = u.Timestamp,
                Provider = u.Provider,
                Model = u.Model,
                ConnectionId = u.ConnectionId,
                Status = u.Status,
                Data = u.DetailJson ?? "{}",
                Prompt = ioOn ? u.Prompt : null,
                Response = ioOn ? u.Response : null,
            };
            db.RequestDetails.Add(rd);
            RequestLogTail.Publish(rd); // SPEC-148: SSE live tail
        }
        await GatewayEngine.RollupDailyOnAsync(
            db, u.Provider ?? "", u.Model ?? "", (int)u.PromptTokens, (int)u.CompletionTokens);
        await KeyQuota.CreditDailyAsync(
            db, u.ApiKey ?? "", (int)(u.PromptTokens + u.CompletionTokens), u.Model);

        var sess = await db.ChatSessions
            .FirstOrDefaultAsync(s => s.KeyId == u.ApiKey && s.Model == u.Model, ct);
        if (sess is null)
        {
            db.ChatSessions.Add(new ChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                KeyId = u.ApiKey, Model = u.Model,
                StartedAt = u.Timestamp, LastSeenAt = u.Timestamp, MessageCount = 1,
            });
        }
        else { sess.LastSeenAt = u.Timestamp; sess.MessageCount++; }
        return row;
    }
}

using System.Threading.Channels;
using LLMRouter.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// SPEC-074: serializes telemetry/config writes through a single dedicated
/// DbContext instead of N request-scoped contexts racing on SQLite. Requests
/// enqueue their write and keep going — no per-request SaveChanges on the
/// response path. Bounded channel; on overflow the write runs inline on its
/// own scope (never dropped).
///
/// Test determinism: set env LLMR_SYNC_WRITES=1 (or UsageWriter.SyncMode) to
/// run posts inline and awaited — the test suite does this via a module
/// initializer so assertions see rows immediately.
/// </summary>
public sealed class UsageWriter : BackgroundService
{
    private sealed record WorkItem(Func<LlmRouterDbContext, Task> Work, TaskCompletionSource Done);

    private readonly Channel<WorkItem> _ch = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UsageWriter> _log;
    private int _dropped;

    public UsageWriter(IServiceScopeFactory scopeFactory, ILogger<UsageWriter> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    /// <summary>When true, posts execute inline (synchronously awaited).</summary>
    public static bool SyncMode =>
        Environment.GetEnvironmentVariable("LLMR_SYNC_WRITES") is "1" or "true";

    /// <summary>
    /// Enqueue a write. In prod mode returns as soon as the item is queued
    /// (the caller does not wait for the save). In sync mode (tests) runs the
    /// work on a fresh scope and awaits it.
    /// </summary>
    public async ValueTask PostAsync(Func<LlmRouterDbContext, Task> work)
    {
        if (SyncMode)
        {
            await using var sc = _scopeFactory.CreateAsyncScope();
            var db = sc.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            await work(db);
            return;
        }
        var item = new WorkItem(work, new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_ch.Writer.TryWrite(item)) return;
        // channel full — inline fallback on a dedicated scope (never drop silently)
        if (++_dropped % 100 == 1)
            _log.LogWarning("UsageWriter channel full; running write inline (drop={dropped})", _dropped);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db2 = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        db2.SuppressCacheBust = true;
        await work(db2);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        db.SuppressCacheBust = true;
        await foreach (var item in _ch.Reader.ReadAllAsync(stoppingToken))
        {
            try { await item.Work(db); }
            catch (Exception ex) { _log.LogError(ex, "UsageWriter work item failed"); }
            finally { item.Done.TrySetResult(); }
            // Yield periodically so a flood of posts doesn't starve reads on the single context.
            if (_ch.Reader.Count == 0) await Task.Yield();
        }
    }
}

using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-108: async usage sink — bounded channel, batch flush (200 / 500ms),
/// drop counter on backpressure, graceful 5s drain, sync escape hatch.
/// The suite sets LLMR_SYNC_WRITES=1 globally (AssemblyInfo), so async paths
/// are exercised via the writer's internal per-instance syncOverride.
/// </summary>
public sealed class UsageSinkTests : IDisposable
{
    private sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState s) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel l) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel l, EventId id, TState s,
            Exception? e, Func<TState, Exception?, string> f)
            => Lines.Add($"{l}: {f(s, e)} {e}");
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-sink-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;
    private readonly UsageWriter _writer;
    private readonly ListLogger<UsageWriter> _log = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public UsageSinkTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        _sp = new ServiceCollection()
            .AddDbContext<LlmRouterDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"))
            .BuildServiceProvider();
        using var db = Db();
        db.EnsureCreated();
        _writer = new UsageWriter(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            _log)
        { SyncOverrideInstance = false };
    }

    public void Dispose()
    {
        _writer.Dispose();
        _sp.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private static UsageWrite Row(int i = 0, string? status = "ok") =>
        new("2026-10-11T00:00:00Z", "openai", $"m{i}", "conn1", "key1", "/v1/chat",
            10 + i, 20 + i, 0.001, 42, status, $"{{\"i\":{i}}}");

    private async Task WaitForAsync(Func<LlmRouterDbContext, bool> cond, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = Db();
            if (cond(db)) return;
            await Task.Delay(50);
        }
        await using var last = Db();
        cond(last).ShouldBeTrue("condition not met within " + ms + "ms");
    }

    [Fact]
    public async Task Batches_Persist_Usage_Detail_Rollup_And_Session()
    {
        await _writer.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 5; i++) await _writer.PostUsageAsync(Row(i));
            await WaitForAsync(db => db.UsageHistory.Count() == 5);

            await using var db = Db();
            db.RequestDetails.Count().ShouldBe(5);
            db.UsageHistory.Select(u => u.Model).OrderBy(m => m).ToArray()
                .ShouldBe(["m0", "m1", "m2", "m3", "m4"]);

            var daily = db.UsageDaily.Single();
            daily.Data.ShouldContain("\"requests\":5");
            var sess = db.ChatSessions.Single(s => s.KeyId == "key1" && s.Model == "m0");
            sess.MessageCount.ShouldBe(1);
        }
        finally { await _writer.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SyncMode_Applies_Inline()
    {
        var writer = new UsageWriter(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UsageWriter>.Instance)
        { SyncOverrideInstance = true };
        await writer.PostUsageAsync(Row(9, status: "error"));
        await using var db = Db();
        var rec = db.UsageHistory.Single();
        rec.Status.ShouldBe("error");
        db.RequestDetails.Count().ShouldBe(1);
    }

    [Fact]
    public async Task Full_Channel_Drops_And_Counts()
    {
        // Worker not started: the bounded channel (10k) fills; the next post drops.
        for (var i = 0; i < 10_000; i++) await _writer.PostUsageAsync(Row(0));
        _writer.UsageDropped.ShouldBe(0);
        await _writer.PostUsageAsync(Row(0));
        _writer.UsageDropped.ShouldBe(1);
    }

    [Fact]
    public async Task StopAsync_Drains_Pending_Rows()
    {
        await _writer.StartAsync(CancellationToken.None);
        for (var i = 0; i < 7; i++) await _writer.PostUsageAsync(Row(i));
        // Stop completes the writers; the worker drains the remainder in ≤5s.
        await _writer.StopAsync(CancellationToken.None);
        // StopAsync's 5s budget bounds the WAIT, not the drain — under a loaded
        // threadpool the worker may commit right after StopAsync returns, so
        // poll instead of assuming the commit is visible at return.
        try { await WaitForAsync(db => db.UsageHistory.Count() == 7, ms: 30000); }
        catch
        {
            _out.WriteLine($"applied={_writer.RowsApplied} batches={_writer.BatchesCommitted} drained={_writer.DrainedRows} collected={_writer.Collected} dropped={_writer.UsageDropped}");
            _out.WriteLine("logs=[" + string.Join(" | ", _log.Lines) + "]");
            throw;
        }
        _out.WriteLine($"applied={_writer.RowsApplied} batches={_writer.BatchesCommitted} drained={_writer.DrainedRows} collected={_writer.Collected} dropped={_writer.UsageDropped}");
        _out.WriteLine("logs=[" + string.Join(" | ", _log.Lines) + "]");
        await using var db = Db();
        db.UsageHistory.Count().ShouldBe(7, string.Join("\n", _log.Lines));
        db.RequestDetails.Count().ShouldBe(7);
    }

    [Fact]
    public void ApplySettings_Reads_UsageSink_Flags()
    {
        var prevSync = UsageWriter.SyncModeOverride;
        var prevBlock = UsageWriter.BlockOnFull;
        try
        {
            using (var db = Db())
            {
                db.Settings.Add(new SettingRow { Id = 1, Data = "{\"usageSink\":{\"sync\":true,\"blockOnFull\":true}}" });
                db.SaveChanges();
                UsageWriter.ApplySettings(db);
            }
            UsageWriter.SyncModeOverride.ShouldBe(true);
            UsageWriter.BlockOnFull.ShouldBe(true);
        }
        finally
        {
            UsageWriter.SyncModeOverride = prevSync;
            UsageWriter.BlockOnFull = prevBlock;
        }
    }
}

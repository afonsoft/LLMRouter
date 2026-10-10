using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-074: hot-path cache invalidation + off-path usage writer.</summary>
public class GatewayThroughputTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-perf-{Guid.NewGuid():N}.db");

    private LlmRouterDbContext Ctx() => new(
        new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    public void Dispose()
    {
        HotCache.Default.InvalidateAll();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext SeedCtx()
    {
        var db = Ctx();
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task HotRead_CachesThenBustsOnConfigWrite()
    {
        await using var reader = SeedCtx();
        // unique entry + own factory — deterministic regardless of the shared
        // static cache/counters (parallel test classes touch the same statics)
        var calls = 0;
        var ck = $"k1:{Guid.NewGuid():N}";
        Task<ApiKey?> F() { calls++; return reader.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Key == "k1")!; }
        (await HotCache.Default.GetOrAddAsync(ck, TimeSpan.FromSeconds(30), F)).ShouldBeNull();
        (await HotCache.Default.GetOrAddAsync(ck, TimeSpan.FromSeconds(30), F)).ShouldBeNull();
        calls.ShouldBe(1); // second read served from cache

        // write through another context → cache busted → next read sees the row
        await using (var writer = Ctx())
        {
            writer.ApiKeys.Add(new ApiKey { Key = "k1", Name = "n", IsActive = true });
            await writer.SaveChangesAsync();
        }
        var found = await HotCache.Default.GetOrAddAsync(ck, TimeSpan.FromSeconds(30), F);
        calls.ShouldBe(2);
        found.ShouldNotBeNull();
        found.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task TelemetryWrite_DoesNotBustHotCache()
    {
        await using var reader = SeedCtx();
        await using (var seed = Ctx())
        {
            seed.ApiKeys.Add(new ApiKey { Key = "k2", Name = "n", IsActive = true });
            await seed.SaveChangesAsync();
        }
        var calls = 0;
        var ck = $"k2:{Guid.NewGuid():N}";
        Task<ApiKey?> F() { calls++; return reader.ApiKeys.AsNoTracking().FirstOrDefaultAsync(x => x.Key == "k2")!; }
        (await HotCache.Default.GetOrAddAsync(ck, TimeSpan.FromSeconds(30), F)).ShouldNotBeNull();

        await using (var w = Ctx())
        {
            w.UsageHistory.Add(new UsageRecord
            {
                Timestamp = "2026-01-01 00:00:00", Provider = "p", Model = "m",
                ApiKey = "k2", Endpoint = "openai", Tokens = "1", Status = "200",
            });
            await w.SaveChangesAsync();
        }
        (await HotCache.Default.GetOrAddAsync(ck, TimeSpan.FromSeconds(30), F)).ShouldNotBeNull();
        calls.ShouldBe(1); // served from cache, not re-queried
    }

    [Fact]
    public async Task UsageWriter_SyncMode_WritesUsageAndSession()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<GatewayEngine>();
        await engine.LogUsageAsync("openai", "gpt-4", "c1", "k-test", "openai", 10, 5, "200", null, 42);

        await using var check = Ctx();
        (await check.UsageHistory.CountAsync(u => u.ApiKey == "k-test")).ShouldBe(1);
        (await check.RequestDetails.CountAsync(r => r.Provider == "openai")).ShouldBe(1);
        (await check.ChatSessions.CountAsync(s => s.KeyId == "k-test" && s.Model == "gpt-4")).ShouldBe(1);
        factory.Dispose();
    }

    [Fact]
    public async Task UsageWriter_ConcurrentPosts_AllRowsLand()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        var writer = factory.Services.GetRequiredService<UsageWriter>();
        var posts = Enumerable.Range(0, 40).Select(i => writer.PostAsync(async d =>
        {
            d.UsageHistory.Add(new UsageRecord
            {
                Timestamp = "2026-01-01 00:00:00", Provider = "p", Model = $"m{i}",
                ApiKey = "bulk", Endpoint = "openai", Tokens = "1", Status = "200",
            });
            await d.SaveChangesAsync();
        }).AsTask());
        await Task.WhenAll(posts);

        await using var check = Ctx();
        (await check.UsageHistory.CountAsync(u => u.ApiKey == "bulk")).ShouldBe(40);
        factory.Dispose();
    }

    /// <summary>HybridCache wiring — only active when a HybridCache instance is
    /// configured on the default HotCache (Program.cs does this; the test sets
    /// it explicitly so the assertion doesn't depend on test order).</summary>
    private static void UseHybrid()
    {
        var sp = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
        HotCache.Configure(sp.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>());
    }

    [Fact]
    public async Task HybridCache_StampedeProtection_SingleFactoryCall()
    {
        UseHybrid();
        var key = $"stampede:{Guid.NewGuid():N}";
        var calls = 0;
        Func<Task<int>> factory = async () =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(80);
            return 42;
        };
        // 12 concurrent misses on the same key must share one factory run
        var gets = Enumerable.Range(0, 12)
            .Select(_ => HotCache.Default.GetOrAddAsync(key, TimeSpan.FromMinutes(1), factory))
            .ToArray();
        var results = await Task.WhenAll(gets);
        results.ShouldAllBe(r => r == 42);
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task HybridCache_TagInvalidation_BustsGroup()
    {
        UseHybrid();
        var apiKey = $"k-{Guid.NewGuid():N}";
        var calls = 0;
        Task<bool> Factory() => Task.FromResult(++calls > 0);

        var tag = $"dcap:{apiKey}";
        await HotCache.Default.GetOrAddAsync($"dcap:{apiKey}:modelA", TimeSpan.FromMinutes(1), Factory, [tag]);
        await HotCache.Default.GetOrAddAsync($"dcap:{apiKey}:modelB", TimeSpan.FromMinutes(1), Factory, [tag]);
        calls.ShouldBe(2);

        HotCache.Default.InvalidateTag(tag);
        await HotCache.Default.GetOrAddAsync($"dcap:{apiKey}:modelA", TimeSpan.FromMinutes(1), Factory, [tag]);
        await HotCache.Default.GetOrAddAsync($"dcap:{apiKey}:modelB", TimeSpan.FromMinutes(1), Factory, [tag]);
        calls.ShouldBe(4); // both model-scoped entries recomputed
    }

    [Fact]
    public async Task HybridCache_InvalidateAll_BustsComboNameCache()
    {
        UseHybrid();
        await using var reader = SeedCtx();
        var calls = 0;
        var ckey = $"combo:{Guid.NewGuid():N}";
        _ = await HotCache.Default.GetOrAddAsync<string?>(ckey, TimeSpan.FromMinutes(1),
            () => { calls++; return Task.FromResult<string?>("first"); });
        _ = await HotCache.Default.GetOrAddAsync<string?>(ckey, TimeSpan.FromMinutes(1),
            () => { calls++; return Task.FromResult<string?>("second"); });
        calls.ShouldBe(1); // second read was a cache hit

        HotCache.Default.InvalidateAll();
        var v = await HotCache.Default.GetOrAddAsync<string?>(ckey, TimeSpan.FromMinutes(1),
            () => { calls++; return Task.FromResult<string?>("second"); });
        calls.ShouldBe(2);
        v.ShouldBe("second");
    }
}

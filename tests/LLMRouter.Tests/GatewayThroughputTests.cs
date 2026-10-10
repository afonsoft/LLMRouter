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
        // first read = miss, second = hit
        var m0 = HotCache.Default.Misses;
        (await HotReads.ApiKeyAsync(reader, "k1")).ShouldBeNull();
        (await HotReads.ApiKeyAsync(reader, "k1")).ShouldBeNull();
        HotCache.Default.Hits.ShouldBeGreaterThan(0);

        // write through another context → cache busted → next read sees the row
        await using (var writer = Ctx())
        {
            writer.ApiKeys.Add(new ApiKey { Key = "k1", Name = "n", IsActive = true });
            await writer.SaveChangesAsync();
        }
        var found = await HotReads.ApiKeyAsync(reader, "k1");
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
        var k = await HotReads.ApiKeyAsync(reader, "k2");
        k.ShouldNotBeNull();
        var hits = HotCache.Default.Hits;

        await using (var w = Ctx())
        {
            w.UsageHistory.Add(new UsageRecord
            {
                Timestamp = "2026-01-01 00:00:00", Provider = "p", Model = "m",
                ApiKey = "k2", Endpoint = "openai", Tokens = "1", Status = "200",
            });
            await w.SaveChangesAsync();
        }
        (await HotReads.ApiKeyAsync(reader, "k2")).ShouldNotBeNull();
        HotCache.Default.Hits.ShouldBe(hits + 1); // served from cache, not re-queried
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
}

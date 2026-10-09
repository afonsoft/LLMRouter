using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Resilience;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-020: combo selection strategies — ordering with stub usage data.</summary>
[Collection("StaticState")]
public class ComboStrategiesTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly ProviderRegistry _registry = new();

    public void Dispose()
    {
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db()
    {
        var ctx = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        ctx.EnsureCreated();
        return ctx;
    }

    private void SeedUsage(string provider, string model, int requests, double cost, long latencyMs)
    {
        using var db = Db();
        for (var i = 0; i < requests; i++)
            db.UsageHistory.Add(new UsageRecord
            {
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                Provider = provider,
                Model = model,
                Cost = cost,
                LatencyMs = latencyMs,
            });
        db.SaveChanges();
    }

    private async Task<List<string>> Order(string kind, List<string> models)
    {
        using var db = Db();
        return await ComboStrategies.OrderAsync(kind, "test-combo", models, db, _registry);
    }

    [Fact]
    public async Task Least_used_orders_by_request_count()
    {
        SeedUsage("openai", "gpt-a", 10, 0, 0);
        SeedUsage("openai", "gpt-b", 2, 0, 0);
        var models = new List<string> { "openai/gpt-a", "openai/gpt-b", "openai/gpt-c" };
        var ordered = await Order("least-used", models);
        // c has zero usage → first; then b (2), then a (10)
        ordered.ShouldBe(["openai/gpt-c", "openai/gpt-b", "openai/gpt-a"]);
    }

    [Fact]
    public async Task Cost_optimized_orders_by_avg_cost()
    {
        SeedUsage("openai", "gpt-expensive", 3, 0.10, 0);
        SeedUsage("openai", "gpt-cheap", 3, 0.001, 0);
        var ordered = await Order("cost-optimized",
            new List<string> { "openai/gpt-expensive", "openai/gpt-cheap", "openai/gpt-unused" });
        ordered[0].ShouldBe("openai/gpt-unused"); // no data ≈ free
        ordered[1].ShouldBe("openai/gpt-cheap");
        ordered[2].ShouldBe("openai/gpt-expensive");
    }

    [Fact]
    public async Task Weighted_prefers_highest_weight_as_head()
    {
        var models = new List<string> { "openai/a~1", "openai/b~100", "openai/c~1" };
        var heavy = 0;
        for (var i = 0; i < 30; i++)
        {
            var ordered = await Order("weighted", models);
            ordered.Count.ShouldBe(3);
            if (ordered[0] == "openai/b") heavy++;
        }
        heavy.ShouldBeGreaterThan(20); // ~98% expected; >20/30 is safe
    }

    [Fact]
    public async Task Strict_random_returns_single_pick()
    {
        var ordered = await Order("strict-random",
            new List<string> { "openai/a", "openai/b", "openai/c" });
        ordered.Count.ShouldBe(1);
        new[] { "openai/a", "openai/b", "openai/c" }.ShouldContain(ordered[0]);
    }

    [Fact]
    public async Task Lkgp_puts_last_good_first()
    {
        var models = new List<string> { "openai/a", "openai/b", "openai/c" };
        (await Order("lkgp", models))[0].ShouldBe("openai/a"); // nothing recorded → list order
        ComboStrategies.RecordSuccess("test-combo", "openai/c");
        (await Order("lkgp", models))[0].ShouldBe("openai/c");
    }

    [Fact]
    public async Task Cache_optimized_uses_prompt_affinity()
    {
        var models = new List<string> { "openai/a", "openai/b" };
        var body = JsonDocument.Parse("""{"messages":[{"role":"user","content":"my prefix prompt"}]}""").RootElement;
        var hash = ComboStrategies.PromptHash(body);
        using var db = Db();
        ComboStrategies.RecordCacheHit("test-combo", hash, "openai/b");
        var ordered = await ComboStrategies.OrderAsync("cache-optimized", "test-combo",
            models, db, _registry, body);
        ordered[0].ShouldBe("openai/b");
    }

    [Fact]
    public async Task Reset_aware_deprioritizes_cooling_connections()
    {
        var coolId = $"conn-cool-{Guid.NewGuid():N}";
        using (var db = Db())
        {
            db.ProviderConnections.Add(new ProviderConnection
            {
                Id = coolId, Provider = "openai", AuthType = "apikey", IsActive = true,
            });
            db.ProviderConnections.Add(new ProviderConnection
            {
                Id = "conn-fine-" + coolId, Provider = "anthropic", AuthType = "apikey", IsActive = true,
            });
            db.SaveChanges();
        }
        // shared statics (threshold etc.) may be tuned by other tests — report
        // until the connection cools (bounded) instead of assuming a threshold
        CooldownTracker.Clear(coolId);
        for (var i = 0; i < 200 && !CooldownTracker.IsCooling(coolId); i++)
            CooldownTracker.ReportFailure(coolId);
        try
        {
            var ordered = await Order("reset-aware",
                new List<string> { "openai/a", "anthropic/b" });
            Assert.True(ordered[^1] == "openai/a",
                $"ordered=[{string.Join(",", ordered)}] rem={CooldownTracker.Remaining(coolId)}");
        }
        finally { CooldownTracker.Clear(coolId); }
    }

    [Fact]
    public async Task Headroom_orders_by_remaining_quota()
    {
        using (var db = Db())
        {
            db.ProviderConnections.Add(new ProviderConnection
            {
                Id = "conn-tight", Provider = "openai", AuthType = "apikey", IsActive = true,
                Data = """{"quotaDaily":100}""",
            });
            db.ProviderConnections.Add(new ProviderConnection
            {
                Id = "conn-roomy", Provider = "anthropic", AuthType = "apikey", IsActive = true,
                Data = """{"quotaDaily":10000}""",
            });
            db.SaveChanges();
            db.UsageHistory.Add(new UsageRecord
            {
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                Provider = "openai", Model = "a", ConnectionId = "conn-tight",
                PromptTokens = 90,
            });
            db.UsageHistory.Add(new UsageRecord
            {
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                Provider = "anthropic", Model = "b", ConnectionId = "conn-roomy",
                PromptTokens = 10,
            });
            db.SaveChanges();
        }
        var ordered = await Order("headroom", new List<string> { "openai/a", "anthropic/b" });
        ordered[0].ShouldBe("anthropic/b"); // 9990 remaining > 10 remaining
    }

    [Fact]
    public async Task Context_optimized_prefers_smallest_adequate_context()
    {
        var models = new List<string>
        {
            "anthropic/claude-fable-5",     // 1048576
            "anthropic/claude-fable-5-1",   // 1000000
            "openai/gpt-6-astra",           // 1050000
        };
        var body = JsonDocument.Parse("""{"messages":[{"role":"user","content":"hi"}]}""").RootElement;
        using var db = Db();
        var ordered = await ComboStrategies.OrderAsync("context-optimized", "c", models,
            db, _registry, body);
        ordered[0].ShouldBe("anthropic/claude-fable-5-1"); // smallest context ≥ estimate
        ordered[^1].ShouldBe("openai/gpt-6-astra");
    }

    [Fact]
    public async Task Auto_and_p2c_keep_all_candidates()
    {
        SeedUsage("openai", "a", 5, 0.01, 100);
        SeedUsage("openai", "b", 1, 0.05, 50);
        var models = new List<string> { "openai/a", "openai/b", "openai/c" };
        var auto = await Order("auto", models);
        auto.OrderBy(x => x).ShouldBe(models.OrderBy(x => x).ToList());
        var p2c = await Order("p2c", models);
        p2c.OrderBy(x => x).ShouldBe(models.OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task Fusion_and_pipeline_pass_through_as_list()
    {
        var models = new List<string> { "openai/a", "openai/b" };
        (await Order("fusion", models)).ShouldBe(models);
        (await Order("pipeline", models)).ShouldBe(models);
        ComboStrategies.IsExecutionStrategy("fusion").ShouldBeTrue();
        ComboStrategies.IsExecutionStrategy("fallback").ShouldBeFalse();
    }
}

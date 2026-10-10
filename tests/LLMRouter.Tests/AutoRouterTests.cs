using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-078: auto-router strategies — rules/score/cost/latency/sla/nadir plumbing.</summary>
[Collection("StaticState")]
public class AutoRouterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");

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

    private static AutoRouter.Config Cfg() => new(
        ExplorationRate: 0, SlaTargetP95Ms: 100, SlaMaxErrorRate: 0.1,
        SlaMaxCostPer1MTokens: 0, SlaHardConstraints: false, LkgpEnabled: true,
        NadirApiKey: null, NadirBaseUrl: null, NadirTimeoutMs: 100);

    private static AutoRouter.Candidate Cand(string model, double cost = 0, double lat = 0,
        double err = 0, string cb = "CLOSED") =>
        new("p", model, cb, 1, cost, lat, lat, 0, err);

    [Fact]
    public async Task Cost_orders_cheapest_first()
    {
        using var db = Db();
        var ordered = await AutoRouter.OrderAsync("cost",
            [Cand("p/exp", cost: 9), Cand("p/cheap", cost: 1), Cand("p/mid", cost: 5)],
            Cfg(), null, db, default);
        ordered[0].ShouldBe("p/cheap");
        ordered[^1].ShouldBe("p/exp");
    }

    [Fact]
    public async Task Rules_prefers_healthy_and_skips_open_when_others_exist()
    {
        using var db = Db();
        var ordered = await AutoRouter.OrderAsync("rules",
            [Cand("p/open", cb: "OPEN"), Cand("p/closed")], Cfg(), null, db, default);
        ordered[0].ShouldBe("p/closed");
    }

    [Fact]
    public async Task Sla_hard_constraints_sorts_by_violation_first()
    {
        using var db = Db();
        var cfg = Cfg() with { SlaHardConstraints = true };
        var ordered = await AutoRouter.OrderAsync("sla",
            [Cand("p/violator", lat: 999, err: 0.9), Cand("p/good", lat: 50, err: 0)],
            cfg, null, db, default);
        ordered[0].ShouldBe("p/good");
    }

    [Fact]
    public async Task Lkgp_prefers_last_successful_provider_then_rules()
    {
        using var db = Db();
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            Provider = "anthropic", Model = "b", Status = "success",
        });
        await db.SaveChangesAsync();
        var ordered = await AutoRouter.OrderAsync("lkgp",
            [Cand("openai/a"), Cand("anthropic/b") with { Provider = "anthropic" }],
            Cfg(), null, db, default);
        ordered[0].ShouldBe("anthropic/b");
    }

    [Fact]
    public void Extract_last_user_text_variants()
    {
        AutoRouter.ExtractLastUserText(null).ShouldBeNull();
        var body = JsonDocument.Parse("""{"messages":[{"role":"user","content":"hello"}]}""").RootElement;
        AutoRouter.ExtractLastUserText(body).ShouldBe("hello");
        var parts = JsonDocument.Parse("""{"messages":[{"role":"user","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}""").RootElement;
        AutoRouter.ExtractLastUserText(parts).ShouldBe("a\nb");
        var sys = JsonDocument.Parse("""{"messages":[{"role":"system","content":"s"}]}""").RootElement;
        AutoRouter.ExtractLastUserText(sys).ShouldBeNull();
    }

    [Fact]
    public void Nadir_base_url_normalization()
    {
        AutoRouter.NormalizeNadirBaseUrl("https://api.getnadir.com/v1").ShouldBe("https://api.getnadir.com");
        AutoRouter.NormalizeNadirBaseUrl("https://x.test/").ShouldBe("https://x.test");
        AutoRouter.NormalizeNadirBaseUrl("ftp://x").ShouldBeNull();
        AutoRouter.NormalizeNadirBaseUrl(null).ShouldBeNull();
    }

    [Fact]
    public async Task Combo_strategies_routes_auto_kinds()
    {
        using var db = Db();
        var ordered = await ComboStrategies.OrderAsync("cost", "c",
            new List<string> { "p/exp", "p/cheap" }, db, new ProviderRegistry());
        ordered.Count.ShouldBe(2); // no telemetry → stable, both kept
        AutoRouter.IsAutoStrategy("sla-aware").ShouldBeTrue();
        AutoRouter.IsAutoStrategy("round-robin").ShouldBeFalse();
    }
}

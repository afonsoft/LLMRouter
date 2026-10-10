using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-084: 16-weight scorer + modePacks + taskFitness + adaptiveRouting + arena sync.</summary>
public class IntelligentScoringTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private LlmRouterDbContext Db() => new(
        new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
    public void Dispose()
    {
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public void DefaultWeightsSumToOneAndScoreMatchesUpstream()
    {
        var w = new IntelligentScoring.Weights();
        w.IsValid().ShouldBeTrue(); // sum = 1.0 ±0.01

        // Hand-computed: all-neutral factors → score = sum of weights over
        // factors with value 1 (quota, health, cost? no — use explicit values).
        var f = new IntelligentScoring.Factors(
            Quota: 1, Health: 1, CostInv: 0, LatencyInv: 0, TaskFit: 0.5,
            Stability: 0, TierPriority: 0.33, TierAffinity: 0.5, SpecificityMatch: 0.5,
            ContextAffinity: 0.5, CacheAffinity: 0, SessionAvailability: 1,
            ResetWindowAffinity: 0.5, ConnectionDensity: 0, Quality: 0.5, Reliability: 1);
        var expected =
            0.1429 * 1 + 0.1605 * 1 + 0.1429 * 0 + 0.1143 * 0 + 0.0762 * 0.5
            + 0.0476 * 0 + 0.0476 * 0.33 + 0.0476 * 0.5 + 0.0476 * 0.5
            + 0.0476 * 0.5 + 0 * 0 + 0.0476 * 1 + 0 * 0.5 + 0.0476 * 0
            + 0.03 * 0.5 + 0 * 1;
        IntelligentScoring.CalculateScore(f, w).ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void NormalizeWeightsRenormalizesAndFallsBackOnZero()
    {
        var n = IntelligentScoring.NormalizeWeights(new Dictionary<string, double>
        { ["health"] = 2, ["quota"] = 2, ["costInv"] = -5 });
        n.Health.ShouldBe(0.5); n.Quota.ShouldBe(0.5); n.CostInv.ShouldBe(0);
        n.IsValid().ShouldBeTrue();

        IntelligentScoring.NormalizeWeights(new Dictionary<string, double> { ["quota"] = 0 })
            .Sum().ShouldBe(new IntelligentScoring.Weights().Sum(), 1e-9); // all-zero → defaults
    }

    [Fact]
    public void ModePackAndScorePoolOrder()
    {
        var w = IntelligentScoring.ResolveModePack("cost-saver");
        w.CostInv.ShouldBe(0.3324);

        var cheap = new IntelligentScoring.Candidate("p1", "p1/cheap", 50, "CLOSED", 0.1, 100, 10, 0.01);
        var pricey = new IntelligentScoring.Candidate("p2", "p2/pricey", 50, "CLOSED", 9.9, 100, 10, 0.01);
        var ranked = IntelligentScoring.ScorePool([pricey, cheap], w, _ => 0.5);
        ranked[0].Model.ShouldBe("p1/cheap");
    }

    [Fact]
    public async Task TaskFitnessChainPrefersOverrideThenEloThenStaticThenWildcard()
    {
        using var db = Db();
        db.Database.EnsureCreated();
        // wildcard: "coder"+"code" both match → 0.5+0.15+0.1=0.75
        (await ModelIntelligence.GetTaskFitnessAsync(db, "my-coder-1b", "coding")).ShouldBe(0.75);
        // static table: gpt-4o + coding → 0.9 (segment-boundary match)
        (await ModelIntelligence.GetTaskFitnessAsync(db, "openai/gpt-4o-2024", "coding")).ShouldBe(0.9);
        // arena_elo row beats static table
        await ModelIntelligence.SetAsync(db, "arena_elo", "gpt-4o", "coding", 0.77);
        (await ModelIntelligence.GetTaskFitnessAsync(db, "gpt-4o", "coding")).ShouldBe(0.77);
        // user override beats arena
        await ModelIntelligence.SetAsync(db, "user_override", "gpt-4o", "coding", 0.55);
        (await ModelIntelligence.GetTaskFitnessAsync(db, "gpt-4o", "coding")).ShouldBe(0.55);
    }

    [Fact]
    public async Task BudgetCapDropsExpensiveCandidates()
    {
        using var db = Db();
        db.Database.EnsureCreated();
        var cheap = new AutoRouter.Candidate("p", "p/cheap", "CLOSED", 100, 0.1, 0, 0, 0, 0.01);
        var pricey = new AutoRouter.Candidate("p", "p/pricey", "CLOSED", 100, 99, 0, 0, 0, 0.01);
        var cfg = new AutoRouter.Config(0, 2000, 0.05, 0, false, true, null, null, 2000,
            ModePack: "cost-saver", BudgetCap: 1.0);
        var order = await AutoRouter.OrderAsync("rules", [pricey, cheap], cfg, null, db, default);
        order[0].ShouldBe("p/cheap");
    }

    [Fact]
    public void AdaptiveRoutingMarksIneligibleAndRanks()
    {
        var dead = new AdaptiveRouting.RoutingCandidate("p", "p/dead", 1,
            AdaptiveRouting.Allocation.Deny, 1, AdaptiveRouting.Circuit.Open,
            AdaptiveRouting.QuotaStatus.Exhausted);
        var ok = new AdaptiveRouting.RoutingCandidate("p", "p/ok", 0.9,
            AdaptiveRouting.Allocation.Allow, 0.9, AdaptiveRouting.Circuit.Closed,
            AdaptiveRouting.QuotaStatus.Healthy, LatencyMs: 100, ErrorRate: 0.01);
        var ranked = AdaptiveRouting.RankCandidates([dead, ok]);
        ranked.Selected!.ModelId.ShouldBe("p/ok");
        ranked.Candidates[0].Score.ShouldBeGreaterThan(ranked.Candidates[1].Score);
        ranked.Candidates.Single(c => c.ModelId == "p/dead").Eligible.ShouldBeFalse();
        AdaptiveRouting.ShouldFailover("rate_limit", true).ShouldBeTrue();
        AdaptiveRouting.ShouldFailover("network_error", true).ShouldBeTrue();
        AdaptiveRouting.ShouldFailover("timeout", false).ShouldBeFalse();
    }

    [Fact]
    public void ArenaEloTransformsMatchUpstream()
    {
        ArenaEloSync.NormalizeModelName("anthropic/claude-opus-4-6-thinking").ShouldBe("claude-opus-4-6");
        ArenaEloSync.NormalizeModelName("openai/gpt-4o").ShouldBe("gpt-4o");
        ArenaEloSync.ComputeConfidence(6000).ShouldBe("high");
        ArenaEloSync.ComputeConfidence(1500).ShouldBe("medium");
        ArenaEloSync.ComputeConfidence(10).ShouldBe("low");
        // ELO normalization into [0.4, 0.98]
        ArenaEloSync.EloToTaskFit(1500, 1500, 1500).ShouldBe(0.4);          // min==max → floor
        ArenaEloSync.EloToTaskFit(1500, 1400, 1600).ShouldBe(0.69, 1e-9);   // mid
        ArenaEloSync.EloToTaskFit(1600, 1400, 1600).ShouldBe(0.98, 1e-9);   // top
    }
}

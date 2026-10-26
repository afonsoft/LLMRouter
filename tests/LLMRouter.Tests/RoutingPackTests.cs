using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

public sealed class RoutingPackTests
{
    [Fact]
    public void Response_predicates_accept_valid_response_and_latency()
    {
        using var doc = JsonDocument.Parse(
            """{"choices":[{"finish_reason":"stop","message":{"content":"hello"}}]}""");

        var result = ResponsePredicates.Evaluate(doc.RootElement,
            ["finish_reason", "nonempty_content", "max_latency_ms:1000"], 42);

        result.Passed.ShouldBeTrue();
        result.FailedPredicate.ShouldBeNull();
    }

    [Fact]
    public void Response_predicates_reject_empty_content()
    {
        using var doc = JsonDocument.Parse(
            """{"choices":[{"finish_reason":"stop","message":{"content":""}}]}""");

        var result = ResponsePredicates.Evaluate(doc.RootElement, ["nonempty_content"], 1);

        result.Passed.ShouldBeFalse();
        result.FailedPredicate.ShouldBe("nonempty_content");
    }

    [Fact]
    public void Prefix_affinity_key_is_stable_and_uses_first_200_characters()
    {
        using var first = JsonDocument.Parse(
            """{"messages":[{"role":"user","content":"same prefix and a long suffix"}]}""");
        using var second = JsonDocument.Parse(
            """{"messages":[{"role":"user","content":"same prefix and a long suffix"}]}""");

        PrefixAffinity.ComputeHash(first.RootElement).ShouldBe(
            PrefixAffinity.ComputeHash(second.RootElement));
        PrefixAffinity.ComputeKey(first.RootElement).ShouldStartWith("affinity:");
    }

    [Fact]
    public async Task Prefix_affinity_store_expires_entries()
    {
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"routing-{Guid.NewGuid():N}.db")}")
            .Options);
        db.EnsureCreated();

        await PrefixAffinity.PutAsync(db, "hash", "provider/model",
            TimeSpan.FromMinutes(15), CancellationToken.None);
        (await PrefixAffinity.GetAsync(db, "hash", CancellationToken.None))
            .ShouldBe("provider/model");
    }

    [Fact]
    public async Task Hedge_returns_second_attempt_when_first_is_slow()
    {
        var result = await HedgedDispatch.ExecuteAsync(
            async ct =>
            {
                await Task.Delay(250, ct);
                return "first";
            },
            async _ =>
            {
                await Task.Delay(5);
                return "second";
            },
            TimeSpan.FromMilliseconds(20),
            value => value is not null,
            CancellationToken.None);

        result.ShouldBe("second");
    }

    [Fact]
    public void Shadow_divergence_detects_status_or_token_difference()
    {
        ShadowMirror.IsDivergent(200, 10, 200, 11).ShouldBeTrue();
        ShadowMirror.IsDivergent(200, 10, 200, 10).ShouldBeFalse();
        ShadowMirror.IsDivergent(200, 10, 500, 10).ShouldBeTrue();
    }

    [Fact]
    public void Routing_pack_options_parse_combo_json()
    {
        var combo = new Combo
        {
            HedgeJson = """{"delayMs":25}""",
            PredicatesJson = """["finish_reason","max_latency_ms:500"]""",
            MirrorJson = """{"model":"openai/shadow"}""",
        };

        var options = RoutingPackOptions.From(combo);

        options.HedgeDelayMs.ShouldBe(25);
        options.Predicates.ShouldBe(["finish_reason", "max_latency_ms:500"]);
        options.MirrorModel.ShouldBe("openai/shadow");
    }
}

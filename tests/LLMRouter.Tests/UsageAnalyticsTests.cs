using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using LLMRouter.Core.Usage;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-005: usage analytics, timeseries buckets, pricing math, provider stats.</summary>
public class UsageAnalyticsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public UsageAnalyticsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public void ParsePrice_reads_input_output_per_1M()
    {
        var e = JsonDocument.Parse("""{"input":5,"output":15}""").RootElement;
        var p = PricingService.ParsePrice(e);
        p.ShouldNotBeNull();
        p.Value.input.ShouldBe(5);
        p.Value.output.ShouldBe(15);
    }

    [Fact]
    public void ParsePrice_accepts_prompt_completion_aliases()
    {
        var e = JsonDocument.Parse("""{"prompt":1.5,"completion":7.5}""").RootElement;
        PricingService.ParsePrice(e).ShouldBe((1.5, 7.5));
    }

    [Fact]
    public async Task Timeseries_returns_daily_buckets()
    {
        await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/usage/timeseries?range=week");
        r.GetProperty("hourly").GetBoolean().ShouldBeFalse();
        r.TryGetProperty("buckets", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Provider_stats_shape_and_p95()
    {
        await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/provider-stats?range=all");
        r.TryGetProperty("providers", out var provs).ShouldBeTrue();
        foreach (var p in provs.EnumerateArray())
        {
            p.TryGetProperty("p50", out _).ShouldBeTrue();
            p.TryGetProperty("p95", out _).ShouldBeTrue();
            p.TryGetProperty("errorRate", out _).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Pricing_override_round_trip()
    {
        await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        var put = await _client.PutAsJsonAsync("/api/pricing/openai/gpt-4o", new { input = 2.5, output = 10 });
        put.EnsureSuccessStatusCode();
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/pricing");
        r.GetProperty("overrides").TryGetProperty("openai/gpt-4o", out var ov).ShouldBeTrue();
        ov.GetProperty("input").GetDouble().ShouldBe(2.5);
        // clear
        var del = await _client.PutAsync("/api/pricing/openai/gpt-4o",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        del.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Costs_endpoint_and_budget_round_trip()
    {
        await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        var put = await _client.PutAsJsonAsync("/api/costs/budget", new { budget = 42.5 });
        put.EnsureSuccessStatusCode();
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/costs");
        r.GetProperty("budget").GetDouble().ShouldBe(42.5);
        r.TryGetProperty("monthCost", out _).ShouldBeTrue();
        r.TryGetProperty("byModel", out _).ShouldBeTrue();
    }
}

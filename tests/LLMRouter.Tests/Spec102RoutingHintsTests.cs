using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-102: request-level routing hints (OpenRouter-style provider object).</summary>
public class Spec102RoutingHintsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public Spec102RoutingHintsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private async Task LoginAsync() =>
        (await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    private async Task SeedAsync(bool anthropicTrains = false)
    {
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.EnsureCreated();
        db.Combos.Add(new Combo
        {
            Id = "c1", Name = "mix", CreatedAt = "x", UpdatedAt = "x",
            Models = """["openai/m1","anthropic/m2"]""",
        });
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "oc1", Provider = "openai", Name = "o", IsActive = true,
            Data = """{"apiKey":"x"}""", CreatedAt = "x", UpdatedAt = "x",
        });
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "ac1", Provider = "anthropic", Name = "a", IsActive = true,
            Data = anthropicTrains ? """{"apiKey":"x","trainsOnData":true}""" : """{"apiKey":"x"}""",
            CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<JsonElement>> SimulateAsync(object body)
    {
        var r = await _client.PostAsJsonAsync("/api/playground/simulate-route",
            new { model = "mix", body });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var el = await r.Content.ReadFromJsonAsync<JsonElement>();
        return el.GetProperty("targets").EnumerateArray().ToList();
    }

    private static object ChatWith(object? provider = null) => new
    {
        model = "mix",
        messages = new[] { new { role = "user", content = "hi" } },
        provider,
    };

    [Fact]
    public async Task Order_forces_provider_sequence_and_restricts_to_listed()
    {
        await LoginAsync();
        await SeedAsync();
        var targets = await SimulateAsync(ChatWith(new { order = new[] { "anthropic", "openai" } }));
        targets.Count.ShouldBe(2);
        targets[0].GetProperty("provider").GetString().ShouldBe("anthropic");
        targets[1].GetProperty("provider").GetString().ShouldBe("openai");

        var restricted = await SimulateAsync(ChatWith(new { order = new[] { "anthropic" } }));
        restricted.ShouldAllBe(t => t.GetProperty("provider").GetString() == "anthropic");
    }

    [Fact]
    public async Task Order_unknown_slug_returns_400_invalid_provider()
    {
        await LoginAsync();
        await SeedAsync();
        var r = await _client.PostAsJsonAsync("/api/playground/simulate-route",
            new { model = "mix", body = ChatWith(new { order = new[] { "bogus-llm" } }) });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).ShouldContain("invalid_provider");
    }

    [Fact]
    public async Task Allow_fallbacks_false_yields_single_target()
    {
        await LoginAsync();
        await SeedAsync();
        var targets = await SimulateAsync(ChatWith(new { allow_fallbacks = false }));
        targets.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Max_price_excludes_over_cap_candidate()
    {
        await LoginAsync();
        await SeedAsync();
        var raw = await _client.PutAsJsonAsync("/api/settings",
            JsonDocument.Parse("""{"catalogUserPricing":{"openai/m1":{"input":999.0,"output":999.0}}}""").RootElement);
        raw.StatusCode.ShouldBe(HttpStatusCode.OK);

        var targets = await SimulateAsync(ChatWith(new { max_price = new { prompt = 0.5 } }));
        targets.ShouldAllBe(t => t.GetProperty("provider").GetString() == "anthropic");
    }

    [Fact]
    public async Task Sort_price_picks_cheapest_first()
    {
        await LoginAsync();
        await SeedAsync();
        var raw = await _client.PutAsJsonAsync("/api/settings",
            JsonDocument.Parse("""{"catalogUserPricing":{"openai/m1":{"input":9.0,"output":9.0},"anthropic/m2":{"input":1.0,"output":1.0}}}""").RootElement);
        raw.StatusCode.ShouldBe(HttpStatusCode.OK);

        var targets = await SimulateAsync(ChatWith(new { sort = "price" }));
        targets[0].GetProperty("provider").GetString().ShouldBe("anthropic");
    }

    [Fact]
    public async Task Data_collection_deny_skips_connection_flagged_trainsOnData()
    {
        await LoginAsync();
        await SeedAsync(anthropicTrains: true);
        var targets = await SimulateAsync(ChatWith(new { data_collection = "deny" }));
        targets.ShouldAllBe(t => t.GetProperty("provider").GetString() == "openai");
    }

    [Fact]
    public async Task Require_parameters_filters_to_fully_capable_models()
    {
        await LoginAsync();
        await SeedAsync();
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.CapabilityOverrides.Add(new CapabilityOverride
        {
            Id = "ov1", Provider = "anthropic", Model = "m2",
            Capabilities = "{\"vision\":true}", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();

        var vision = JsonDocument.Parse("""
            {"model":"mix","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AA"}}]}],
             "provider":{"require_parameters":true}}
            """).RootElement;
        var r = await _client.PostAsJsonAsync("/api/playground/simulate-route",
            new { model = "mix", body = vision });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var targets = (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("targets").EnumerateArray().ToList();
        targets.ShouldAllBe(t => t.GetProperty("provider").GetString() == "anthropic");
    }
}

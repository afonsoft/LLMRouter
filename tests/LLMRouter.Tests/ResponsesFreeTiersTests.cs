using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Translation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-029: free-tiers ranked by real health + /v1/responses output mapping.</summary>
public class ResponsesFreeTiersTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ResponsesFreeTiersTests()
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

    private async Task LoginAsync()
        => (await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    private LlmRouterDbContext Db()
    {
        var o = new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options;
        return new LlmRouterDbContext(o);
    }

    [Fact]
    public async Task Free_tiers_rank_by_health_score()
    {
        await LoginAsync();
        var now = DateTime.UtcNow.ToString("O");
        using (var db = Db())
        {
            db.Database.EnsureCreated();
            db.ProviderConnections.AddRange(
                new ProviderConnection { Id = "ft-ok", Provider = "ollama", AuthType = "apikey", IsActive = true },
                new ProviderConnection { Id = "ft-bad", Provider = "ollama", AuthType = "apikey", IsActive = true },
                new ProviderConnection { Id = "ft-flag", Provider = "openai", AuthType = "apikey", IsActive = true, Data = """{"free":true}""" });
            // ft-bad has all-error history → lowest score
            for (var i = 0; i < 4; i++)
                db.UsageHistory.Add(new UsageRecord
                {
                    Timestamp = now, Provider = "ollama", Model = "m", ConnectionId = "ft-bad",
                    LatencyMs = 5000, Status = "error",
                });
            db.UsageHistory.Add(new UsageRecord
            {
                Timestamp = now, Provider = "ollama", Model = "m", ConnectionId = "ft-ok",
                LatencyMs = 100, Status = "ok",
            });
            db.SaveChanges();
        }

        var resp = await _client.GetFromJsonAsync<JsonElement>("/api/free-tiers");
        var providers = resp.GetProperty("providers").EnumerateArray().ToList();
        providers.Count.ShouldBe(3);
        providers[^1].GetProperty("id").GetString().ShouldBe("ft-bad"); // all-error history ranks last
        var scores = providers.Select(p => p.GetProperty("score").GetDouble()).ToList();
        scores.ShouldBe(scores.OrderByDescending(s => s).ToList()); // sorted desc
        providers[0].GetProperty("score").GetDouble()
            .ShouldBeGreaterThan(providers[^1].GetProperty("score").GetDouble());
        providers.All(p => p.TryGetProperty("breaker", out _)).ShouldBeTrue();
        providers.All(p => p.TryGetProperty("errorRate", out _)).ShouldBeTrue();
        resp.GetProperty("total").GetInt32().ShouldBe(3);
        resp.GetProperty("healthy").GetInt32().ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Responses_api_upstream_maps_to_output_text()
    {
        // openai upstream → responsesApi inbound produces output[] + output_text + usage tokens
        var upstream = JsonDocument.Parse(
            """{"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""").RootElement;
        var result = Translators.TranslateResponse(upstream, "openai", "responsesApi", "m").AsObject();
        var output = result["output"]!.AsArray();
        output.ShouldNotBeEmpty();
        output[0]!["type"]!.GetValue<string>().ShouldBe("message");
        var content = output[0]!["content"]!.AsArray();
        content[0]!["type"]!.GetValue<string>().ShouldBe("output_text");
        content[0]!["text"]!.GetValue<string>().ShouldBe("Hello");
        result["usage"]!["input_tokens"]!.GetValue<int>().ShouldBe(3);
        result["usage"]!["output_tokens"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void Responses_passthrough_keeps_output_shape()
    {
        var upstream = JsonDocument.Parse(
            """{"id":"resp_1","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hi"}]}],"usage":{"input_tokens":3,"output_tokens":2}}""").RootElement;
        var result = Translators.TranslateResponse(upstream, "responsesApi", "responsesApi", "m").AsObject();
        result["output"]!.AsArray()[0]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>().ShouldBe("Hi");
    }
}

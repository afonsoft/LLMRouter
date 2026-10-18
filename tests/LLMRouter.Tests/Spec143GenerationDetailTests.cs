using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-143: GET /api/v1/generation(+content) — meta, waterfall, ioLogging gate.</summary>
public class Spec143GenerationDetailTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public Spec143GenerationDetailTests()
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

    private async Task<long> SeedAsync(string corr = "abcd1234ef567890")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var row = new UsageRecord
        {
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            Provider = "anthropic", Model = "claude-sonnet-4", Endpoint = "chatCompletions",
            PromptTokens = 10, CompletionTokens = 5, Cost = 0.001, LatencyMs = 100,
            Status = "200", ApiKey = "k", Meta = $$"""{"correlationId":"{{corr}}"}""",
        };
        db.UsageHistory.Add(row);
        db.ProxyLogs.Add(new ProxyLog
        {
            Id = "pl1", Timestamp = row.Timestamp, Status = "200", Provider = "anthropic",
            CorrelationId = corr, AttemptNumber = 1, LatencyMs = 100, UpstreamStatus = 200,
            Level = "direct", TargetUrl = "https://api.anthropic.com/v1/messages",
        });
        db.RequestDetails.Add(new RequestDetail
        {
            Id = "rd1", Timestamp = row.Timestamp, Provider = "anthropic",
            Model = "claude-sonnet-4", Status = "200",
            Data = $$"""{"correlationId":"{{corr}}"}""",
            Prompt = "{\"model\":\"x\",\"messages\":[]}", Response = "{\"choices\":[]}",
        });
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task SetIoLoggingAsync(bool on)
    {
        var s = await _client.GetFromJsonAsync<JsonElement>("/api/settings");
        var dict = s.TryGetProperty("observability", out var o) && o.ValueKind == JsonValueKind.Object
            ? o.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : new Dictionary<string, JsonElement>();
        dict["ioLogging"] = JsonSerializer.SerializeToElement(on);
        (await _client.PutAsJsonAsync("/api/settings", new { observability = dict }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Generation_returns_meta_and_waterfall()
    {
        await LoginAsync();
        var id = await SeedAsync();
        var r = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/generation?id={id}");
        r.GetProperty("id").GetInt64().ShouldBe(id);
        r.GetProperty("correlationId").GetString().ShouldBe("abcd1234ef567890");
        r.GetProperty("promptTokens").GetInt32().ShouldBe(10);
        var wf = r.GetProperty("waterfall").EnumerateArray().ToList();
        wf.Count.ShouldBe(1);
        wf[0].GetProperty("upstreamStatus").GetInt32().ShouldBe(200);
        wf[0].GetProperty("attemptNumber").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Generation_404_unknown_and_401_unauth()
    {
        await LoginAsync();
        (await _client.GetAsync("/api/v1/generation?id=99999")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/generation?id=1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Content_403_when_ioLogging_off_200_with_payloads_when_on()
    {
        await LoginAsync();
        var id = await SeedAsync();
        (await _client.GetAsync($"/api/v1/generation/content?id={id}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await SetIoLoggingAsync(true);
        var r = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/generation/content?id={id}");
        r.GetProperty("prompt").GetString().ShouldContain("messages");
        r.GetProperty("response").GetString().ShouldContain("choices");
    }
}

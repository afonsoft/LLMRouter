using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-039: rateLimits CRUD + gateway sliding-window enforcement.</summary>
public class RateLimitTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RateLimitTests()
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

    private async Task<LlmRouterDbContext> NewDb()
    {
        var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.EnsureCreated();
        return await Task.FromResult(db);
    }

    private async Task LoginAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private HttpRequestMessage Chat(string model, string key) =>
        new(HttpMethod.Post, "/v1/chat/completions")
        {
            Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key) },
            Content = JsonContent.Create(new { model, messages = new[] { new { role = "user", content = "hi" } } }),
        };

    [Fact]
    public async Task Crud_round_trip()
    {
        await LoginAsync();
        var created = await _client.PostAsJsonAsync("/api/rate-limits",
            new { scope = "apiKey", scopeValue = "k-test", rpm = 10, tpm = 1000, burst = 2 });
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rule = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = rule.GetProperty("id").GetString()!;

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/rate-limits");
        list.GetProperty("rules").EnumerateArray().ShouldContain(r => r.GetProperty("id").GetString() == id);

        var upd = await _client.PutAsJsonAsync($"/api/rate-limits/{id}", new { rpm = 5, enabled = false });
        upd.StatusCode.ShouldBe(HttpStatusCode.OK);
        var r2 = await upd.Content.ReadFromJsonAsync<JsonElement>();
        r2.GetProperty("rpm").GetInt32().ShouldBe(5);
        r2.GetProperty("enabled").GetBoolean().ShouldBeFalse();

        (await _client.DeleteAsync($"/api/rate-limits/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var list2 = await _client.GetFromJsonAsync<JsonElement>("/api/rate-limits");
        list2.GetProperty("rules").EnumerateArray().ShouldAllBe(r => r.GetProperty("id").GetString() != id);
    }

    [Fact]
    public async Task Two_rpm_limit_429s_third_request()
    {
        var key = "sk-rl-test-key";
        await using (var db = await NewDb())
        {
            db.ApiKeys.Add(new ApiKey { Id = "k1", Key = key, Name = "rl", IsActive = true });
            db.RateLimits.Add(new RateLimit
            {
                Id = "rl1", Scope = "apiKey", ScopeValue = key, Rpm = 2,
                CreatedAt = "2026-01-01 00:00:00", UpdatedAt = "2026-01-01 00:00:00",
            });
            await db.SaveChangesAsync();
        }

        // requests 1-2 reach the pipeline (model_not_found — no connections); request 3 → 429
        (await _client.SendAsync(Chat("openai/x", key))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.SendAsync(Chat("openai/x", key))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var third = await _client.SendAsync(Chat("openai/x", key));
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Disabled_rule_is_ignored()
    {
        var key = "sk-rl-disabled";
        await using (var db = await NewDb())
        {
            db.ApiKeys.Add(new ApiKey { Id = "k1", Key = key, IsActive = true });
            db.RateLimits.Add(new RateLimit
            {
                Id = "rl1", Scope = "apiKey", ScopeValue = key, Rpm = 1, Enabled = false,
                CreatedAt = "2026-01-01 00:00:00", UpdatedAt = "2026-01-01 00:00:00",
            });
            await db.SaveChangesAsync();
        }

        (await _client.SendAsync(Chat("openai/x", key))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.SendAsync(Chat("openai/x", key))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.SendAsync(Chat("openai/x", key))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Model_scoped_limit_does_not_leak_to_other_models()
    {
        var key = "sk-rl-model";
        await using (var db = await NewDb())
        {
            db.ApiKeys.Add(new ApiKey { Id = "k1", Key = key, IsActive = true });
            db.RateLimits.Add(new RateLimit
            {
                Id = "rl1", Scope = "model", ScopeValue = "openai/limited", Rpm = 1,
                CreatedAt = "2026-01-01 00:00:00", UpdatedAt = "2026-01-01 00:00:00",
            });
            await db.SaveChangesAsync();
        }

        (await _client.SendAsync(Chat("openai/limited", key))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.SendAsync(Chat("openai/limited", key))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        // a different model under the same key is unaffected
        (await _client.SendAsync(Chat("openai/other", key))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-045: prompt cache hit/miss/expiry/purge through the gateway.</summary>
public class PromptCacheTests : IDisposable
{
    private sealed class FakeUpstream : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            var body = """{"choices":[{"message":{"role":"assistant","content":"cached!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeUpstream _upstream = new();

    public PromptCacheTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => _upstream)));
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private HttpClient LoginClient()
    {
        var c = _factory.CreateClient();
        var r = c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result;
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    private async Task SeedAsync(bool cacheEnabled)
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", Name = "main", IsActive = true,
            Data = """{"baseUrl":"http://fake"}""", CreatedAt = "x", UpdatedAt = "x",
        });
        // api key for /v1 + enable promptCache in settings
        db.ApiKeys.Add(new ApiKey { Id = "k1", Key = "sk-rl-cache-test", Name = "t", IsActive = true, CreatedAt = "x" });
        var data = $"{{\"promptCache\":{{\"enabled\":{cacheEnabled.ToString().ToLowerInvariant()},\"ttlMinutes\":60,\"maxEntries\":10}}}}";
        if (await db.Settings.FirstOrDefaultAsync() is { } s) s.Data = data;
        else db.Settings.Add(new SettingRow { Data = data });
        await db.SaveChangesAsync();
    }

    private static async Task<string> Chat(HttpClient c)
    {
        var r = await c.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "openai/gpt-4o",
            messages = new[] { new { role = "user", content = "hi" } },
            stream = false,
        });
        if (r.StatusCode != HttpStatusCode.OK)
            throw new Exception($"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        r.Headers.TryGetValues("x-cache", out var hdr);
        await r.Content.ReadAsStringAsync();
        return hdr?.FirstOrDefault() ?? "";
    }

    private HttpClient GatewayClient()
    {
        // no login cookie — dashboard cookies resolve as "dashboard" auth
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("x-api-key", "sk-rl-cache-test");
        return c;
    }

    [Fact]
    public async Task Second_identical_request_is_a_cache_hit()
    {
        await SeedAsync(cacheEnabled: true);
        var gw = GatewayClient();
        (await Chat(gw)).ShouldBe(""); // miss → upstream called
        _upstream.Calls.ShouldBe(1);
        (await Chat(gw)).ShouldBe("hit"); // hit → no upstream call
        _upstream.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Expired_entry_misses_and_purge_clears()
    {
        await SeedAsync(cacheEnabled: true);
        var gw = GatewayClient();
        await Chat(gw);
        // force expiry
        await using (var db = Db())
        {
            foreach (var e in db.CacheEntries) e.ExpiresAt = "2000-01-01 00:00:00";
            await db.SaveChangesAsync();
        }
        (await Chat(gw)).ShouldBe(""); // expired → miss
        _upstream.Calls.ShouldBe(2);

        var dash = LoginClient();
        var purge = await dash.PostAsJsonAsync("/api/cache/purge", new { });
        (await purge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purged").GetInt32()
            .ShouldBeGreaterThanOrEqualTo(1);
        var entries = await dash.GetFromJsonAsync<JsonElement>("/api/cache/entries");
        entries.GetProperty("total").GetInt32().ShouldBe(0);
    }
}

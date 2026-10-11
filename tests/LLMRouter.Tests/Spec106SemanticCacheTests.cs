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

/// <summary>SPEC-106: semantic prompt cache — opt-in similarity layer behind
/// the exact-match PromptCache. Local n-gram embedder (no embedding provider
/// configured) makes near-identical prompts deterministic hits.</summary>
public class Spec106SemanticCacheTests : IDisposable
{
    private sealed class FakeUpstream : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            var body = """{"choices":[{"message":{"role":"assistant","content":"sem!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeUpstream _upstream = new();

    public Spec106SemanticCacheTests()
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

    private HttpClient Gw()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("x-api-key", "sk-rl-sem-test");
        return c;
    }

    private HttpClient Dash()
    {
        var c = _factory.CreateClient();
        var r = c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result;
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    private async Task SeedAsync(double threshold = 0.85, bool optOut = false, bool enabled = true)
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", Name = "main", IsActive = true,
            Data = optOut ? """{"baseUrl":"http://fake","cacheOptOut":true}""" : """{"baseUrl":"http://fake"}""",
            CreatedAt = "x", UpdatedAt = "x",
        });
        db.ApiKeys.Add(new ApiKey { Id = "k1", Key = "sk-rl-sem-test", Name = "t", IsActive = true, CreatedAt = "x" });
        var data = "{\"promptCache\":{\"enabled\":true,\"ttlMinutes\":60,\"semantic\":{\"enabled\":"
            + enabled.ToString().ToLowerInvariant() + ",\"threshold\":"
            + threshold.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}";
        if (await db.Settings.FirstOrDefaultAsync() is { } s) s.Data = data;
        else db.Settings.Add(new SettingRow { Data = data });
        await db.SaveChangesAsync();
    }

    private static async Task<string> Chat(HttpClient c, string content, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = "openai/gpt-4o",
            ["messages"] = new[] { new { role = "user", content } },
        };
        if (extra is Dictionary<string, object?> d) foreach (var kv in d) body[kv.Key] = kv.Value;
        var r = await c.PostAsJsonAsync("/v1/chat/completions", body);
        if (r.StatusCode != HttpStatusCode.OK)
            throw new Exception($"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        r.Headers.TryGetValues("x-cache", out var hdr);
        await r.Content.ReadAsStringAsync();
        return hdr?.FirstOrDefault() ?? "";
    }

    [Fact]
    public async Task Near_identical_prompt_hits_semantic_layer()
    {
        await SeedAsync(threshold: 0.80);
        var gw = Gw();
        (await Chat(gw, "what is the capital of france")).ShouldBe("");
        _upstream.Calls.ShouldBe(1);
        (await Chat(gw, "what is the capital of france?")).ShouldBe("semantic");
        _upstream.Calls.ShouldBe(1); // served from semantic cache, no upstream
        // hit counter is process-wide — tolerate accumulation across test app instances
        (await Dash().GetFromJsonAsync<JsonElement>("/api/cache/semantic/stats"))
            .GetProperty("hits").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Below_threshold_prompt_goes_upstream()
    {
        await SeedAsync(threshold: 0.80);
        var gw = Gw();
        await Chat(gw, "what is the capital of france");
        // CJK text shares ~no char n-grams with the stored English prompt
        (await Chat(gw, "北京大学在哪里")).ShouldBe("");
        _upstream.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Tools_request_bypasses_both_layers()
    {
        await SeedAsync();
        var gw = Gw();
        await Chat(gw, "ping");
        var tools = new Dictionary<string, object?> { ["tools"] = new[] { new { type = "function" } } };
        (await Chat(gw, "ping", tools)).ShouldBe("");
        _upstream.Calls.ShouldBe(2); // exact+semantic skipped entirely
    }

    [Fact]
    public async Task Threshold_one_degrades_to_exact_only()
    {
        await SeedAsync(threshold: 1.0);
        var gw = Gw();
        await Chat(gw, "what is the capital of france");
        // near-identical: cosine < 1.0 → semantic miss → upstream
        (await Chat(gw, "what is the capital of france?")).ShouldBe("");
        _upstream.Calls.ShouldBe(2);
        // identical: exact layer still serves
        (await Chat(gw, "what is the capital of france")).ShouldBe("hit");
        _upstream.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task CacheOptOut_connection_never_serves_or_stores()
    {
        await SeedAsync(optOut: true);
        var gw = Gw();
        (await Chat(gw, "ping")).ShouldBe("");
        (await Chat(gw, "ping")).ShouldBe(""); // exact layer also skipped
        _upstream.Calls.ShouldBe(2);
        await using var db = Db();
        (await db.SemanticCache.CountAsync()).ShouldBe(0);
        (await db.CacheEntries.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Debug_cosine_direct()
    {
        await SeedAsync(threshold: 0.80);
        await using var db = Db();
        var sdata = JsonDocument.Parse((await db.Settings.FirstAsync()).Data).RootElement;
        var cfg = LLMRouter.Core.Cache.SemanticCache.Read(sdata);
        var hf = _factory.Services.GetRequiredService<IHttpClientFactory>();
        var va = LLMRouter.Core.Memory.VectorMemory.LocalEmbed("what is the capital of france");
        await LLMRouter.Core.Cache.SemanticCache.StoreAsync(db, sdata, "h1", "openai/gpt-4o", va, "{}", cfg);
        var vb = LLMRouter.Core.Memory.VectorMemory.LocalEmbed("北京大学在哪里");
        var hit = await LLMRouter.Core.Cache.SemanticCache.LookupAsync(db, hf, sdata, "openai/gpt-4o", vb, cfg);
        Console.Error.WriteLine($"SIM={hit?.Similarity} RESP={(hit is null ? "null" : "hit")}");
        hit.ShouldBeNull();
    }

    [Fact]
    public async Task Semantic_stats_and_flush_endpoints()
    {
        await SeedAsync();
        var gw = Gw();
        await Chat(gw, "hello world");
        var st = await Dash().GetFromJsonAsync<JsonElement>("/api/cache/semantic/stats");
        st.GetProperty("entries").GetInt32().ShouldBeGreaterThan(0);
        var d = await Dash().SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/cache/semantic"));
        (await d.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("purged").GetInt32().ShouldBeGreaterThan(0);
    }
}

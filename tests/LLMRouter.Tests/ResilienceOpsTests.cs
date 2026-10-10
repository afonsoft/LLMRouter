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

/// <summary>SPEC-070: model cooldowns, fallback chains, routing decisions.</summary>
public class ResilienceOpsTests : IDisposable
{
    private sealed class CountingUpstream : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            var body = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly CountingUpstream _upstream = new();
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public ResilienceOpsTests()
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
        c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    private async Task SeedAsync()
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", AuthType = "apikey", Name = "t",
            Data = """{"baseUrl":"http://fake","apiKey":"sk-fake"}""",
            CreatedAt = "x", UpdatedAt = "x",
        });
        db.Combos.Add(new Combo
        {
            Id = "cb1", Name = "cool-test", Kind = "fallback",
            Models = """["openai/gpt-4o"]""", CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ModelCooldown_crud_and_blocks_dispatch()
    {
        await SeedAsync();
        var c = LoginClient();
        var before = _upstream.Calls;
        var create = await c.PostAsJsonAsync("/api/resilience/model-cooldowns",
            new { provider = "openai", model = "gpt-4o", minutes = 30, reason = "t" });
        create.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cd = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cooldown");
        var cdId = cd.GetProperty("id").GetString()!;

        var list = await c.GetFromJsonAsync<JsonElement>("/api/resilience/model-cooldowns");
        list.GetProperty("cooldowns").EnumerateArray().ShouldContain(x => x.GetProperty("id").GetString() == cdId);

        // cooled model → no targets → gateway fails WITHOUT hitting upstream
        var chat = await c.PostAsJsonAsync("/v1/chat/completions",
            new { model = "cool-test", stream = false, messages = new[] { new { role = "user", content = "hi" } } });
        _upstream.Calls.ShouldBe(before);
        chat.IsSuccessStatusCode.ShouldBeFalse();

        var del = await c.DeleteAsync($"/api/resilience/model-cooldowns/{cdId}");
        del.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FallbackChain_serves_when_combo_exhausted()
    {
        await SeedAsync();
        var c = LoginClient();
        // cool the only combo model, add a chain to a working model
        var create = await c.PostAsJsonAsync("/api/resilience/model-cooldowns",
            new { provider = "openai", model = "gpt-4o", minutes = 30 });
        create.StatusCode.ShouldBe(HttpStatusCode.OK);
        var chain = await c.PostAsJsonAsync("/api/fallback/chains",
            new { name = "emergency", steps = new[] { new { model = "openai/gpt-4o-mini" } }, active = true });
        chain.StatusCode.ShouldBe(HttpStatusCode.OK);
        var chainId = (await chain.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("chain").GetProperty("id").GetString()!;

        var chat = await c.PostAsJsonAsync("/v1/chat/completions",
            new { model = "cool-test", stream = false, messages = new[] { new { role = "user", content = "hi" } } });
        chat.StatusCode.ShouldBe(HttpStatusCode.OK);

        var del = await c.DeleteAsync($"/api/fallback/chains/{chainId}");
        del.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RoutingDecision_returns_trace_for_known_request()
    {
        await SeedAsync();
        var c = LoginClient();
        var chat = await c.PostAsJsonAsync("/v1/chat/completions",
            new { model = "cool-test", stream = false, messages = new[] { new { role = "user", content = "hi" } } });
        chat.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = Db();
        var detail = await db.RequestDetails.OrderByDescending(x => x.Timestamp).FirstAsync();
        var r = await c.GetAsync($"/api/routing/decisions/{detail.Id}");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var d = await r.Content.ReadFromJsonAsync<JsonElement>();
        d.GetProperty("requestId").GetString().ShouldBe(detail.Id);

        (await c.GetAsync("/api/routing/decisions/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

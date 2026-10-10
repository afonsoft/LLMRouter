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

/// <summary>SPEC-072: virtual auto/* combos — pool, duplicate, gateway resolution.</summary>
public class AutoCombosTests : IDisposable
{
    private sealed class StubUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public AutoCombosTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => new StubUpstream())));
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
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Auto_list_resolves_pools_from_connections()
    {
        await SeedAsync();
        var c = LoginClient();
        var r = await c.GetFromJsonAsync<JsonElement>("/api/combos/auto");
        var combos = r.GetProperty("combos").EnumerateArray().ToList();
        combos.Select(x => x.GetProperty("id").GetString()).ShouldContain("auto/best");
        var best = combos.First(x => x.GetProperty("id").GetString() == "auto/best");
        best.GetProperty("count").GetInt32().ShouldBeGreaterThan(0);
        var pool = await c.GetFromJsonAsync<JsonElement>("/api/combos/auto/pool?name=auto/gpt-4o");
        pool.GetProperty("candidates").EnumerateArray()
            .Select(x => x.GetString()).ShouldContain("openai/gpt-4o");
    }

    [Fact]
    public async Task Duplicate_materializes_virtual_combo()
    {
        await SeedAsync();
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/api/combos/duplicate", new { autoId = "auto/best", name = "my-best" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var combo = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("combo");
        combo.GetProperty("name").GetString().ShouldBe("my-best");
        JsonDocument.Parse(combo.GetProperty("models").GetString()!).RootElement.GetArrayLength()
            .ShouldBeGreaterThan(0);
        // conflict on existing name
        (await c.PostAsJsonAsync("/api/combos/duplicate", new { autoId = "auto/best", name = "my-best" }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        // bad autoId → 400 with JSON body
        (await c.PostAsJsonAsync("/api/combos/duplicate", new { autoId = "nope", name = "x" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Gateway_resolves_auto_model_live()
    {
        await SeedAsync();
        var c = LoginClient();
        var chat = await c.PostAsJsonAsync("/v1/chat/completions",
            new { model = "auto/best", stream = false, messages = new[] { new { role = "user", content = "hi" } } });
        chat.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

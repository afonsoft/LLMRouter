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

/// <summary>SPEC-052: evals — suites, self-dispatched runs, per-case verdicts.</summary>
public class EvalsTests : IDisposable
{
    private sealed class CapturingUpstream : HttpMessageHandler
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

    public EvalsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                {
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => new CapturingUpstream());
                    // self-dispatch back into the test server
                    s.AddHttpClient("batches").ConfigurePrimaryHttpMessageHandler(() => _factory.Server.CreateHandler());
                }));
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

    private async Task SeedProviderAsync()
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", Name = "main", IsActive = true,
            Data = """{"baseUrl":"http://fake"}""", CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Suite_crud_with_cases()
    {
        await using (var db = Db()) db.EnsureCreated();
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/api/evals/suites", new
        {
            name = "smoke",
            cases = new[]
            {
                new { input = "a", expectType = "contains", expectValue = "ok", weight = 2 },
                new { input = "b", expectType = "regex", expectValue = "^o", weight = 1 },
            },
        });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var s = await r.Content.ReadFromJsonAsync<JsonElement>();
        var id = s.GetProperty("id").GetString()!;
        s.GetProperty("cases").GetArrayLength().ShouldBe(2);

        var list = await c.GetFromJsonAsync<JsonElement>("api/evals/suites");
        list.GetProperty("suites").EnumerateArray().First()
            .GetProperty("cases").GetArrayLength().ShouldBe(2);

        (await c.PutAsJsonAsync($"/api/evals/suites/{id}", new
        { name = "smoke2", cases = new[] { new { input = "x", expectType = "json", expectValue = "", weight = 1 } } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var db2 = Db())
            (await db2.EvalCases.CountAsync()).ShouldBe(1);

        (await c.DeleteAsync($"/api/evals/suites/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.DeleteAsync($"/api/evals/suites/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Run_produces_per_case_verdicts_and_score()
    {
        await SeedProviderAsync();
        var c = LoginClient();
        var created = await (await c.PostAsJsonAsync("/api/evals/suites", new
        {
            name = "s1",
            cases = new[]
            {
                new { input = "hi", expectType = "contains", expectValue = "ok", weight = 1 },
                new { input = "hi", expectType = "contains", expectValue = "nope", weight = 1 },
            },
        })).Content.ReadFromJsonAsync<JsonElement>();
        var suiteId = created.GetProperty("id").GetString()!;

        var run = await (await c.PostAsJsonAsync($"/api/evals/suites/{suiteId}/run",
            new { target = "openai/gpt-4o" })).Content.ReadFromJsonAsync<JsonElement>();
        run.GetProperty("status").GetString().ShouldBe("done");
        run.GetProperty("score").GetDouble().ShouldBe(50);
        var results = run.GetProperty("results").EnumerateArray().ToArray();
        results.Length.ShouldBe(2);
        results[0].GetProperty("pass").GetBoolean().ShouldBeTrue();
        results[1].GetProperty("pass").GetBoolean().ShouldBeFalse();
        results[0].GetProperty("output").GetString().ShouldBe("ok");

        var runs = await c.GetFromJsonAsync<JsonElement>("api/evals/runs");
        runs.GetProperty("runs").EnumerateArray().First()
            .GetProperty("suiteName").GetString().ShouldBe("s1");
        var detail = await c.GetFromJsonAsync<JsonElement>($"api/evals/runs/{run.GetProperty("id").GetString()}");
        detail.GetProperty("results").GetArrayLength().ShouldBe(2);
        (await c.GetAsync("api/evals/runs/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

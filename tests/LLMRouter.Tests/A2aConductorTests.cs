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

/// <summary>SPEC-053: A2A task lifecycle + conductor ask.</summary>
public class A2aConductorTests : IDisposable
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

    public A2aConductorTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => new CapturingUpstream())));
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
    public async Task Task_drains_to_done_with_result()
    {
        await SeedProviderAsync();
        var c = LoginClient(); // also boots the hosted executor
        var created = await (await c.PostAsJsonAsync("/api/a2a/tasks", new
        { agent = "bot1", payload = new { model = "openai/gpt-4o", text = "hi" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        created.GetProperty("state").GetString().ShouldBe("queued");

        // executor polls every 1.5s — wait for done
        JsonElement task = default;
        for (var i = 0; i < 20; i++)
        {
            task = await c.GetFromJsonAsync<JsonElement>($"api/a2a/tasks/{id}");
            if (task.GetProperty("state").GetString() is "done" or "failed") break;
            await Task.Delay(1000);
        }
        task.GetProperty("state").GetString().ShouldBe("done");
        task.GetProperty("result").GetString().ShouldBe("ok");

        var hist = await c.GetFromJsonAsync<JsonElement>($"api/a2a/tasks/{id}/history");
        hist.GetProperty("history").GetArrayLength().ShouldBeGreaterThanOrEqualTo(3);

        var status = await c.GetFromJsonAsync<JsonElement>("api/a2a/status");
        status.GetProperty("queue").GetProperty("done").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Cancel_queued_task()
    {
        await using (var db = Db()) db.EnsureCreated();
        var c = LoginClient();
        var created = await (await c.PostAsJsonAsync("/api/a2a/tasks",
            new { payload = new { model = "openai/gpt-4o", text = "x" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        var r = await c.PostAsJsonAsync($"/api/a2a/tasks/{id}/cancel", new { });
        r.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
        var t = await c.GetFromJsonAsync<JsonElement>($"api/a2a/tasks/{id}");
        t.GetProperty("state").GetString().ShouldBeOneOf("cancelled", "done", "running");
        (await c.GetAsync("api/a2a/tasks/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Conductor_ask_runs_goal_to_done()
    {
        await SeedProviderAsync();
        var c = LoginClient();
        var task = await (await c.PostAsJsonAsync("/api/conductor/ask",
            new { goal = "say hi", model = "openai/gpt-4o" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        task.GetProperty("state").GetString().ShouldBe("done");
        task.GetProperty("result").GetProperty("final").GetString().ShouldBe("ok");

        var list = await c.GetFromJsonAsync<JsonElement>("api/conductor/tasks");
        list.GetProperty("tasks").GetArrayLength().ShouldBe(1);

        var fleet = await c.GetFromJsonAsync<JsonElement>("api/conductor/fleet");
        fleet.GetProperty("workers").GetArrayLength().ShouldBe(1);
        fleet.GetProperty("conductorTasks").GetProperty("done").GetInt32().ShouldBe(1);

        (await c.PostAsJsonAsync($"api/conductor/tasks/{task.GetProperty("id").GetString()}/cancel", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict); // already done
    }
}

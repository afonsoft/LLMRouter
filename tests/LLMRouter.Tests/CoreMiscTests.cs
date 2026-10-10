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

/// <summary>SPEC-073: quota windows, credential expiration, tags, policies, sessions, translator ops.</summary>
public class CoreMiscTests : IDisposable
{
    private sealed class StubUpstream : HttpMessageHandler
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

    private readonly StubUpstream _upstream = new();
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public CoreMiscTests()
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
            Id = "cb1", Name = "qw-test", Kind = "fallback",
            Models = """["openai/gpt-4o"]""", CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task QuotaWindow_crud_and_blocks_dispatch_when_saturated()
    {
        await SeedAsync();
        var c = LoginClient();

        var bad = await c.PostAsJsonAsync("/api/quota-windows", new { provider = "", windowMinutes = 0 });
        ((int)bad.StatusCode).ShouldBeInRange(400, 499);

        var create = await c.PostAsJsonAsync("/api/quota-windows",
            new { provider = "openai", name = "tight", windowMinutes = 60, maxRequests = 0 });
        create.StatusCode.ShouldBe(HttpStatusCode.OK);
        var w = (await create.Content.ReadFromJsonAsync<JsonElement>());
        var wid = w.GetProperty("id").GetString()!;

        // maxRequests = 0 → no cap... need a real cap: set maxRequests = 1 and log one request
        await c.DeleteAsync($"/api/quota-windows/{wid}");
        var create2 = await c.PostAsJsonAsync("/api/quota-windows",
            new { provider = "openai", windowMinutes = 60, maxRequests = 1 });
        create2.StatusCode.ShouldBe(HttpStatusCode.OK);

        // first chat goes through (window fills), second is skipped → no target → non-2xx
        var chat1 = await c.PostAsJsonAsync("/v1/chat/completions",
            new { model = "qw-test", messages = new[] { new { role = "user", content = "hi" } } });
        chat1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var callsAfterFirst = _upstream.Calls;

        var chat2 = await c.PostAsJsonAsync("/v1/chat/completions",
            new { model = "qw-test", messages = new[] { new { role = "user", content = "hi" } } });
        ((int)chat2.StatusCode).ShouldBeGreaterThanOrEqualTo(400);
        _upstream.Calls.ShouldBe(callsAfterFirst);
    }

    [Fact]
    public async Task CredentialExpiration_crud_and_expiring_list()
    {
        await SeedAsync();
        var c = LoginClient();

        var create = await c.PostAsJsonAsync("/api/credentials/expiration",
            new { connectionId = "c1", expiresAt = DateTime.UtcNow.AddDays(3).ToString("o"), warnDays = 7 });
        create.StatusCode.ShouldBe(HttpStatusCode.OK);
        var row = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = row.GetProperty("id").GetString()!;

        var expiring = await c.GetFromJsonAsync<JsonElement>("/api/credentials/expiring");
        var list = expiring.GetProperty("expiring").EnumerateArray().ToList();
        list.Count.ShouldBe(1);
        list[0].GetProperty("expired").GetBoolean().ShouldBeFalse();
        list[0].GetProperty("connection").GetProperty("name").GetString().ShouldBe("t");

        // past expiry → expired = true
        var create2 = await c.PostAsJsonAsync("/api/credentials/expiration",
            new { connectionId = "c1", expiresAt = DateTime.UtcNow.AddDays(-1).ToString("o") });
        create2.StatusCode.ShouldBe(HttpStatusCode.OK);
        var expiring2 = await c.GetFromJsonAsync<JsonElement>("/api/credentials/expiring");
        expiring2.GetProperty("expiring")[0].GetProperty("expired").GetBoolean().ShouldBeTrue();

        var del = await c.DeleteAsync($"/api/credentials/expiration/{id}");
        del.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Tags_dedup_and_filters()
    {
        var c = LoginClient();
        var t1 = await c.PostAsJsonAsync("/api/tags",
            new { targetType = "provider", targetId = "openai", value = "prod" });
        t1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var t2 = await c.PostAsJsonAsync("/api/tags",
            new { targetType = "provider", targetId = "openai", value = "prod" });
        t2.StatusCode.ShouldBe(HttpStatusCode.OK);
        await c.PostAsJsonAsync("/api/tags", new { targetType = "combo", targetId = "cb1", value = "x" });

        var all = await c.GetFromJsonAsync<JsonElement>("/api/tags?targetType=provider");
        all.EnumerateArray().Count().ShouldBe(1); // deduped

        var bad = await c.PostAsJsonAsync("/api/tags", new { targetType = "", targetId = "", value = "" });
        ((int)bad.StatusCode).ShouldBeInRange(400, 499);
    }

    [Fact]
    public async Task Policies_crud_and_evaluate()
    {
        var c = LoginClient();
        var create = await c.PostAsJsonAsync("/api/policies", new
        {
            name = "block-gpt4",
            priority = 10,
            rule = JsonDocument.Parse("""{"match":{"provider":"openai","model":"gpt-*"},"action":"deny"}""").RootElement,
        });
        create.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pid = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var eval = await c.PostAsJsonAsync("/api/policies/evaluate",
            new { provider = "openai", model = "gpt-4o" });
        var matches = (await eval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        matches.GetArrayLength().ShouldBe(1);
        matches[0].GetProperty("action").GetString().ShouldBe("deny");

        var evalMiss = await c.PostAsJsonAsync("/api/policies/evaluate",
            new { provider = "gemini", model = "x" });
        (await evalMiss.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("matches").GetArrayLength().ShouldBe(0);

        var put = await c.PutAsJsonAsync($"/api/policies/{pid}", new { enabled = false });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var evalOff = await c.PostAsJsonAsync("/api/policies/evaluate",
            new { provider = "openai", model = "gpt-4o" });
        (await evalOff.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("matches").GetArrayLength().ShouldBe(0);

        (await c.DeleteAsync($"/api/policies/{pid}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Chat_sessions_tracked_and_translator_detect()
    {
        await SeedAsync();
        var c = LoginClient();

        var chat = await c.PostAsJsonAsync("/v1/chat/completions",
            new { model = "qw-test", messages = new[] { new { role = "user", content = "hi" } } });
        chat.StatusCode.ShouldBe(HttpStatusCode.OK);

        var sessions = await c.GetFromJsonAsync<JsonElement>("/api/sessions");
        var arr = sessions.EnumerateArray().ToList();
        arr.Count.ShouldBeGreaterThanOrEqualTo(1);
        arr[0].GetProperty("model").GetString().ShouldBe("gpt-4o"); // upstream model, not combo name
        arr[0].GetProperty("messageCount").GetInt32().ShouldBeGreaterThanOrEqualTo(1);

        var detect = await c.PostAsJsonAsync("/api/translator/detect",
            JsonDocument.Parse("""{"contents":[{"parts":[{"text":"hi"}]}]}""").RootElement);
        (await detect.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("format").GetString().ShouldBe("gemini");

        var detect2 = await c.PostAsJsonAsync("/api/translator/detect",
            JsonDocument.Parse("""{"messages":[{"role":"user","content":"hi"}],"model":"x"}""").RootElement);
        (await detect2.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("format").GetString().ShouldBe("openai");

        var formats = await c.GetFromJsonAsync<JsonElement>("/api/translator/formats");
        formats.GetProperty("formats").GetArrayLength().ShouldBeGreaterThanOrEqualTo(4);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-041: key groups, regenerate/reveal, per-key usage limits, plans, preview.</summary>
public class KeysQuotaTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public KeysQuotaTests()
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
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<string> NewKeyAsync()
    {
        var r = await _client.PostAsJsonAsync("/api/keys", new { name = "t" });
        return (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("key").GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> Chat(string key) =>
        // fresh client: the login cookie would make AuthenticatedKey return
        // "dashboard" and bypass the Bearer key entirely
        await _factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "openai/x", messages = new[] { new { role = "user", content = "hi" } } }),
            Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key) },
        });

    [Fact]
    public async Task Regenerate_keeps_limits_and_rotates_key()
    {
        await LoginAsync();
        var id = await NewKeyAsync();
        await _client.PutAsJsonAsync($"/api/keys/{id}/usage-limits", new { rpm = 5, dailyTokens = 1000 });

        var regen = await _client.PostAsJsonAsync($"/api/keys/{id}/regenerate", new { });
        var newKey = (await regen.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("key").GetProperty("key").GetString()!;

        var ul = await _client.GetFromJsonAsync<JsonElement>($"/api/keys/{id}/usage-limits");
        ul.GetProperty("limits").GetProperty("rpm").GetInt32().ShouldBe(5);
        ul.GetProperty("limits").GetProperty("dailyTokens").GetInt32().ShouldBe(1000);

        // the SPEC-039 rule followed the new key string
        var rl = await _client.GetFromJsonAsync<JsonElement>("/api/rate-limits");
        rl.GetProperty("rules").EnumerateArray().ShouldContain(r =>
            r.GetProperty("scope").GetString() == "apiKey" &&
            r.GetProperty("scopeValue").GetString() == newKey);
    }

    [Fact]
    public async Task Rpm_limit_blocks_second_request()
    {
        await LoginAsync();
        var id = await NewKeyAsync();
        var key = (await _client.GetFromJsonAsync<JsonElement>("/api/keys"))
            .GetProperty("keys").EnumerateArray().First(x => x.GetProperty("id").GetString() == id)
            .GetProperty("key").GetString()!;
        var put = await _client.PutAsJsonAsync($"/api/keys/{id}/usage-limits", new { rpm = 1 });
        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        (await Chat(key)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // model_not_found — limit consumed
        (await Chat(key)).StatusCode.ShouldBe((HttpStatusCode)429);
    }

    [Fact]
    public async Task Daily_token_cap_blocks_request()
    {
        await LoginAsync();
        var id = await NewKeyAsync();
        var key = (await _client.GetFromJsonAsync<JsonElement>("/api/keys"))
            .GetProperty("keys").EnumerateArray().First(x => x.GetProperty("id").GetString() == id)
            .GetProperty("key").GetString()!;
        await _client.PutAsJsonAsync($"/api/keys/{id}/usage-limits", new { dailyTokens = 10 });

        // burn the daily counter directly
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.EnsureCreated();
        db.Kv.Add(new KvEntry { Scope = KeyQuota.UsageScope, Key = KeyQuota.DayKey(key), Value = "10" });
        await db.SaveChangesAsync();

        (await Chat(key)).StatusCode.ShouldBe((HttpStatusCode)429);
    }

    [Fact]
    public async Task Preview_returns_allow_and_deny_with_reason()
    {
        await LoginAsync();
        var id = await NewKeyAsync();
        var key = (await _client.GetFromJsonAsync<JsonElement>("/api/keys"))
            .GetProperty("keys").EnumerateArray().First(x => x.GetProperty("id").GetString() == id)
            .GetProperty("key").GetString()!;

        var ok = await _client.PostAsJsonAsync("/api/quota/preview", new { keyId = id, model = "openai/x", estTokens = 5 });
        (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("allow").GetBoolean().ShouldBeTrue();

        await _client.PutAsJsonAsync($"/api/keys/{id}/usage-limits", new { dailyTokens = 10 });
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.EnsureCreated();
        db.Kv.Add(new KvEntry { Scope = KeyQuota.UsageScope, Key = KeyQuota.DayKey(key), Value = "10" });
        await db.SaveChangesAsync();

        var deny = await _client.PostAsJsonAsync("/api/quota/preview", new { keyId = id, model = "openai/x", estTokens = 5 });
        var d = await deny.Content.ReadFromJsonAsync<JsonElement>();
        d.GetProperty("allow").GetBoolean().ShouldBeFalse();
        d.GetProperty("reasons").EnumerateArray().First().GetString().ShouldContain("daily token cap");
    }

    [Fact]
    public async Task Groups_and_plans_crud()
    {
        await LoginAsync();
        var id = await NewKeyAsync();

        var grp = await _client.PostAsJsonAsync("/api/keys/groups", new { name = "g1", keys = new[] { id } });
        grp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var gid = (await grp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("group").GetProperty("id").GetString()!;

        var plan = await _client.PostAsJsonAsync("/api/quota/plans", new { name = "p1", limits = new { rpm = 3 } });
        var pid = (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan").GetProperty("id").GetString()!;

        var assign = await _client.PostAsJsonAsync($"/api/keys/{id}/plan", new { planId = pid });
        assign.StatusCode.ShouldBe(HttpStatusCode.OK);

        var ul = await _client.GetFromJsonAsync<JsonElement>($"/api/keys/{id}/usage-limits");
        ul.GetProperty("limits").GetProperty("rpm").GetInt32().ShouldBe(3);

        (await _client.DeleteAsync($"/api/keys/groups/{gid}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.DeleteAsync($"/api/quota/plans/{pid}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

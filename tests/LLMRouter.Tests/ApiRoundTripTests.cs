using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>End-to-end management API round-trips through WebApplicationFactory.</summary>
public class ApiRoundTripTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiRoundTripTests()
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
        // first-run: any non-empty password becomes the credential
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Connection_crud_round_trip_on_node_provider()
    {
        await LoginAsync();

        // create a custom openai-compatible node
        var node = await _client.PostAsJsonAsync("/api/provider-nodes",
            new { kind = "openai-compatible-chat", name = "local", data = new { baseUrl = "http://localhost:1" } });
        node.StatusCode.ShouldBe(HttpStatusCode.OK);
        var nodeId = (await node.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("node").GetProperty("id").GetString()!;
        nodeId.ShouldStartWith("openai-compatible-chat-");

        // connections are accepted on node provider ids
        var conn = await _client.PostAsJsonAsync("/api/provider-connections",
            new { provider = nodeId, name = "c1", data = new { apiKey = "k" } });
        conn.StatusCode.ShouldBe(HttpStatusCode.OK);
        var connId = (await conn.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connection").GetProperty("id").GetString()!;

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/provider-connections");
        list.GetProperty("connections").EnumerateArray().ShouldContain(x => x.GetProperty("id").GetString() == connId);

        // unknown registry provider is still rejected
        var bad = await _client.PostAsJsonAsync("/api/provider-connections",
            new { provider = "no-such-provider", name = "x", data = new { } });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var del = await _client.DeleteAsync($"/api/provider-connections/{connId}");
        del.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_key_create_and_gateway_auth_round_trip()
    {
        await LoginAsync();
        var key = await _client.PostAsJsonAsync("/api/keys", new { name = "t" });
        var keyStr = (await key.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("key").GetProperty("key").GetString()!;
        keyStr.ShouldStartWith("sk-");

        // gateway accepts the key on /v1/models
        var req = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {keyStr}");
        var models = await _client.SendAsync(req);
        models.StatusCode.ShouldBe(HttpStatusCode.OK);

        // and rejects an unknown one (fresh client — no session cookie)
        using var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var bad = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        bad.Headers.TryAddWithoutValidation("Authorization", "Bearer sk-nope");
        (await anon.SendAsync(bad)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Management_api_requires_cookie()
    {
        var resp = await _client.GetAsync("/api/provider-connections");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

[Fact]
    public async Task Model_aliases_and_combo_mappings_round_trip()
    {
        await LoginAsync();
        // alias: foo → openai/gpt-4o
        var put = await _client.PutAsJsonAsync("/api/model-aliases",
            new { aliases = new Dictionary<string, string> { ["foo"] = "openai/gpt-4o" } });
        put.EnsureSuccessStatusCode();
        var aliases = await _client.GetFromJsonAsync<JsonElement>("/api/model-aliases");
        aliases.GetProperty("aliases").GetProperty("foo").GetString().ShouldBe("openai/gpt-4o");
        // mapping: gpt-4 → combo name
        var combo = await _client.PostAsJsonAsync("/api/combos",
            new { name = "mycombo", kind = "round-robin", stickyLimit = 2, models = new[] { "openai/gpt-4o" } });
        combo.EnsureSuccessStatusCode();
        var map = await _client.PostAsJsonAsync("/api/model-combo-mappings",
            new { model = "gpt-4", combo = "mycombo" });
        map.EnsureSuccessStatusCode();
        var mappings = await _client.GetFromJsonAsync<JsonElement>("/api/model-combo-mappings");
        mappings.GetProperty("mappings").GetProperty("gpt-4").GetString().ShouldBe("mycombo");
        // combo list exposes stickyLimit
        var combos = await _client.GetFromJsonAsync<JsonElement>("/api/combos");
        var c = combos.GetProperty("combos").EnumerateArray().First(x => x.GetProperty("name").GetString() == "mycombo");
        c.GetProperty("kind").GetString().ShouldBe("round-robin");
        c.GetProperty("stickyLimit").GetInt32().ShouldBe(2);
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-068: POST /api/providers/{bulk,validate,test-batch}.</summary>
public class ProviderOpsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ProviderOpsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient();
        _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Wait();
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private async Task<string> CreateNodeAsync(string baseUrl = "http://localhost:1")
    {
        var node = await _client.PostAsJsonAsync("/api/provider-nodes",
            new { kind = "openai-compatible-chat", name = "local", data = new { baseUrl } });
        node.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await node.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("node").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Bulk_creates_connections_with_per_entry_results()
    {
        var r = await _client.PostAsJsonAsync("/api/providers/bulk", new
        {
            provider = "openai",
            keys = new[] { "sk-validkey0001", "bad key with space", "sk-validkey0001", "sk-validkey0002", "" },
        });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var j = (await r.Content.ReadFromJsonAsync<JsonElement>());
        j.GetProperty("created").GetInt32().ShouldBe(2);
        j.GetProperty("total").GetInt32().ShouldBe(5);
        var results = j.GetProperty("results").EnumerateArray().ToList();
        results.Count.ShouldBe(5);
        results[0].GetProperty("ok").GetBoolean().ShouldBeTrue();
        results[0].GetProperty("id").GetString()!.Length.ShouldBeGreaterThan(0);
        results[0].GetProperty("masked").GetString().ShouldBe("sk-v…0001");
        results[1].GetProperty("ok").GetBoolean().ShouldBeFalse();
        results[1].GetProperty("error").GetString()!.ShouldContain("whitespace");
        results[2].GetProperty("ok").GetBoolean().ShouldBeFalse();
        results[2].GetProperty("error").GetString()!.ShouldContain("duplicate");
        results[3].GetProperty("ok").GetBoolean().ShouldBeTrue();
        results[4].GetProperty("ok").GetBoolean().ShouldBeFalse();
        results[4].GetProperty("error").GetString()!.ShouldContain("empty");

        var conns = await _client.GetFromJsonAsync<JsonElement>("/api/provider-connections");
        var mine = conns.GetProperty("connections").EnumerateArray()
            .Where(c => c.GetProperty("provider").GetString() == "openai").ToList();
        mine.Count.ShouldBe(2);
        mine.ShouldContain(c => c.GetProperty("name").GetString() == "openai-1");
    }

    [Fact]
    public async Task Bulk_second_call_flags_keys_already_stored()
    {
        await _client.PostAsJsonAsync("/api/providers/bulk",
            new { provider = "openai", keys = new[] { "sk-firstkey0001" } });
        var r2 = await _client.PostAsJsonAsync("/api/providers/bulk",
            new { provider = "openai", keys = new[] { "sk-firstkey0001", "sk-secondkey01" } });
        var j = (await r2.Content.ReadFromJsonAsync<JsonElement>());
        j.GetProperty("created").GetInt32().ShouldBe(1);
        var results = j.GetProperty("results").EnumerateArray().ToList();
        results[0].GetProperty("error").GetString()!.ShouldContain("duplicate");
        results[1].GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Bulk_rejects_bad_input_with_json_error()
    {
        var unknown = await _client.PostAsJsonAsync("/api/providers/bulk",
            new { provider = "no-such-provider", keys = new[] { "sk-xxxxxxxxxx" } });
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString()!.ShouldContain("unknown provider");

        var empty = await _client.PostAsJsonAsync("/api/providers/bulk",
            new { provider = "openai", keys = Array.Empty<string>() });
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Validate_checks_credential_shape()
    {
        var ok = await _client.PostAsJsonAsync("/api/providers/validate",
            new { provider = "openai", key = "sk-abcdefghijklmn" });
        var j = (await ok.Content.ReadFromJsonAsync<JsonElement>());
        j.GetProperty("valid").GetBoolean().ShouldBeTrue();
        j.GetProperty("errors").GetArrayLength().ShouldBe(0);

        var spaced = await _client.PostAsJsonAsync("/api/providers/validate",
            new { provider = "openai", key = "sk-abc def" });
        (await spaced.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("valid").GetBoolean().ShouldBeFalse();

        var emptyKey = await _client.PostAsJsonAsync("/api/providers/validate",
            new { provider = "openai", key = "" });
        (await emptyKey.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("valid").GetBoolean().ShouldBeFalse();

        var missing = await _client.PostAsJsonAsync("/api/providers/validate",
            new { provider = "openai" });
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Validate_live_ping_reports_network_failure()
    {
        var nodeId = await CreateNodeAsync(); // http://localhost:1 → refused
        var r = await _client.PostAsJsonAsync("/api/providers/validate",
            new { provider = nodeId, key = "sk-abcdefghijklmn", live = true });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var j = (await r.Content.ReadFromJsonAsync<JsonElement>());
        j.GetProperty("valid").GetBoolean().ShouldBeTrue();
        var live = j.GetProperty("live");
        live.GetProperty("ok").GetBoolean().ShouldBeFalse();
        live.GetProperty("status").GetString().ShouldBe("network");
        live.GetProperty("error").GetString()!.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task TestBatch_returns_per_connection_results_and_persists()
    {
        var nodeId = await CreateNodeAsync(); // http://localhost:1 → refused
        var created = new List<string>();
        foreach (var name in new[] { "a", "b" })
        {
            var c = await _client.PostAsJsonAsync("/api/provider-connections",
                new { provider = nodeId, name, data = new { apiKey = "sk-batchkey1234" } });
            created.Add((await c.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("connection").GetProperty("id").GetString()!);
        }

        var ids = created.Concat(["no-such-id"]).ToArray();
        var r = await _client.PostAsJsonAsync("/api/providers/test-batch", new { ids });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var j = (await r.Content.ReadFromJsonAsync<JsonElement>());
        j.GetProperty("total").GetInt32().ShouldBe(3);
        j.GetProperty("tested").GetInt32().ShouldBe(2);
        var results = j.GetProperty("results").EnumerateArray().ToList();
        results.Count.ShouldBe(3);
        foreach (var res in results.Take(2))
        {
            res.GetProperty("ok").GetBoolean().ShouldBeFalse();
            res.GetProperty("status").GetString().ShouldBe("network");
            res.GetProperty("error").GetString()!.Length.ShouldBeGreaterThan(0);
            res.GetProperty("latencyMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        }
        results[2].GetProperty("id").GetString().ShouldBe("no-such-id");
        results[2].GetProperty("ok").GetBoolean().ShouldBeFalse();
        results[2].GetProperty("error").GetString().ShouldBe("not found");

        // outcome persisted on the connection rows
        var conns = await _client.GetFromJsonAsync<JsonElement>("/api/provider-connections");
        var c0 = conns.GetProperty("connections").EnumerateArray()
            .First(c => c.GetProperty("id").GetString() == created[0]);
        var data = JsonDocument.Parse(c0.GetProperty("data").GetString()!).RootElement;
        data.GetProperty("testStatus").GetString().ShouldBe("network");
        data.TryGetProperty("lastTestAt", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task TestBatch_rejects_empty_ids()
    {
        var r = await _client.PostAsJsonAsync("/api/providers/test-batch", new { ids = Array.Empty<string>() });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

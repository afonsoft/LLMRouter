using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LLMRouter.Server.Services.TrafficInspector;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-127: Traffic Inspector — maskSecrets/sanitizeHeaders/detectKind/contextKey
/// unit contracts + buffer filters + sessions/HAR/capture-modes endpoints.
/// </summary>
public class Spec127TrafficInspectorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public Spec127TrafficInspectorTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private const string Base = "/api/tools/traffic-inspector";

    [Fact]
    public void MaskSecrets_masks_bearer_prefixed_and_opaque()
    {
        MaskSecrets.Mask("Bearer sk-abc123def456ghi789").ShouldBe("Bearer ***");
        var key = "sk-" + new string('a', 20);
        var masked = MaskSecrets.Mask(key);
        masked.ShouldStartWith("sk-aaa");
        masked.ShouldEndWith("aa");
        masked.ShouldContain("…");
        var opaque = "x" + new string('b', 45);
        var mo = MaskSecrets.Mask(opaque);
        mo.ShouldStartWith("xbbb");
        mo.ShouldContain("…");
    }

    [Fact]
    public void SanitizeHeaders_drops_forbidden_redacts_cookies_masks_credentials()
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer sk-livekey12345678",
            ["Cookie"] = "session=secret",
            ["Host"] = "api.openai.com",
            ["X-Forwarded-For"] = "1.2.3.4",
            ["Content-Type"] = "application/json",
        };
        var s = InspectorModel.SanitizeHeaders(headers);
        s.ShouldNotContainKey("host");
        s.ShouldNotContainKey("x-forwarded-for");
        s["cookie"].ShouldBe("[REDACTED]");
        s["authorization"].ShouldContain("***");
        s["content-type"].ShouldBe("application/json");
    }

    [Fact]
    public void DetectKind_classifies_llm_app_and_unknown()
    {
        var req = NewReq("api.openai.com", "/anything", null);
        InspectorModel.DetectKind(req).ShouldBe("llm");

        req = NewReq("example.com", "/v1/chat/completions", null);
        InspectorModel.DetectKind(req).ShouldBe("llm");

        req = NewReq("example.com", "/api/data", "{\"model\":\"gpt-4\"}");
        InspectorModel.DetectKind(req).ShouldBe("llm");

        req = NewReq("example.com", "/api/data", "{\"foo\":1}");
        InspectorModel.DetectKind(req).ShouldBe("app");

        req = NewReq("example.com", "/plain", "not json");
        InspectorModel.DetectKind(req).ShouldBe("unknown");

        req = NewReq("example.com", "/x", null);
        req.RequestHeaders["user-agent"] = "codex-cli/1.0";
        InspectorModel.DetectKind(req).ShouldBe("llm");
    }

    [Fact]
    public void ContextKey_hashes_system_prompts_across_shapes()
    {
        // OpenAI messages[0].system
        var req = NewReq("x", "/v1/chat/completions",
            JsonNode.Parse("""{"messages":[{"role":"system","content":"You are helpful"},{"role":"user","content":"hi"}]}""")!.ToJsonString());
        var k1 = InspectorModel.ComputeContextKey(req);
        k1.ShouldNotBeNull();
        k1!.Length.ShouldBe(12);

        // Anthropic top-level system
        req.RequestBody = """{"system":"You are helpful","messages":[{"role":"user","content":"hi"}]}""";
        InspectorModel.ComputeContextKey(req).ShouldBe(k1);

        // No system → null
        req.RequestBody = """{"messages":[{"role":"user","content":"hi"}]}""";
        InspectorModel.ComputeContextKey(req).ShouldBeNull();
    }

    [Fact]
    public void Buffer_pushes_updates_filters_and_broadcasts()
    {
        var buffer = new TrafficBuffer();
        var events = new List<JsonObject>();
        buffer.Subscribe(events.Add);

        var req = NewReq("api.openai.com", "/v1/chat/completions",
            """{"model":"gpt-4","messages":[{"role":"system","content":"sys"},{"role":"user","content":"hi"}]}""");
        req.Id = "r1";
        buffer.Push(req);
        buffer.Count.ShouldBe(1);
        events.ShouldHaveSingleItem()["type"]!.GetValue<string>().ShouldBe("new");
        req.DetectedKind.ShouldBe("llm");
        req.ContextKey.ShouldNotBeNull();

        req.Status = 200;
        buffer.Update("r1", req);
        events.Last()["type"]!.GetValue<string>().ShouldBe("update");

        buffer.List(new ListFilters(null, null, null, "2xx", null, null)).ShouldHaveSingleItem();
        buffer.List(new ListFilters("llm", null, null, null, null, null)).ShouldHaveSingleItem();
        buffer.List(new ListFilters(null, "other.com", null, null, null, null)).ShouldBeEmpty();

        buffer.Clear();
        buffer.Count.ShouldBe(0);
        events.Last()["type"]!.GetValue<string>().ShouldBe("clear");
    }

    [Fact]
    public async Task Sessions_crud_and_har_export_flow()
    {
        // create
        var created = await _client.PostAsJsonAsync($"{Base}/sessions", new { name = "test-session" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = created.Content.ReadFromJsonAsync<JsonObject>().Result!["id"]!.GetValue<string>();

        // append request payload
        var payload = new { id = "r1", source = "http-proxy", timestamp = "2026-01-01T00:00:00Z",
            method = "POST", host = "api.openai.com", path = "/v1/chat/completions",
            requestHeaders = new Dictionary<string, string>(), requestSize = 10,
            responseHeaders = new Dictionary<string, string>(), responseSize = 20, status = 200 };
        var appended = await _client.PostAsJsonAsync($"{Base}/sessions/{id}/requests", payload);
        appended.IsSuccessStatusCode.ShouldBeTrue();

        // get with requests
        var got = await _client.GetFromJsonAsync<JsonObject>($"{Base}/sessions/{id}");
        got!["request_count"]!.GetValue<int>().ShouldBe(1);
        got["requests"]!.AsArray().Count.ShouldBe(1);

        // export.har
        var har = await _client.GetAsync($"{Base}/sessions/{id}/export.har");
        har.StatusCode.ShouldBe(HttpStatusCode.OK);
        har.Content.Headers.ContentDisposition!.FileName!.ShouldContain(".har");
        var harDoc = await har.Content.ReadFromJsonAsync<JsonObject>();
        harDoc!["log"]!["version"]!.GetValue<string>().ShouldBe("1.2");
        harDoc["log"]!["entries"]!.AsArray().Count.ShouldBe(1);
        harDoc["log"]!["entries"]![0]!["_omniRouteId"]!.GetValue<string>().ShouldBe("r1");

        // patch stop + delete
        var stop = await _client.PatchAsJsonAsync($"{Base}/sessions/{id}", new { action = "stop" });
        stop.IsSuccessStatusCode.ShouldBeTrue();
        (await _client.DeleteAsync($"{Base}/sessions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetAsync($"{Base}/sessions/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Requests_endpoints_list_get_annotate_replay_clear()
    {
        var svc = _factory.Services.GetRequiredService<TrafficInspectorService>();
        var req = NewReq("api.openai.com", "/v1/models", null);
        req.Id = "e2e-1";
        svc.Buffer.Push(req);

        var list = await _client.GetFromJsonAsync<JsonObject>($"{Base}/requests");
        list!["requests"]!.AsArray().Count.ShouldBeGreaterThan(0);

        var got = await _client.GetFromJsonAsync<JsonObject>($"{Base}/requests/e2e-1");
        got!["id"]!.GetValue<string>().ShouldBe("e2e-1");

        var ann = await _client.PutAsJsonAsync($"{Base}/requests/e2e-1/annotation", new { annotation = "looks fine" });
        ann.IsSuccessStatusCode.ShouldBeTrue();
        svc.Buffer.Get("e2e-1")!.Annotation.ShouldBe("looks fine");

        // replay targets a real listener (env base) — unreachable in TestServer → 400 path
        var replay = await _client.PostAsync($"{Base}/requests/e2e-1/replay", null);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var rd = await replay.Content.ReadFromJsonAsync<JsonObject>();
        rd!["error"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

        (await _client.DeleteAsync($"{Base}/requests")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        svc.Buffer.Count.ShouldBe(0);
    }

    [Fact]
    public async Task CaptureModes_report_and_httpproxy_start_stop()
    {
        var modes = await _client.GetFromJsonAsync<JsonObject>($"{Base}/capture-modes");
        modes!["httpProxy"].ShouldNotBeNull();
        modes["agentBridge"].ShouldNotBeNull();
        modes["tlsIntercept"].ShouldNotBeNull();

        var start = await _client.PostAsJsonAsync($"{Base}/capture-modes/http-proxy", new { action = "start", port = 0 });
        start.StatusCode.ShouldBe(HttpStatusCode.Created);
        var started = await start.Content.ReadFromJsonAsync<JsonObject>();
        started!["running"]!.GetValue<bool>().ShouldBeTrue();
        var port = started["port"]!.GetValue<int>();
        port.ShouldBeGreaterThan(0);

        var stop = await _client.PostAsJsonAsync($"{Base}/capture-modes/http-proxy", new { action = "stop" });
        stop.IsSuccessStatusCode.ShouldBeTrue();
    }

    [Fact]
    public async Task Hosts_crud_and_tls_intercept_toggle()
    {
        var added = await _client.PostAsJsonAsync($"{Base}/hosts", new { host = "my-proxy.example.com", label = "test" });
        added.StatusCode.ShouldBe(HttpStatusCode.Created);
        var hosts = await _client.GetFromJsonAsync<JsonObject>($"{Base}/hosts");
        hosts!["hosts"]!.AsArray().ShouldContain(h => h!["host"]!.GetValue<string>() == "my-proxy.example.com");

        var tls = await _client.PostAsJsonAsync($"{Base}/capture-modes/tls-intercept", new { enabled = true });
        tls.IsSuccessStatusCode.ShouldBeTrue();
        (await tls.Content.ReadFromJsonAsync<JsonObject>())!["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    private static InterceptedRequest NewReq(string host, string path, string? body) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Source = "test",
        Timestamp = DateTime.UtcNow.ToString("o"),
        Method = "POST", Host = host, Path = path,
        RequestBody = body,
        RequestSize = body?.Length ?? 0,
        Status = 200,
    };
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-044: openapi spec + try-it self-dispatch.</summary>
public class OpenApiExplorerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public OpenApiExplorerTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("batches").ConfigurePrimaryHttpMessageHandler(
                        () => _factory.Server.CreateHandler())));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private async Task LoginAsync() =>
        (await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public async Task Spec_lists_chat_completions_path()
    {
        await LoginAsync();
        var spec = await _client.GetFromJsonAsync<JsonElement>("/api/openapi/spec");
        spec.GetProperty("openapi").GetString().ShouldBe("3.1.0");
        spec.GetProperty("paths").TryGetProperty("/v1/chat/completions", out var chat).ShouldBeTrue();
        chat.TryGetProperty("post", out _).ShouldBeTrue();
        // grouped tags exist for the /api surface too
        spec.GetProperty("paths").TryGetProperty("/api/openapi/spec", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Try_it_dispatches_a_get_endpoint()
    {
        await LoginAsync();
        var r = await _client.PostAsJsonAsync("/api/openapi/try",
            new { path = "/api/jobs", method = "GET" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().ShouldBe(200);
        body.GetProperty("ms").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }
}

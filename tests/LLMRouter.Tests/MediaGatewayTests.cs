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
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-063: /v1 media gateway — kind-aware routing, fallback, 422/404.</summary>
public class MediaGatewayTests : IDisposable
{
    private sealed class CapturingUpstream : HttpMessageHandler
    {
        public List<(string Method, string Url, byte[]? Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bodyBytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Calls.Add((request.Method.Method, request.RequestUri!.ToString(), bodyBytes));
            var json = """{"ok":true,"data":[{"id":"v1"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly CapturingUpstream _up = new();
    private readonly WebApplicationFactory<Program> _factory;

    public MediaGatewayTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => _up)));
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

    private async Task SeedAsync(params ProviderConnection[] conns)
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.AddRange(conns);
        await db.SaveChangesAsync();
    }

    private static ProviderConnection Conn(string id, string baseUrl, string? mediaKinds = null) => new()
    {
        Id = id, Provider = "openai", AuthType = "apikey", Name = id, IsActive = true,
        Data = mediaKinds is null
            ? $$"""{"baseUrl":"{{baseUrl}}","apiKey":"sk-fake"}"""
            : $$"""{"baseUrl":"{{baseUrl}}","apiKey":"sk-fake","mediaKinds":{{mediaKinds}}}""",
        CreatedAt = "x", UpdatedAt = "x",
    };

    [Fact]
    public async Task Audio_speech_falls_back_to_tts_declared_connection()
    {
        await SeedAsync(Conn("tts1", "http://tts-host", """["tts"]"""));
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/v1/audio/speech", new { input = "oi" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        _up.Calls.ShouldHaveSingleItem().Url.ShouldBe("http://tts-host/audio/speech");
        await using var db = Db();
        (await db.UsageHistory.FirstAsync()).Endpoint.ShouldBe("tts");
    }

    [Fact]
    public async Task Generic_connection_without_kind_still_serves_media()
    {
        await SeedAsync(Conn("g1", "http://gen"));
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/v1/images/edits", new { model = "gpt-4o", prompt = "x" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        _up.Calls.ShouldHaveSingleItem().Url.ShouldBe("http://gen/images/edits");
    }

    [Theory]
    [InlineData("/v1/voices", "tts")]
    [InlineData("/v1/speech-to-text", "stt")]
    [InlineData("/v1/music/generations", "music")]
    [InlineData("/v1/rerank", "rerank")]
    [InlineData("/v1/ocr", "ocr")]
    [InlineData("/v1/segment", "segment")]
    [InlineData("/v1/web/map", "fetch")]
    public async Task Media_kinds_route_to_kind_declaring_connection(string path, string kind)
    {
        await SeedAsync(Conn("k1", "http://kind-host", $"[\"{kind}\"]"));
        var c = LoginClient();
        var r = await c.PostAsJsonAsync(path, new { input = "x" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        _up.Calls.ShouldHaveSingleItem().Url.ShouldContain("kind-host");
        await using var db = Db();
        (await db.UsageHistory.FirstAsync()).Endpoint.ShouldBe(kind);
    }

    [Fact]
    public async Task Videos_generations_post_and_poll()
    {
        await SeedAsync(Conn("v1c", "http://vid", """["video"]"""));
        var c = LoginClient();
        (await c.PostAsJsonAsync("/v1/videos/generations", new { prompt = "clip" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        _up.Calls[0].Url.ShouldBe("http://vid/videos/generations");
        (await c.GetAsync("/v1/videos/generations/abc123"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        _up.Calls[1].Url.ShouldBe("http://vid/videos/generations/abc123");
        _up.Calls[1].Method.ShouldBe("GET");
    }

    [Fact]
    public async Task Text_to_speech_voice_id_forwards_path()
    {
        await SeedAsync(Conn("t2", "http://tts2", """["tts"]"""));
        var c = LoginClient();
        (await c.PostAsJsonAsync("/v1/text-to-speech/alloy", new { input = "hi" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        _up.Calls.ShouldHaveSingleItem().Url.ShouldBe("http://tts2/text-to-speech/alloy");
    }

    [Fact]
    public async Task Unsupported_media_kind_returns_descriptive_422()
    {
        await SeedAsync(Conn("g9", "http://gen9"));
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/v1/ocr", new { image = "x" });
        r.StatusCode.ShouldBe((HttpStatusCode)422);
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("ocr");
    }

    [Fact]
    public async Task Unknown_media_path_returns_404()
    {
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/v1/telepathy", new { x = 1 });
        r.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ws_endpoint_returns_documented_501()
    {
        var c = LoginClient();
        var r = await c.GetAsync("/v1/ws");
        r.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        (await r.Content.ReadAsStringAsync()).ShouldContain("not_implemented");
    }

    [Fact]
    public async Task Search_analytics_aggregates_search_usage()
    {
        await SeedAsync(Conn("s1", "http://s1", """["search"]"""));
        var c = LoginClient();
        (await c.PostAsJsonAsync("/v1/search", new { query = "q" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var r = await c.GetFromJsonAsync<JsonElement>("/api/usage/search-analytics?days=1");
        r.GetProperty("requests").GetInt32().ShouldBe(1);
        r.GetProperty("byProvider")[0].GetProperty("provider").GetString().ShouldBe("openai");
    }

    [Fact]
    public async Task Media_requires_auth()
    {
        var c = _factory.CreateClient();
        var r = await c.PostAsJsonAsync("/v1/audio/speech", new { input = "x" });
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-088: probes + /v1/providers/{p}/* scoped surface.</summary>
public class ProviderScopedTests : WebAppTestBase
{
    private sealed class CapturingUpstream : HttpMessageHandler
    {
        public List<(string Method, string Url, byte[]? Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bodyBytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Calls.Add((request.Method.Method, request.RequestUri!.ToString(), bodyBytes));
            var isStream = bodyBytes is not null && Encoding.UTF8.GetString(bodyBytes).Contains("\"stream\":true");
            var payload = isStream
                ? "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"delta\":{\"content\":\"hi\"},\"index\":0}]}\n\ndata: [DONE]\n\n"
                : """{"id":"c1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8,
                    isStream ? "text/event-stream" : "application/json"),
            };
        }
    }

    private readonly CapturingUpstream _up = new();

    protected override void ConfigureTestServices(IServiceCollection s)
        => s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => _up);

    private async Task SeedAsync(params ProviderConnection[] conns)
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.AddRange(conns);
        await db.SaveChangesAsync();
    }

    private static ProviderConnection Conn(string id, string provider, string baseUrl) => new()
    {
        Id = id, Provider = provider, AuthType = "apikey", Name = id, IsActive = true,
        Data = $$"""{"baseUrl":"{{baseUrl}}","apiKey":"sk-fake"}""",
        CreatedAt = "x", UpdatedAt = "x",
    };

    [Fact]
    public async Task Probes_are_anonymous()
    {
        (await Client.GetAsync("/healthz")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/livez")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var r = await Client.GetAsync("/readyz");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).ShouldContain("\"ok\":true");
    }

    [Fact]
    public async Task Health_ping_authed()
    {
        (await Client.GetAsync("/api/health/ping")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await LoginAsync();
        (await Client.GetAsync("/api/health/ping")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Scoped_chat_reaches_only_that_provider()
    {
        await SeedAsync(Conn("c-openai", "openai", "http://openai-host"),
            Conn("c-anth", "anthropic", "http://anth-host"));
        await LoginAsync();
        var r = await Client.PostAsJsonAsync("/v1/providers/openai/chat/completions",
            new { model = "gpt-4o", messages = new[] { new { role = "user", content = "hi" } } });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        _up.Calls.ShouldHaveSingleItem().Url.ShouldContain("openai-host");
    }

    [Fact]
    public async Task Scoped_chat_stream_passes_through()
    {
        await SeedAsync(Conn("c-openai", "openai", "http://openai-host"));
        await LoginAsync();
        var r = await Client.PostAsJsonAsync("/v1/providers/openai/chat/completions",
            new { model = "gpt-4o", stream = true, messages = new[] { new { role = "user", content = "hi" } } });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        _up.Calls.ShouldHaveSingleItem().Url.ShouldContain("openai-host");
    }

    [Fact]
    public async Task Unknown_provider_is_400()
    {
        await LoginAsync();
        var r = await Client.PostAsJsonAsync("/v1/providers/zzz-nope/chat/completions",
            new { model = "m", messages = new[] { new { role = "user", content = "hi" } } });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Mismatched_model_prefix_is_400()
    {
        await SeedAsync(Conn("c-openai", "openai", "http://openai-host"));
        await LoginAsync();
        var r = await Client.PostAsJsonAsync("/v1/providers/openai/chat/completions",
            new { model = "anthropic/claude-3", messages = new[] { new { role = "user", content = "hi" } } });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).ShouldContain("does not belong");
    }

    [Fact]
    public async Task Scoped_embeddings_uses_short_upstream_path()
    {
        await SeedAsync(Conn("c-openai", "openai", "http://openai-host"));
        await LoginAsync();
        var r = await Client.PostAsJsonAsync("/v1/providers/openai/embeddings",
            new { model = "text-embedding-3-small", input = "x" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        _up.Calls.ShouldHaveSingleItem().Url.ShouldBe("http://openai-host/embeddings");
    }

    [Fact]
    public async Task Provider_models_lists_registry_and_synced()
    {
        await SeedAsync(Conn("c-openai", "openai", "http://openai-host"));
        await using (var db = Db())
        {
            db.SyncedModels.Add(new SyncedModel
            { Id = "s1", Provider = "openai", Model = "synced-1", Available = true, LastSyncAt = "x" });
            await db.SaveChangesAsync();
        }
        await LoginAsync();
        var r = await Client.GetAsync("/v1/providers/openai/models");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("\"object\":\"list\"");
        body.ShouldContain("synced-1");
    }

    [Fact]
    public async Task Limits_round_trip()
    {
        await LoginAsync();
        var put = await Client.PutAsJsonAsync("/v1/providers/openai/limits",
            new { maxActiveKeys = 3, dailyIssueLimit = 10 });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var get = await Client.GetAsync("/v1/providers/openai/limits");
        var body = await get.Content.ReadAsStringAsync();
        body.ShouldContain("\"maxActiveKeys\":3");
        body.ShouldContain("\"dailyIssueLimit\":10");
    }

    [Fact]
    public async Task Voices_rejects_non_fishaudio()
    {
        await LoginAsync();
        var r = await Client.GetAsync("/v1/providers/openai/voices");
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).ShouldContain("not supported");
    }

    [Fact]
    public async Task Fishaudio_voices_proxies_to_connection()
    {
        await SeedAsync(Conn("c-fish", "fishaudio", "http://fish-host"));
        await LoginAsync();
        var r = await Client.GetAsync("/v1/providers/fishaudio/voices");
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        _up.Calls.ShouldHaveSingleItem().Url.ShouldBe("http://fish-host/model");
    }

    [Fact]
    public async Task Suggested_models_validates_params()
    {
        await LoginAsync();
        (await Client.GetAsync("/v1/providers/suggested-models?type=video"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Client.GetAsync("/v1/providers/suggested-models?sortBy=bogus"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Dual_mount_api_v1_works()
    {
        await SeedAsync(Conn("c-openai", "openai", "http://openai-host"));
        await LoginAsync();
        var r = await Client.PostAsJsonAsync("/api/v1/providers/openai/chat/completions",
            new { model = "gpt-4o", messages = new[] { new { role = "user", content = "hi" } } });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        _up.Calls.ShouldHaveSingleItem().Url.ShouldContain("openai-host");
    }
}

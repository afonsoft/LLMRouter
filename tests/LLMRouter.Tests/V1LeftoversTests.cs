using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-089: /v1 leftovers surface.</summary>
public class V1LeftoversTests : WebAppTestBase
{
    private sealed class CapturingUpstream : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"c1","object":"chat.completion","choices":[],"usage":{}}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private readonly CapturingUpstream _up = new();

    protected override void ConfigureHost(WebHostBuilderContext ctx, IConfigurationBuilder cfg)
        => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Bifrost:BaseUrl"] = "http://bifrost-host" });

    protected override void ConfigureTestServices(IServiceCollection s)
        => s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => _up);

    private async Task<LlmRouterDbContext> SeedAsync()
    {
        var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c-openai", Provider = "openai", AuthType = "apikey", Name = "o", IsActive = true,
            Data = """{"baseUrl":"http://openai-host","apiKey":"sk-fake"}""",
            CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Responses_input_tokens_counts()
    {
        await LoginAsync();
        var r = await Client.PostAsJsonAsync("/v1/responses/input_tokens",
            new { model = "openai/gpt-4o", input = "hello world this is a test" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("input_tokens");
    }

    [Fact]
    public async Task Responses_unknown_subpath_passthrough()
    {
        await SeedAsync();
        await LoginAsync();
        var r = await Client.PostAsJsonAsync("/v1/responses/resp_123/input_items",
            new { model = "openai/gpt-4o" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        _up.Calls.ShouldHaveSingleItem().Url.ShouldContain("/responses/resp_123/input_items");
    }

    [Fact]
    public async Task Nested_model_id_card()
    {
        await SeedAsync();
        await LoginAsync();
        var ok = await Client.GetAsync("/v1/models/openai%2Fgpt-4o");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        (await ok.Content.ReadAsStringAsync()).ShouldContain("\"id\":\"openai/gpt-4o\"");
        (await Client.GetAsync("/v1/models/zzz%2Fnope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Bifrost_relay_proxies_to_sidecar()
    {
        var db = await SeedAsync();
        db.RelayTokens.Add(new RelayToken
        { Id = "t1", Token = "rl-test", IsActive = true, AllowedModels = "[]", CreatedAt = "x" });
        await db.SaveChangesAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/relay/chat/completions/bifrost")
        {
            Headers = { Authorization = new("Bearer", "rl-test") },
            Content = JsonContent.Create(new { model = "openai/gpt-4o", messages = new object[] { } }),
        };
        var r = await Client.SendAsync(req);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        _up.Calls.ShouldHaveSingleItem().Url.ShouldBe("http://bifrost-host/v1/chat/completions");
    }

    [Fact]
    public async Task Bifrost_relay_rejects_bad_token()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/relay/chat/completions/bifrost")
        { Content = JsonContent.Create(new { model = "m" }) };
        (await Client.SendAsync(req)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Agents_health_and_tasks()
    {
        var db = await SeedAsync();
        db.A2aTasks.Add(new A2aTask
        { Id = "a1", Agent = "bot-1", State = "running", Payload = "{}", CreatedAt = "now" });
        await db.SaveChangesAsync();
        await LoginAsync();
        var h = await Client.GetAsync("/v1/agents/health");
        h.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await h.Content.ReadAsStringAsync()).ShouldContain("bot-1");
        var tasks = await Client.GetAsync("/v1/agents/tasks/a1");
        tasks.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await tasks.Content.ReadAsStringAsync()).ShouldContain("\"State\":\"running\"");
    }

    [Fact]
    public async Task Registered_key_detail_and_revoke()
    {
        var db = await SeedAsync();
        db.ApiKeys.Add(new ApiKey { Id = "k1", Key = "sk-abcdef", Name = "test", IsActive = true, CreatedAt = "x" });
        await db.SaveChangesAsync();
        await LoginAsync();
        var d = await Client.GetAsync("/v1/registered-keys/k1");
        d.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rv = await Client.PostAsync("/v1/registered-keys/k1/revoke", null);
        rv.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db2 = Db();
        (await db2.ApiKeys.FindAsync("k1"))!.IsActive.ShouldBeFalse();
        (await db2.Kv.FindAsync("apiKeyRevoked", "k1")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Batches_delete_completed_prunes_terminal_only()
    {
        var db = await SeedAsync();
        db.Kv.Add(new KvEntry { Scope = "v1batches", Key = "b1", Value = """{"id":"b1","status":"completed"}""" });
        db.Kv.Add(new KvEntry { Scope = "v1batches", Key = "b2", Value = """{"id":"b2","status":"in_progress"}""" });
        await db.SaveChangesAsync();
        await LoginAsync();
        var r = await Client.PostAsync("/v1/batches/delete-completed", null);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).ShouldContain("\"deleted\":1");
        (await db.Kv.FindAsync("v1batches", "b2")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Vscode_raw_sends_model_verbatim()
    {
        var db = await SeedAsync();
        db.VscodeTokens.Add(new VscodeToken
        { Id = "v1", Token = "vsc-test", Name = "t", IsActive = true, CreatedAt = "x", DefaultCombo = "combo-x" });
        await db.SaveChangesAsync();
        var r = await Client.PostAsJsonAsync("/v1/vscode/raw/vsc-test/chat/completions",
            new { model = "openai/gpt-4o", messages = new[] { new { role = "user", content = "hi" } } });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var call = _up.Calls.ShouldHaveSingleItem();
        call.Url.ShouldContain("openai-host");
        call.Body!.ShouldContain("gpt-4o");
        call.Body.ShouldNotContain("combo-x");
    }

    [Fact]
    public async Task Proxy_assignments_bulk_and_health()
    {
        var db = await SeedAsync();
        db.ProxyPools.Add(new ProxyPool { Id = "px1", IsActive = true, TestStatus = "ok", Data = "{}", CreatedAt = "x", UpdatedAt = "x" });
        await db.SaveChangesAsync();
        await LoginAsync();
        var r = await Client.PostAsJsonAsync("/v1/management/proxies/bulk-assign",
            new[] { new { proxyId = "px1", providerId = "openai" } });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var h = await Client.GetAsync("/v1/management/proxies/health");
        (await h.Content.ReadAsStringAsync()).ShouldContain("\"providerId\":\"openai\"");
    }

    [Fact]
    public async Task Proxy_subscription_roundtrip_and_refresh()
    {
        await LoginAsync();
        var put = await Client.PutAsJsonAsync("/v1/management/proxy-subscriptions/openai",
            new { url = "http://sub", nodes = new[] { new { host = "n1" } } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var get = await Client.GetAsync("/v1/management/proxy-subscriptions/openai");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var nodes = await Client.GetAsync("/v1/management/proxy-subscriptions/openai/nodes");
        (await nodes.Content.ReadAsStringAsync()).ShouldContain("n1");
        var rf = await Client.PostAsync("/v1/management/proxy-subscriptions/openai/refresh", null);
        (await rf.Content.ReadAsStringAsync()).ShouldContain("\"nodes\":1");
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-116 (slice 2): request logs — filtros upstream, meta endpoint, recording flag.</summary>
public class Spec116LogsTests : WebAppTestBase
{
    private static string Now(int back = 0) =>
        DateTime.UtcNow.AddMinutes(-back).ToString("yyyy-MM-dd HH:mm:ss");

    private void SeedCall(string provider, string model, string status,
        string? key = "k1", string? conn = "c1", string? type = "/v1/chat/completions", string? meta = null)
    {
        using var db = Db();
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = Now(), Provider = provider, Model = model, ConnectionId = conn,
            ApiKey = key, Endpoint = type, PromptTokens = 10, CompletionTokens = 20,
            Cost = 0.001, LatencyMs = 100, Status = status, Meta = meta,
        });
        db.SaveChanges();
    }

    private async Task<JsonElement> GetLogs(string qs)
    {
        var r = await Client.GetAsync($"api/usage/call-logs?{qs}");
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
    }

    [Fact]
    public async Task Call_logs_providers_multi_select_filtra()
    {
        await LoginAsync();
        SeedCall("openai", "gpt-4", "200");
        SeedCall("groq", "llama", "200");
        SeedCall("anthropic", "claude", "200");

        var items = (await GetLogs("providers[]=openai&providers[]=groq"))
            .GetProperty("items").EnumerateArray().ToList();
        items.ShouldAllBe(i => new[] { "openai", "groq" }.Contains(i.GetProperty("provider").GetString()));
        items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Call_logs_q_busca_em_model_provider()
    {
        await LoginAsync();
        SeedCall("openai", "gpt-4", "200");
        SeedCall("groq", "llama", "200");

        var items = (await GetLogs("q=llama")).GetProperty("items").EnumerateArray().ToList();
        items.ShouldAllBe(i => i.GetProperty("model").GetString() == "llama");
    }

    [Fact]
    public async Task Call_logs_status_classe_4xx_5xx()
    {
        await LoginAsync();
        SeedCall("openai", "gpt-4", "200");
        SeedCall("openai", "gpt-4", "404");
        SeedCall("openai", "gpt-4", "500");

        var items = (await GetLogs("status=5xx")).GetProperty("items").EnumerateArray().ToList();
        items.ShouldAllBe(i => i.GetProperty("status").GetString()!.StartsWith('5'));
        items.Count.ShouldBe(1);
        var err = (await GetLogs("status=err")).GetProperty("items").EnumerateArray().ToList();
        err.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Call_logs_meta_retorna_opcoes_distintas()
    {
        await LoginAsync();
        SeedCall("openai", "gpt-4", "200", key: "k-aaa", conn: "conn-1");
        SeedCall("groq", "llama", "200", key: "k-bbb", conn: "conn-2");

        var r = await Client.GetAsync("api/usage/call-logs/meta");
        r.EnsureSuccessStatusCode();
        var m = (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        m.GetProperty("providers").EnumerateArray().Select(x => x.GetString()).ShouldContain("openai");
        m.GetProperty("models").EnumerateArray().Select(x => x.GetString()).ShouldContain("llama");
        m.GetProperty("keys").EnumerateArray().Select(x => x.GetString()).ShouldContain("k-aaa");
        m.GetProperty("accounts").EnumerateArray().Select(x => x.GetString()).ShouldContain("conn-1");
    }

    [Fact]
    public async Task RequestLogging_false_nao_escreve_request_details()
    {
        await LoginAsync();
        await Client.PutAsJsonAsync("api/settings", new { requestLogging = false });

        await using var scope = Factory.Services.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<GatewayEngine>();
        await engine.LogUsageAsync("specprov", "specmodel", "c1", "spec116-key", "openai", 10, 5, "200", null, 42);

        await using var db = Db();
        (await db.UsageHistory.CountAsync(u => u.ApiKey == "spec116-key")).ShouldBe(1);
        (await db.RequestDetails.CountAsync(r => r.Provider == "specprov")).ShouldBe(0);
    }

    // ---- SPEC-116 slice 3: proxy-logs journal ----

    private void SeedProxyLog(string status = "success", string level = "direct",
        string? provider = "openai", string? proxyJson = null,
        string? target = "https://api.openai.com/v1/chat", string? error = null,
        string? clientIp = "10.0.0.1")
    {
        using var db = Db();
        db.ProxyLogs.Add(new ProxyLog
        {
            Id = Guid.NewGuid().ToString("N"), Timestamp = Now(),
            Status = status, Level = level, Provider = provider,
            Proxy = proxyJson, TargetUrl = target, ClientIp = clientIp,
            LatencyMs = 42, Error = error,
        });
        db.SaveChanges();
    }

    private async Task<JsonElement> GetProxyLogs(string qs = "")
    {
        var r = await Client.GetAsync($"api/usage/proxy-logs?{qs}");
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
    }

    [Fact]
    public async Task Proxy_logs_retorna_array_cru_no_shape_upstream()
    {
        await LoginAsync();
        SeedProxyLog(level: "provider",
            proxyJson: """{"id":"px1","type":"http","host":"10.0.0.9","port":3128,"name":"murphy"}""");

        var rows = (await GetProxyLogs()).EnumerateArray().ToList();
        var row = rows.Single(r => r.GetProperty("level").GetString() == "provider");
        row.GetProperty("status").GetString().ShouldBe("success");
        row.GetProperty("proxy").GetProperty("host").GetString().ShouldBe("10.0.0.9");
        row.GetProperty("targetUrl").GetString().ShouldContain("openai.com");
        row.GetProperty("isProviderTestLog").GetBoolean().ShouldBeFalse();
        row.GetProperty("latencyMs").GetInt64().ShouldBe(42);
    }

    [Fact]
    public async Task Proxy_logs_status_ok_mapeia_para_success()
    {
        await LoginAsync();
        SeedProxyLog(status: "success");
        SeedProxyLog(status: "error", error: "boom");

        var rows = (await GetProxyLogs("status=ok")).EnumerateArray().ToList();
        rows.ShouldAllBe(r => r.GetProperty("status").GetString() == "success");
    }

    [Fact]
    public async Task Proxy_logs_level_e_search_e_type_filtram()
    {
        await LoginAsync();
        SeedProxyLog(level: "direct");
        SeedProxyLog(level: "provider-test", target: "https://api.x/v1/models/connection-test");
        SeedProxyLog(level: "provider", provider: "groq",
            proxyJson: """{"id":"s1","type":"socks5","host":"1.2.3.4","port":1080,"name":"sock"}""");

        var tests = (await GetProxyLogs("level=provider-test")).EnumerateArray().ToList();
        tests.ShouldNotBeEmpty();
        tests.ShouldAllBe(r => r.GetProperty("isProviderTestLog").GetBoolean());

        var found = (await GetProxyLogs("search=sock")).EnumerateArray().ToList();
        found.ShouldAllBe(r =>
            r.GetProperty("proxy").ValueKind == JsonValueKind.Object
            && r.GetProperty("proxy").GetProperty("name").GetString() == "sock");
        found.ShouldNotBeEmpty();

        var socks = (await GetProxyLogs("type=socks5")).EnumerateArray().ToList();
        socks.ShouldAllBe(r => r.GetProperty("proxy").GetProperty("type").GetString() == "socks5");
        socks.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Proxy_logs_delete_limpa_tabela()
    {
        await LoginAsync();
        SeedProxyLog();
        var r = await Client.DeleteAsync("api/usage/proxy-logs");
        r.EnsureSuccessStatusCode();
        (await GetProxyLogs()).EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Log_usage_escreve_proxy_log_com_correlation()
    {
        await LoginAsync();
        await using var scope = Factory.Services.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<GatewayEngine>();
        await engine.LogUsageAsync("specprov", "specmodel", "c1", "spec116-px", "openai",
            10, 5, "200", null, 42, plog: new ProxyLogInfo("provider", "pool-1",
                "px1", "http", "10.0.0.9", 3128, "murphy", "https://api.openai.com/v1/chat"));

        await using var db = Db();
        var row = await db.ProxyLogs.SingleAsync(p => p.Provider == "specprov");
        row.Level.ShouldBe("provider");
        row.CorrelationId.ShouldNotBeNullOrEmpty();
        row.Proxy.ShouldContain("10.0.0.9");
        var usage = await db.UsageHistory.SingleAsync(u => u.ApiKey == "spec116-px");
        usage.Meta.ShouldContain(row.CorrelationId);
    }

    [Fact]
    public async Task Provider_test_escreve_row_provider_test()
    {
        await LoginAsync();
        await using var scope = Factory.Services.CreateAsyncScope();
        var db1 = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        db1.ProviderConnections.Add(new ProviderConnection
        {
            Id = "conn-pt1", Provider = "openai", AuthType = "apikey",
            Data = """{"apiKey":"x"}""", CreatedAt = Now(), UpdatedAt = Now(),
        });
        await db1.SaveChangesAsync();

        // hit the real test endpoint — upstream call fails (no network) but the
        // journal row is written regardless of the probe outcome
        await Client.PostAsync("api/provider-connections/conn-pt1/test", null);

        await using var db = Db();
        var row = await db.ProxyLogs.SingleOrDefaultAsync(p => p.Level == "provider-test");
        row.ShouldNotBeNull();
        row.ConnectionId.ShouldBe("conn-pt1");
        row.TargetUrl.ShouldContain("/connection-test");
    }
}

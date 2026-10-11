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
}

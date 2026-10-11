using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-114: contratos de API usados pelos cards/filtros da página de API keys.</summary>
public class Spec114ApiKeysPageTests : WebAppTestBase
{
    private async Task<JsonDocument> CreateKey(string name)
    {
        var r = await Client.PostAsJsonAsync("api/keys", new { name });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonDocument>())!;
    }

    [Fact]
    public async Task Keys_lista_campos_que_os_cards_precisam()
    {
        await LoginAsync();
        await CreateKey("card-key");
        var r = await Client.GetAsync("api/keys");
        r.EnsureSuccessStatusCode();
        var keys = (await r.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("keys").EnumerateArray().ToList();
        keys.ShouldNotBeEmpty();
        keys.ShouldAllBe(k => HasAll(k));
    }

    private static bool HasAll(JsonElement k) =>
        k.TryGetProperty("id", out _) && k.TryGetProperty("key", out _)
        && k.TryGetProperty("isActive", out _) && k.TryGetProperty("name", out _)
        && k.TryGetProperty("createdAt", out _);

    [Fact]
    public async Task Usage_limits_retorna_limits_e_dailyUsed()
    {
        await LoginAsync();
        var k = await CreateKey("lim-key");
        var id = k.RootElement.GetProperty("key").GetProperty("id").GetString()!;
        var put = await Client.PutAsJsonAsync($"api/keys/{id}/usage-limits",
            new Dictionary<string, int> { ["rpm"] = 60 });
        put.EnsureSuccessStatusCode();
        var r = await Client.GetAsync($"api/keys/{id}/usage-limits");
        var doc = (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        doc.GetProperty("limits").GetProperty("rpm").GetInt32().ShouldBe(60);
        doc.TryGetProperty("dailyUsed", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Reveal_audita_visualizacao_da_chave()
    {
        await LoginAsync();
        var k = await CreateKey("reveal-key");
        var id = k.RootElement.GetProperty("key").GetProperty("id").GetString()!;
        var r = await Client.PostAsJsonAsync($"api/keys/{id}/reveal", new { });
        r.EnsureSuccessStatusCode();

        var ev = await Client.GetAsync("api/audit/events?category=apikeys&limit=50");
        var events = (await ev.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("events").EnumerateArray().ToList();
        events.Select(e => e.GetProperty("action").GetString()).ShouldContain("apikey.reveal");
    }

    [Fact]
    public async Task Disable_key_persiste_isActive_false()
    {
        await LoginAsync();
        var k = await CreateKey("toggle-key");
        var id = k.RootElement.GetProperty("key").GetProperty("id").GetString()!;
        (await Client.PutAsJsonAsync($"api/keys/{id}", new { isActive = false }))
            .EnsureSuccessStatusCode();

        var r = await Client.GetAsync("api/keys");
        var key = (await r.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("keys").EnumerateArray()
            .First(e => e.GetProperty("id").GetString() == id);
        key.GetProperty("isActive").GetBoolean().ShouldBeFalse();
    }
}

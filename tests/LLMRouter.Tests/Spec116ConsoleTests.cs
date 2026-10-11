using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-116 (fatia console): contrato do /api/logs/console usado pelo painel dark.</summary>
public class Spec116ConsoleTests : WebAppTestBase
{
    [Fact]
    public async Task Console_tail_retorna_items_com_level_e_message()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/logs/console?tail=50");
        r.EnsureSuccessStatusCode();
        var items = (await r.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("items").EnumerateArray().ToList();
        items.ShouldAllBe(i => HasLevelMsg(i));
        items.ShouldAllBe(i => new[] { "error", "warning", "info" }.Contains(i.GetProperty("level").GetString()));
    }

    [Fact]
    public async Task Console_level_filter_so_retorna_o_nivel_pedido()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/logs/console?tail=100&level=info");
        r.EnsureSuccessStatusCode();
        var items = (await r.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("items").EnumerateArray().ToList();
        items.ShouldAllBe(i => i.GetProperty("level").GetString() == "info");
    }

    [Fact]
    public async Task Console_tail_limitado_a_500()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/logs/console?tail=99999");
        r.EnsureSuccessStatusCode();
        var items = (await r.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBeLessThanOrEqualTo(500);
    }

    private static bool HasLevelMsg(JsonElement i) =>
        i.TryGetProperty("level", out _) && i.TryGetProperty("message", out _);
}

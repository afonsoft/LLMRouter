using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-124: home finishing parity — announcement slot via settings,
/// topology data from provider-connections.</summary>
public class Spec124HomeTests : WebAppTestBase
{
    private async Task<JsonElement> GetJson(string url)
    {
        var r = await Client.GetAsync(url);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
    }

    [Fact]
    public async Task Announcement_round_trip_via_settings()
    {
        await LoginAsync();
        var put = await Client.PutAsJsonAsync("api/settings", new { announcement = "Maintenance Sunday 02:00 UTC" });
        put.EnsureSuccessStatusCode();

        var s = await GetJson("api/settings");
        s.GetProperty("data").GetProperty("announcement").GetString()
            .ShouldBe("Maintenance Sunday 02:00 UTC");

        // clearing hides the banner
        await Client.PutAsJsonAsync("api/settings", new { announcement = "" });
        var s2 = await GetJson("api/settings");
        s2.GetProperty("data").GetProperty("announcement").GetString().ShouldBe("");
    }

    [Fact]
    public async Task Announcement_object_form_com_text_e_enabled()
    {
        await LoginAsync();
        await Client.PutAsJsonAsync("api/settings", new
        {
            announcement = new { enabled = true, text = "new combo released" },
        });
        var s = await GetJson("api/settings");
        var an = s.GetProperty("data").GetProperty("announcement");
        an.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        an.GetProperty("text").GetString().ShouldBe("new combo released");
    }

    [Fact]
    public async Task Provider_connections_trazem_campos_da_topologia()
    {
        await LoginAsync();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LLMRouter.Core.Data.LlmRouterDbContext>();
        db.ProviderConnections.Add(new LLMRouter.Core.Data.ProviderConnection
        {
            Id = "c1", Provider = "openai", AuthType = "api_key",
            Name = "test", IsActive = true, Data = "{}",
        });
        await db.SaveChangesAsync();

        var el = await GetJson("api/provider-connections");
        var conns = el.GetProperty("connections");
        conns.GetArrayLength().ShouldBeGreaterThan(0);
        var c = conns[0];
        c.TryGetProperty("provider", out _).ShouldBeTrue();
        c.TryGetProperty("isActive", out _).ShouldBeTrue();
    }
}

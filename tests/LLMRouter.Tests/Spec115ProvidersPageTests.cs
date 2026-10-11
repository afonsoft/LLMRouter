using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-115: contratos da página de providers — categorias, fields dos tiles e conexões.</summary>
public class Spec115ProvidersPageTests : WebAppTestBase
{
    [Fact]
    public async Task Providers_retorna_categorias_conhecidas()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/providers");
        r.EnsureSuccessStatusCode();
        var cats = (await r.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("categories").EnumerateArray()
            .Select(c => c.GetProperty("kind").GetString()).ToList();
        cats.ShouldContain("APIKEY_PROVIDERS");
        cats.ShouldContain("OAUTH_PROVIDERS");
        // a UI mapeia esses enums crus pra labels amigáveis — garante que todos são mapeáveis
        string[] known =
        [
            "APIKEY_PROVIDERS", "OAUTH_PROVIDERS", "NOAUTH_PROVIDERS", "LOCAL_PROVIDERS",
            "UPSTREAM_PROXY_PROVIDERS", "WEB_COOKIE_PROVIDERS", "AUDIO_ONLY_PROVIDERS",
            "SEARCH_PROVIDERS", "SYSTEM_PROVIDERS", "CLOUD_AGENT_PROVIDERS",
        ];
        cats.ShouldAllBe(c => known.Contains(c));
    }

    [Fact]
    public async Task Providers_emitem_campos_dos_tiles()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/providers");
        var providers = (await r.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("categories").EnumerateArray()
            .SelectMany(c => c.GetProperty("providers").EnumerateArray()).ToList();
        providers.ShouldNotBeEmpty();
        providers.ShouldAllBe(p =>
            HasProp(p, "id") && HasProp(p, "backend") && HasProp(p, "connected")
            && HasProp(p, "connectionCount"));
    }

    [Fact]
    public async Task Provider_connections_tem_provider_e_isActive_para_expand()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/provider-connections");
        r.EnsureSuccessStatusCode();
        var conns = (await r.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("connections").EnumerateArray().ToList();
        conns.ShouldAllBe(c => HasProp(c, "id") && HasProp(c, "provider") && HasProp(c, "isActive"));
    }

    [Fact]
    public async Task Provider_connection_reflete_no_connectionCount()
    {
        await LoginAsync();
        var post = await Client.PostAsJsonAsync("api/provider-connections",
            new { provider = "groq", name = "spec115-conn", data = new { apiKey = "gsk_spec115test" } });
        post.EnsureSuccessStatusCode();

        var r = await Client.GetAsync("api/providers");
        var groq = (await r.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("categories").EnumerateArray()
            .SelectMany(c => c.GetProperty("providers").EnumerateArray())
            .First(p => p.GetProperty("id").GetString() == "groq");
        groq.GetProperty("connectionCount").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        groq.GetProperty("connected").GetBoolean().ShouldBeTrue();
    }

    private static bool HasProp(JsonElement e, string name) => e.TryGetProperty(name, out _);
}

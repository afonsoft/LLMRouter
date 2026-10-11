using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-122: docs hub parity — categorized tree, titles, locale folder.</summary>
public class Spec122DocsHubTests : WebAppTestBase
{
    private async Task<JsonElement> GetJson(string url)
    {
        var r = await Client.GetAsync(url);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
    }

    private static List<(string Category, List<(string Slug, string Title)> Pages)> Tree(JsonElement root) =>
        root.GetProperty("tree").EnumerateArray().Select(c =>
            (c.GetProperty("category").GetString()!,
             c.GetProperty("pages").EnumerateArray()
                .Select(p => (p.GetProperty("slug").GetString()!, p.GetProperty("title").GetString()!))
                .ToList())).ToList();

    [Fact]
    public async Task Tree_agrupa_por_categorias_na_ordem_upstream()
    {
        await LoginAsync();
        var tree = Tree(await GetJson("api/docs/tree"));
        var cats = tree.Select(t => t.Category).ToList();
        cats.ShouldContain("home");
        cats.ShouldContain("routing");
        cats.ShouldContain("comparison");
        cats.ShouldContain("operations");
        // upstream order: home before operations
        cats.IndexOf("home").ShouldBeLessThan(cats.IndexOf("operations"));
    }

    [Fact]
    public async Task Tree_paginas_tem_slug_e_titulo_do_heading()
    {
        await LoginAsync();
        var tree = Tree(await GetJson("api/docs/tree"));
        var home = tree.First(t => t.Category == "home").Pages;
        home.ShouldContain(p => p.Slug == "en/README" && p.Title.Contains("LLMRouter"));
        var routing = tree.First(t => t.Category == "routing").Pages;
        routing.ShouldContain(p => p.Slug == "en/routing-strategies" && p.Title.Contains("Routing"));
    }

    [Fact]
    public async Task Doc_abre_sob_o_no_da_arvore_e_renderiza_html()
    {
        await LoginAsync();
        var tree = Tree(await GetJson("api/docs/tree"));
        var doc = tree.SelectMany(t => t.Pages).First(p => p.Slug == "en/architecture");
        var r = await Client.GetAsync($"api/docs/{doc.Slug}");
        r.EnsureSuccessStatusCode();
        var el = (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        el.GetProperty("html").GetString().ShouldContain("Architecture");
    }

    [Fact]
    public async Task Tree_lang_ptbr_usa_pasta_espelho()
    {
        await LoginAsync();
        var pt = Tree(await GetJson("api/docs/tree?lang=pt-BR"));
        pt.SelectMany(t => t.Pages).ShouldContain(p => p.Slug.StartsWith("pt-BR/"));
        pt.SelectMany(t => t.Pages).ShouldNotContain(p => p.Slug.StartsWith("en/"));
    }

    [Fact]
    public async Task Featured_cards_e_secoes_tem_alvos_reais_na_arvore()
    {
        await LoginAsync();
        var slugs = Tree(await GetJson("api/docs/tree"))
            .SelectMany(t => t.Pages).Select(p => p.Slug).ToList();
        slugs.ShouldContain("en/README");
        slugs.ShouldContain("en/routing-strategies");
        slugs.ShouldContain("en/configuration");
    }
}

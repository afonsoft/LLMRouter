using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-119: combos + model catalog parity — auto-routing catalog,
/// combo kind surfaced for All/Intelligent/Deterministic tabs, catalog search
/// material, combo rows flagged type=combo.</summary>
public class Spec119CombosModelsTests : WebAppTestBase
{
    private async Task<JsonElement> GetJson(string url)
    {
        var r = await Client.GetAsync(url);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
    }

    [Fact]
    public async Task Auto_catalog_lista_combos_virtuais()
    {
        await LoginAsync();
        var el = await GetJson("api/combos/auto");
        var combos = el.GetProperty("combos").EnumerateArray().ToList();
        combos.ShouldNotBeEmpty();
        combos.ShouldAllBe(x => x.GetProperty("id").GetString()!.StartsWith("auto"));
        combos.ShouldAllBe(x => HasCount(x));
    }

    private static bool HasCount(JsonElement x) =>
        x.TryGetProperty("count", out var cv) && cv.ValueKind == JsonValueKind.Number;

    [Fact]
    public async Task Combos_retornam_kind_para_tabs()
    {
        await LoginAsync();
        var create = await Client.PostAsJsonAsync("api/combos",
            new { name = "det-1", kind = "round-robin", models = new[] { "openai/gpt-4" } });
        create.StatusCode.ShouldBe(HttpStatusCode.OK);
        var el = await GetJson("api/combos");
        var row = el.GetProperty("combos").EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == "det-1");
        row.GetProperty("kind").GetString().ShouldBe("round-robin");
    }

    [Fact]
    public async Task Models_endpoint_retorna_campos_do_catalogo()
    {
        await LoginAsync();
        var el = await GetJson("api/models");
        el.TryGetProperty("models", out var ms).ShouldBeTrue();
        if (ms.GetArrayLength() > 0)
        {
            var m = ms[0];
            m.TryGetProperty("model", out _).ShouldBeTrue();
            m.TryGetProperty("provider", out _).ShouldBeTrue();
            m.TryGetProperty("capabilities", out _).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Combo_defaults_e_health_para_banners()
    {
        await LoginAsync();
        var cd = await GetJson("api/settings/combo-defaults");
        cd.TryGetProperty("defaultCombo", out _).ShouldBeTrue();
        var ch = await GetJson("api/usage/combo-health");
        ch.TryGetProperty("autopilot", out var ap).ShouldBeTrue();
        ap.TryGetProperty("summary", out var sm).ShouldBeTrue();
        sm.TryGetProperty("issueCount", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Combos_persistem_e_listam()
    {
        await LoginAsync();
        await Client.PostAsJsonAsync("api/combos",
            new { name = "int-1", kind = "cost-optimized", models = new[] { "a/b", "c/d" } });
        var el = await GetJson("api/combos");
        var row = el.GetProperty("combos").EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == "int-1");
        row.GetProperty("kind").GetString().ShouldBe("cost-optimized");
        row.GetProperty("models").GetRawText().ShouldContain("a/b");
    }
}

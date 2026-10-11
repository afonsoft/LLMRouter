using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-121: categorias de CLI no manifest, auto-sync via settings, perfil.</summary>
public class Spec121ProfileCliTests : WebAppTestBase
{
    private async Task<JsonDocument> Tools()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/cli-tools");
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonDocument>())!;
    }

    [Fact]
    public async Task CliTools_retorna_category_valida_para_todas_as_ferramentas()
    {
        var tools = (await Tools()).RootElement.GetProperty("tools").EnumerateArray().ToList();
        tools.Count.ShouldBeGreaterThanOrEqualTo(8);
        tools.ShouldAllBe(t =>
            new[] { "code", "agent", "ide" }.Contains(t.GetProperty("category").GetString()));
    }

    [Fact]
    public async Task CliTools_categorias_cobrem_as_tres_secoes()
    {
        var tools = (await Tools()).RootElement.GetProperty("tools").EnumerateArray().ToList();
        tools.Where(t => t.GetProperty("category").GetString() == "code").ShouldNotBeEmpty();
        tools.Where(t => t.GetProperty("category").GetString() == "agent").ShouldNotBeEmpty();
        tools.Where(t => t.GetProperty("category").GetString() == "ide").ShouldNotBeEmpty();
        tools.First(t => t.GetProperty("id").GetString() == "claude-code")
            .GetProperty("category").GetString().ShouldBe("code");
        tools.First(t => t.GetProperty("id").GetString() == "cursor")
            .GetProperty("category").GetString().ShouldBe("agent");
        tools.First(t => t.GetProperty("id").GetString() == "continue")
            .GetProperty("category").GetString().ShouldBe("ide");
    }

    [Fact]
    public async Task CliTools_detected_e_bool_e_env_e_objeto()
    {
        var tools = (await Tools()).RootElement.GetProperty("tools").EnumerateArray().ToList();
        tools.ShouldAllBe(t =>
            t.GetProperty("detected").ValueKind == JsonValueKind.True
            || t.GetProperty("detected").ValueKind == JsonValueKind.False);
        tools.ShouldAllBe(t => t.GetProperty("env").ValueKind == JsonValueKind.Object);
        tools.ShouldAllBe(t => t.GetProperty("env").EnumerateObject().Count() > 0);
    }

    [Fact]
    public async Task Settings_persiste_cliProfileAutoSync()
    {
        await LoginAsync();
        var put = await Client.PutAsJsonAsync("api/settings", new
        {
            cliProfileAutoSync = new { enabled = true, tools = new[] { "codex", "kiro" } },
        });
        put.EnsureSuccessStatusCode();

        await using var db = Db();
        var row = await db.Settings.FirstAsync(s => s.Id == 1);
        var doc = JsonDocument.Parse(row.Data);
        var cfg = doc.RootElement.GetProperty("cliProfileAutoSync");
        cfg.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        cfg.GetProperty("tools").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["codex", "kiro"]);
    }

    [Fact]
    public async Task CliTool_config_endpoint_continua_funcionando()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/cli-tools/codex/config");
        r.EnsureSuccessStatusCode();
        var doc = (await r.Content.ReadFromJsonAsync<JsonDocument>())!;
        doc.RootElement.EnumerateObject().Count().ShouldBeGreaterThan(0);
    }
}

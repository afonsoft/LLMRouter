using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-061: catálogo de services, adopt detection, provider-expose, flags.</summary>
public class Spec061ServicesTests : WebAppTestBase
{
    [Fact]
    public void Catalogo_tem_os_8_servicos_upstream()
    {
        ServiceManager.Catalog.Select(d => d.Id).ShouldBe(
            ["9router", "bifrost", "cliproxy", "dario", "mux", "openwa", "llmlingua", "redis"], ignoreOrder: true);
        ServiceManager.Catalog.ShouldAllBe(d => d.Port > 0 && d.HealthPath.StartsWith('/'));
    }

    [Fact]
    public async Task Adopt_detection_via_health_stub()
    {
        // sobe um HttpListener na porta do "dario" respondendo /health
        var d = ServiceManager.Find("dario")!;
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{d.Port}/");
        try { listener.Start(); } catch { return; } // sem permissão → skip silencioso
        _ = Task.Run(async () =>
        {
            var c = await listener.GetContextAsync();
            c.Response.StatusCode = 200;
            c.Response.Close();
        });
        (await ServiceManager.AdoptedAsync(d)).ShouldBeTrue();
        listener.Stop();
    }

    [Fact]
    public async Task ProviderExpose_cria_conexao()
    {
        await LoginAsync();
        var r = await Client.PostAsync("/api/services/llmlingua/provider-expose", null);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = Db();
        var c = await db.ProviderConnections.FindAsync("svc-llmlingua");
        c.ShouldNotBeNull();
        c!.Data.ShouldContain("127.0.0.1:8899");
    }

    [Fact]
    public async Task Endpoints_status_flags_e_install()
    {
        await LoginAsync();
        var list = await Client.GetAsync("/api/services");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync()).ShouldContain("llmlingua");

        (await Client.PostAsync("/api/services/mux/install", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.PutAsJsonAsync("/api/services/mux/auto-start", new { enabled = true }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var st = await Client.GetAsync("/api/services/mux/status");
        var json = await JsonDocument.ParseAsync(await st.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("autoStart").GetBoolean().ShouldBeTrue();
        json.RootElement.GetProperty("installed").GetBoolean().ShouldBeTrue();
    }
}

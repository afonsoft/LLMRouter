using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-113: /api/audit/events com ?category= + campo category computado.</summary>
public class Spec113ActivityFeedTests : WebAppTestBase
{
    private async Task Seed()
    {
        await using var db = Db();
        db.AuditEvents.AddRange(
            new AuditEvent { Action = "provider.update", Target = "openai", At = "2026-10-11T10:00:00Z", Actor = "dashboard" },
            new AuditEvent { Action = "combo.create", Target = "free-stack", At = "2026-10-11T10:01:00Z", Actor = "dashboard" },
            new AuditEvent { Action = "userkey.create", Target = "vk-x", At = "2026-10-11T10:02:00Z", Actor = "dashboard" },
            new AuditEvent { Action = "settings.update", At = "2026-10-11T10:03:00Z", Actor = "dashboard" },
            new AuditEvent { Action = "auth.login", At = "2026-10-11T10:04:00Z", Actor = "anonymous" },
            new AuditEvent { Action = "power.restart", At = "2026-10-11T10:05:00Z", Actor = "system" });
        await db.SaveChangesAsync();
    }

    private static List<JsonElement> Events(JsonDocument d) =>
        d.RootElement.GetProperty("events").EnumerateArray().ToList();

    [Fact]
    public async Task Category_providers_filtra_somente_provider()
    {
        await Seed(); await LoginAsync();
        var r = await Client.GetAsync("api/audit/events?category=providers&limit=100");
        r.EnsureSuccessStatusCode();
        var ev = Events(await r.Content.ReadFromJsonAsync<JsonDocument>()!);
        ev.ShouldNotBeEmpty();
        ev.ShouldAllBe(e => e.GetProperty("action").GetString()!.StartsWith("provider"));
    }

    [Fact]
    public async Task Category_auth_inclui_auth_e_user()
    {
        await Seed(); await LoginAsync();
        var r = await Client.GetAsync("api/audit/events?category=auth&limit=100");
        var ev = Events(await r.Content.ReadFromJsonAsync<JsonDocument>()!);
        ev.Select(e => e.GetProperty("action").GetString()).ShouldContain("auth.login");
        ev.ShouldAllBe(e => e.GetProperty("category").GetString() == "auth");
    }

    [Fact]
    public async Task Category_system_retorna_nao_categorizados()
    {
        await Seed(); await LoginAsync();
        var r = await Client.GetAsync("api/audit/events?category=system&limit=100");
        var ev = Events(await r.Content.ReadFromJsonAsync<JsonDocument>()!);
        ev.ShouldNotBeEmpty();
        ev.ShouldAllBe(e => e.GetProperty("category").GetString() == "system");
        ev.Select(e => e.GetProperty("action").GetString()).ShouldContain("power.restart");
        ev.ShouldAllBe(e => !e.GetProperty("action").GetString()!.StartsWith("provider"));
    }

    [Fact]
    public async Task Sem_category_retorna_todos_com_categoria_computada()
    {
        await Seed(); await LoginAsync();
        var r = await Client.GetAsync("api/audit/events?limit=100");
        var ev = Events(await r.Content.ReadFromJsonAsync<JsonDocument>()!);
        ev.Count.ShouldBeGreaterThanOrEqualTo(6);
        ev.First(e => e.GetProperty("action").GetString() == "combo.create")
            .GetProperty("category").GetString().ShouldBe("combos");
        ev.First(e => e.GetProperty("action").GetString() == "userkey.create")
            .GetProperty("category").GetString().ShouldBe("apikeys");
    }

    [Fact]
    public async Task Category_desconhecida_retorna_vazio()
    {
        await LoginAsync();
        var r = await Client.GetAsync("api/audit/events?category=bogus");
        r.EnsureSuccessStatusCode();
        Events(await r.Content.ReadFromJsonAsync<JsonDocument>()!).ShouldBeEmpty();
    }
}

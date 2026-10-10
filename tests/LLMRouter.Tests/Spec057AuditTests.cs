using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-057: auditEvents table, mcpToolCalls trilha, compliance export.</summary>
public class Spec057AuditTests : WebAppTestBase
{
    [Fact]
    public async Task LoginFalha_gera_evento_auditado()
    {
        // first-run aceita qualquer senha — fixa via change-password para poder falhar
        (await Client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .EnsureSuccessStatusCode();
        (await Client.PostAsJsonAsync("/api/auth/change-password", new { newPassword = "test1234" }))
            .EnsureSuccessStatusCode();
        var fail = await Client.PostAsJsonAsync("/api/auth/login", new { password = "errada" });
        fail.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using var db = Db();
        var ev = await db.AuditEvents.Where(e => e.Action == "auth.login.fail").ToListAsync();
        ev.ShouldNotBeEmpty();
        ev.Last().Actor.ShouldBe("anonymous");
        (await db.AuditEvents.AnyAsync(e => e.Action == "auth.login")).ShouldBeTrue();
    }

    [Fact]
    public async Task KeyReveal_gera_evento_auditado()
    {
        await LoginAsync();
        var create = await Client.PostAsJsonAsync("/api/keys", new { name = "k-audit" });
        create.EnsureSuccessStatusCode();
        var keyJson = await JsonDocument.ParseAsync(await create.Content.ReadAsStreamAsync());
        var kid = keyJson.RootElement.GetProperty("key").GetProperty("id").GetString()!;

        (await Client.PostAsync($"/api/keys/{kid}/reveal", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = Db();
        (await db.AuditEvents.AnyAsync(e => e.Action == "apikey.create")).ShouldBeTrue();
        (await db.AuditEvents.AnyAsync(e => e.Action == "apikey.reveal")).ShouldBeTrue();
    }

    [Fact]
    public async Task McpToolCall_grava_linha_na_trilha()
    {
        await LoginAsync();
        var rpc = new { jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "providers.list", arguments = new { } } };
        var r = await Client.PostAsJsonAsync("/mcp", rpc);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = Db();
        var calls = await db.McpToolCalls.Where(c => c.Tool == "providers.list").ToListAsync();
        calls.ShouldNotBeEmpty();
        calls.Last().Ok.ShouldBeTrue();
        calls.Last().ArgsHash.ShouldNotBeNullOrEmpty();

        var audit = await Client.GetAsync("/api/mcp/audit?tool=providers.list");
        (await audit.Content.ReadAsStringAsync()).ShouldContain("providers.list");
        var stats = await Client.GetAsync("/api/mcp/audit/stats");
        (await stats.Content.ReadAsStringAsync()).ShouldContain("\"total\":1");
    }

    [Fact]
    public async Task ComplianceExport_filtra_por_action_e_exporta_csv()
    {
        await LoginAsync(); // gera auth.login
        var purge = await Client.PostAsync("/api/settings/purge/usage", null);
        ((int)purge.StatusCode).ShouldBeLessThan(500);

        var json = await Client.GetAsync("/api/compliance/audit-log?action=auth.login&format=json");
        var doc = await JsonDocument.ParseAsync(await json.Content.ReadAsStreamAsync());
        foreach (var e in doc.RootElement.GetProperty("events").EnumerateArray())
            e.GetProperty("action").GetString().ShouldBe("auth.login");

        var csv = await Client.GetAsync("/api/compliance/audit-log?format=csv");
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var body = await csv.Content.ReadAsStringAsync();
        body.ShouldContain("at,actor,action,target,ip,meta");
        body.ShouldContain("auth.login");
    }
}

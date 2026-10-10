using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-065: bots — telegram reply, copilot stub, dahl tokens, issue-agent.</summary>
public sealed class Spec065BotsTests : WebAppTestBase
{
    [Fact]
    public async Task TelegramComandoDeveResponderStatus()
    {
        using var db = Db();
        var reply = await LLMRouter.Core.Bots.BotCore.ReplyAsync(db, "/status");
        reply.ShouldContain("LLMRouter ok");
        (await LLMRouter.Core.Bots.BotCore.ReplyAsync(db, "/nada")).ShouldContain("Comandos");
    }

    [Fact]
    public async Task CopilotDeveResponderComTraco()
    {
        using var db = Db();
        var r = await LLMRouter.Core.Bots.BotCore.CopilotAsync(db, "qual o status e uso?");
        r.GetProperty("answer").GetString()!.ShouldContain("LLMRouter ok");
        r.GetProperty("tools").EnumerateArray().Select(x => x.GetString()).ShouldContain("status.get");
    }

    [Fact]
    public async Task DahlTokenDeveVerificar()
    {
        using var db = Db();
        var t = await LLMRouter.Core.Bots.BotCore.DahlIssueAsync(db, "fx");
        (await LLMRouter.Core.Bots.BotCore.DahlVerifyAsync(db, t)).ShouldBeTrue();
        (await LLMRouter.Core.Bots.BotCore.DahlVerifyAsync(db, "dahl_falso")).ShouldBeFalse();
    }

    [Fact]
    public async Task EndpointsDevemResponder()
    {
        var client = Client;
        await LoginAsync();
        var r = await client.PostAsJsonAsync("/api/issue-agent/runs", new { source = "manual", issue = "afonsoft/LLMRouter#1" });
        Xunit.Assert.True(r.IsSuccessStatusCode, $"POST runs: {r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        var runs = await client.GetFromJsonAsync<JsonElement>("/api/issue-agent/runs");
        runs.GetProperty("runs").EnumerateArray().ShouldNotBeEmpty();
        var tg = await client.PostAsJsonAsync("/api/telegram/update", new { message = new { chat = new { id = 1 }, text = "/providers" } });
        Xunit.Assert.True(tg.IsSuccessStatusCode, $"POST telegram: {tg.StatusCode} {await tg.Content.ReadAsStringAsync()}");
        var vnc = await client.GetAsync("/api/vnc-session/status");
        Xunit.Assert.True(vnc.IsSuccessStatusCode, $"GET vnc: {vnc.StatusCode} {await vnc.Content.ReadAsStringAsync()}");
    }
}

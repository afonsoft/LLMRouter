using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-087: MCP tools novos, kv partition, registry caps mapping.</summary>
public sealed class Spec087ParityTests : WebAppTestBase
{
    private async Task<JsonElement> Rpc(string method, object? prms = null)
    {
        var resp = await Client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = prms ?? new { } });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("result");
    }

    private static JsonElement CalledText(JsonElement result) =>
        JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;

    [Fact]
    public async Task NovasToolsDevemSerChamaveis()
    {
        await LoginAsync();
        var tools = (await Rpc("tools/list")).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToHashSet();
        foreach (var n in new[] { "jobs.list", "jobs.run", "evals.list", "routing.explain", "memory.reindex", "quota.preview", "quota.pools", "keys.usage-limits", "discovery.scan", "analytics.combo-health" })
            tools.ShouldContain(n);

        CalledText(await Rpc("tools/call", new { name = "jobs.list", arguments = new { } })).TryGetProperty("jobs", out _).ShouldBeTrue();
        CalledText(await Rpc("tools/call", new { name = "quota.preview", arguments = new { key = "k1" } })).GetProperty("dailyUsed").GetInt64().ShouldBe(0);
        CalledText(await Rpc("tools/call", new { name = "routing.explain", arguments = new { model = "ollama-cloud/minimax-m3" } })).GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()).ShouldContain("vision");
        CalledText(await Rpc("tools/call", new { name = "discovery.scan", arguments = new { } })).GetProperty("scanned").GetString().ShouldBe("local");
    }

    [Fact]
    public void CapsDevemMapearSupportsDoRegistry()
    {
        var reg = Factory.Services.GetRequiredService<LLMRouter.Core.Registry.ProviderRegistry>();
        var planner = new LLMRouter.Core.Routing.ComboPlanner(reg);
        planner.GetCapabilitiesForModel("ollama-cloud/minimax-m3").ShouldContain("vision");
        var caps = planner.GetCapabilitiesForModel("synthetic/hf:zai-org/GLM-5.2");
        caps.ShouldContain("tools"); caps.ShouldContain("reasoning");
    }

    [Fact]
    public async Task AuditBlobDeveMigrarUmaVez()
    {
        using var db = Db();
        db.Kv.Add(new LLMRouter.Core.Data.KvEntry { Scope = "audit", Key = "log", Value = "[{\"at\":\"2026-01-01\",\"action\":\"x\",\"detail\":\"d\"}]" });
        await db.SaveChangesAsync();
        var n = await LLMRouter.Core.KvPartition.KvPartition.MigrateAuditAsync(db);
        n.ShouldBe(1);
        (await LLMRouter.Core.KvPartition.KvPartition.MigrateAuditAsync(db)).ShouldBe(0);
        (await db.AuditEvents.CountAsync()).ShouldBe(1);
    }
}

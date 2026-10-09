using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-031: plugin hooks — onRequest (setModel/addHeaders) and onResponse (annotate).</summary>
public class PluginHooksTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db()
    {
        var ctx = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        ctx.EnsureCreated();
        return ctx;
    }

    private void SeedPlugins(string json)
    {
        using var db = Db();
        db.Kv.Add(new KvEntry { Scope = "plugins", Key = "registered", Value = json });
        db.SaveChanges();
    }

    [Fact]
    public async Task OnRequest_setModel_and_addHeaders_apply()
    {
        SeedPlugins("""
        [{"id":"p1","name":"rewriter","enabled":true,"hooks":[
            {"on":"onRequest","action":"setModel","value":"gpt-4o"},
            {"on":"onRequest","action":"addHeaders","value":{"X-Trace":"abc"}}]}]
        """);
        using var db = Db();
        var body = new JsonObject { ["model"] = "gpt-3.5" };
        var headers = await PluginHooks.ApplyRequestAsync(db, body);
        body["model"]!.GetValue<string>().ShouldBe("gpt-4o");
        headers["X-Trace"].ShouldBe("abc");
    }

    [Fact]
    public async Task Disabled_plugin_is_skipped()
    {
        SeedPlugins("""[{"id":"p1","name":"off","enabled":false,"hooks":[{"on":"onRequest","action":"setModel","value":"x"}]}]""");
        using var db = Db();
        var body = new JsonObject { ["model"] = "orig" };
        var headers = await PluginHooks.ApplyRequestAsync(db, body);
        body["model"]!.GetValue<string>().ShouldBe("orig");
        headers.ShouldBeEmpty();
    }

    [Fact]
    public void OnResponse_annotate_adds_plugin_names()
    {
        var plugins = JsonNode.Parse("""
        [{"id":"p1","name":"ann","enabled":true,"hooks":[{"on":"onResponse","action":"annotate"}]}]
        """)!.AsArray();
        var resp = new JsonObject { ["model"] = "m" };
        PluginHooks.ApplyResponse(plugins, resp);
        resp["x_plugins"]!.AsArray()[0]!.GetValue<string>().ShouldBe("ann");
    }
}

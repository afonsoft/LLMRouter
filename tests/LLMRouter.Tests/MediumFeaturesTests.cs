using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-095: medium features batch.</summary>
public class MediumFeaturesTests : WebAppTestBase
{
    private async Task<JsonElement> GetJson(string path)
    {
        var r = await Client.GetAsync(path);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Agent_skills_crud_coverage_generate_raw()
    {
        await LoginAsync();

        var gen = await (await Client.PostAsJsonAsync("/api/agent-skills/generate",
            new { agentId = "devin", name = "lint-check", description = "runs lint" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var skillId = gen.GetProperty("id").GetString()!;
        gen.GetProperty("content").GetString()!.ShouldContain("lint-check");

        var post = await Client.PostAsJsonAsync("/api/agent-skills",
            new { agentId = "devin", skillRef = "x", enabled = true });
        post.StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        (await GetJson("/api/agent-skills")).GetProperty("items").GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);
        var cov = await GetJson("/api/agent-skills/coverage");
        cov.GetProperty("coverage").EnumerateArray().ShouldContain(c => c.GetProperty("agentId").GetString() == "devin");

        var raw = await GetJson($"/api/agent-skills/{skillId}/raw");
        raw.GetProperty("content").GetString()!.ShouldContain("# lint-check");

        var put = await Client.PutAsJsonAsync($"/api/agent-skills/{id}", new { enabled = false });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await GetJson($"/api/agent-skills/{id}");
        detail.GetProperty("enabled").GetBoolean().ShouldBeFalse();

        (await Client.DeleteAsync($"/api/agent-skills/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync($"/api/agent-skills/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Acp_agents_crud_and_well_known_card()
    {
        await LoginAsync();
        var post = await Client.PostAsJsonAsync("/api/acp/agents",
            new { name = "peer-agent", endpoint = "https://peer.example/a2a", caps = new[] { "chat" }, enabled = true });
        post.StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        (await GetJson("/api/acp/agents")).GetProperty("agents").GetArrayLength().ShouldBe(1);
        (await Client.PutAsJsonAsync($"/api/acp/agents/{id}", new { enabled = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetJson($"/api/acp/agents/{id}")).GetProperty("enabled").GetBoolean().ShouldBeFalse();

        // well-known card needs no cookie
        var anon = Factory.CreateClient();
        var card = await (await anon.GetAsync("/.well-known/agent-card.json"))
            .Content.ReadFromJsonAsync<JsonElement>();
        card.GetProperty("name").GetString().ShouldBe("LLMRouter");
        card.GetProperty("skills").EnumerateArray()
            .ShouldContain(s => s.GetProperty("id").GetString() == id);

        (await Client.DeleteAsync($"/api/acp/agents/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Modality_bridge_stats_and_runtime()
    {
        await LoginAsync();
        var stats = await GetJson("/api/modality-bridge/stats");
        stats.TryGetProperty("modalities", out _).ShouldBeTrue();
        var rt = await GetJson("/api/modality-bridge/video/runtime");
        rt.GetProperty("workers").GetInt32().ShouldBe(0);
        rt.GetProperty("queueDepth").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Memory_item_round_trip()
    {
        await LoginAsync();
        var put = await Client.PutAsJsonAsync("/api/memory/m1", new { content = "hello memory", tags = new[] { "x" } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        var got = await GetJson("/api/memory/m1");
        got.GetProperty("value").GetProperty("content").GetString().ShouldBe("hello memory");

        var put2 = await Client.PutAsJsonAsync("/api/memory/m1", new { content = "updated" });
        put2.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetJson("/api/memory/m1")).GetProperty("value").GetProperty("content").GetString().ShouldBe("updated");

        (await Client.DeleteAsync("/api/memory/m1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/api/memory/m1")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Conversation_tree_serializes_seeded_thread()
    {
        await LoginAsync();
        using (var db = Db())
        {
            db.Kv.Add(new KvEntry
            {
                Scope = "conversations", Key = "conv1",
                Value = """{"title":"t","messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"}]}""",
            });
            await db.SaveChangesAsync();
        }
        var tree = await GetJson("/api/conversations/conv1/tree");
        var nodes = tree.GetProperty("nodes").EnumerateArray().ToList();
        nodes.Count.ShouldBe(2);
        nodes[0].GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
        nodes[1].GetProperty("parentId").GetString().ShouldBe("m0");
        nodes[1].GetProperty("content").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task Translator_extras_translate_and_transform_stream()
    {
        await LoginAsync();
        var tr = await (await Client.PostAsJsonAsync("/api/translator/translate",
            new { from = "openai", to = "openai", payload = new { model = "gpt-4o", messages = new[] { new { role = "user", content = "x" } } } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        tr.GetProperty("translated").GetProperty("model").GetString().ShouldBe("gpt-4o");

        var ts = await (await Client.PostAsJsonAsync("/api/translator/transform-stream",
            new { from = "openai", to = "openai", frames = new[] { "{\"a\":1}" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        ts.GetProperty("frames").GetArrayLength().ShouldBe(1);

        // send without url = preview only
        var send = await (await Client.PostAsJsonAsync("/api/translator/send",
            new { from = "openai", to = "openai", payload = new { model = "m" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        send.GetProperty("sent").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Plugins_lifecycle_activate_deactivate_config()
    {
        await LoginAsync();
        (await Client.PostAsJsonAsync("/api/plugins/marketplace/install", new { id = "plug-1" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.PostAsync("/api/plugins/plug-1/deactivate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.PostAsync("/api/plugins/plug-1/activate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Client.PutAsJsonAsync("/api/plugins/plug-1/config", new { opt = "v1" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var cfg = await GetJson("/api/plugins/plug-1/config");
        cfg.GetProperty("opt").GetString().ShouldBe("v1");

        var scan = await (await Client.PostAsync("/api/plugins/scan", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        scan.GetProperty("found").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Skills_detail_combos_metrics_evals()
    {
        await LoginAsync();
        using (var db = Db())
        {
            db.Combos.Add(new Combo { Id = "c1", Name = "combo-1", Kind = "round-robin", Models = """["openai/gpt-9"]""" });
            db.EvalSuites.Add(new EvalSuite { Id = "s1", Name = "suite-1" });
            db.EvalRuns.Add(new EvalRun { Id = "r1", SuiteId = "s1" });
            await db.SaveChangesAsync();
        }

        var cm = await GetJson("/api/combos/metrics");
        cm.GetProperty("combos").EnumerateArray().ShouldContain(c => c.GetProperty("id").GetString() == "c1");

        var evals = await GetJson("/api/evals");
        evals.GetProperty("evals").EnumerateArray().ShouldContain(e => e.GetProperty("id").GetString() == "s1");
        (await GetJson("/api/evals/s1")).GetProperty("runs").GetInt32().ShouldBe(1);

        // skills/{id} 404 when the skill dir doesn't exist
        (await Client.GetAsync("/api/skills/no-such-skill")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Db_backups_docs_search_context_default()
    {
        await LoginAsync();
        var backups = await GetJson("/api/db-backups");
        backups.TryGetProperty("files", out _).ShouldBeTrue();

        var anon = Factory.CreateClient();
        var search = await (await anon.GetAsync("/docs/api/search?q=combos"))
            .Content.ReadFromJsonAsync<JsonElement>();
        search.GetProperty("results").EnumerateArray().ShouldContain(r => r.GetProperty("doc").GetString() == "combos");

        (await Client.PutAsJsonAsync("/api/context/combos/default", new { combo = "combo-1" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetJson("/api/context/combos/default")).GetProperty("combo").GetString().ShouldBe("combo-1");

        var verify = await GetJson("/api/compression/compare/verify?id=none");
        verify.GetProperty("verified").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Analytics_init_assess_ratelimit_resilience_version_metrics()
    {
        await LoginAsync();
        using (var db = Db())
        {
            db.UsageHistory.Add(new UsageRecord
            {
                Timestamp = DateTimeOffset.UtcNow.ToString("o"), Provider = "openai", Model = "gpt-9",
                Endpoint = "/v1/chat/completions", Status = "ok",
            });
            await db.SaveChangesAsync();
        }

        (await GetJson("/api/analytics/auto-routing")).GetProperty("decisions").GetArrayLength().ShouldBe(1);
        var div = await GetJson("/api/analytics/diversity");
        div.GetProperty("providers").GetInt32().ShouldBe(1);

        (await GetJson("/api/init")).GetProperty("initialized").GetBoolean().ShouldBeTrue();
        (await GetJson("/api/assess")).GetProperty("ok").GetBoolean().ShouldBeTrue();
        var rl = await GetJson("/api/rate-limit");
        rl.TryGetProperty("rules", out _).ShouldBeTrue();
        (await GetJson("/api/resilience")).GetProperty("ok").GetBoolean().ShouldBeTrue();
        var conns = await GetJson("/api/resilience/connections");
        conns.TryGetProperty("connections", out _).ShouldBeTrue();
        var ver = await GetJson("/api/system/version");
        ver.GetProperty("version").GetString().ShouldBe("0.1");
        (await GetJson("/api/provider-metrics")).GetProperty("providers").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Provider_models_validate_add_and_nodes_ssrf_guard()
    {
        await LoginAsync();
        var add = await (await Client.PostAsJsonAsync("/api/provider-models/validate-and-add",
            new { provider = "openai", model = "gpt-99" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        add.GetProperty("added").GetBoolean().ShouldBeTrue();
        // idempotent
        var again = await (await Client.PostAsJsonAsync("/api/provider-models/validate-and-add",
            new { provider = "openai", model = "gpt-99" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("added").GetBoolean().ShouldBeFalse();

        var bad = await (await Client.PostAsJsonAsync("/api/provider-nodes/validate",
            new { url = "http://127.0.0.1:8080/x" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        bad.GetProperty("valid").GetBoolean().ShouldBeFalse();
        var meta = await (await Client.PostAsJsonAsync("/api/provider-nodes/validate",
            new { url = "http://169.254.169.254/latest" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        meta.GetProperty("valid").GetBoolean().ShouldBeFalse();
        var ok = await (await Client.PostAsJsonAsync("/api/provider-nodes/validate",
            new { url = "https://api.example.com/v1" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        ok.GetProperty("valid").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Free_tier_summary_and_video_extract()
    {
        await LoginAsync();
        var ft = await GetJson("/api/free-tier/summary");
        ft.TryGetProperty("count", out _).ShouldBeTrue();

        // video extract: requires jobId or url
        (await Client.PostAsJsonAsync("/api/modality-bridge/video/extract", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var ex = await (await Client.PostAsJsonAsync("/api/modality-bridge/video/extract", new { jobId = "job-1" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        ex.GetProperty("found").GetBoolean().ShouldBeFalse();
        var dd = await GetJson("/api/modality-bridge/video/drilldown?jobId=job-1");
        dd.GetProperty("found").GetBoolean().ShouldBeFalse();
    }
}

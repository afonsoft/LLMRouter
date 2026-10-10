using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-094: second MCP tools batch — routing dry-runs, setters,
/// pool/cache/db ops, metrics, ccr, misc.</summary>
public class McpToolsExtraTests : WebAppTestBase
{
    private async Task<JsonElement> Rpc(string method, object? prms = null)
    {
        var resp = await Client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 1, method, @params = prms ?? new { },
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("result");
    }

    private async Task<JsonElement> Call(string name, object args) =>
        JsonDocument.Parse((await Rpc("tools/call", new { name, arguments = args }))
            .GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;

    private void SeedRoute()
    {
        using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", AuthType = "apikey", Name = "c1", IsActive = true,
            Data = """{"baseUrl":"https://x.test","apiKey":"k"}""", CreatedAt = "x", UpdatedAt = "x",
        });
        db.Combos.Add(new Combo
        {
            Id = "cb1", Name = "fast", Models = """["openai/gpt-x"]""",
            Kind = "round-robin", CreatedAt = "x", UpdatedAt = "x",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Tools_list_includes_extra_batch_names()
    {
        await LoginAsync();
        var names = (await Rpc("tools/list")).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToHashSet();
        foreach (var expected in new[]
        {
            "routing.routeRequest", "routing.simulateRoute", "routing.bestCombo",
            "settings.setRoutingStrategy", "settings.setBudgetGuard",
            "pool.health", "cache.stats", "db.health", "tool.search",
            "metrics.provider", "models.catalog", "pricing.sync",
            "memory.clear", "validation.echo", "ccr.store", "ccr.retrieve",
            "skills.execute", "skills.executions", "oneproxy.current",
            "media.music", "web.search", "browser.poolStatus",
        })
            names.ShouldContain(expected);
    }

    [Fact]
    public async Task Route_request_resolves_combo_to_provider()
    {
        SeedRoute();
        await LoginAsync();
        var r = await Call("routing.routeRequest", new { model = "fast" });
        r.GetProperty("resolved").GetProperty("provider").GetString().ShouldBe("openai");
        r.GetProperty("resolved").GetProperty("model").GetString().ShouldBe("gpt-x");
        var sim = await Call("routing.simulateRoute", new { model = "fast" });
        sim.GetProperty("candidates").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Set_tools_write_settings_blob()
    {
        await LoginAsync();
        (await Call("settings.setRoutingStrategy", new { strategy = "nadir" }))
            .GetProperty("ok").GetBoolean().ShouldBeTrue();
        (await Call("settings.setBudgetGuard", new { dailyUsd = 5.5, warnAt = 80 }))
            .GetProperty("ok").GetBoolean().ShouldBeTrue();
        var s = await Call("settings.get", new { });
        s.GetProperty("adaptiveRouting").GetProperty("strategy").GetString().ShouldBe("nadir");
        s.GetProperty("budgetGuard").GetProperty("dailyUsd").GetDouble().ShouldBe(5.5);
    }

    [Fact]
    public async Task Pool_tools_reflect_and_reset_sessions()
    {
        using (var db = Db())
        {
            db.EnsureCreated();
            db.SessionPools.Add(new SessionPoolRow
            {
                Id = "p1", Name = "p1", Provider = "openai", CreatedAt = "x", UpdatedAt = "x",
            });
            db.PoolSessions.Add(new PoolSession
            {
                Id = "s1", PoolId = "p1", State = "busy", BusyUntil = "x", CreatedAt = "x",
            });
            db.SaveChanges();
        }
        await LoginAsync();
        var health = await Call("pool.health", new { });
        health.GetProperty("pools")[0].GetProperty("busy").GetInt32().ShouldBe(1);
        (await Call("pool.reset", new { poolId = "p1" }))
            .GetProperty("reset").GetInt32().ShouldBe(1);
        var sessions = await Call("pool.sessions", new { poolId = "p1" });
        sessions.GetProperty("sessions")[0].GetProperty("state").GetString().ShouldBe("idle");
    }

    [Fact]
    public async Task Cache_stats_and_flush_and_db_health()
    {
        await LoginAsync();
        var stats = await Call("cache.stats", new { });
        stats.TryGetProperty("hits", out _).ShouldBeTrue();
        (await Call("cache.flush", new { })).GetProperty("ok").GetBoolean().ShouldBeTrue();
        var health = await Call("db.health", new { });
        health.GetProperty("tables").GetProperty("combos").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Ccr_round_trip()
    {
        await LoginAsync();
        (await Call("ccr.store", new { key = "k1", content = "blob" }))
            .GetProperty("ok").GetBoolean().ShouldBeTrue();
        (await Call("ccr.retrieve", new { key = "k1" }))
            .GetProperty("content").GetString().ShouldBe("blob");
        (await Call("ccr.stats", new { })).GetProperty("blobs").GetInt32().ShouldBe(1);
        (await Call("ccr.delete", new { key = "k1" })).GetProperty("ok").GetBoolean().ShouldBeTrue();
        (await Call("ccr.retrieve", new { key = "k1" }))
            .GetProperty("found").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Memory_clear_requires_confirm()
    {
        await LoginAsync();
        (await Call("memory.clear", new { confirm = false }))
            .GetProperty("ok").GetBoolean().ShouldBeFalse();
        (await Call("memory.clear", new { confirm = true }))
            .GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Catalog_quota_pricing_token_tools()
    {
        using (var db = Db())
        {
            db.EnsureCreated();
            db.SyncedModels.Add(new SyncedModel
            {
                Id = "s1", Provider = "openai", Model = "gpt-9", Available = true,
                LastSyncAt = "x", MaxOutputTokens = 4000,
            });
            db.Kv.Add(new KvEntry { Scope = "quotaPools", Key = "pool-x", Value = "{}" });
            db.SaveChanges();
        }
        await LoginAsync();
        var cat = await Call("models.catalog", new { provider = "openai" });
        cat.GetProperty("models")[0].GetProperty("model").GetString().ShouldBe("gpt-9");
        var quota = await Call("quota.check", new { });
        quota.GetProperty("pools").GetArrayLength().ShouldBe(1);
        (await Call("pricing.sync", new
        {
            items = new[] { new { model = "gpt-9", inputPer1M = 1.0 } },
        })).GetProperty("synced").GetInt32().ShouldBe(1);
        (await Call("token.sync", new { })).TryGetProperty("synced", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Echo_snapshot_and_stub_tools()
    {
        await LoginAsync();
        var echo = await Call("validation.echo", new { ping = "pong" });
        echo.GetProperty("ping").GetString().ShouldBe("pong");
        var snap = await Call("metrics.sessionSnapshot", new { });
        snap.TryGetProperty("settings", out _).ShouldBeTrue();
        (await Call("browser.poolStatus", new { }))
            .GetProperty("enabled").GetBoolean().ShouldBeFalse();
        (await Call("oneproxy.current", new { })).TryGetProperty("current", out _).ShouldBeTrue();
        var ts = await Call("tool.search", new { query = "pool" });
        ts.GetProperty("tools").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Skills_executions_and_metrics_provider()
    {
        using (var db = Db())
        {
            db.EnsureCreated();
            db.SkillExecutions.Add(new SkillExecution
            {
                Skill = "demo", Command = "run", ExitCode = 0, DurationMs = 5, At = "x",
            });
            db.UsageHistory.Add(new UsageRecord
            {
                Provider = "openai", Model = "m1", ApiKey = "k",
                PromptTokens = 1, CompletionTokens = 1,
                LatencyMs = 10, Status = "ok", Timestamp = "x",
            });
            db.SaveChanges();
        }
        await LoginAsync();
        (await Call("skills.executions", new { }))
            .GetProperty("executions").GetArrayLength().ShouldBe(1);
        var m = await Call("metrics.provider", new { });
        m.GetProperty("providers")[0].GetProperty("requests").GetInt64().ShouldBe(1);
    }
}

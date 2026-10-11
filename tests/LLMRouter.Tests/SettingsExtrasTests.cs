using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-096: settings sub-endpoints, auth reset, misc leftovers.</summary>
public class SettingsExtrasTests : WebAppTestBase
{
    private async Task<JsonElement> GetJson(string path)
    {
        var r = await Client.GetAsync(path);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Forgot_password_issues_and_reset_consumes_token()
    {
        await LoginAsync();
        var anon = Factory.CreateClient();
        var f = await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email = "admin@x" });
        f.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await f.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sent").GetBoolean().ShouldBeTrue();

        string token;
        using (var db = Db())
            token = db.Kv.Where(k => k.Scope == "passwordResets").Select(k => k.Key).Single();

        var r = await anon.PostAsJsonAsync("/api/auth/reset-password",
            new { token, password = "brand-new-pw" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);

        // token consumed — second use fails
        (await anon.PostAsJsonAsync("/api/auth/reset-password",
            new { token, password = "x" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // new password authenticates
        var login = await anon.PostAsJsonAsync("/api/auth/login", new { password = "brand-new-pw" });
        (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Maintenance_mode_redirects_ui_but_leaves_api()
    {
        await LoginAsync();
        await Client.PutAsJsonAsync("/api/settings", new { maintenanceMode = true });

        var anon = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false });
        var page = await anon.GetAsync("/dashboard");
        page.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        page.Headers.Location!.ToString().ShouldContain("/maintenance");

        var maint = await anon.GetAsync("/maintenance");
        maint.StatusCode.ShouldBe(HttpStatusCode.OK);

        // API unaffected
        (await GetJson("/api/settings")).TryGetProperty("data", out _).ShouldBeTrue();

        await Client.PutAsJsonAsync("/api/settings", new { maintenanceMode = false });
        (await anon.GetAsync("/dashboard")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ip_filter_round_trips_into_settings_blob()
    {
        await LoginAsync();
        var put = await Client.PutAsJsonAsync("/api/settings/ip-filter",
            new { enabled = true, mode = "deny", cidrs = new[] { "203.0.113.0/24" } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        var got = await GetJson("/api/settings/ip-filter");
        got.GetProperty("value").GetProperty("enabled").GetBoolean().ShouldBeTrue();
        got.GetProperty("value").GetProperty("cidrs")[0].GetString().ShouldBe("203.0.113.0/24");

        // enforcement: a deny-list that doesn't cover the client → still allowed
        (await Client.GetAsync("/api/settings")).StatusCode.ShouldBe(HttpStatusCode.OK);
        // and IpAllowed itself honors the written shape
        using (var db = Db())
        {
            var s = await db.Settings.FirstAsync();
            var sdata = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(s.Data);
            LLMRouter.Core.Routing.RoutingOps.IpAllowed(sdata, "203.0.113.5").ShouldBeFalse();
            LLMRouter.Core.Routing.RoutingOps.IpAllowed(sdata, "127.0.0.1").ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Purge_ops_clear_targets_and_vacuum_runs()
    {
        await LoginAsync();
        using (var db = Db())
        {
            db.AuditEvents.Add(new AuditEvent { Action = "test.seed", At = "now" });
            db.PoolSessions.Add(new PoolSession { Id = "s1", PoolId = "p1" });
            await db.SaveChangesAsync();
        }

        (await Client.PostAsync("/api/settings/purge-logs", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.PostAsync("/api/settings/purge-sessions", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.PostAsync("/api/settings/purge-cache", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.PostAsync("/api/settings/database/vacuum", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using (var db = Db())
        {
            db.AuditEvents.Count().ShouldBe(0);
            db.PoolSessions.Count().ShouldBe(0);
        }
    }

    [Fact]
    public async Task Sub_settings_round_trip()
    {
        await LoginAsync();
        foreach (var (route, payload) in new (string, object)[]
        {
            ("system-prompt", new { text = "be terse" }),
            ("thinking-budget", new { maxTokens = 4096 }),
            ("tier-config", new { tiers = new[] { "free", "pro" } }),
            ("feature-flags", new { beta = true }),
            ("task-routing", new { chat = "combo-1" }),
        })
        {
            (await Client.PutAsJsonAsync($"/api/settings/{route}", payload)).StatusCode.ShouldBe(HttpStatusCode.OK);
            var got = await GetJson($"/api/settings/{route}");
            got.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Object);
        }
        (await GetJson("/api/settings/system-prompt")).GetProperty("value")
            .GetProperty("text").GetString().ShouldBe("be terse");
    }

    [Fact]
    public async Task Authz_inventory_cache_metrics_lkgp_and_cc()
    {
        await LoginAsync();
        var inv = await GetJson("/api/settings/authz-inventory");
        inv.GetProperty("routes").GetArrayLength().ShouldBeGreaterThan(0);

        var cm = await GetJson("/api/settings/cache-metrics");
        cm.TryGetProperty("hits", out _).ShouldBeTrue();

        (await GetJson("/api/settings/lkgp-cache")).TryGetProperty("entries", out _).ShouldBeTrue();
        (await Client.PostAsync("/api/settings/lkgp-cache/clear", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Client.PutAsJsonAsync("/api/settings/qdrant/collections", new { main = "v1" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetJson("/api/settings/qdrant/collections")).GetProperty("main").GetString().ShouldBe("v1");

        (await GetJson("/api/settings/cc-discovery-metrics")).TryGetProperty("metrics", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Cli_tokens_vnc_middleware_hooks_and_codex()
    {
        await LoginAsync();
        using (var db = Db())
        {
            db.Kv.Add(new KvEntry { Scope = "cliTokens", Key = "t1", Value = """{"id":"t1","cli":"opencode","approved":true}""" });
            db.ProviderConnections.Add(new ProviderConnection { Id = "pc1", Provider = "openai", IsActive = true });
            await db.SaveChangesAsync();
        }

        var tokens = await GetJson("/api/cli/tokens");
        tokens.GetProperty("tokens").GetArrayLength().ShouldBe(1);

        // vnc-session + middleware/hooks + cursor-cli are pre-existing surfaces
        var vnc = await GetJson("/api/vnc-session/status");
        vnc.GetProperty("id").GetString().ShouldBe("novnc");

        (await Client.PutAsJsonAsync("/api/middleware/hooks", new { hooks = new[] { "ipFilter", "rateLimit" } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var hooks = await GetJson("/api/middleware/hooks");
        hooks.GetProperty("hooks").EnumerateArray().ShouldContain(h => h.GetString() == "rateLimit");

        var cc = await Client.PostAsync("/api/codex/connect/pc1", null);
        cc.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provider").GetString().ShouldBe("openai");

        var vm = await Client.PostAsync("/api/version-manager/start", null);
        (await vm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("running").GetBoolean().ShouldBeTrue();
        var vmStop = await Client.PostAsync("/api/version-manager/stop", null);
        (await vmStop.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("running").GetBoolean().ShouldBeFalse();

        // cursor-cli passthrough: 400 while settings.data.cursorCli.baseUrl is unset
        (await Client.GetAsync("/api/cursor-cli/status")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // upstream-proxy: non-loopback URL rejected
        (await Client.GetAsync("/api/upstream-proxy/anything?url=https://example.com/x"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var ws = await GetJson("/api/internal/codex-responses-ws");
        ws.GetProperty("available").GetBoolean().ShouldBeFalse();

        // qdrant sub-sections live in the sdata blob
        (await Client.PutAsJsonAsync("/api/settings/qdrant/collections", new { main = "v1" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetJson("/api/settings/qdrant/collections")).GetProperty("main").GetString().ShouldBe("v1");
    }
}

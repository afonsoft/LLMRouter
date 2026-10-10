using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace LLMRouter.Tests;

/// <summary>SPEC-092: management batch — guardrails, mcp mgmt, quota, keys, webhooks, pricing, models.</summary>
public sealed class MgmtBatchTests : WebAppTestBase
{
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    [Fact]
    public async Task Guardrails_crud_and_test_matches()
    {
        await LoginAsync();
        var created = await Client.PostAsJsonAsync("/api/guardrails",
            new { name = "no-secrets", kind = "keyword", pattern = "SECRET_TOKEN", action = "block", target = "request" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var rule = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("rule");
        var id = rule.GetProperty("id").GetString();

        var got = await Client.GetStringAsync($"/api/guardrails/{id}");
        Assert.Equal("no-secrets", JsonDocument.Parse(got).RootElement.GetProperty("name").GetString());

        var test = await Client.PostAsJsonAsync("/api/guardrails/test",
            new { input = "please use SECRET_TOKEN here", target = "request" });
        var matches = JsonDocument.Parse(await test.Content.ReadAsStringAsync()).RootElement;
        Assert.True(matches.GetProperty("wouldBlock").GetBoolean());
        Assert.Contains(matches.GetProperty("matches").EnumerateArray(),
            m => m.GetProperty("ruleId").GetString() == id);

        var del = await Client.DeleteAsync($"/api/guardrails/{id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/guardrails/{id}")).StatusCode);
    }

    [Fact]
    public async Task Guardrail_block_rejects_chat_request()
    {
        await LoginAsync();
        await Client.PostAsJsonAsync("/api/guardrails",
            new { name = "ban-word", kind = "keyword", pattern = "FORBIDDEN_PHRASE", action = "block", target = "request" });
        var r = await Client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "openai/gpt-4o", messages = new[] { new { role = "user", content = "say FORBIDDEN_PHRASE" } } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("guardrail_blocked", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Guardrail_redact_strips_pattern_from_response()
    {
        // upstream stub echoes content including the sensitive word; response-side redact strips it
        await LoginAsync();
        await Client.PostAsJsonAsync("/api/guardrails",
            new { name = "redact-secret", kind = "keyword", pattern = "TOPSECRET", action = "redact", target = "response" });
        using var db = Db();
        var before = await LLMRouter.Server.Endpoints.MgmtBatchEndpoints
            .RedactResponseAsync(db, "{\"choices\":[{\"text\":\"the key is TOPSECRET ok\"}]}");
        Assert.DoesNotContain("TOPSECRET", before);
        Assert.Contains("***", before);
    }

    [Fact]
    public async Task Mcp_status_tools_and_restart()
    {
        await LoginAsync();
        var status = JsonDocument.Parse(await Client.GetStringAsync("/api/mcp/status"));
        Assert.True(status.RootElement.GetProperty("running").GetBoolean());
        Assert.True(status.RootElement.GetProperty("tools").GetInt32() > 0);

        var tools = JsonDocument.Parse(await Client.GetStringAsync("/api/mcp/tools"));
        Assert.True(tools.RootElement.GetProperty("tools").GetArrayLength() > 0);

        var gen0 = status.RootElement.GetProperty("generation").GetInt64();
        var restart = JsonDocument.Parse(await (await Client.PostAsync("/api/mcp/restart", null))
            .Content.ReadAsStringAsync());
        Assert.True(restart.RootElement.GetProperty("restarted").GetBoolean());
        var status2 = JsonDocument.Parse(await Client.GetStringAsync("/api/mcp/status"));
        Assert.True(status2.RootElement.GetProperty("generation").GetInt64() > gen0);
    }

    [Fact]
    public async Task Quota_groups_and_pools_crud_roundtrip()
    {
        await LoginAsync();
        var g = await Client.PostAsJsonAsync("/api/quota/groups",
            new { name = "tier-a", providerIds = new[] { "prov-a" }, windows = new[] { "daily" } });
        Assert.Equal(HttpStatusCode.OK, g.StatusCode);
        var gid = JsonDocument.Parse(await g.Content.ReadAsStringAsync())
            .RootElement.GetProperty("item").GetProperty("id").GetString();

        var list = JsonDocument.Parse(await Client.GetStringAsync("/api/quota/groups"));
        Assert.Contains(list.RootElement.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("id").GetString() == gid);

        var p = await Client.PostAsJsonAsync("/api/quota/pools",
            new { name = "pool-1", capacity = 1000, used = 0 });
        var pid = JsonDocument.Parse(await p.Content.ReadAsStringAsync())
            .RootElement.GetProperty("item").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/quota/pools/{pid}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.DeleteAsync($"/api/quota/pools/{pid}")).StatusCode);
    }

    [Fact]
    public async Task Key_group_permissions_deny_embeddings()
    {
        using var db = Db();
        db.ApiKeys.Add(new ApiKey { Id = "k1", Key = "sk-test-restricted", Name = "restricted", IsActive = true, CreatedAt = Now() });
        db.KeyGroups.Add(new KeyGroup { Id = "grp1", Name = "chat-only", KeysJson = "[\"k1\"]", CreatedAt = Now() });
        db.Kv.Add(new KvEntry { Scope = "keyGroupPerms", Key = "grp1", Value = "{\"scopes\":[\"chat\"]}" });
        await db.SaveChangesAsync();

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
        {
            Content = JsonContent.Create(new { model = "openai/text-embedding-3-small", input = "hi" }),
        };
        req.Headers.Add("Authorization", "Bearer sk-test-restricted");
        var r = await Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("scope_denied", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Key_access_log_and_devices()
    {
        using var db = Db();
        db.ApiKeys.Add(new ApiKey { Id = "k2", Key = "sk-abc", Name = "k2", IsActive = true, CreatedAt = Now() });
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = Now(), Provider = "prov-a", Model = "m1", ConnectionId = "c",
            ApiKey = "sk-abc", Endpoint = "/v1/chat/completions", Status = "ok",
            Meta = "{\"ip\":\"10.0.0.9\"}",
        });
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = Now(), Provider = "prov-a", Model = "m1", ConnectionId = "c",
            ApiKey = "sk-abc", Endpoint = "/v1/embeddings", Status = "ok",
            Meta = "{\"ip\":\"10.0.0.9\"}",
        });
        await db.SaveChangesAsync();
        await LoginAsync();

        var access = JsonDocument.Parse(await Client.GetStringAsync("/api/keys/k2/access"));
        Assert.Equal(2, access.RootElement.GetProperty("calls").GetArrayLength());

        var devices = JsonDocument.Parse(await Client.GetStringAsync("/api/keys/k2/devices"));
        var list = devices.RootElement.GetProperty("devices").EnumerateArray().Select(d => d.GetString()).ToList();
        Assert.Equal(new[] { "10.0.0.9" }, list);
    }

    [Fact]
    public async Task Webhook_detail_test_delivery_and_validate_url()
    {
        await LoginAsync();
        await Client.PostAsJsonAsync("/api/webhooks", new { url = "https://example.com/hook", events = new[] { "usage" } });
        var list = JsonDocument.Parse(await Client.GetStringAsync("/api/webhooks"));
        var wh = list.RootElement.GetProperty("webhooks").EnumerateArray().First();
        var wid = wh.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/webhooks/{wid}")).StatusCode);
        var test = await Client.PostAsync($"/api/webhooks/{wid}/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var deliveries = JsonDocument.Parse(await Client.GetStringAsync($"/api/webhooks/{wid}/deliveries"));
        Assert.True(deliveries.RootElement.GetProperty("deliveries").GetArrayLength() >= 1);

        var bad = await Client.PostAsJsonAsync("/api/webhooks/validate-url", new { url = "http://127.0.0.1:8080/x" });
        var badR = JsonDocument.Parse(await bad.Content.ReadAsStringAsync());
        Assert.False(badR.RootElement.GetProperty("valid").GetBoolean());
        var good = await Client.PostAsJsonAsync("/api/webhooks/validate-url", new { url = "https://hooks.example.com/x" });
        Assert.True(JsonDocument.Parse(await good.Content.ReadAsStringAsync()).RootElement.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Pricing_defaults_models_and_sync_fixture()
    {
        await LoginAsync();
        await Client.PutAsJsonAsync("/api/pricing/defaults", new { inputPer1M = 1.5, outputPer1M = 3.0 });
        var sync = await Client.PostAsJsonAsync("/api/pricing/sync",
            new { items = new[] { new { model = "prov-a/m1", inputPer1M = 2.0, outputPer1M = 4.0 } } });
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await sync.Content.ReadAsStringAsync())
            .RootElement.GetProperty("synced").GetInt32());

        using var db = Db();
        db.SyncedModels.Add(new SyncedModel { Id = "sm1", Provider = "prov-a", Model = "m1", Available = true, LastSyncAt = Now() });
        await db.SaveChangesAsync();
        var models = JsonDocument.Parse(await Client.GetStringAsync("/api/pricing/models"));
        var row = models.RootElement.GetProperty("models").EnumerateArray()
            .First(m => m.GetProperty("model").GetString() == "prov-a/m1");
        Assert.Equal(2.0, row.GetProperty("pricing").GetProperty("inputPer1M").GetDouble());
    }

    [Fact]
    public async Task Model_alias_crud_and_resolution()
    {
        await LoginAsync();
        var cr = await Client.PostAsJsonAsync("/api/models/alias",
            new { alias = "smart", target = "prov-a/gpt-4o" });
        Assert.Equal(HttpStatusCode.OK, cr.StatusCode);
        var list = JsonDocument.Parse(await Client.GetStringAsync("/api/models/alias"));
        Assert.Contains(list.RootElement.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("target").GetString() == "prov-a/gpt-4o");

        using var db = Db();
        var resolved = await LLMRouter.Server.Endpoints.MgmtBatchEndpoints.ResolveAliasAsync(db, "smart");
        Assert.Equal("prov-a/gpt-4o", resolved);
        var untouched = await LLMRouter.Server.Endpoints.MgmtBatchEndpoints.ResolveAliasAsync(db, "other");
        Assert.Equal("other", untouched);
    }

    [Fact]
    public async Task Models_catalog_and_openrouter_shape()
    {
        using var db = Db();
        db.SyncedModels.Add(new SyncedModel { Id = "sm2", Provider = "prov-a", Model = "m9", Available = true, LastSyncAt = Now() });
        await db.SaveChangesAsync();
        await LoginAsync();
        var cat = JsonDocument.Parse(await Client.GetStringAsync("/api/models/catalog"));
        Assert.True(cat.RootElement.TryGetProperty("registry", out _));
        Assert.Contains(cat.RootElement.GetProperty("synced").EnumerateArray(),
            s => s.GetString() == "prov-a/m9");

        var orc = JsonDocument.Parse(await Client.GetStringAsync("/api/models/openrouter-catalog"));
        Assert.Contains(orc.RootElement.GetProperty("data").EnumerateArray(),
            d => d.GetProperty("id").GetString() == "prov-a/m9");
    }

    [Fact]
    public async Task Models_test_all_returns_per_provider_results()
    {
        await LoginAsync();
        var r = await Client.PostAsync("/api/models/test-all", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var results = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.True(results.Count > 0);
        foreach (var item in results)
        {
            Assert.True(item.TryGetProperty("provider", out _));
            Assert.True(item.TryGetProperty("ok", out _));
            Assert.True(item.TryGetProperty("latencyMs", out _));
        }
    }
}

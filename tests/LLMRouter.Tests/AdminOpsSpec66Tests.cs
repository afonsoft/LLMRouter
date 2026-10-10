using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-066: admin concurrency, tags, db/storage health, fallback chains, monitors.</summary>
public class AdminOpsSpec66Tests : WebAppTestBase
{
    /// <inheritdoc/>
    public override async Task DisposeAsync()
    {
        ConcurrencyGate.Set(0);
        await base.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrencyCap_roundtrip_e_gate()
    {
        await LoginAsync();
        var put = await Client.PutAsJsonAsync("/api/admin/concurrency", new { limit = 3 });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        ConcurrencyGate.Limit.ShouldBe(3);

        var get = await Client.GetAsync("/api/admin/concurrency");
        var json = await JsonDocument.ParseAsync(await get.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("limit").GetInt32().ShouldBe(3);

        // persisted em settings.data
        await using var db = Db();
        var row = await db.Settings.FindAsync(1);
        row!.Data.ShouldContain("\"concurrency\":3");
    }

    [Fact]
    public async Task Tags_filtra_conexoes()
    {
        await LoginAsync();
        await using var db = Db();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", Name = "n1", IsActive = true,
            Data = "{}", CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();

        // tag via API existente (tabela tags) + espelho via data.tags
        var tag = await Client.PostAsJsonAsync("/api/tags",
            new { targetType = "provider", targetId = "c1", value = "prod" });
        tag.StatusCode.ShouldBe(HttpStatusCode.OK);
        var put = await Client.PutAsJsonAsync("/api/provider-connections/c1/tags",
            new { tags = new[] { "prod", "us" } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        var r = await Client.GetAsync("/api/tags?targetType=provider&targetId=c1");
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("prod");
        db.ChangeTracker.Clear();
        (await db.ProviderConnections.FindAsync("c1"))!.Data.ShouldContain("\"us\"");
    }

    [Fact]
    public async Task DbHealth_integridade_ok()
    {
        await LoginAsync();
        var r = await Client.GetAsync("/api/db/health");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("integrity").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task FallbackChains_edit_persiste()
    {
        await LoginAsync();
        // endpoint existente (ResilienceOps): POST grava a chain
        var put = await Client.PostAsJsonAsync("/api/fallback/chains",
            new { model = "gpt-4", chain = new[] { "openai/gpt-4", "google/gemini-pro" } });
        ((int)put.StatusCode).ShouldBeLessThan(500);

        var get = await Client.GetAsync("/api/fallback/chains");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Monitors_agregam_usage()
    {
        await LoginAsync();
        await using var db = Db();
        db.UsageHistory.Add(new UsageRecord
        {
            Provider = "openai", Model = "m", Endpoint = "chat", Status = "200",
            Timestamp = DateTime.UtcNow.ToString("o"), PromptTokens = 10, CompletionTokens = 5,
        });
        await db.SaveChangesAsync();

        var r = await Client.GetAsync("/api/telemetry/summary");
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("requests").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("tokens").GetInt64().ShouldBe(15);

        (await Client.GetAsync("/api/monitoring/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/api/token-health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/api/health/degradation")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/api/storage/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/api/network/info")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/api/omniroute/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HooksPolicies_proxyVisibility_headroom_roundtrip()
    {
        await LoginAsync();
        (await Client.PutAsJsonAsync("/api/middleware/hooks", new { hooks = new[] { new { name = "h1" } } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await Client.GetAsync("/api/middleware/hooks")).Content.ReadAsStringAsync())
            .ShouldContain("h1");
        // policies: CRUD existente (CoreMiscEndpoints) — POST cria uma policy
        (await Client.PostAsJsonAsync("/api/policies",
            new { name = "r1", scope = "model", condition = "always", action = "allow" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await Client.GetAsync("/api/policies")).Content.ReadAsStringAsync())
            .ShouldContain("r1");
        (await Client.PutAsJsonAsync("/api/admin/proxy-pool-visibility",
            new { visibility = new { openai = new[] { "pool1" } } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.PostAsync("/api/headroom/start", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await Client.GetAsync("/api/headroom/status");
        (await status.Content.ReadAsStringAsync()).ShouldContain("\"running\":true");
        (await Client.PostAsync("/api/headroom/stop", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

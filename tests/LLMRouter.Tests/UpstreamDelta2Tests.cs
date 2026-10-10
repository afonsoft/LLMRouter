using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Translation;
using LLMRouter.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-093: upstream delta wave-2 — synced caps, reasoning surface,
/// error classification, stop sanitization, uncataloged warn.</summary>
public class UpstreamDelta2Tests : WebAppTestBase
{
    private LlmRouterDbContext SeedCaps()
    {
        var db = Db();
        db.EnsureCreated();
        db.SyncedModels.Add(new SyncedModel
        {
            Id = "s1", Provider = "openai", Model = "gpt-9", Available = true,
            LastSyncAt = "x", MaxOutputTokens = 4000, Vision = true, Tools = true,
            Reasoning = true, ReasoningEfforts = """["low","high"]""",
        });
        db.SyncedModels.Add(new SyncedModel
        {
            Id = "s2", Provider = "openai", Model = "gone", Available = false, LastSyncAt = "x",
        });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task ProviderModels_surfaces_synced_caps_and_efforts()
    {
        await using var db = SeedCaps();
        await LoginAsync();
        var resp = await Client.GetAsync("/v1/providers/openai/models");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = (await resp.Content.ReadFromJsonAsync<JsonObject>())!["data"]!.AsArray();
        var m = data.Select(x => (JsonObject)x!).FirstOrDefault(x => x["id"]?.GetValue<string>() == "gpt-9");
        m.ShouldNotBeNull();
        m["max_output_tokens"]!.GetValue<int>().ShouldBe(4000);
        m["reasoning_efforts"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldBe(["low", "high"]);
    }

    [Fact]
    public async Task ApplySyncedCaps_clamps_max_tokens_to_provider_cap()
    {
        await using var db = SeedCaps();
        var ctx = new DefaultHttpContext();
        var body = new JsonObject { ["model"] = "openai/gpt-9", ["max_tokens"] = 10000 };
        var bytes = await ProviderScopedEndpoints.ApplySyncedCapsAsync(
            ctx, db, new ProviderEntry { Id = "openai" }, body, CancellationToken.None);
        var roundTrip = JsonSerializer.Deserialize<JsonObject>(bytes!)!;
        roundTrip["max_tokens"]!.GetValue<int>().ShouldBe(4000);
    }

    [Fact]
    public async Task ApplySyncedCaps_no_cap_no_fabricated_floor()
    {
        await using var db = SeedCaps();
        var ctx = new DefaultHttpContext();
        var body = new JsonObject { ["model"] = "openai/gpt-9", ["max_completion_tokens"] = 500 };
        var bytes = await ProviderScopedEndpoints.ApplySyncedCapsAsync(
            ctx, db, new ProviderEntry { Id = "openai" }, body, CancellationToken.None);
        var roundTrip = JsonSerializer.Deserialize<JsonObject>(bytes!)!;
        roundTrip["max_completion_tokens"]!.GetValue<int>().ShouldBe(500);
        roundTrip["max_tokens"].ShouldBeNull();
    }

    [Fact]
    public async Task ApplySyncedCaps_warns_on_reasoning_effort_outside_set()
    {
        await using var db = SeedCaps();
        var ctx = new DefaultHttpContext();
        var body = new JsonObject { ["model"] = "openai/gpt-9", ["reasoning_effort"] = "ultra" };
        await ProviderScopedEndpoints.ApplySyncedCapsAsync(
            ctx, db, new ProviderEntry { Id = "openai" }, body, CancellationToken.None);
        ctx.Response.Headers["X-Reasoning-Effort-Warn"].ToString().ShouldContain("ultra");
    }

    [Fact]
    public async Task ComboPlanner_merges_synced_caps_over_registry()
    {
        await using var db = SeedCaps();
        var synced = await ComboPlanner.LoadSyncedCapsAsync(db);
        synced.ShouldContainKey("openai/gpt-9");
        synced.ShouldNotContainKey("openai/gone"); // Available=false filtered
        var caps = new ComboPlanner(new ProviderRegistry())
            .GetCapabilitiesForModel("openai/gpt-9", synced: synced);
        caps.ShouldContain("vision");
        caps.ShouldContain("tools");
        caps.ShouldContain("reasoning");
        caps.ShouldContain("maxOut:4000");
    }

    [Fact]
    public void TranslateSse_reasoning_deltas_become_progress_events()
    {
        var state = new Translators.SseState();
        var ev = Translators.TranslateSse(
            """{"type":"response.reasoning_text.delta","delta":"opaque"}""",
            "responsesApi", "openai", "m", state).ToList();
        ev.Count.ShouldBe(1);
        var delta = JsonNode.Parse(ev[0].Data)!["choices"]![0]!["delta"]!;
        delta["phase"]!.GetValue<string>().ShouldBe("reasoning");
        delta["content"].ShouldBeNull(); // never user-visible text
    }

    [Fact]
    public void TranslateSse_empty_finish_reason_stop_is_valid_turn()
    {
        var state = new Translators.SseState();
        var ev = Translators.TranslateSse(
            """{"type":"response.completed","response":{"usage":{"input_tokens":1,"output_tokens":0,"total_tokens":1}}}""",
            "responsesApi", "openai", "m", state).ToList();
        var choice = JsonNode.Parse(ev[0].Data)!["choices"]![0]!;
        choice["finish_reason"]!.GetValue<string>().ShouldBe("stop");
    }

    [Fact]
    public void DropNullStops_strips_null_entries()
    {
        var arr = JsonNode.Parse("""["a",null,"b"]""");
        var clean = (JsonArray)Translators.DropNullStops(arr)!;
        clean.Count.ShouldBe(2);
        Translators.DropNullStops(JsonNode.Parse("[null]")).ShouldBeNull();
        Translators.DropNullStops(JsonNode.Parse("null")).ShouldBeNull();
        Translators.DropNullStops(JsonNode.Parse("\"x\""))!.GetValue<string>().ShouldBe("x");
    }

    [Fact]
    public void ClassifyResponsesError_maps_error_type_to_status()
    {
        GatewayEndpoints.ClassifyResponsesError(400,
            """{"error":{"type":"rate_limit_error"}}""").ShouldBe(429);
        GatewayEndpoints.ClassifyResponsesError(400,
            """{"error":{"type":"invalid_request_error"}}""").ShouldBe(400);
        GatewayEndpoints.ClassifyResponsesError(500,
            """{"error":{"type":"rate_limit_error"}}""").ShouldBe(500); // 5xx passthrough
        GatewayEndpoints.ClassifyResponsesError(400,
            "not json").ShouldBe(400);
        GatewayEndpoints.ClassifyResponsesError(402,
            """{"error":{"type":"unknown_x"}}""").ShouldBe(402);
    }

    [Fact]
    public async Task Uncataloged_combo_target_does_not_crash_resolution()
    {
        await using var db = SeedCaps();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", AuthType = "apikey", Name = "c1", IsActive = true,
            Data = """{"baseUrl":"https://x.test","apiKey":"k"}""", CreatedAt = "x", UpdatedAt = "x",
        });
        db.Combos.Add(new Combo
        {
            Id = "cb1", Name = "uncat", Models = """["openai/never-heard-of-it"]""",
            Kind = "round-robin", CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
        await LoginAsync();
        // resolution succeeds (model passes through, warn-once is side-effect only);
        // fake upstream fails → 502, never a routing 500
        var resp = await Client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "uncat", messages = new[] { new { role = "user", content = "hi" } } });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }
}

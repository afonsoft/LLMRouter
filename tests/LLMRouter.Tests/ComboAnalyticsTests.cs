using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace LLMRouter.Tests;

/// <summary>SPEC-091: combo/route analytics + budgets.</summary>
public sealed class ComboAnalyticsTests : WebAppTestBase
{
    private static string Now(int backMinutes = 0)
        => DateTime.UtcNow.AddMinutes(-backMinutes).ToString("yyyy-MM-dd HH:mm:ss");

    private UsageRecord SeedUsage(string provider, string model, string status = "ok",
        long latency = 100, double cost = 0.001, int backMinutes = 0, string? meta = null)
    {
        var u = new UsageRecord
        {
            Timestamp = Now(backMinutes), Provider = provider, Model = model,
            ConnectionId = "c1", ApiKey = "k", Endpoint = "/v1/chat/completions",
            PromptTokens = 10, CompletionTokens = 20, Cost = cost,
            LatencyMs = latency, Status = status, Meta = meta,
        };
        using var db = Db();
        db.UsageHistory.Add(u); db.SaveChanges();
        return u;
    }

    private void SeedCombo(string id, string name, params string[] models)
    {
        using var db = Db();
        db.Combos.Add(new Combo
        {
            Id = id, Name = name, Models = JsonSerializer.Serialize(models.ToList()),
            Kind = "round-robin", CreatedAt = Now(60),
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Combo_health_dashboard_rows_include_availability_and_error_rates()
    {
        SeedCombo("cb1", "my-combo", "prov-a/m1", "prov-b/m2");
        SeedUsage("prov-a", "m1", latency: 50);
        SeedUsage("prov-a", "m1", status: "error");
        await LoginAsync();
        var client = Client;
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/usage/combo-health-dashboard"));
        var items = doc.RootElement.GetProperty("items");
        var row = items.EnumerateArray().First(i => i.GetProperty("name").GetString() == "my-combo");
        var models = row.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(2, models.Count);
        var m1 = models.First(m => m.GetProperty("model").GetString() == "prov-a/m1");
        Assert.Equal(2, m1.GetProperty("calls1h").GetInt32());
        Assert.True(m1.GetProperty("errorRate1h").GetDouble() > 0);
    }

    [Fact]
    public async Task Combo_forecast_flags_degrading_model()
    {
        SeedCombo("cb2", "forecast-combo", "prov-a/m1");
        // rising error: only errors in the last hour, all ok before
        SeedUsage("prov-a", "m1", status: "error");
        SeedUsage("prov-a", "m1", status: "error");
        SeedUsage("prov-a", "m1", status: "ok", backMinutes: 180);
        SeedUsage("prov-a", "m1", status: "ok", backMinutes: 180);
        await LoginAsync();
        var client = Client;
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/usage/combo-forecast"));
        var row = doc.RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("name").GetString() == "forecast-combo");
        Assert.Equal("degrading", row.GetProperty("trend").GetString());
        Assert.Contains("prov-a/m1", row.GetProperty("degradingModels").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Scoring_inspector_returns_factor_breakdown()
    {
        SeedCombo("cb3", "inspect-combo", "prov-a/m1");
        SeedUsage("prov-a", "m1", latency: 80);
        await LoginAsync();
        var client = Client;
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/usage/combo-scoring-inspector?comboId=cb3"));
        var row = doc.RootElement.GetProperty("items").EnumerateArray().Single();
        var models = row.GetProperty("models").EnumerateArray().ToList();
        var m = Assert.Single(models);
        var factors = m.GetProperty("factors");
        Assert.True(factors.TryGetProperty("quota", out _));
        Assert.True(factors.TryGetProperty("taskFit", out _));
        Assert.True(factors.TryGetProperty("connectionDensity", out _));
        Assert.True(m.GetProperty("total").GetDouble() > 0);
    }

    [Fact]
    public async Task Combo_trace_and_route_explain_reproduce_logged_decision()
    {
        using var db = Db();
        db.RequestDetails.Add(new RequestDetail
        {
            Id = "req-91", Timestamp = Now(), Provider = "prov-a", Model = "m1",
            ConnectionId = "c1", Status = "ok",
            Data = JsonSerializer.Serialize(new
            {
                combo = "trace-combo",
                routing = new { chosen = "prov-a/m1", reason = "top-score", candidates = new[] { "prov-a/m1", "prov-b/m2" } },
            }),
        });
        db.SaveChanges();
        await LoginAsync();
        var client = Client;
        var trace = JsonDocument.Parse(await client.GetStringAsync("/api/usage/combo-trace/trace-combo"));
        var t = Assert.Single(trace.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("prov-a/m1", t.GetProperty("chosen").GetString());
        Assert.Equal("top-score", t.GetProperty("reason").GetString());

        var ex = JsonDocument.Parse(await client.GetStringAsync("/api/usage/route-explain/req-91"));
        Assert.Equal("prov-a", ex.RootElement.GetProperty("provider").GetString());
        Assert.Equal("top-score", ex.RootElement.GetProperty("decision").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Model_latency_stats_percentiles()
    {
        foreach (var (lat, back) in new[] { (10, 0), (20, 10), (30, 20), (40, 30), (50, 40), (100, 50) })
            SeedUsage("prov-a", "lat-model", latency: lat, backMinutes: back);
        SeedUsage("prov-a", "old-model", latency: 9999, backMinutes: 60 * 48); // outside window
        await LoginAsync();
        var client = Client;
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/usage/model-latency-stats?window=24"));
        var row = doc.RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("model").GetString() == "lat-model");
        Assert.Equal(6, row.GetProperty("calls").GetInt32());
        Assert.True(row.GetProperty("p50").GetDouble() <= 40);
        Assert.True(row.GetProperty("p99").GetDouble() >= 50);
        Assert.DoesNotContain(doc.RootElement.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("model").GetString() == "old-model");
    }

    [Fact]
    public async Task Provider_limits_and_window_costs()
    {
        using var db = Db();
        db.RateLimits.Add(new RateLimit
        {
            Id = "rl-1", Scope = "provider", ScopeValue = "prov-a",
            Rpm = 100, Tpm = 100000, Enabled = true, CreatedAt = Now(60), UpdatedAt = Now(60),
        });
        db.SaveChanges();
        SeedUsage("prov-a", "m1", cost: 0.5);
        SeedUsage("prov-a", "m1", cost: 0.25);
        await LoginAsync();
        var client = Client;
        var lim = JsonDocument.Parse(await client.GetStringAsync("/api/usage/provider-limits"));
        var row = Assert.Single(lim.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("prov-a", row.GetProperty("provider").GetString());
        Assert.Equal(100, row.GetProperty("rpm").GetInt32());
        Assert.True(row.GetProperty("headroomRpm").GetInt32() <= 100);

        var costs = JsonDocument.Parse(await client.GetStringAsync("/api/usage/provider-window-costs?bucket=days"));
        var crow = costs.RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("provider").GetString() == "prov-a");
        Assert.True(crow.GetProperty("cost").GetDouble() >= 0.75);
    }

    [Fact]
    public async Task Budget_bulk_set_then_enforcement_returns_429()
    {
        SeedCombo("cbb", "gated-combo", "prov-a/m1");
        // spend over the cap so the next call 429s
        SeedUsage("prov-a", "m1", cost: 9.99, meta: "{\"comboId\":\"gated-combo\"}");
        await LoginAsync();
        var client = Client;
        var bulk = await client.PostAsJsonAsync("/api/usage/budget/bulk",
            new[] { new { scope = "combo", id = "gated-combo", dailyUsd = 5.0 } });
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);

        var chat = await client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "gated-combo", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.TooManyRequests, chat.StatusCode);
        var body = await chat.Content.ReadAsStringAsync();
        Assert.Contains("budget_exceeded", body);
    }

    [Fact]
    public async Task Cache_health_exposes_counters()
    {
        await LoginAsync();
        var client = Client;
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/usage/cache-health"));
        Assert.True(doc.RootElement.TryGetProperty("hits", out _));
        Assert.True(doc.RootElement.TryGetProperty("misses", out _));
        Assert.True(doc.RootElement.TryGetProperty("kvScopes", out _));
    }
}

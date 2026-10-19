using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Alerts;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-146: alert engine — metric/event rules, lifecycle, delivery dispatch.</summary>
public class AlertsTests : WebAppTestBase
{
    private static string Ts(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss");

    [Fact]
    public async Task RuleCrud_ValidatesAndRoundtrips()
    {
        await LoginAsync();
        var bad = await Client.PostAsJsonAsync("/api/alerts/rules", new { name = "", kind = "metric", expr = "{}" });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var created = await (await Client.PostAsJsonAsync("/api/alerts/rules", new
        {
            name = "err-rate", kind = "metric", severity = "warn",
            expr = """{"metric":"error_rate","op":"gt","threshold":5,"windowMin":15}""",
            channelIds = "[]",
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        id.ShouldStartWith("ar-");

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/alerts/rules");
        list.GetProperty("rules").EnumerateArray().Any(r => r.GetProperty("id").GetString() == id).ShouldBeTrue();

        (await Client.DeleteAsync($"/api/alerts/rules/{id}")).EnsureSuccessStatusCode();
        var after = await Client.GetFromJsonAsync<JsonElement>("/api/alerts/rules");
        after.GetProperty("rules").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task MetricRule_FiresAndResolves()
    {
        await LoginAsync();
        await using (var db = Db())
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 10; i++)
                db.UsageHistory.Add(new UsageRecord
                {
                    Timestamp = Ts(now.AddMinutes(-1)), Model = "m", LatencyMs = 100,
                    Status = i < 8 ? "500" : "200",
                });
            await db.SaveChangesAsync();
            db.AlertRules.Add(new AlertRule
            {
                Id = "ar-t1", Name = "errors", Kind = "metric",
                Expr = """{"metric":"error_rate","op":"gt","threshold":5,"windowMin":15}""",
                CreatedAt = Ts(now), UpdatedAt = Ts(now),
            });
            await db.SaveChangesAsync();
            var r = await AlertEngine.EvaluateAsync(db);
            r.Fired.ShouldBe(1);
            var inst = await db.AlertInstances.SingleAsync();
            inst.State.ShouldBe("firing");
            inst.Detail.ShouldContain("\"error_rate\"");
        }
        // recover — purge history so the window is clean
        await using (var db = Db())
        {
            await db.UsageHistory.ExecuteDeleteAsync();
            var r = await AlertEngine.EvaluateAsync(db);
            r.Resolved.ShouldBe(1);
            (await db.AlertInstances.SingleAsync()).State.ShouldBe("resolved");
        }
    }

    [Fact]
    public async Task EventRule_MatchesAuditActions()
    {
        await LoginAsync();
        await using var db = Db();
        var now = DateTime.UtcNow;
        db.AuditEvents.Add(new AuditEvent { Actor = "a", Action = "guardrail.block", At = now.ToString("O") });
        db.AuditEvents.Add(new AuditEvent { Actor = "a", Action = "guardrail.block", At = now.ToString("O") });
        await db.SaveChangesAsync();
        var rule = new AlertRule
        {
            Id = "ar-ev", Name = "guardrails", Kind = "event",
            Expr = """{"match":"guardrail.","threshold":2,"windowMin":15}""",
            CreatedAt = Ts(now), UpdatedAt = Ts(now),
        };
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync();
        var (breached, _) = await AlertEngine.CheckRuleAsync(db, rule, now);
        breached.ShouldBeTrue();
    }

    [Fact]
    public async Task Deliveries_EnqueueAndSendViaWebhook()
    {
        await LoginAsync();
        await using var db = Db();
        var now = DateTime.UtcNow;
        db.AlertRules.Add(new AlertRule
        {
            Id = "ar-d", Name = "r", Kind = "metric", ChannelIds = """["ac-d"]""",
            Expr = """{"metric":"requests","op":"gt","threshold":0}""",
            CreatedAt = Ts(now), UpdatedAt = Ts(now),
        });
        db.AlertChannels.Add(new AlertChannel
        {
            Id = "ac-d", Name = "wh", Type = "webhook",
            Config = """{"url":"http://127.0.0.1:9/hook"}""",
            CreatedAt = Ts(now), UpdatedAt = Ts(now),
        });
        db.AlertInstances.Add(new AlertInstance
        {
            RuleId = "ar-d", State = "firing", Detail = "{}",
            StartedAt = now.ToString("O"), UpdatedAt = now.ToString("O"),
        });
        await db.SaveChangesAsync();
        var inst = await db.AlertInstances.SingleAsync();
        AlertEngine.EnqueueDeliveries(db, await db.AlertRules.SingleAsync(), inst.Id);
        await db.SaveChangesAsync();
        (await db.AlertDeliveries.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Lifecycle_Transitions()
    {
        await LoginAsync();
        var id = 0L;
        await using (var db = Db())
        {
            var now = DateTime.UtcNow;
            db.AlertRules.Add(new AlertRule
            {
                Id = "ar-l", Name = "r", Kind = "metric", Expr = """{"metric":"requests","op":"gt","threshold":0}""",
                CreatedAt = Ts(now), UpdatedAt = Ts(now),
            });
            var inst = new AlertInstance
            {
                RuleId = "ar-l", State = "firing", Detail = "{}",
                StartedAt = now.ToString("O"), UpdatedAt = now.ToString("O"),
            };
            db.AlertInstances.Add(inst);
            await db.SaveChangesAsync();
            id = inst.Id;
        }
        foreach (var to in new[] { "acked", "snoozed", "escalated" })
        {
            var res = await Client.PostAsJsonAsync($"/api/alerts/instances/{id}/transition",
                to == "snoozed" ? new { to, minutes = 30 } : (object)new { to });
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("state").GetString().ShouldBe(to);
        }
        var resolved = await Client.PostAsJsonAsync($"/api/alerts/instances/{id}/transition", new { to = "resolved" });
        resolved.EnsureSuccessStatusCode();
        // resolved → anything else conflicts
        var back = await Client.PostAsJsonAsync($"/api/alerts/instances/{id}/transition", new { to = "acked" });
        back.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ComputeMetric_Math()
    {
        var lat = new List<long> { 10, 20, 30, 40, 50 };
        var st = new List<string?> { "200", "500", "200", "500", "200" };
        var cost = new List<double> { 1, 2, 3, 4, 5 };
        AlertEngine.ComputeMetric("latency_p50", lat, st, cost).ShouldBe(30);
        AlertEngine.ComputeMetric("latency_p95", lat, st, cost).ShouldBe(50);
        AlertEngine.ComputeMetric("error_rate", lat, st, cost).ShouldBe(40);
        AlertEngine.ComputeMetric("spend", lat, st, cost).ShouldBe(15);
        AlertEngine.ComputeMetric("requests", lat, st, cost).ShouldBe(5);
    }
}

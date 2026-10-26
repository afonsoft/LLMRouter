using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-150: budgets soft/hard/held + overageRequests/grants.</summary>
public class BudgetsTests : WebAppTestBase
{
    private async Task<string> SeedKey()
    {
        await using var db = Db();
        var k = new ApiKey
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Key = $"sk-bud{Guid.NewGuid():N}", Name = "b", IsActive = true, CreatedAt = "x",
        };
        db.ApiKeys.Add(k);
        await db.SaveChangesAsync();
        return k.Key;
    }

    private async Task<string> SeedBudget(string scope, string? ownerId, double? limitUsd, string mode)
    {
        await using var db = Db();
        var b = new Budget
        {
            Id = Guid.NewGuid().ToString("N")[..12], Name = $"b-{mode}",
            Scope = scope, OwnerId = ownerId, LimitUsd = limitUsd,
            Period = "monthly", Mode = mode, Enabled = true,
            CreatedAt = DateTime.UtcNow.ToString("O"),
        };
        db.Budgets.Add(b);
        await db.SaveChangesAsync();
        return b.Id;
    }

    private async Task SeedSpend(string key, double cost)
    {
        await using var db = Db();
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = DateTime.UtcNow.ToString("O"), ApiKey = key, Model = "m",
            Cost = cost, Status = "200",
        });
        await db.SaveChangesAsync();
    }

    private HttpClient Anon() => Factory.CreateClient();

    private HttpRequestMessage ChatReq(string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        { Content = JsonContent.Create(new { model = "m", messages = new[] { new { role = "user", content = "hi" } } }) };
        req.Headers.Authorization = new("Bearer", key);
        return req;
    }

    [Fact]
    public async Task Hard_budget_blocks_with_402()
    {
        await LoginAsync();
        var key = await SeedKey();
        await SeedSpend(key, 5);
        await SeedBudget("key", key, 1.0, "hard");
        var res = await Anon().SendAsync(ChatReq(key));
        res.StatusCode.ShouldBe((HttpStatusCode)402);
        (await res.Content.ReadAsStringAsync()).ShouldContain("budget_exceeded");
    }

    [Fact]
    public async Task Soft_budget_allows()
    {
        await LoginAsync();
        var key = await SeedKey();
        await SeedSpend(key, 5);
        await SeedBudget("key", key, 1.0, "soft");
        var res = await Anon().SendAsync(ChatReq(key));
        res.StatusCode.ShouldNotBe((HttpStatusCode)402);
    }

    [Fact]
    public async Task Held_creates_request_then_approve_opens_grant()
    {
        await LoginAsync();
        var key = await SeedKey();
        await SeedSpend(key, 5);
        var bid = await SeedBudget("key", key, 1.0, "held");

        var res = await Anon().SendAsync(ChatReq(key));
        res.StatusCode.ShouldBe((HttpStatusCode)402);
        (await res.Content.ReadAsStringAsync()).ShouldContain("budget_held");
        res.Headers.TryGetValues("x-budget-request", out var hv).ShouldBeTrue();

        // dedup: mesma chave → mesma request pendente
        var reqs = await Client.GetFromJsonAsync<JsonElement>("/api/budgets/requests?state=pending");
        var pending = reqs.GetProperty("requests").EnumerateArray()
            .Where(r => r.GetProperty("budgetId").GetString() == bid).ToList();
        pending.Count.ShouldBe(1);
        var reqId = pending[0].GetProperty("id").GetInt64();

        // aprova → grant ativo → request passa o gate
        var ap = await Client.PostAsync($"/api/budgets/requests/{reqId}/approve?amount=100&ttlMinutes=60", null);
        ap.EnsureSuccessStatusCode();
        var res2 = await Anon().SendAsync(ChatReq(key));
        res2.StatusCode.ShouldNotBe((HttpStatusCode)402);
    }

    [Fact]
    public async Task Deny_keeps_blocking()
    {
        await LoginAsync();
        var key = await SeedKey();
        await SeedSpend(key, 5);
        await SeedBudget("key", key, 1.0, "held");
        await Anon().SendAsync(ChatReq(key));

        var reqs = await Client.GetFromJsonAsync<JsonElement>("/api/budgets/requests?state=pending");
        var reqId = reqs.GetProperty("requests").EnumerateArray().First().GetProperty("id").GetInt64();
        var dn = await Client.PostAsync($"/api/budgets/requests/{reqId}/deny", null);
        dn.EnsureSuccessStatusCode();

        var res = await Anon().SendAsync(ChatReq(key));
        res.StatusCode.ShouldBe((HttpStatusCode)402);
    }

    [Fact]
    public async Task Expired_grant_no_longer_extends()
    {
        await LoginAsync();
        var key = await SeedKey();
        await SeedSpend(key, 5);
        var bid = await SeedBudget("key", key, 1.0, "hard");
        await using (var db = Db())
        {
            db.OverageGrants.Add(new OverageGrant
            {
                BudgetId = bid, Amount = 100,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1).ToString("O"), // expirado
                CreatedAt = DateTime.UtcNow.ToString("O"),
            });
            await db.SaveChangesAsync();
        }
        var res = await Anon().SendAsync(ChatReq(key));
        res.StatusCode.ShouldBe((HttpStatusCode)402);
    }

    [Fact]
    public async Task Crud_and_counters()
    {
        await LoginAsync();
        var created = await Client.PostAsJsonAsync("/api/budgets",
            new { name = "gw", scope = "gateway", limitUsd = 10.0, period = "daily", mode = "soft" });
        created.EnsureSuccessStatusCode();
        var b = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = b.GetProperty("id").GetString()!;

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/budgets");
        var row = list.GetProperty("budgets").EnumerateArray()
            .First(x => x.GetProperty("id").GetString() == id);
        row.GetProperty("spentUsd").GetDouble().ShouldBeGreaterThanOrEqualTo(0);
        row.GetProperty("grantUsd").GetDouble().ShouldBe(0);

        var bad = await Client.PostAsJsonAsync("/api/budgets",
            new { name = "x", scope = "bogus", limitUsd = 1.0, period = "daily", mode = "soft" });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var del = await Client.DeleteAsync($"/api/budgets/{id}");
        del.EnsureSuccessStatusCode();
    }
}

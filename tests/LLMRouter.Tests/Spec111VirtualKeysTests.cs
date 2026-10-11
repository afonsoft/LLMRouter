using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-111: virtual keys — auth, gates, ledger e self-serve.</summary>
public class Spec111VirtualKeysTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;   // cookie admin
    private readonly HttpClient _vk;       // consumidor de API: sem cookie

    public Spec111VirtualKeysTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _vk = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose(); _vk.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private async Task LoginAsync() =>
        (await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    private async Task<(string userId, string vk)> SeedUserAndKeyAsync(
        string modelsAllow = "[\"*\"]", double budgetUsd = 0)
    {
        var u = await _client.PostAsJsonAsync("/api/users", new { email = "e2e@t.dev", name = "E2E" });
        u.StatusCode.ShouldBe(HttpStatusCode.Created);
        var userId = (await u.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var k = await _client.PostAsJsonAsync("/api/user-keys",
            new { userId, name = "k1", modelsAllow = JsonDocument.Parse(modelsAllow).RootElement, budgetUsd });
        k.StatusCode.ShouldBe(HttpStatusCode.Created);
        var vk = (await k.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        vk.ShouldStartWith("vk-");
        return (userId, vk);
    }

    [Fact]
    public async Task Admin_users_and_keys_crud_with_masked_listing()
    {
        await LoginAsync();
        var (userId, vk) = await SeedUserAndKeyAsync();

        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/user-keys?userId={userId}");
        var masked = list.GetProperty("keys")[0].GetProperty("key").GetString()!;
        masked.ShouldNotBe(vk);
        masked.ShouldContain("…");

        var patch = await _client.PatchAsJsonAsync($"/api/users/{userId}", new { role = "viewer" });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);

        var del = await _client.DeleteAsync($"/api/users/{userId}");
        del.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await _client.GetFromJsonAsync<JsonElement>($"/api/users/{userId}");
        after.GetProperty("isActive").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Virtual_key_authenticates_and_inactive_key_is_rejected()
    {
        await LoginAsync();
        var (_, vk) = await SeedUserAndKeyAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        req.Headers.Add("Authorization", $"Bearer {vk}");
        (await _vk.SendAsync(req)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var keys = await _client.GetFromJsonAsync<JsonElement>("/api/user-keys");
        var kid = keys.GetProperty("keys")[0].GetProperty("id").GetString()!;
        (await _client.DeleteAsync($"/api/user-keys/{kid}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var req2 = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        req2.Headers.Add("Authorization", $"Bearer {vk}");
        (await _vk.SendAsync(req2)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ModelsAllow_blocks_outside_list_with_403()
    {
        await LoginAsync();
        var (_, vk) = await SeedUserAndKeyAsync(modelsAllow: "[\"openai/gpt-4o\"]");

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "anthropic/claude-3", messages = new[] { new { role = "user", content = "hi" } } }),
        };
        req.Headers.Add("Authorization", $"Bearer {vk}");
        var resp = await _vk.SendAsync(req);
        var body403 = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body403);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("model_not_allowed");
    }

    [Fact]
    public async Task Budget_exceeded_returns_402()
    {
        await LoginAsync();
        var (userId, vk) = await SeedUserAndKeyAsync(budgetUsd: 0.01);

        await using (var ctx = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options))
        {
            var k = await ctx.UserKeys.FirstAsync(x => x.UserId == userId);
            k.SpendUsd = 0.02;
            k.SpendPeriodStart = VirtualKeyGate.PeriodStart("none", DateTime.UtcNow);
            await ctx.SaveChangesAsync();
        }

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "openai/gpt-4o", messages = new[] { new { role = "user", content = "hi" } } }),
        };
        req.Headers.Add("Authorization", $"Bearer {vk}");
        var resp = await _vk.SendAsync(req);
        var body402 = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.ShouldBe((HttpStatusCode)402, body402);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("budget_exceeded");
    }

    [Fact]
    public async Task Account_self_serve_with_vk_and_admin_key_forbidden()
    {
        await LoginAsync();
        var (userId, vk) = await SeedUserAndKeyAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/account/me");
        req.Headers.Add("Authorization", $"Bearer {vk}");
        var resp = await _vk.SendAsync(req);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("user").GetProperty("id").GetString().ShouldBe(userId);

        // chave admin (apiKeys sem userId) → 403 no self-serve
        var adminKey = await _client.PostAsJsonAsync("/api/keys", new { name = "adm" });
        var ak = (await adminKey.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("key").GetProperty("key").GetString();
        if (!string.IsNullOrEmpty(ak))
        {
            var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/account/me");
            req2.Headers.Add("Authorization", $"Bearer {ak}");
            (await _vk.SendAsync(req2)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Topup_and_usage_attribution_write_ledger_rows()
    {
        await LoginAsync();
        var (userId, _) = await SeedUserAndKeyAsync();

        var top = await _client.PostAsJsonAsync("/api/credit-ledger/topup", new { userId, amount = 5.0 });
        top.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await top.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("balanceAfter").GetDouble().ShouldBe(5.0);

        // usage com userId/userKeyId → débito + spendUsd
        await using (var ctx = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options))
        {
            var k = await ctx.UserKeys.FirstAsync(x => x.UserId == userId);
            var row = new UsageRecord
            {
                Timestamp = DateTime.UtcNow.ToString("O"), Model = "m", Cost = 1.25,
                UserId = userId, UserKeyId = k.Id,
            };
            ctx.UsageHistory.Add(row);
            await ctx.SaveChangesAsync();
            await UsageSideEffects.ApplyAsync(ctx, row);
            await ctx.SaveChangesAsync();

            var ledger = await ctx.CreditLedger.Where(l => l.UserId == userId && l.Reason == "usage")
                .OrderByDescending(l => l.Id).FirstAsync();
            ledger.Delta.ShouldBe(-1.25);
            ledger.BalanceAfter.ShouldBe(3.75);
            (await ctx.UserKeys.FirstAsync(x => x.Id == k.Id)).SpendUsd.ShouldBe(1.25);
        }
    }

    [Fact]
    public void VirtualKeyGate_model_globs_and_period_reset()
    {
        var uk = new UserKey { ModelsAllow = "[\"openai/*\",\"anthropic/claude-3\"]" };
        VirtualKeyGate.ModelAllowed(uk, "openai/gpt-4o").ShouldBeTrue();
        VirtualKeyGate.ModelAllowed(uk, "anthropic/claude-3").ShouldBeTrue();
        VirtualKeyGate.ModelAllowed(uk, "google/gemini").ShouldBeFalse();
        uk.ModelsAllow = "[]";
        VirtualKeyGate.ModelAllowed(uk, "qualquer").ShouldBeTrue();

        // reset lazy: spend de período anterior não conta
        uk.SpendUsd = 10;
        uk.SpendPeriodStart = "2000-01-01";
        uk.BudgetPeriod = "daily";
        VirtualKeyGate.EffectiveSpend(uk, DateTime.UtcNow).ShouldBe(0);
        VirtualKeyGate.PeriodStart("daily", new DateTime(2026, 10, 11, 5, 0, 0))
            .ShouldBe(VirtualKeyGate.PeriodStart("daily", new DateTime(2026, 10, 11, 23, 0, 0)));
    }

    [Fact]
    public void RateLimiter_user_scope_produces_violation()
    {
        var rl = new RateLimiter();
        var rule = new RateLimit
        {
            Id = "r1", Scope = "user", ScopeValue = "u-1", Rpm = 1, Enabled = true,
        };
        rl.CheckAndConsume(new[] { rule }, "k", null, "m", userId: "u-1").ShouldBeNull();
        var vio = rl.CheckAndConsume(new[] { rule }, "k", null, "m", userId: "u-1");
        vio.ShouldNotBeNull();
        vio!.Scope.ShouldBe("user");
        // outro usuário não é afetado
        rl.CheckAndConsume(new[] { rule }, "k", null, "m", userId: "u-2").ShouldBeNull();
    }
}

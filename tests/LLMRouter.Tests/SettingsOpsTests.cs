using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-046: purge cutoffs, system-prompt injection, auto-disable.</summary>
public class SettingsOpsTests : IDisposable
{
    private sealed class CapturingUpstream : HttpMessageHandler
    {
        public static string? LastBody;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            LastBody = r.Content?.ReadAsStringAsync(ct).Result;
            var body = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public SettingsOpsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => new CapturingUpstream())));
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private HttpClient LoginClient()
    {
        var c = _factory.CreateClient();
        c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    private HttpClient GatewayClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("x-api-key", "sk-rl-ops-test");
        return c;
    }

    private async Task SeedAsync(string settingsJson)
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", Name = "main", IsActive = true,
            Data = """{"baseUrl":"http://fake"}""", CreatedAt = "x", UpdatedAt = "x",
        });
        db.ApiKeys.Add(new ApiKey { Id = "k1", Key = "sk-rl-ops-test", Name = "t", IsActive = true, CreatedAt = "x" });
        if (await db.Settings.FirstOrDefaultAsync() is { } s) s.Data = settingsJson;
        else db.Settings.Add(new SettingRow { Data = settingsJson });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Purge_deletes_only_rows_before_cutoff()
    {
        // keep rows inside the usage-prune retention window (30d) — the
        // hosted JobScheduler prunes older rows when the host first boots
        var ts = (DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss");
        var now = DateTime.UtcNow;
        var cutoff = ts(now.AddDays(-1));
        await SeedAsync("{}");
        await using (var db = Db())
        {
            db.UsageHistory.AddRange(
                new UsageRecord { Timestamp = ts(now.AddDays(-2)), Provider = "p", Model = "m", Status = "200", Tokens = "1" },
                new UsageRecord { Timestamp = ts(now), Provider = "p", Model = "m", Status = "200", Tokens = "1" });
            await db.SaveChangesAsync();
        }
        var c = LoginClient();
        var r = await c.PostAsJsonAsync($"/api/settings/purge/usage-history?before={cutoff}", new { });
        var raw = await r.Content.ReadAsStringAsync();
        r.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        JsonDocument.Parse(raw).RootElement
            .GetProperty("deleted").GetInt32().ShouldBe(1, raw);
        await using (var db = Db())
            (await db.UsageHistory.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task System_prompt_injected_once_upstream()
    {
        await SeedAsync("""{"systemPrompt":{"text":"INJECTED"}}""");
        var r = await GatewayClient().PostAsJsonAsync("/v1/chat/completions", new
        { model = "openai/gpt-4o", messages = new[] { new { role = "user", content = "hi" } }, stream = false });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        CapturingUpstream.LastBody.ShouldContain("INJECTED");
        var doc = JsonDocument.Parse(CapturingUpstream.LastBody!).RootElement;
        doc.GetProperty("messages").EnumerateArray()
            .Count(m => m.GetProperty("content").GetString()!.Contains("INJECTED")).ShouldBe(1);
    }

    [Fact]
    public async Task Existing_system_prompt_is_not_duplicated()
    {
        await SeedAsync("""{"systemPrompt":{"text":"INJECTED"}}""");
        var r = await GatewayClient().PostAsJsonAsync("/v1/chat/completions", new
        { model = "openai/gpt-4o", messages = new[] { new { role = "system", content = "OWN" }, new { role = "user", content = "hi" } }, stream = false });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        CapturingUpstream.LastBody.ShouldNotContain("INJECTED");
    }

    [Fact]
    public async Task Connection_auto_disables_after_threshold_errors()
    {
        await SeedAsync("""{"autoDisableAccounts":{"threshold":2}}""");
        await using (var db = Db())
        {
            var sdata = await Core.Usage.PricingService.SettingsDataAsync(db);
            await Core.Routing.SettingsOps.ReportConnectionAsync(db, sdata, "c1", success: false);
            await Core.Routing.SettingsOps.ReportConnectionAsync(db, sdata, "c1", success: false);
        }
        await using (var db = Db())
        {
            (await db.ProviderConnections.FindAsync("c1"))!.IsActive.ShouldBeFalse();
            // re-enable path: with reenableMinutes=0 nothing re-enables; success resets the counter
        }
    }
}

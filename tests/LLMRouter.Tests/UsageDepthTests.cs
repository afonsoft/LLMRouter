using System.Net;
using LLMRouter.Core.Data;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-090: usage/logs depth endpoints.</summary>
public class UsageDepthTests : WebAppTestBase
{
    private async Task<LlmRouterDbContext> SeedAsync()
    {
        var db = Db();
        db.EnsureCreated();
        db.UsageHistory.AddRange(
            new UsageRecord
            {
                Timestamp = "2026-10-10 12:00:00", Provider = "openai", Model = "gpt-4o",
                ApiKey = "k1", Endpoint = "chat", PromptTokens = 10, CompletionTokens = 5,
                Cost = 0.01, LatencyMs = 100, Status = "ok",
                Meta = """{"requestId":"req-1","proxyId":"px-1","runId":"run-a"}""",
            },
            new UsageRecord
            {
                Timestamp = "2026-10-10 12:00:01", Provider = "openai", Model = "gpt-4o-mini",
                ApiKey = "k1", Endpoint = "chat", PromptTokens = 4, CompletionTokens = 2,
                Cost = 0.002, LatencyMs = 50, Status = "ok",
                Meta = """{"requestId":"req-1","proxyId":"px-1","runId":"run-a"}""",
            },
            new UsageRecord
            {
                Timestamp = "2026-10-10 12:01:00", Provider = "anthropic", Model = "claude-3",
                ApiKey = "k2", Endpoint = "messages", PromptTokens = 20, CompletionTokens = 8,
                Cost = 0.05, LatencyMs = 300, Status = "error", Meta = """{"runId":"run-b"}""",
            });
        db.RequestDetails.Add(new RequestDetail
        {
            Id = "d1", Timestamp = "2026-10-10 12:00:00", Provider = "openai",
            Model = "gpt-4o", Status = "ok", Data = """{"request":{"body":"x"},"response":{"body":"y"}}""",
        });
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "cx1", Provider = "codex-main", AuthType = "apikey", Name = "cx", IsActive = true,
            Data = "{}", CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Call_logs_filters_and_total()
    {
        await SeedAsync();
        await LoginAsync();
        var r = await Client.GetAsync("/api/usage/call-logs?provider=openai&status=ok");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("\"total\":2");
        body.ShouldContain("gpt-4o");
        var e = await Client.GetAsync("/api/usage/call-logs?status=error");
        (await e.Content.ReadAsStringAsync()).ShouldContain("\"total\":1");
    }

    [Fact]
    public async Task Call_log_item_includes_detail()
    {
        var db = await SeedAsync();
        await LoginAsync();
        var id = (await db.UsageHistory.FindAsync(1L)) is null ? 1 : 1;
        var r = await Client.GetAsync($"/api/usage/call-logs/{id}");
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await r.Content.ReadAsStringAsync()).ShouldContain("\"detail\"");
    }

    [Fact]
    public async Task Request_logs_groups_by_request_id()
    {
        await SeedAsync();
        await LoginAsync();
        var r = await Client.GetAsync("/api/usage/request-logs");
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("req-1");
        body.ShouldContain("\"calls\":2");
    }

    [Fact]
    public async Task Proxy_logs_attributes_rows()
    {
        await SeedAsync();
        await LoginAsync();
        var r = await Client.GetAsync("/api/usage/proxy-logs");
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("px-1");
        body.ShouldContain("byProxy");
    }

    [Fact]
    public async Task Logs_export_downloads_jsonl()
    {
        await SeedAsync();
        await LoginAsync();
        var r = await Client.GetAsync("/api/logs/export?format=jsonl");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        r.Content.Headers.ContentDisposition!.FileName.ShouldContain("llmrouter-logs-");
        (await r.Content.ReadAsStringAsync()).ShouldContain("\"d1\"");
    }

    [Fact]
    public async Task Console_tail_with_level_filter()
    {
        Server.Services.ConsoleLogBuffer.Instance.Add("test-line-error-xyz fail");
        Server.Services.ConsoleLogBuffer.Instance.Add("test-line-info-xyz");
        await LoginAsync();
        var r = await Client.GetAsync("/api/logs/console?level=error&tail=50");
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("test-line-error-xyz");
        body.ShouldNotContain("test-line-info-xyz");
    }

    [Fact]
    public async Task By_run_rollup()
    {
        await SeedAsync();
        await LoginAsync();
        var r = await Client.GetAsync("/api/usage/by-run");
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("run-a");
        body.ShouldContain("\"calls\":2");
        var f = await Client.GetAsync("/api/usage/by-run?runId=run-b");
        (await f.Content.ReadAsStringAsync()).ShouldContain("run-b");
    }

    [Fact]
    public async Task Codex_reset_card_lists_family()
    {
        await SeedAsync();
        await LoginAsync();
        var r = await Client.GetAsync("/api/usage/codex-reset-credit");
        (await r.Content.ReadAsStringAsync()).ShouldContain("codex-main");
    }

    [Fact]
    public async Task Om_usage_totals_by_family()
    {
        await SeedAsync();
        await LoginAsync();
        var r = await Client.GetAsync("/api/usage/om-usage");
        (await r.Content.ReadAsStringAsync()).ShouldContain("\"family\":\"chat\"");
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Guardrails;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-107: response-side guardrails — credential/PII scan of the
/// assembled upstream response, modes block|redact|warn|log.</summary>
public class Spec107ResponseGuardTests : IDisposable
{
    private sealed class FakeUpstream : HttpMessageHandler
    {
        public int Calls;
        public string Content = "hello";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            var escaped = JsonEncodedText.Encode(Content).ToString();
            var body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\""
                + escaped + "\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":2}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeUpstream _upstream = new();

    public Spec107ResponseGuardTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => _upstream)));
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private HttpClient Gw()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("x-api-key", "sk-rl-rg-test");
        return c;
    }

    private HttpClient Dash()
    {
        var c = _factory.CreateClient();
        var r = c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result;
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    private async Task SeedAsync(string mode = "log", string threshold = "high", bool enabled = true)
    {
        await using var db = Db();
        db.EnsureCreated();
        if (await db.ProviderConnections.FirstOrDefaultAsync(x => x.Id == "c1") is null)
            db.ProviderConnections.Add(new ProviderConnection
            {
                Id = "c1", Provider = "openai", Name = "main", IsActive = true,
                Data = """{"baseUrl":"http://fake"}""", CreatedAt = "x", UpdatedAt = "x",
            });
        if (await db.ApiKeys.FirstOrDefaultAsync(x => x.Id == "k1") is null)
            db.ApiKeys.Add(new ApiKey { Id = "k1", Key = "sk-rl-rg-test", Name = "t", IsActive = true, CreatedAt = "x" });
        var data = "{\"guardrails\":{\"response\":{\"enabled\":" + enabled.ToString().ToLowerInvariant()
            + ",\"mode\":\"" + mode + "\",\"threshold\":\"" + threshold + "\",\"patterns\":[]}}}";
        if (await db.Settings.FirstOrDefaultAsync() is { } s) s.Data = data;
        else db.Settings.Add(new SettingRow { Data = data });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> Chat(HttpClient c)
        => await c.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "openai/gpt-4o",
            messages = new[] { new { role = "user", content = "hi" } },
        });

    [Fact]
    public async Task Log_mode_passes_through_and_records_meta()
    {
        await SeedAsync(mode: "log");
        _upstream.Content = "Here is your key: sk-1234567890abcdefghijklmnop";
        var r = await Chat(Gw());
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        j.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            .ShouldContain("sk-1234567890abcdefghijklmnop");
        await using var db = Db();
        var u = await db.UsageHistory.OrderByDescending(x => x.Id).FirstAsync();
        u.Meta.ShouldContain("api_key");
        u.Meta.ShouldContain("guardrails");
    }

    [Fact]
    public async Task Redact_mode_rewrites_body()
    {
        await SeedAsync(mode: "redact");
        _upstream.Content = "Use sk-1234567890abcdefghijklmnop to auth, mail me at bob@example.com";
        var r = await Chat(Gw());
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        var content = j.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        content.ShouldNotContain("sk-1234567890abcdefghijklmnop");
        content.ShouldContain("[REDACTED:api_key]");
        content.ShouldContain("[REDACTED:email]");
    }

    [Fact]
    public async Task Block_mode_returns_content_filter_error()
    {
        await SeedAsync(mode: "block");
        _upstream.Content = "-----BEGIN PRIVATE KEY----- abcdef";
        var r = await Chat(Gw());
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("content_filter");
        body.ShouldContain("private_key");
        await using var db = Db();
        var u = await db.UsageHistory.OrderByDescending(x => x.Id).FirstAsync();
        u.Status.ShouldBe("400");
    }

    [Fact]
    public async Task Disabled_guardrail_leaves_body_untouched()
    {
        await SeedAsync(enabled: false);
        _upstream.Content = "key sk-1234567890abcdefghijklmnop here";
        var r = await Chat(Gw());
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).ShouldContain("sk-1234567890abcdefghijklmnop");
    }

    [Fact]
    public async Task Threshold_below_severity_does_not_block()
    {
        // email is low severity; threshold high → warn only, still 200
        await SeedAsync(mode: "block", threshold: "high");
        _upstream.Content = "contact bob@example.com";
        var r = await Chat(Gw());
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Guardrails_endpoint_reports_response_section()
    {
        await SeedAsync(mode: "redact");
        var j = await Dash().GetFromJsonAsync<JsonElement>("/api/guardrails");
        j.GetProperty("response").GetProperty("enabled").GetBoolean().ShouldBeTrue();
        j.GetProperty("response").GetProperty("mode").GetString().ShouldBe("redact");
        j.GetProperty("response").GetProperty("builtins").GetArrayLength().ShouldBe(6);
    }

    // --- pure unit checks on ResponseGuard ---

    [Fact]
    public void Detect_finds_credential_shapes_and_pii()
    {
        var dets = ResponseGuard.Detect(
            "jwt eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c "
            + "card 4111 1111 1111 1111 call +1 (555) 123-4567");
        dets.ShouldContain(d => d.Name == "jwt");
        dets.ShouldContain(d => d.Name == "credit_card");
        dets.ShouldContain(d => d.Name == "phone");
        // not-a-card: fails Luhn → no credit_card hit
        ResponseGuard.Detect("number 1234 5678 9012 3456")
            .ShouldNotContain(d => d.Name == "credit_card");
    }
}

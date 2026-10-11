using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-101: /metrics Prometheus scrape + observability settings parse.</summary>
public class ObservabilityTests : IDisposable
{
    private sealed class CapturingUpstream : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null) await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"hi"}}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ObservabilityTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => new CapturingUpstream())));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private async Task LoginAsync() =>
        (await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    private async Task SeedConnAsync()
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "obs-conn-1", Provider = "openai", AuthType = "apikey",
            Name = "obs", IsActive = true,
            Data = """{"baseUrl":"http://stub","apiKey":"sk-fake"}""",
            CreatedAt = "x", UpdatedAt = "x",
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> MetricsAsync(HttpClient? c = null)
    {
        var resp = await (c ?? _client).GetAsync("/metrics");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await resp.Content.ReadAsStringAsync();
        text.ShouldContain("# TYPE");
        return text;
    }

    [Fact]
    public async Task Metrics_endpoint_exposes_requests_total_after_gateway_call()
    {
        await SeedConnAsync();
        await LoginAsync();
        var resp = await _client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "openai/gpt-4o", messages = new[] { new { role = "user", content = "hi" } } });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        var text = await MetricsAsync();
        text.ShouldContain("llmr_requests_total{");
        text.ShouldContain("llmr_request_duration_ms");
        text.ShouldContain("llmr_upstream_ttfb_ms");
        // breaker_state só exporta samples com algum provider não-CLOSED —
        // coberto por Breaker_trip_changes_breaker_state_sample.
    }

    [Fact]
    public async Task Request_counter_labels_carry_provider_model_status()
    {
        await SeedConnAsync();
        await LoginAsync();
        await _client.PostAsJsonAsync("/v1/chat/completions",
            new { model = "openai/gpt-4o", messages = new[] { new { role = "user", content = "hi" } } });

        var text = await MetricsAsync();
        text.ShouldContain("provider=\"openai\"");
        text.ShouldContain("model=\"gpt-4o\"");
        text.ShouldContain("status=\"200\"");
        text.ShouldContain("connection=\"obs-conn-1\"");
        text.ShouldContain("llmr_tokens_total{");
    }

    [Fact]
    public async Task Breaker_trip_changes_breaker_state_sample()
    {
        var prevHerd = LLMRouter.Core.Resilience.ProviderBreaker.HerdWindow;
        try
        {
            LLMRouter.Core.Resilience.ProviderBreaker.ClearAll();
            LLMRouter.Core.Resilience.ProviderBreaker.HerdWindow = TimeSpan.Zero;
            for (var i = 0; i < 12; i++)
                LLMRouter.Core.Resilience.ProviderBreaker.ReportFailure("obs-prov-x", "apikey");
            LLMRouter.Core.Resilience.ProviderBreaker.GetState("obs-prov-x", "apikey")
                .ShouldBe(LLMRouter.Core.Resilience.ProviderBreaker.State.Open);

            // testes paralelos chamam ProviderBreaker.ClearAll() — re-reports
            // antes de cada scrape até o sample aparecer (bounded).
            var found = false;
            string? last = null;
            for (var attempt = 0; attempt < 15 && !found; attempt++)
            {
                for (var i = 0; i < 12; i++)
                    LLMRouter.Core.Resilience.ProviderBreaker.ReportFailure("obs-prov-x", "apikey");
                last = await MetricsAsync();
                found = System.Text.RegularExpressions.Regex.IsMatch(last,
                    @"llmr_breaker_state\{[^}]*provider=""obs-prov-x""[^}]*\} 3");
            }
            if (!found) File.WriteAllText("/tmp/metrics-dump.txt", last ?? "");
            found.ShouldBeTrue("breaker sample never reached /metrics (parallel ClearAll race?)");
        }
        finally
        {
            LLMRouter.Core.Resilience.ProviderBreaker.HerdWindow = prevHerd;
            LLMRouter.Core.Resilience.ProviderBreaker.ClearAll();
        }
    }

    [Fact]
    public void Observability_settings_parse_otlp_and_metrics_auth()
    {
        ObservabilitySettings.Parse(JsonDocument.Parse("{}").RootElement)
            .ShouldBe(ObservabilitySettings.Default);

        var cfg = ObservabilitySettings.Parse(JsonDocument.Parse("""
            {"observability":{"metricsAuth":true,"otlp":{"endpoint":"http://otlp:4317","protocol":"http_protobuf","headers":{"x-key":"v"}}}}
            """).RootElement);
        cfg.MetricsAuth.ShouldBeTrue();
        cfg.Otlp.ShouldNotBeNull();
        cfg.Otlp.Endpoint.ShouldBe("http://otlp:4317");
        cfg.Otlp.Protocol.ShouldBe("http_protobuf");
        cfg.Otlp.Headers["x-key"].ShouldBe("v");

        // malformed/partial nodes fall back to defaults
        ObservabilitySettings.Parse(JsonDocument.Parse("""{"observability":{"otlp":{"protocol":"grpc"}}}""").RootElement)
            .Otlp.ShouldBeNull();
    }

    [Fact]
    public async Task MetricsAuth_flag_gates_metrics_behind_api_key()
    {
        await using (var db = Db())
        {
            db.EnsureCreated();
            db.Settings.Add(new SettingRow { Data = """{"observability":{"metricsAuth":true}}""" });
            db.ApiKeys.Add(new ApiKey { Key = "obs-key-1", Name = "obs", IsActive = true });
            await db.SaveChangesAsync();
        }
        HotCache.Default.InvalidateAll();
        try
        {
            var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            (await anon.GetAsync("/metrics")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            anon.DefaultRequestHeaders.Authorization = new("Bearer", "obs-key-1");
            (await anon.GetAsync("/metrics")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await using var db = Db();
            db.Settings.RemoveRange(db.Settings);
            await db.SaveChangesAsync();
            HotCache.Default.InvalidateAll();
        }
    }
}

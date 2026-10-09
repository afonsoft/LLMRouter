using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-042: log-export destinations — file writes jsonl, webhook posts, filters applied.</summary>
public class LogExportTests : IDisposable
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly string _exportDir = Path.Combine(Path.GetTempPath(), $"llmr-exp-{Guid.NewGuid():N}");
    private readonly RecordingHandler _handler = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public LogExportTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
             .ConfigureServices(s =>
                s.AddHttpClient("logexport").ConfigurePrimaryHttpMessageHandler(() => _handler)));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
        try { Directory.Delete(_exportDir, true); } catch { }
    }

    private async Task LoginAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task SeedDetailsAsync(params (string Provider, string Model, string Status)[] rows)
    {
        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.EnsureCreated();
        foreach (var (p, m, s) in rows)
            db.RequestDetails.Add(new RequestDetail
            {
                Id = Guid.NewGuid().ToString("N"), Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                Provider = p, Model = m, Status = s,
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task File_destination_writes_jsonl()
    {
        await LoginAsync();
        await SeedDetailsAsync(("openai", "gpt-4o", "ok"), ("openai", "gpt-4o", "error"));
        var path = Path.Combine(_exportDir, "out.jsonl");

        var dest = await _client.PostAsJsonAsync("/api/log-export/destinations",
            new { name = "d1", type = "file", config = new { path, fmt = "jsonl" } });
        dest.StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await dest.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("destination").GetProperty("id").GetString()!;

        var run = await _client.PostAsJsonAsync($"/api/log-export/destinations/{id}/run", new { });
        var r = await run.Content.ReadFromJsonAsync<JsonElement>();
        r.GetProperty("status").GetString().ShouldBe("ok");

        var lines = File.ReadAllLines(path);
        lines.Length.ShouldBe(2);
        lines[0].ShouldContain("\"provider\":\"openai\"");
    }

    [Fact]
    public async Task Webhook_posts_payload_and_filters_apply()
    {
        await LoginAsync();
        await SeedDetailsAsync(("openai", "gpt-4o", "ok"), ("anthropic", "claude", "ok"), ("openai", "gpt-4o", "error"));

        var dest = await _client.PostAsJsonAsync("/api/log-export/destinations",
            new
            {
                name = "wh", type = "webhook",
                config = new { url = "http://sink.local/hook" },
                filters = new { provider = "openai", status = "ok" },
            });
        var id = (await dest.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("destination").GetProperty("id").GetString()!;

        var run = await _client.PostAsJsonAsync($"/api/log-export/destinations/{id}/run", new { });
        (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().ShouldBe("ok");

        _handler.Calls.ShouldBe(1);
        _handler.LastBody.ShouldNotBeNull();
        // only the openai/ok row matches the filters
        var lines = _handler.LastBody!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1);
        lines[0].ShouldContain("\"status\":\"ok\"");
    }

    [Fact]
    public async Task Test_endpoint_samples_without_delivering()
    {
        await LoginAsync();
        await SeedDetailsAsync(("openai", "gpt-4o", "ok"));
        var dest = await _client.PostAsJsonAsync("/api/log-export/destinations",
            new { name = "wh2", type = "webhook", config = new { url = "http://sink.local/x" } });
        var id = (await dest.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("destination").GetProperty("id").GetString()!;

        var t = await _client.PostAsJsonAsync($"/api/log-export/destinations/{id}/test", new { });
        var body = await t.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("matched").GetInt32().ShouldBe(1);
        body.GetProperty("sample").GetString().ShouldContain("openai");
        _handler.Calls.ShouldBe(0);
    }
}

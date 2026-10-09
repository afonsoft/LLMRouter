using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-038: job scheduler + /api/jobs endpoints.</summary>
public class JobsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public JobsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private async Task LoginAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed class StubJob(int calls) : IJob
    {
        public string Id => "stub-job";
        public string Name => "Stub";
        public TimeSpan Interval => TimeSpan.FromDays(1);
        public bool EnabledByDefault => true;
        public Task<string> RunAsync(IServiceProvider services, CancellationToken ct)
        {
            calls++;
            return Task.FromResult($"ran {calls} times");
        }
    }

    [Fact]
    public async Task List_returns_registered_jobs_with_state()
    {
        await LoginAsync();
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/jobs");
        var jobs = r.GetProperty("jobs").EnumerateArray().ToList();
        jobs.ShouldContain(j => j.GetProperty("id").GetString() == "proxy-pool-health");
        jobs.ShouldContain(j => j.GetProperty("id").GetString() == "usage-prune");
        jobs.ShouldContain(j => j.GetProperty("id").GetString() == "db-backup");
        jobs.ShouldAllBe(j => j.GetProperty("enabled").GetBoolean() == true);
    }

    [Fact]
    public async Task Run_now_executes_and_records_run()
    {
        await LoginAsync();
        var resp = await _client.PostAsync("/api/jobs/usage-prune/run-now", null);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
        r.GetProperty("run").GetProperty("status").GetString().ShouldBe("ok");

        var runs = await _client.GetFromJsonAsync<JsonElement>("/api/jobs/usage-prune/runs");
        // The hosted scheduler may already have run the job once at startup; run-now's own row is what matters.
        runs.GetProperty("runs").GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/jobs");
        var job = list.GetProperty("jobs").EnumerateArray().First(j => j.GetProperty("id").GetString() == "usage-prune");
        job.GetProperty("lastStatus").GetString().ShouldBe("ok");
        job.GetProperty("nextRun").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Disable_and_enable_persists()
    {
        await LoginAsync();
        (await _client.PostAsync("/api/jobs/db-backup/disable", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/jobs");
        r.GetProperty("jobs").EnumerateArray().First(j => j.GetProperty("id").GetString() == "db-backup")
            .GetProperty("enabled").GetBoolean().ShouldBeFalse();
        (await _client.PostAsync("/api/jobs/db-backup/enable", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        r = await _client.GetFromJsonAsync<JsonElement>("/api/jobs");
        r.GetProperty("jobs").EnumerateArray().First(j => j.GetProperty("id").GetString() == "db-backup")
            .GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_job_returns_404()
    {
        await LoginAsync();
        (await _client.PostAsync("/api/jobs/nope/run-now", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Usage_prune_job_deletes_only_old_rows()
    {
        var db = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        var old = DateTime.UtcNow.AddDays(-40).ToString("yyyy-MM-dd HH:mm:ss");
        var recent = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss");
        db.UsageHistory.AddRange(
            new UsageRecord { Timestamp = old, Provider = "p", Model = "m", Status = "200" },
            new UsageRecord { Timestamp = recent, Provider = "p", Model = "m", Status = "200" });
        db.SaveChanges(); db.Dispose();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }).Build())
            .AddDbContext<LlmRouterDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"))
            .BuildServiceProvider();
        var output = await new BuiltinJobs.UsagePruneJob().RunAsync(services, CancellationToken.None);
        output.ShouldContain("usage=1");

        var db2 = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        db2.UsageHistory.Count().ShouldBe(1);
        db2.Dispose();
        services.Dispose();
    }
}

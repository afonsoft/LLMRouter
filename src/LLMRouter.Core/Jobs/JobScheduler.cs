using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LLMRouter.Core.Jobs;

/// <summary>A scheduled job. Returns a short human-readable result string.</summary>
public interface IJob
{
    string Id { get; }
    string Name { get; }
    TimeSpan Interval { get; }
    bool EnabledByDefault { get; }
    Task<string> RunAsync(IServiceProvider services, CancellationToken ct);
}

/// <summary>SPEC-038: hosted scheduler — ticks every 30s, runs due enabled jobs,
/// persists state in jobStates + history in jobRuns.</summary>
public sealed class JobScheduler(IServiceProvider services, ILogger<JobScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private readonly List<IJob> _registry = [];

    public IReadOnlyList<IJob> Registry => _registry;

    public void Register(IJob job) => _registry.Add(job);

    public IJob? Find(string id) => _registry.FirstOrDefault(j => j.Id == id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedStatesAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunDueAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, "job tick failed"); }
            await Task.Delay(Tick, stoppingToken);
        }
    }

    private async Task SeedStatesAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        foreach (var job in _registry)
            if (await db.JobStates.FindAsync(job.Id) is null)
                db.JobStates.Add(new JobState { Id = job.Id, Enabled = job.EnabledByDefault });
        await db.SaveChangesAsync(ct);
    }

    public async Task RunDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        List<JobState> due;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            var states = await db.JobStates.ToListAsync(ct);
            due = states.Where(s => s.Enabled && Due(s, now)).ToList();
        }
        foreach (var st in due)
        {
            var job = Find(st.Id);
            if (job is null) continue;
            await RunNowAsync(job, ct);
        }
    }

    private bool Due(JobState s, DateTime now)
    {
        var job = Find(s.Id);
        if (job is null) return false;
        if (s.LastRun is null || !DateTime.TryParse(s.LastRun, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var last)) return true;
        return now - last >= job.Interval;
    }

    /// <summary>Runs a job immediately (scheduler tick or manual run-now) and
    /// records jobStates + jobRuns.</summary>
    public async Task<JobRun> RunNowAsync(IJob job, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string status = "ok", output;
        try { output = await job.RunAsync(services, ct); }
        catch (Exception ex)
        {
            status = "fail";
            output = ex.Message;
            logger.LogWarning(ex, "job {Id} failed", job.Id);
        }
        sw.Stop();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var run = new JobRun
        {
            JobId = job.Id,
            StartedAt = started.ToString("yyyy-MM-dd HH:mm:ss"),
            DurationMs = sw.ElapsedMilliseconds,
            Status = status,
            Output = output?.Length > 4000 ? output[..4000] : output,
        };
        db.JobRuns.Add(run);
        var st = await db.JobStates.FindAsync(job.Id) ?? db.JobStates.Add(new JobState { Id = job.Id }).Entity;
        st.LastRun = run.StartedAt;
        st.NextRun = (started + job.Interval).ToString("yyyy-MM-dd HH:mm:ss");
        st.LastStatus = status;
        st.LastError = status == "fail" ? output : null;
        st.LastDurationMs = sw.ElapsedMilliseconds;
        await db.SaveChangesAsync(ct);
        return run;
    }
}

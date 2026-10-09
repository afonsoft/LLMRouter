using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Jobs;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-038: /api/jobs — scheduler status + control.</summary>
public static class JobsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/jobs", async (JobScheduler scheduler, LlmRouterDbContext db) =>
        {
            var states = await db.JobStates.ToListAsync();
            return Results.Json(new
            {
                jobs = scheduler.Registry.Select(j =>
                {
                    var st = states.FirstOrDefault(s => s.Id == j.Id);
                    return new
                    {
                        id = j.Id,
                        name = j.Name,
                        intervalSeconds = (long)j.Interval.TotalSeconds,
                        enabled = st?.Enabled ?? j.EnabledByDefault,
                        lastRun = st?.LastRun,
                        nextRun = st?.NextRun,
                        lastStatus = st?.LastStatus,
                        lastError = st?.LastError,
                        lastDurationMs = st?.LastDurationMs ?? 0,
                    };
                }),
            }, JsonOpts);
        });

        g.MapGet("/jobs/{id}/runs", async (string id, LlmRouterDbContext db, int? limit) =>
        {
            var runs = await db.JobRuns.Where(r => r.JobId == id)
                .OrderByDescending(r => r.Id).Take(Math.Min(limit ?? 50, 200)).ToListAsync();
            return Results.Json(new { runs }, JsonOpts);
        });

        g.MapPost("/jobs/{id}/enable", async (string id, JobScheduler scheduler, LlmRouterDbContext db) =>
        {
            if (scheduler.Find(id) is null) return Results.Json(new { error = "unknown job" }, JsonOpts, statusCode: 404);
            var st = await db.JobStates.FindAsync(id) ?? db.JobStates.Add(new JobState { Id = id }).Entity;
            st.Enabled = true;
            await db.SaveChangesAsync();
            return Results.Json(new { enabled = true }, JsonOpts);
        });

        g.MapPost("/jobs/{id}/disable", async (string id, JobScheduler scheduler, LlmRouterDbContext db) =>
        {
            if (scheduler.Find(id) is null) return Results.Json(new { error = "unknown job" }, JsonOpts, statusCode: 404);
            var st = await db.JobStates.FindAsync(id) ?? db.JobStates.Add(new JobState { Id = id }).Entity;
            st.Enabled = false;
            await db.SaveChangesAsync();
            return Results.Json(new { enabled = false }, JsonOpts);
        });

        g.MapPost("/jobs/{id}/run-now", async (string id, JobScheduler scheduler) =>
        {
            var job = scheduler.Find(id);
            if (job is null) return Results.Json(new { error = "unknown job" }, JsonOpts, statusCode: 404);
            var run = await scheduler.RunNowAsync(job);
            return Results.Json(new { run }, JsonOpts);
        });
    }
}

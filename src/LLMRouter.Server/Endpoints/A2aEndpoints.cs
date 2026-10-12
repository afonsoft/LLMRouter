using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-053: A2A task lifecycle — queued tasks drained by A2aTaskExecutor
/// (in-process InternalChat dispatch), with poll/cancel/history/status.
/// </summary>
public static class A2aEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private static object View(A2aTask t) => new
    {
        t.Id, t.Agent, t.State, t.Result, t.Error, t.Skill,
        t.CreatedAt, t.StartedAt, t.FinishedAt,
        payload = JsonSerializer.Deserialize<JsonElement>(t.Payload),
    };

    /// <summary>ours → upstream state names for the history contract.</summary>
    private static string UpstreamState(string s) => s switch
    {
        "queued" => "submitted",
        "running" => "working",
        "done" => "completed",
        _ => s,
    };

    private static readonly HashSet<string> UpstreamStates =
        ["submitted", "working", "completed", "failed", "cancelled"];

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/a2a").RequireAuthorization();

        g.MapPost("/tasks", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var t = new A2aTask
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Agent = req.TryGetProperty("agent", out var a) ? a.GetString() : null,
                Payload = req.TryGetProperty("payload", out var p) ? p.GetRawText() : req.GetRawText(),
                Skill = req.TryGetProperty("skill", out var sk) ? sk.GetString() : null,
                CreatedAt = Now(),
            };
            db.A2aTasks.Add(t);
            await db.SaveChangesAsync();
            return Results.Json(View(t), JsonOpts, statusCode: 201);
        });

        g.MapGet("/tasks", async (HttpContext ctx, LlmRouterDbContext db, string? state) =>
        {
            var q = db.A2aTasks.AsQueryable();
            if (!string.IsNullOrEmpty(state)) q = q.Where(t => t.State == state);
            var rows = await q.OrderByDescending(t => t.CreatedAt).Take(200).ToListAsync();
            return Results.Json(new { tasks = rows.Select(View) }, JsonOpts);
        });

        g.MapGet("/tasks/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var t = await db.A2aTasks.FindAsync(id);
            return t is null
                ? Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404)
                : Results.Json(View(t), JsonOpts);
        });

        g.MapPost("/tasks/{id}/cancel", async (string id, LlmRouterDbContext db) =>
        {
            var t = await db.A2aTasks.FindAsync(id);
            if (t is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            if (t.State is "done" or "failed")
                return Results.Json(new { error = $"already {t.State}" }, JsonOpts, statusCode: 409);
            t.State = "cancelled";
            t.FinishedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(View(t), JsonOpts);
        });

        g.MapGet("/tasks/{id}/history", async (string id, LlmRouterDbContext db) =>
        {
            var t = await db.A2aTasks.FindAsync(id);
            if (t is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var history = new List<object> { new { state = "queued", at = t.CreatedAt } };
            if (t.StartedAt is not null) history.Add(new { state = "running", at = t.StartedAt });
            if (t.FinishedAt is not null) history.Add(new { state = t.State, at = t.FinishedAt });
            return Results.Json(new { id = t.Id, history }, JsonOpts);
        });

        // SPEC-129: upstream GET /api/a2a/tasks/history — persisted history with
        // from/to/skill/state/limit(≤500)/offset filters and upstream state names.
        g.MapGet("/tasks/history", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var q2 = ctx.Request.Query;
            var state = q2["state"].FirstOrDefault();
            if (state is not null && !UpstreamStates.Contains(state))
                return Results.Json(new { error = new { message = $"invalid state: {state}" } }, JsonOpts, statusCode: 400);
            var limit = int.TryParse(q2["limit"].FirstOrDefault(), out var l) && l > 0 ? Math.Min(l, 500) : 100;
            var offset = int.TryParse(q2["offset"].FirstOrDefault(), out var o) && o >= 0 ? o : 0;

            var q = db.A2aTasks.AsQueryable();
            if (q2["from"].FirstOrDefault() is { } from && DateTime.TryParse(from, out var fdt))
                q = q.Where(t => string.Compare(t.CreatedAt, fdt.ToString("yyyy-MM-dd HH:mm:ss")) >= 0);
            if (q2["to"].FirstOrDefault() is { } to && DateTime.TryParse(to, out var tdt))
                q = q.Where(t => string.Compare(t.CreatedAt, tdt.ToString("yyyy-MM-dd HH:mm:ss")) <= 0);
            if (q2["skill"].FirstOrDefault() is { } skill) q = q.Where(t => t.Skill == skill);
            if (state is not null)
            {
                var ours = state switch
                {
                    "submitted" => "queued", "working" => "running", "completed" => "done",
                    _ => state,
                };
                q = q.Where(t => t.State == ours);
            }
            var rows = await q.OrderByDescending(t => t.CreatedAt).Skip(offset).Take(limit).ToListAsync();
            return Results.Json(new
            {
                items = rows.Select(t => new
                {
                    id = t.Id,
                    state = UpstreamState(t.State),
                    skill = t.Skill,
                    createdAt = t.CreatedAt,
                    updatedAt = t.FinishedAt ?? t.StartedAt ?? t.CreatedAt,
                    completedAt = t.FinishedAt,
                }),
            }, JsonOpts);
        });

        g.MapGet("/status", async (LlmRouterDbContext db) =>
        {
            var counts = await db.A2aTasks.GroupBy(t => t.State)
                .Select(x => new { State = x.Key, Count = x.Count() }).ToListAsync();
            return Results.Json(new
            {
                queue = counts.ToDictionary(x => x.State, x => x.Count),
                executor = "running",
            }, JsonOpts);
        });
    }
}

/// <summary>Drains queued a2aTasks through the in-process chat pipeline.</summary>
public sealed class A2aTaskExecutor(IServiceProvider sp) : BackgroundService
{
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = sp.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
                var task = await db.A2aTasks
                    .Where(t => t.State == "queued")
                    .OrderBy(t => t.CreatedAt)
                    .FirstOrDefaultAsync(stoppingToken);
                if (task is not null)
                {
                    task.State = "running";
                    task.StartedAt = Now();
                    await db.SaveChangesAsync(stoppingToken);
                    try
                    {
                        var p = JsonSerializer.Deserialize<JsonElement>(task.Payload);
                        var model = p.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                        var text = p.TryGetProperty("text", out var t2) ? t2.GetString() ?? task.Payload : task.Payload;
                        var output = await InternalChat.CallAsync(sp, model, text, $"a2a:{task.Id}");
                        // re-read: a cancel may have landed mid-run
                        await db.Entry(task).ReloadAsync(stoppingToken);
                        if (task.State != "cancelled")
                        {
                            task.State = output.StartsWith("error:") ? "failed" : "done";
                            if (task.State == "failed") task.Error = output; else task.Result = output;
                            task.FinishedAt = Now();
                            await db.SaveChangesAsync(stoppingToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        task.State = "failed";
                        task.Error = ex.Message;
                        task.FinishedAt = Now();
                        try { await db.SaveChangesAsync(CancellationToken.None); } catch { }
                    }
                    continue; // check for more queued work immediately
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* best-effort: failure is non-fatal */ }
            catch { /* transient db error — keep polling */ }
            try { await Task.Delay(1500, stoppingToken); }
            catch (OperationCanceledException) { /* best-effort: failure is non-fatal */ }
        }
    }
}

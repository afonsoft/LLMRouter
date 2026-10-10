using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-053: conductor lifecycle — natural-language goal → LLM decomposes to
/// steps → executed via the existing Conductor engine → stored as a
/// conductorTask with state + result.
/// </summary>
public static class ConductorEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private static object? JsonOrString(string? s)
    {
        if (s is null) return null;
        if (s.StartsWith('{') || s.StartsWith('['))
            try { return JsonSerializer.Deserialize<JsonElement>(s); } catch { }
        return s;
    }

    private static object View(ConductorTask t) => new
    {
        t.Id, t.Goal, t.Model, t.State, t.Error,
        t.CreatedAt, t.FinishedAt,
        result = JsonOrString(t.Result),
        steps = JsonSerializer.Deserialize<JsonElement>(t.Steps),
    };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/conductor").RequireAuthorization();

        g.MapPost("/ask", async (HttpContext ctx, IServiceProvider sp, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var goal = req.TryGetProperty("goal", out var g2) ? g2.GetString() ?? "" : "";
            if (goal.Length == 0)
                return Results.Json(new { error = "goal required" }, JsonOpts, statusCode: 400);
            var model = req.TryGetProperty("model", out var m) ? m.GetString() : null;

            var task = new ConductorTask
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Goal = goal, Model = model,
                State = "running", CreatedAt = Now(),
            };
            db.ConductorTasks.Add(task);
            await db.SaveChangesAsync();

            try
            {
                // 1. decompose the goal into steps (falls back to a single step)
                var planModel = model ?? "auto";
                var plan = await InternalChat.CallAsync(sp, planModel,
                    "Break this goal into 1-4 sequential steps. Reply with ONLY JSON: " +
                    "{\"steps\":[{\"name\":\"...\",\"prompt\":\"...\"}]}\nGoal: " + goal,
                    "conductor:plan");
                string stepsJson;
                try
                {
                    var json = plan[(plan.IndexOf('{'))..];
                    var parsed = JsonDocument.Parse(json);
                    if (!parsed.RootElement.TryGetProperty("steps", out _)) throw new Exception("no steps");
                    stepsJson = json[..(json.LastIndexOf('}') + 1)];
                }
                catch
                {
                    stepsJson = JsonSerializer.Serialize(new
                    { steps = new[] { new { name = "execute", model = model ?? "auto", prompt = "{{input}}" } } });
                }
                task.Steps = stepsJson;

                // 2. run through the existing conductor engine
                var steps = Core.Orchestration.Conductor.Parse(stepsJson);
                var result = await Core.Orchestration.Conductor.RunAsync(steps, goal,
                    (m, prompt) => InternalChat.CallAsync(sp, m.Length > 0 ? m : planModel, prompt, $"conductor:{task.Id}"));
                task.State = "done";
                task.Result = JsonSerializer.Serialize(new
                {
                    final = result.Final,
                    steps = result.Steps.Select(s => new { s.Name, s.Model, s.Ms, output = s.Output }),
                }, JsonOpts);
            }
            catch (Exception ex)
            {
                task.State = "failed";
                task.Error = ex.Message;
            }
            task.FinishedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(View(task), JsonOpts);
        });

        g.MapGet("/tasks", async (LlmRouterDbContext db) =>
            Results.Json(new
            { tasks = (await db.ConductorTasks.OrderByDescending(t => t.CreatedAt).Take(200).ToListAsync()).Select(View) },
            JsonOpts));

        g.MapGet("/tasks/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var t = await db.ConductorTasks.FindAsync(id);
            return t is null
                ? Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404)
                : Results.Json(View(t), JsonOpts);
        });

        g.MapPost("/tasks/{id}/cancel", async (string id, LlmRouterDbContext db) =>
        {
            var t = await db.ConductorTasks.FindAsync(id);
            if (t is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            if (t.State is "done" or "failed")
                return Results.Json(new { error = $"already {t.State}" }, JsonOpts, statusCode: 409);
            t.State = "cancelled";
            t.FinishedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(View(t), JsonOpts);
        });

        g.MapGet("/fleet", async (LlmRouterDbContext db) => Results.Json(new
        {
            workers = (await db.ProviderConnections.Where(c => c.IsActive)
                .Select(c => new { c.Id, c.Provider, c.Name }).ToListAsync()),
            a2aQueue = (await db.A2aTasks.GroupBy(t => t.State)
                .Select(x => new { x.Key, Count = x.Count() }).ToListAsync())
                .ToDictionary(x => x.Key, x => x.Count),
            conductorTasks = (await db.ConductorTasks.GroupBy(t => t.State)
                .Select(x => new { x.Key, Count = x.Count() }).ToListAsync())
                .ToDictionary(x => x.Key, x => x.Count),
        }, JsonOpts));
    }
}

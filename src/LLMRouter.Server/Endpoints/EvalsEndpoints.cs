using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-052: evals — suites of scored cases run against a combo/model through
/// the real /v1/chat/completions pipeline (self-dispatch via the "batches"
/// client, forwarding the caller's auth). Case judges: contains, regex,
/// json (valid JSON), judge (LLM verdict on PASS/FAIL).
/// </summary>
public static class EvalsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private static object SuiteView(EvalSuite s, IEnumerable<EvalCase> cases) => new
    {
        s.Id, s.Name, s.CreatedAt, s.UpdatedAt,
        cases = cases.OrderBy(c => c.Ord).Select(c => new
        { c.Id, c.Input, c.ExpectType, c.ExpectValue, c.Weight }),
    };

    private static object RunView(EvalRun r, EvalSuite? s) => new
    {
        r.Id, r.SuiteId, suiteName = s?.Name, r.Target, r.Status,
        r.Score, results = JsonSerializer.Deserialize<JsonElement>(r.Results),
        r.StartedAt, r.DurationMs,
    };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/evals").RequireAuthorization();

        g.MapGet("/suites", async (LlmRouterDbContext db) =>
        {
            var suites = await db.EvalSuites.OrderByDescending(s => s.CreatedAt).ToListAsync();
            var cases = await db.EvalCases.ToListAsync();
            return Results.Json(new
            { suites = suites.Select(s => SuiteView(s, cases.Where(c => c.SuiteId == s.Id))) }, JsonOpts);
        });

        g.MapPost("/suites", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var name = req.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.Length == 0)
                return Results.Json(new { error = "name required" }, JsonOpts, statusCode: 400);
            var now = Now();
            var s = new EvalSuite { Id = Guid.NewGuid().ToString("N")[..12], Name = name, CreatedAt = now, UpdatedAt = now };
            db.EvalSuites.Add(s);
            foreach (var (c, ord) in ReadCases(req).Select((c, i) => (c, i)))
            {
                c.Id = Guid.NewGuid().ToString("N")[..12];
                c.SuiteId = s.Id;
                c.Ord = ord;
                db.EvalCases.Add(c);
            }
            await db.SaveChangesAsync();
            return Results.Json(SuiteView(s, db.EvalCases.Where(c => c.SuiteId == s.Id)), JsonOpts);
        });

        g.MapPut("/suites/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var s = await db.EvalSuites.FindAsync(id);
            if (s is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (req.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                s.Name = n.GetString()!;
            if (req.TryGetProperty("cases", out _))
            {
                db.EvalCases.RemoveRange(db.EvalCases.Where(c => c.SuiteId == id));
                foreach (var (c, ord) in ReadCases(req).Select((c, i) => (c, i)))
                {
                    c.Id = Guid.NewGuid().ToString("N")[..12];
                    c.SuiteId = id;
                    c.Ord = ord;
                    db.EvalCases.Add(c);
                }
            }
            s.UpdatedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(SuiteView(s, await db.EvalCases.Where(c => c.SuiteId == id).ToListAsync()), JsonOpts);
        });

        g.MapDelete("/suites/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var s = await db.EvalSuites.FindAsync(id);
            if (s is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            db.EvalSuites.Remove(s);
            db.EvalCases.RemoveRange(db.EvalCases.Where(c => c.SuiteId == id));
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        g.MapPost("/suites/{id}/run", async (string id, HttpContext ctx, IHttpClientFactory hf, LlmRouterDbContext db) =>
        {
            var s = await db.EvalSuites.FindAsync(id);
            if (s is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var cases = await db.EvalCases.Where(c => c.SuiteId == id).OrderBy(c => c.Ord).ToListAsync();
            if (cases.Count == 0)
                return Results.Json(new { error = "suite has no cases" }, JsonOpts, statusCode: 400);
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var target = req.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
            if (target.Length == 0)
                return Results.Json(new { error = "target required" }, JsonOpts, statusCode: 400);

            var run = new EvalRun
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                SuiteId = id, Target = target, StartedAt = Now(),
            };
            db.EvalRuns.Add(run);
            await db.SaveChangesAsync();

            var sw = Stopwatch.StartNew();
            var http = hf.CreateClient("batches");
            http.BaseAddress = new Uri($"{ctx.Request.Scheme}://{ctx.Request.Host}");
            var auth = ctx.Request.Headers.Authorization.ToString();
            var cookie = ctx.Request.Headers.Cookie.ToString();

            var results = new List<JsonElement>();
            double weighted = 0, total = 0;
            foreach (var c in cases)
            {
                var content = await ChatOnce(http, auth, cookie, target, c.Input);
                var (pass, verdict) = await Judge(http, auth, cookie, target, c, content);
                weighted += pass ? c.Weight : 0;
                total += c.Weight;
                results.Add(JsonSerializer.SerializeToElement(new
                {
                    caseId = c.Id, c.Input, c.ExpectType, c.ExpectValue, c.Weight,
                    pass, verdict,
                    output = content is null ? null : (content.Length > 500 ? content[..500] + "…" : content),
                }, JsonOpts));
            }
            run.Status = "done";
            run.Score = total > 0 ? Math.Round(weighted / total * 100, 1) : 0;
            run.Results = "[" + string.Join(",", results.Select(e => e.GetRawText())) + "]";
            run.DurationMs = sw.ElapsedMilliseconds;
            await db.SaveChangesAsync();
            await Core.Extras.Extras.AuditAsync(db, "evals.run", $"{s.Name}→{target}:{run.Score}");
            return Results.Json(RunView(run, s), JsonOpts);
        });

        g.MapGet("/runs", async (LlmRouterDbContext db) =>
        {
            var runs = await db.EvalRuns.OrderByDescending(r => r.StartedAt).Take(100).ToListAsync();
            var suites = await db.EvalSuites.ToDictionaryAsync(s => s.Id);
            return Results.Json(new
            { runs = runs.Select(r => RunView(r, suites.GetValueOrDefault(r.SuiteId))) }, JsonOpts);
        });

        g.MapGet("/runs/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var r = await db.EvalRuns.FindAsync(id);
            if (r is null) return Results.Json(new { error = "not found" }, JsonOpts, statusCode: 404);
            var s = await db.EvalSuites.FindAsync(r.SuiteId);
            return Results.Json(RunView(r, s), JsonOpts);
        });
    }

    private static List<EvalCase> ReadCases(JsonElement req)
    {
        var list = new List<EvalCase>();
        if (!req.TryGetProperty("cases", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var c in arr.EnumerateArray())
            list.Add(new EvalCase
            {
                Input = c.TryGetProperty("input", out var i) ? i.GetString() ?? "" : "",
                ExpectType = c.TryGetProperty("expectType", out var e) ? e.GetString() ?? "contains" : "contains",
                ExpectValue = c.TryGetProperty("expectValue", out var v) ? v.GetString() ?? "" : "",
                Weight = c.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : 1,
            });
        return list;
    }

    /// <summary>One non-streaming chat call through our own /v1 pipeline; returns assistant text or null.</summary>
    private static async Task<string?> ChatOnce(HttpClient http, string auth, string cookie, string target, string prompt)
    {
        try
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = target, stream = false,
                    messages = new[] { new { role = "user", content = prompt } },
                }, JsonOpts), Encoding.UTF8, "application/json"),
            };
            if (auth.Length > 0) r.Headers.TryAddWithoutValidation("Authorization", auth);
            if (cookie.Length > 0) r.Headers.TryAddWithoutValidation("Cookie", cookie);
            var resp = await http.SendAsync(r);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return body.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
                && ch[0].TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c)
                ? c.GetString() : null;
        }
        catch { return null; }
    }

    private static async Task<(bool pass, string verdict)> Judge(
        HttpClient http, string auth, string cookie, string target, EvalCase c, string? output)
    {
        if (output is null) return (false, "no response");
        switch (c.ExpectType)
        {
            case "regex":
                try { return (Regex.IsMatch(output, c.ExpectValue, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)), "regex"); }
                catch { return (false, "invalid regex"); }
            case "json":
                try { JsonDocument.Parse(output); return (true, "valid json"); }
                catch { return (false, "invalid json"); }
            case "judge":
                var verdict = await ChatOnce(http, auth, cookie, target,
                    $"Evaluate whether this response satisfies the expectation. " +
                    $"Expectation: {c.ExpectValue}\nResponse: {output}\n" +
                    "Answer with exactly PASS or FAIL followed by one short reason.");
                if (verdict is null) return (false, "judge unavailable");
                return (verdict.TrimStart().StartsWith("PASS", StringComparison.OrdinalIgnoreCase),
                    verdict.Length > 120 ? verdict[..120] + "…" : verdict);
            default: // contains
                return (output.Contains(c.ExpectValue, StringComparison.OrdinalIgnoreCase), "contains");
        }
    }
}

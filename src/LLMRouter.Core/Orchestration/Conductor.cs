using System.Text.Json;

namespace LLMRouter.Core.Orchestration;

/// <summary>
/// SPEC-012 conductor: multi-step workflow engine. A workflow is JSON:
/// {"name":"wf","steps":[{"name":"draft","model":"m","prompt":"...{{input}}..."},
/// {"name":"fan","fan":["m1","m2"],"prompt":"...{{prev}}...","join":"merge"}]}
/// {{input}} = original input, {{prev}} = previous step output (merged if fan-out).
/// </summary>
public static class Conductor
{
    public sealed record StepResult(string Name, string Model, string Output, int Ms);
    public sealed record RunResult(List<StepResult> Steps, string Final);

    public sealed record WorkflowStep(
        string Name, string? Model, string[]? Fan, string Prompt, string? Join);

    /// <summary>Parse a workflow definition (steps array).</summary>
    public static List<WorkflowStep> Parse(string workflowJson)
    {
        var w = JsonSerializer.Deserialize<JsonElement>(workflowJson);
        var steps = new List<WorkflowStep>();
        if (!w.TryGetProperty("steps", out var arr)) return steps;
        foreach (var s in arr.EnumerateArray())
        {
            string? model = s.TryGetProperty("model", out var m) ? m.GetString() : null;
            string[]? fan = s.TryGetProperty("fan", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(e => e.GetString()!).ToArray() : null;
            steps.Add(new WorkflowStep(
                s.TryGetProperty("name", out var n) ? n.GetString() ?? $"step{steps.Count}" : $"step{steps.Count}",
                model, fan,
                s.TryGetProperty("prompt", out var p) ? p.GetString() ?? "{{prev}}" : "{{prev}}",
                s.TryGetProperty("join", out var j) ? j.GetString() : null));
        }
        return steps;
    }

    /// <summary>Substitute {{input}}/{{prev}} variables.</summary>
    public static string Bind(string template, string input, string prev) =>
        template.Replace("{{input}}", input).Replace("{{prev}}", prev);

    /// <summary>
    /// Execute a workflow. <paramref name="call"/> performs one model call
    /// (model, prompt) → text — the gateway's chat pipeline supplies it.
    /// </summary>
    public static async Task<RunResult> RunAsync(
        List<WorkflowStep> steps, string input, Func<string, string, Task<string>> call)
    {
        var results = new List<StepResult>();
        var prev = input;
        foreach (var s in steps)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var prompt = Bind(s.Prompt, input, prev);
            if (s.Fan is { Length: > 0 } fan)
            {
                var outs = await Task.WhenAll(fan.Select(async m => (m, t: await call(m, prompt))));
                sw.Stop();
                var joined = s.Join == "merge"
                    ? string.Join("\n\n---\n\n", outs.Select(o => o.t))
                    : outs[0].t;
                results.Add(new StepResult(s.Name, string.Join("+", fan), joined, (int)sw.ElapsedMilliseconds));
                prev = joined;
            }
            else
            {
                var outText = await call(s.Model ?? "", prompt);
                sw.Stop();
                results.Add(new StepResult(s.Name, s.Model ?? "", outText, (int)sw.ElapsedMilliseconds));
                prev = outText;
            }
        }
        return new RunResult(results, prev);
    }
}

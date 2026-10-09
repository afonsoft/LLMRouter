using System.Text.Json.Nodes;

namespace LLMRouter.Core.Compression;

public static class CompressionRegistry
{
    private static readonly ICompressionEngine[] All =
    [
        new SessionDedupEngine(), new CcrEngine(), new HeadroomEngine(), new CavemanEngine(),
        new LiteEngine(), new AggressiveEngine(), new LlmlinguaEngine(), new UltraEngine(),
        new RtkEngine(), new OmniglyphEngine(),
    ];

    private static readonly Dictionary<string, ICompressionEngine> ById =
        All.ToDictionary(e => e.Id);

    public static IReadOnlyList<ICompressionEngine> Engines => All;
    public static ICompressionEngine? Get(string id) => ById.GetValueOrDefault(id);

    /// <summary>Upstream CompressionMode → engine id(s). "stacked" means "use the configured pipeline".</summary>
    public static readonly Dictionary<string, string[]> ModeToEngines = new()
    {
        ["off"] = [],
        ["lite"] = ["lite"],
        ["standard"] = ["caveman"],
        ["caveman"] = ["caveman"],
        ["aggressive"] = ["aggressive"],
        ["ultra"] = ["ultra"],
        ["rtk"] = ["rtk"],
        ["omniglyph"] = ["omniglyph"],
        ["codex-responses"] = [],
        ["stacked"] = ["session-dedup", "lite"], // default when no stackedPipeline configured
    };
}

public sealed record PipelineStep(string Engine, string? Intensity, JsonObject? Config);

public static class CompressionPipeline
{
    /// <summary>Parse a pipeline JSON array ([{engine,intensity?,config?}]) into steps.</summary>
    public static List<PipelineStep> ParseSteps(JsonNode? node)
    {
        var steps = new List<PipelineStep>();
        if (node is not JsonArray arr) return steps;
        foreach (var s in arr.OfType<JsonObject>())
        {
            var eng = s["engine"]?.GetValue<string>();
            if (eng is null || CompressionRegistry.Get(eng) is null) continue;
            steps.Add(new PipelineStep(eng,
                s["intensity"]?.GetValue<string>(),
                s["config"] as JsonObject));
        }
        return steps;
    }

    /// <summary>
    /// Resolve the effective plan for a request:
    /// compression-combo assignment → settings.compression.stackedPipeline → comboOverrides[combo] mode → defaultMode.
    /// Returns ordered steps (empty = no compression).
    /// </summary>
    public static List<PipelineStep> ResolvePlan(JsonObject? compressionSettings, string? routingComboId, JsonNode? comboPipeline)
    {
        // combo-assigned pipeline wins (from compressionComboAssignments lookup)
        if (comboPipeline is not null)
        {
            var steps = ParseSteps(comboPipeline);
            if (steps.Count > 0) return steps;
        }

        if (compressionSettings is null) return [];

        // per-combo mode override
        if (routingComboId is not null
            && compressionSettings["comboOverrides"] is JsonObject ov
            && ov[routingComboId]?.GetValue<string>() is { } overrideMode
            && CompressionRegistry.ModeToEngines.TryGetValue(overrideMode, out var ovEngines))
            return ovEngines.Select(e => new PipelineStep(e, null, null)).ToList();

        if (compressionSettings["stackedPipeline"] is JsonArray sp && sp.Count > 0)
        {
            var steps = ParseSteps(sp);
            if (steps.Count > 0) return steps;
        }

        var mode = compressionSettings["defaultMode"]?.GetValue<string>() ?? "off";
        return CompressionRegistry.ModeToEngines.TryGetValue(mode, out var engines)
            ? engines.Select(e => new PipelineStep(e, null, null)).ToList()
            : [];
    }

    /// <summary>Run a plan over a body. Engines execute in stackPriority order; each gets its stepConfig + engineConfigs[id].</summary>
    public static (JsonObject Body, List<EngineRunStats> Runs) Run(
        JsonObject body, IReadOnlyList<PipelineStep> steps, EngineOptions baseOptions,
        JsonObject? engineConfigs = null)
    {
        var runs = new List<EngineRunStats>();
        var current = body;
        foreach (var step in steps
            .Select(s => (step: s, engine: CompressionRegistry.Get(s.Engine)))
            .Where(x => x.engine is not null)
            .OrderBy(x => x.engine!.StackPriority))
        {
            var stepCfg = step.step.Config?.DeepClone() as JsonObject ?? new JsonObject();
            if (step.step.Intensity is not null) stepCfg["intensity"] = step.step.Intensity;
            if (engineConfigs?[step.step.Engine] is JsonObject ec)
                foreach (var kv in ec)
                    stepCfg[kv.Key] ??= kv.Value?.DeepClone();
            try
            {
                var r = step.engine!.Apply(current, baseOptions with
                {
                    StepConfig = stepCfg,
                    CompressionComboId = baseOptions.CompressionComboId,
                });
                current = r.Body;
                if (r.Stats is not null) runs.Add(r.Stats);
            }
            catch
            {
                runs.Add(new EngineRunStats(step.engine!.Id, 0, 0, [], "engine_error"));
            }
        }
        return (current, runs);
    }
}

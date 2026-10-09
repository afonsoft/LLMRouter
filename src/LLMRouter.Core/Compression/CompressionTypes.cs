using System.Text.Json.Nodes;

namespace LLMRouter.Core.Compression;

/// <summary>One configurable field on an engine's config schema (schema-driven UI).</summary>
public sealed record EngineConfigField(
    string Key,
    string Type,            // boolean | number | string | select
    string Label,
    object? DefaultValue = null,
    string[]? Options = null,
    double? Min = null,
    double? Max = null,
    string? Description = null);

/// <summary>Per-engine run stats recorded in compressionRuns.</summary>
public sealed record EngineRunStats(
    string Engine,
    int BeforeChars,
    int AfterChars,
    string[] Techniques,
    string? Skipped = null)
{
    public int SavedChars => Math.Max(0, BeforeChars - AfterChars);
    public double SavingsPercent => BeforeChars > 0 ? Math.Round(SavedChars * 10000.0 / BeforeChars) / 100.0 : 0;
}

public sealed record CompressionResult(JsonObject Body, bool Compressed, EngineRunStats? Stats);

public sealed record EngineOptions(
    string? Model = null,
    bool? SupportsVision = null,
    string? PrincipalId = null,
    string? CompressionComboId = null,
    JsonObject? StepConfig = null);

/// <summary>A compression engine: transforms a chat body (JsonObject with messages[]).</summary>
public interface ICompressionEngine
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Icon { get; }
    bool Stackable { get; }
    int StackPriority { get; }
    bool Stable { get; }
    CompressionResult Apply(JsonObject body, EngineOptions options);
    IReadOnlyList<EngineConfigField> ConfigSchema { get; }
}

using System.Text.Json.Serialization;

namespace LLMRouter.Core.Registry;

/// <summary>Backend provider registry entry (ported from open-sse/config/providers/registry).</summary>
public class ProviderEntry
{
    public string Id { get; set; } = "";
    public string? Alias { get; set; }
    public string[]? Aliases { get; set; }
    /// <summary>"openai" | "claude" | "gemini" | "openai-responses" | provider-specific.</summary>
    public string Format { get; set; } = "openai";
    public string? Executor { get; set; }
    public string? BaseUrl { get; set; }
    public string? MessagesUrl { get; set; }
    public string? ModelsUrl { get; set; }
    public string? TestKeyBaseUrl { get; set; }
    public string? TestKeyModelsUrl { get; set; }
    public string? ResponsesBaseUrl { get; set; }
    public string? ChatPath { get; set; }
    /// <summary>"apikey" | "oauth" | "optional" | "none".</summary>
    public string AuthType { get; set; } = "apikey";
    /// <summary>"bearer" | "x-api-key" | "raw" | custom header name.</summary>
    public string? AuthHeader { get; set; }
    public string? AuthPrefix { get; set; }
    public string? AnonymousApiKey { get; set; }
    public int? DefaultContextLength { get; set; }
    public bool ForceStream { get; set; }
    public bool PassthroughModels { get; set; }
    public string? UrlSuffix { get; set; }
    public string? ModelIdPrefix { get; set; }
    public string[]? AcceptedModelIdPrefixes { get; set; }
    public string[]? UnsupportedParams { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public Dictionary<string, string>? ExtraHeaders { get; set; }
    public object? RequestDefaults { get; set; }
    public object? BodyStringReplacements { get; set; }
    public List<RegistryModel>? Models { get; set; }
    public object? Oauth { get; set; }
    public object? PoolConfig { get; set; }
    public int? TimeoutMs { get; set; }
    public bool? RequiresPlainStringContent { get; set; }
    public bool? RequiresReasoningContentEcho { get; set; }
    public string? ReasoningTransport { get; set; }
    public bool? StrictChatHistory { get; set; }
    public bool? LiveCatalogAuthoritative { get; set; }
    public object? AlternateFormats { get; set; }
    public object? BaseUrls { get; set; }
    public object? RegistryDispatchUnion { get; set; }
    public object? NaiveResetTimezone { get; set; }
    public string? ClientVersion { get; set; }
    public int? FetchStartTimeoutCapMs { get; set; }
    public int? ToolNameMaxLength { get; set; }
    public string[]? DefaultSupportedThinkingEfforts { get; set; }
}

public class RegistryModel
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public int? ContextLength { get; set; }
    public string? Type { get; set; }
    public string? TargetFormat { get; set; }
    public string? ScoresAs { get; set; }
    public bool? Vision { get; set; }
    public bool? ImageToText { get; set; }
    public string[]? Capabilities { get; set; }
    public string[]? UnsupportedParams { get; set; }
    public string[]? SupportedThinkingEfforts { get; set; }
    public object? Pricing { get; set; }
    public object? Limits { get; set; }
}

/// <summary>UI-facing provider display entry (ported from src/shared/constants/providers/*).</summary>
public class UiProviderEntry
{
    public string Id { get; set; } = "";
    public string? Alias { get; set; }
    public string? Name { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public string? TextIcon { get; set; }
    public string? Website { get; set; }
    public string[]? ServiceKinds { get; set; }
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

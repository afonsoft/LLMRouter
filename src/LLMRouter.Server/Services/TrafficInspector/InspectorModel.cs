using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LLMRouter.Server.Services.TrafficInspector;

/// <summary>
/// Intercepted request entry — verbatim port of mitm/inspector/types.ts.
/// `status` serializes as a number or "in-flight"/"error".
/// </summary>
public sealed class InterceptedRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("agent")] public string? Agent { get; set; }
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("requestHeaders")] public Dictionary<string, string> RequestHeaders { get; set; } = new();
    [JsonPropertyName("requestBody")] public string? RequestBody { get; set; }
    [JsonPropertyName("requestSize")] public int RequestSize { get; set; }
    [JsonPropertyName("responseHeaders")] public Dictionary<string, string> ResponseHeaders { get; set; } = new();
    [JsonPropertyName("responseBody")] public string? ResponseBody { get; set; }
    [JsonPropertyName("responseSize")] public int ResponseSize { get; set; }
    [JsonPropertyName("status")] public object Status { get; set; } = "in-flight";
    [JsonPropertyName("proxyLatencyMs")] public double? ProxyLatencyMs { get; set; }
    [JsonPropertyName("upstreamLatencyMs")] public double? UpstreamLatencyMs { get; set; }
    [JsonPropertyName("totalLatencyMs")] public double? TotalLatencyMs { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("sourceModel")] public string? SourceModel { get; set; }
    [JsonPropertyName("mappedModel")] public string? MappedModel { get; set; }
    [JsonPropertyName("detectedKind")] public string? DetectedKind { get; set; }
    [JsonPropertyName("contextKey")] public string? ContextKey { get; set; }
    [JsonPropertyName("annotation")] public string? Annotation { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("pid")] public int? Pid { get; set; }
    [JsonPropertyName("processName")] public string? ProcessName { get; set; }
}

/// <summary>List filters — port of ListFilters (InspectorListQuerySchema).</summary>
public sealed record ListFilters(string? Profile, string? Host, string? Agent,
    string? Status, string? Source, string? SessionId);

/// <summary>
/// Header/secret hygiene + kind detection + context-key fingerprint — ports of
/// mitm/sanitizeHeaders.ts, mitm/inspector/kindDetector.ts and contextKey.ts.
/// </summary>
public static partial class InspectorModel
{
    private const string Redacted = "[REDACTED]";

    // Forbidden upstream headers — verbatim port of FORBIDDEN in
    // src/shared/constants/upstreamHeaders.ts.
    private static readonly HashSet<string> Forbidden =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "host", "connection", "content-length", "keep-alive", "proxy-connection",
            "proxy-authenticate", "proxy-authorization", "transfer-encoding", "te",
            "trailer", "upgrade", "x-forwarded-for", "x-forwarded-host",
            "x-forwarded-proto", "x-forwarded-port", "x-forwarded-server",
            "x-real-ip", "cf-connecting-ip", "true-client-ip", "client-ip",
            "forwarded", "via",
        };

    private static readonly HashSet<string> MaskedCredentialHeaders =
        new(StringComparer.OrdinalIgnoreCase)
            { "authorization", "api-key", "x-api-key", "x-auth-token", "x-goog-api-key" };

    private static readonly HashSet<string> FullyRedactedHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "cookie", "set-cookie" };

    /// <summary>sanitizeHeaders port: drop forbidden, redact cookies, mask credentials.</summary>
    public static Dictionary<string, string> SanitizeHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var sanitized = new Dictionary<string, string>();
        foreach (var (rawName, rawValue) in headers)
        {
            var name = rawName.Trim().ToLowerInvariant();
            if (name.Length == 0 || Forbidden.Contains(name)) continue;
            if (FullyRedactedHeaders.Contains(name)) { sanitized[name] = Redacted; continue; }
            sanitized[name] = MaskedCredentialHeaders.Contains(name)
                ? SanitizeCredentialValue(rawValue) : rawValue;
        }
        return sanitized;
    }

    private static string SanitizeCredentialValue(string value)
    {
        var masked = MaskSecrets.Mask(value);
        return ReferenceEquals(masked, value) || masked == value ? Redacted : masked;
    }

    // ── detectKind (kindDetector.ts) ────────────────────────────────────────
    private static readonly string[] LlmHosts =
    [
        "api.openai.com", "api.anthropic.com", "generativelanguage.googleapis.com",
        "api.mistral.ai", "api.deepseek.com", "api.groq.com", "api.together.xyz",
        "api.fireworks.ai", "api.cohere.com", "api.perplexity.ai", "openrouter.ai",
        "api.x.ai", "api.moonshot.ai", "bigmodel.cn",
    ];
    private static readonly string[] LlmHostSuffixes =
        [".openai.azure.com", ".huggingface.co", ".bytedance.com", ".aliyun.com"];
    private static readonly string[] LlmPathPatterns =
    [
        "/v1/chat/completions", "/v1/completions", "/v1beta/", "/messages",
        "/embeddings", "/responses", "/models", "/generateContent", "/streamGenerateContent",
    ];
    private static readonly string[] LlmBodyKeys = ["messages", "contents", "prompt", "input", "model"];

    private static readonly string[] AgentUas =
        ["codex", "claude", "gemini", "antigravity", "kiro", "copilot", "cursor"];

    private const string ErrorStatus = "error";
    private const string InFlightStatus = "in-flight";

    private static bool IsLlmHost(string host)
        => LlmHosts.Any(h => host == h) || LlmHostSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal));

    private static bool IsLlmPath(string path)
        => LlmPathPatterns.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses the JSON body; returns "llm" on an LLM shape, "app" on other JSON, null otherwise.</summary>
    private static string? BodySignal(string? body)
    {
        if (body is null) return null;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject parsed) return null;
            return LlmBodyKeys.Any(k => parsed.ContainsKey(k)) ? "llm" : "app";
        }
        catch (JsonException) { return null; /* non-JSON body — cannot detect */ }
    }

    /// <summary>detectKind port — llm / app / unknown.</summary>
    public static string DetectKind(InterceptedRequest req)
    {
        if (IsLlmHost(req.Host.ToLowerInvariant()) || IsLlmPath(req.Path))
            return "llm";
        var signal = BodySignal(req.RequestBody);
        if (signal == "llm") return "llm";
        var ua = req.RequestHeaders.TryGetValue("user-agent", out var u) ? u : "";
        if (AgentUas.Any(a => ua.Contains(a, StringComparison.OrdinalIgnoreCase)))
            return "llm";
        return signal ?? "unknown";
    }

    /// <summary>computeContextKey port — 12-hex SHA-256 of the system prompt.</summary>
    public static string? ComputeContextKey(InterceptedRequest req)
    {
        var sys = ExtractSystemPrompt(req);
        if (sys is null) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sys)))
            .ToLowerInvariant()[..12];
    }

    /// <summary>extractSystemPrompt port — Anthropic `system`, OpenAI messages[0], Gemini systemInstruction.</summary>
    public static string? ExtractSystemPrompt(InterceptedRequest req)
    {
        if (req.RequestBody is null) return null;
        JsonObject? body;
        try { body = JsonNode.Parse(req.RequestBody) as JsonObject; }
        catch (JsonException) { return null; }
        if (body is null) return null;

        return SystemOfAnthropic(body) ?? SystemOfOpenAi(body) ?? SystemOfGemini(body);
    }

    /// <summary>Anthropic top-level `system`: string | array of {text}.</summary>
    private static string? SystemOfAnthropic(JsonObject body)
    {
        if (body["system"] is not { } sysNode) return null;
        if (sysNode is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
        if (sysNode is JsonArray arr) return JoinTextParts(arr);
        return null;
    }

    /// <summary>OpenAI `messages[0].role == system`: string | array of {text}.</summary>
    private static string? SystemOfOpenAi(JsonObject body)
    {
        if (body["messages"] is not JsonArray messages || messages.Count == 0) return null;
        if (messages[0] is not JsonObject first || first["role"]?.GetValue<string>() != "system") return null;
        var content = first["content"];
        if (content is JsonValue cv && cv.TryGetValue<string>(out var cs)) return cs;
        if (content is JsonArray ca) return JoinTextParts(ca);
        return null;
    }

    /// <summary>Gemini `systemInstruction.parts[].text`.</summary>
    private static string? SystemOfGemini(JsonObject body)
    {
        if (body["systemInstruction"] is not JsonObject si || si["parts"] is not JsonArray parts) return null;
        return JoinTextParts(parts);
    }

    private static string? JoinTextParts(JsonArray arr)
    {
        var parts = arr.Select(TextOfPart).Where(t => t is not null).ToList();
        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    private static string? TextOfPart(JsonNode? p)
        => p is JsonObject o && o["text"]?.GetValue<string>() is { } t ? t : null;

    /// <summary>Numeric status normalized over int/string/JsonElement round-trips.</summary>
    public static int? NumericStatus(object status) => status switch
    {
        int n => n,
        long l => (int)l,
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
        _ => null,
    };

    /// <summary>Status bucket for filtering — port of statusBucket.</summary>
    public static string StatusBucket(object status) => status switch
    {
        string s when s == ErrorStatus => ErrorStatus,
        string s when s == InFlightStatus => InFlightStatus,
        JsonElement je when je.ValueKind == JsonValueKind.String && je.GetString() == ErrorStatus => ErrorStatus,
        JsonElement je when je.ValueKind == JsonValueKind.String => InFlightStatus,
        _ when NumericStatus(status) is { } n => n switch
        {
            >= 200 and < 300 => "2xx",
            >= 300 and < 400 => "3xx",
            >= 400 and < 500 => "4xx",
            >= 500 and < 600 => "5xx",
            _ => "unknown",
        },
        _ => "unknown",
    };

    /// <summary>matchesFilters port.</summary>
    public static bool Matches(InterceptedRequest req, ListFilters? f)
    {
        if (f is null) return true;
        if (f.Profile is not null and not "all")
        {
            if (f.Profile == "llm" && req.DetectedKind != "llm") return false;
            if (f.Profile == "custom" && req.Source != "custom-host") return false;
        }
        if (f.Host is not null && req.Host != f.Host) return false;
        if (f.Agent is not null && req.Agent != f.Agent) return false;
        if (f.Source is not null && req.Source != f.Source) return false;
        if (f.SessionId is not null && req.SessionId != f.SessionId) return false;
        if (f.Status is not null && StatusBucket(req.Status) != f.Status) return false;
        return true;
    }
}

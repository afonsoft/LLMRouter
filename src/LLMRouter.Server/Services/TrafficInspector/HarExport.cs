using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMRouter.Server.Services.TrafficInspector;

/// <summary>
/// HAR v1.2 export — port of lib/inspector/harExport.ts. Secrets are always
/// masked on export; capture metadata rides in underscore custom fields.
/// </summary>
public static class HarExport
{
    private const string OctetStream = "application/octet-stream";

    private static JsonArray HeadersToList(Dictionary<string, string> headers)
    {
        var arr = new JsonArray();
        foreach (var (name, value) in headers)
            arr.Add(new JsonObject { ["name"] = name, ["value"] = MaskSecrets.Mask(value) });
        return arr;
    }

    private static string BuildUrl(string host, string path)
    {
        if (path.StartsWith(':')) return $"https://{host}{path}";
        return host.Length == 0 ? path : $"https://{host}{path}";
    }

    private static string HeaderOf(Dictionary<string, string> headers)
        => headers.TryGetValue("content-type", out var ct) ? ct : OctetStream;

    private static JsonObject BuildEntry(InterceptedRequest req)
    {
        var numericStatus = InspectorModel.NumericStatus(req.Status) ?? 0;
        var statusText = req.Status switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? "",
            _ => "",
        };
        var entry = new JsonObject
        {
            ["startedDateTime"] = req.Timestamp,
            ["time"] = req.TotalLatencyMs ?? 0,
            ["request"] = new JsonObject
            {
                ["method"] = req.Method,
                ["url"] = BuildUrl(req.Host, req.Path),
                ["httpVersion"] = "HTTP/1.1",
                ["headers"] = HeadersToList(req.RequestHeaders),
                ["queryString"] = new JsonArray(),
                ["cookies"] = new JsonArray(),
                ["headersSize"] = -1,
                ["bodySize"] = req.RequestSize,
            },
            ["response"] = new JsonObject
            {
                ["status"] = numericStatus,
                ["statusText"] = statusText,
                ["httpVersion"] = "HTTP/1.1",
                ["headers"] = HeadersToList(req.ResponseHeaders),
                ["cookies"] = new JsonArray(),
                ["content"] = new JsonObject
                {
                    ["size"] = req.ResponseSize,
                    ["mimeType"] = HeaderOf(req.ResponseHeaders),
                    ["text"] = req.ResponseBody is null ? null : MaskSecrets.Mask(req.ResponseBody),
                },
                ["redirectURL"] = "",
                ["headersSize"] = -1,
                ["bodySize"] = req.ResponseSize,
            },
            ["cache"] = new JsonObject(),
            ["timings"] = new JsonObject
            {
                ["send"] = 0,
                ["wait"] = req.UpstreamLatencyMs ?? 0,
                ["receive"] = (req.TotalLatencyMs ?? 0) - (req.UpstreamLatencyMs ?? 0),
            },
            ["_source"] = req.Source,
            ["_omniRouteId"] = req.Id,
        };
        if (req.RequestBody is not null)
            ((JsonObject)entry["request"]!)["postData"] = new JsonObject
            {
                ["mimeType"] = HeaderOf(req.RequestHeaders),
                ["text"] = MaskSecrets.Mask(req.RequestBody),
            };
        if (req.Agent is not null) entry["_agent"] = req.Agent;
        if (req.DetectedKind is not null) entry["_detectedKind"] = req.DetectedKind;
        if (req.ContextKey is not null) entry["_contextKey"] = req.ContextKey;
        if (req.SessionId is not null) entry["_sessionId"] = req.SessionId;
        if (req.Annotation is not null) entry["_annotation"] = req.Annotation;
        if (req.Note is not null) entry["_note"] = req.Note;
        return entry;
    }

    /// <summary>toHar port — HAR v1.2 file; always masks secrets.</summary>
    public static JsonObject ToHar(IEnumerable<InterceptedRequest> requests)
        => new()
        {
            ["log"] = new JsonObject
            {
                ["version"] = "1.2",
                ["creator"] = new JsonObject { ["name"] = "LLMRouter Traffic Inspector", ["version"] = "1.0.0" },
                ["entries"] = new JsonArray(requests.Select(BuildEntry).ToArray<JsonNode>()),
            },
        };
}

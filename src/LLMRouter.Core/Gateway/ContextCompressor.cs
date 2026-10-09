using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// Context compression (SPEC-026, upstream open-sse compress pipeline): when
/// settings.data.contextCompression.enabled and the estimated prompt size
/// (chars/4) exceeds maxTokens, drop middle messages while preserving every
/// system message and the most recent ones; a placeholder system message
/// marks the dropped window. Applied before upstream dispatch.
/// </summary>
public static class ContextCompressor
{
    public sealed record Result(JsonElement Body, int Dropped, bool Compressed);

    /// <summary>Estimate tokens as chars/4 across all message-ish text.</summary>
    public static int EstimateTokens(JsonElement body) => (int)(ScanChars(body) / 4);

    private static long ScanChars(JsonElement e)
    {
        long n = 0;
        switch (e.ValueKind)
        {
            case JsonValueKind.String: n += e.GetString()?.Length ?? 0; break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) n += ScanChars(p.Value);
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) n += ScanChars(x);
                break;
        }
        return n;
    }

    /// <summary>Compresses `messages`/`input` arrays; `contents` (gemini) too.
    /// Keeps all leading system/developer entries plus as many trailing
    /// entries as fit under the budget; the gap is marked by a placeholder.</summary>
    public static Result Apply(JsonElement body, int maxTokens)
    {
        if (maxTokens <= 0 || EstimateTokens(body) <= maxTokens)
            return new Result(body, 0, false);

        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        var dropped = 0;
        foreach (var key in new[] { "messages", "input", "contents" })
        {
            if (node[key] is not JsonArray arr || arr.Count <= 3) continue;
            var budgetChars = maxTokens * 4;

            var head = new List<JsonNode>(); // system/developer msgs from the front
            var i = 0;
            while (i < arr.Count && IsSystem(arr[i])) { head.Add(arr[i]!.DeepClone()); i++; }

            var rest = arr.Skip(i).Select(x => x!.DeepClone()).ToList();
            // keep trailing messages until budget exhausted
            var tail = new List<JsonNode>();
            long used = 0;
            for (var j = rest.Count - 1; j >= 0; j--)
            {
                var sz = ScanChars(JsonSerializer.SerializeToElement(rest[j]));
                if (used + sz > budgetChars && tail.Count > 0) break;
                tail.Insert(0, rest[j]); used += sz;
            }
            dropped = rest.Count - tail.Count;
            if (dropped <= 0) continue;

            var rebuilt = new JsonArray();
            foreach (var h in head) rebuilt.Add(h);
            rebuilt.Add(JsonNode.Parse(JsonSerializer.Serialize(new
            {
                role = "system",
                content = $"[context compressed: {dropped} earlier message(s) omitted]"
            })));
            foreach (var t in tail) rebuilt.Add(t);
            node[key] = rebuilt;
        }
        if (dropped == 0) return new Result(body, 0, false);
        return new Result(JsonDocument.Parse(node.ToJsonString()).RootElement.Clone(), dropped, true);
    }

    private static bool IsSystem(JsonNode? n) =>
        n is JsonObject o && o["role"]?.GetValue<string>() is "system" or "developer";

    /// <summary>Inject skill summaries into the prompt: appended to the first
    /// system message when present, else prepended as a new system entry.</summary>
    public static JsonElement InjectSystem(JsonElement body, string text)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        var arr = node["messages"] as JsonArray ?? node["input"] as JsonArray;
        if (arr is null) return body;
        var firstSys = arr.FirstOrDefault(IsSystem) as JsonObject;
        if (firstSys is not null && firstSys["content"] is JsonValue v && v.TryGetValue<string>(out var cur))
            firstSys["content"] = cur + "\n\n" + text;
        else
            arr.Insert(0, JsonNode.Parse(JsonSerializer.Serialize(new { role = "system", content = text })));
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }
}

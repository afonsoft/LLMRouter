using System.Text.Json.Nodes;

namespace LLMRouter.Core.Compression;

/// <summary>Shared helpers for reading/writing message text inside a chat body.</summary>
public static class TextOps
{
    public static int EstimateTokens(string? text) => text is { Length: > 0 } t ? (int)Math.Ceiling(t.Length / 4.0) : 0;

    public static JsonArray? Messages(JsonObject body) => body["messages"] as JsonArray;

    /// <summary>Extract the text of a message: string content, or the joined text parts of multipart content.</summary>
    public static string ExtractText(JsonNode? msg)
    {
        var c = msg?["content"];
        if (c is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (c is JsonArray arr)
            return string.Join("\n", arr.OfType<JsonObject>()
                .Where(p => p["type"]?.GetValue<string>() == "text")
                .Select(p => p["text"]?.GetValue<string>())
                .Where(t => !string.IsNullOrEmpty(t))!);
        return "";
    }

    /// <summary>Apply fn to every text part of a message's content (string → once; array → each {type:text} part).</summary>
    public static JsonObject MapText(JsonObject msg, Func<string, string> fn)
    {
        var clone = (JsonObject)msg.DeepClone();
        var c = clone["content"];
        if (c is JsonValue v && v.TryGetValue<string>(out var s))
            clone["content"] = fn(s);
        else if (c is JsonArray arr)
        {
            foreach (var p in arr.OfType<JsonObject>())
                if (p["type"]?.GetValue<string>() == "text" && p["text"] is JsonValue tv && tv.TryGetValue<string>(out var t))
                    p["text"] = fn(t);
        }
        return clone;
    }

    public static JsonObject SetText(JsonObject msg, string text)
    {
        var clone = (JsonObject)msg.DeepClone();
        var c = clone["content"];
        if (c is JsonArray arr)
        {
            var first = arr.OfType<JsonObject>().FirstOrDefault(p => p["type"]?.GetValue<string>() == "text");
            if (first is not null) { first["text"] = text; return clone; }
        }
        clone["content"] = text;
        return clone;
    }

    public static string Role(JsonNode? msg) => msg?["role"]?.GetValue<string>() ?? "";

    /// <summary>Total chars of all message text in the body.</summary>
    public static int BodyTextChars(JsonObject body) =>
        (Messages(body)?.Sum(m => ExtractText(m).Length)) ?? 0;

    public static int LastIndexOfRole(JsonArray messages, string role)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (Role(messages[i]) == role) return i;
        return -1;
    }

    /// <summary>First index of the current turn: just after the last assistant message (or end if none).</summary>
    public static int CurrentTurnStart(JsonArray messages)
    {
        var last = LastIndexOfRole(messages, "assistant");
        return last < 0 ? messages.Count : last + 1;
    }

    public static JsonObject NewBody(JsonObject original, JsonArray messages)
    {
        var clone = (JsonObject)original.DeepClone();
        clone["messages"] = messages;
        return clone;
    }

    public static JsonArray CloneMessages(JsonArray? messages) =>
        (JsonArray)(messages?.DeepClone() ?? new JsonArray());
}

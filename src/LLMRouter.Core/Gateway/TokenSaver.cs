using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// Token saver (SPEC-008, upstream /dashboard/token-saver): lightweight prompt
/// compression applied before the upstream call when
/// settings.data.tokenSaver.enabled — collapses whitespace runs, drops empty
/// lines, dedups consecutive identical lines inside message content strings.
/// Returns the rewritten body plus the count of saved characters (~tokens/4).
/// </summary>
public static partial class TokenSaver
{
    public sealed record Result(JsonElement Body, int SavedChars);

    public static Result Apply(JsonElement body, bool dedupLines = true)
    {
        var node = JsonNode.Parse(body.GetRawText());
        var saved = 0;
        if (node is JsonObject o) CompressNode(o, dedupLines, ref saved);
        return new(JsonSerializer.SerializeToElement(node), saved);
    }

    /// <summary>Count of tokens this body roughly saved → persisted on the usage row.</summary>
    public static int SavedTokens(int savedChars) => savedChars / 4;

    private static void CompressNode(JsonObject node, bool dedup, ref int saved)
    {
        foreach (var kv in node.ToList())
        {
            switch (kv.Value)
            {
                case JsonObject o: CompressNode(o, dedup, ref saved); break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        if (a[i] is JsonObject ao) CompressNode(ao, dedup, ref saved);
                        else if (a[i] is JsonArray aa) CompressArray(aa, dedup, ref saved);
                    }
                    break;
                case JsonValue v when v.TryGetValue<string>(out var s)
                                   && (kv.Key is "content" or "text" or "prompt" or "system"
                                       || s.Contains('\n')):
                    var compressed = Compress(s, dedup);
                    saved += s.Length - compressed.Length;
                    if (compressed != s) node[kv.Key] = compressed;
                    break;
            }
        }
    }

    private static void CompressArray(JsonArray arr, bool dedup, ref int saved)
    {
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonObject o) CompressNode(o, dedup, ref saved);
            else if (arr[i] is JsonArray a) CompressArray(a, dedup, ref saved);
            else if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains('\n'))
            {
                var c = Compress(s, dedup);
                saved += s.Length - c.Length;
                if (c != s) arr[i] = c;
            }
        }
    }

    public static string Compress(string s, bool dedup = true)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n');
        var outLines = new List<string>(lines.Length);
        string? prev = null;
        foreach (var raw in lines)
        {
            var line = SpaceRun().Replace(raw.Trim(), " ");
            if (line.Length == 0) { prev = null; continue; }
            if (dedup && line == prev) continue;
            outLines.Add(line);
            prev = line;
        }
        return string.Join('\n', outLines).Trim();
    }

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpaceRun();
}

using System.Text.RegularExpressions;

namespace LLMRouter.Core.Compression;

/// <summary>
/// Tombstone protected constructs (fenced code, inline code, URLs, markdown links,
/// user preservePatterns) so prose engines never mangle them; restore verbatim after.
/// Port of upstream preservation.ts (simplified to the public contract).
/// </summary>
public static partial class Preservation
{
    public sealed record Block(string Placeholder, string Content);

    [GeneratedRegex(@"(?s)```.*?```|~~~.*?~~~|`[^`\n]+`|https?://[^\s)\]>""']+|\[[^\]\n]{1,1000}\]\([^)\n]{1,2000}\)", RegexOptions.Compiled)]
    private static partial Regex ProtectedRe();

    public static (string Text, List<Block> Blocks) Extract(string text, IReadOnlyList<Regex>? extraPatterns = null)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrEmpty(text)) return (text, blocks);

        string Tombstone(Match m)
        {
            var ph = $"⟦P{blocks.Count}⟧";
            blocks.Add(new Block(ph, m.Value));
            return ph;
        }

        var result = ProtectedRe().Replace(text, Tombstone);
        if (extraPatterns is not null)
            foreach (var re in extraPatterns)
                result = re.Replace(result, Tombstone);
        return (result, blocks);
    }

    public static string Restore(string text, IReadOnlyList<Block> blocks)
    {
        foreach (var b in blocks)
            text = text.Replace(b.Placeholder, b.Content);
        return text;
    }

    /// <summary>Split prose from preserved spans so a transform only runs on prose.</summary>
    public static string TransformProseOnly(string text, Func<string, string> fn)
    {
        var (withPh, blocks) = Extract(text);
        if (blocks.Count == 0) return fn(text);
        var parts = new List<string>();
        var cursor = 0;
        foreach (var b in blocks)
        {
            var idx = withPh.IndexOf(b.Placeholder, cursor, StringComparison.Ordinal);
            if (idx < 0) continue;
            if (idx > cursor) parts.Add(fn(withPh[cursor..idx]));
            parts.Add(b.Content);
            cursor = idx + b.Placeholder.Length;
        }
        if (cursor < withPh.Length) parts.Add(fn(withPh[cursor..]));
        return string.Concat(parts);
    }
}

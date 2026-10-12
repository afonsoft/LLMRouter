using System.Text;

namespace LLMRouter.Server.Services.TrafficInspector;

/// <summary>
/// Linear secret scanner for captured Traffic Inspector text — verbatim port
/// of src/mitm/maskSecrets.ts. Bearer credentials take precedence over
/// provider-style prefixed keys, which take precedence over opaque tokens.
/// </summary>
public static class MaskSecrets
{
    private const int PrefixedKeyBodyMin = 16;
    private const int OpaqueTokenMin = 40;

    private readonly record struct Match(int Start, int End, string Replacement);

    private static bool IsKeyChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
    private static bool IsOpaqueChar(char c) => IsKeyChar(c) || c is '.' or '~' or '+' or '/';

    private static bool EqualsIgnoreCase(string v, int i, string expected)
    {
        if (i + expected.Length > v.Length) return false;
        for (var o = 0; o < expected.Length; o++)
            if (char.ToLowerInvariant(v[i + o]) != expected[o]) return false;
        return true;
    }

    private static int SkipPadding(string v, int end)
    {
        while (end < v.Length && v[end] == '=') end++;
        return end;
    }

    private static Match? BearerMatch(string v, int i)
    {
        if (!EqualsIgnoreCase(v, i, "bearer")) return null;
        if (i > 0 && IsKeyChar(v[i - 1])) return null;
        var schemeEnd = i + 6;
        if (schemeEnd >= v.Length || v[schemeEnd] != ' ') return null;
        var tokenStart = schemeEnd;
        while (tokenStart < v.Length && v[tokenStart] == ' ') tokenStart++;
        var end = tokenStart;
        while (end < v.Length && IsOpaqueChar(v[end])) end++;
        if (end == tokenStart) return null;
        end = SkipPadding(v, end);
        return new(tokenStart, end, "***");
    }

    private static Match? PrefixedKeyMatch(string v, int i)
    {
        if (i > 0 && IsOpaqueChar(v[i - 1])) return null;
        if (i + 3 > v.Length) return null;
        var prefix = v.Substring(i, 3);
        if (prefix is not ("sk-" or "ak-" or "pk-")) return null;
        var end = i + 3;
        while (end < v.Length && IsOpaqueChar(v[end])) end++;
        if (end - (i + 3) < PrefixedKeyBodyMin) return null;
        end = SkipPadding(v, end);
        var token = v[i..end];
        return new(i, end, $"{token[..6]}…{token[^2..]}");
    }

    private static Match? OpaqueTokenMatch(string v, int i)
    {
        if (!IsOpaqueChar(v[i])) return null;
        if (i > 0 && IsOpaqueChar(v[i - 1])) return null;
        var end = i + 1;
        while (end < v.Length && IsOpaqueChar(v[end])) end++;
        if (end - i < OpaqueTokenMin) return null;
        end = SkipPadding(v, end);
        var token = v[i..end];
        return new(i, end, $"{token[..4]}…{token[^2..]}");
    }

    /// <summary>Mask Bearer credentials, provider-style keys, and long opaque tokens.</summary>
    public static string Mask(string value)
    {
        var output = new StringBuilder();
        var literalStart = 0;
        var index = 0;
        while (index < value.Length)
        {
            var match = BearerMatch(value, index) ?? PrefixedKeyMatch(value, index) ?? OpaqueTokenMatch(value, index);
            if (match is null) { index++; continue; }
            var m = match.Value;
            output.Append(value, literalStart, m.Start - literalStart);
            output.Append(m.Replacement);
            index = m.End;
            literalStart = m.End;
        }
        if (literalStart == 0) return value;
        return output.Append(value, literalStart, value.Length - literalStart).ToString();
    }
}

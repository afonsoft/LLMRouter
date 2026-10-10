using System.Text.RegularExpressions;

namespace LLMRouter.Core.Data;

/// <summary>Strict-identifier quoting for the rare spots that build SQLite DDL
/// strings (backup export/import). SQLite cannot parameterize identifiers, so
/// we whitelist instead of escaping — anything outside [A-Za-z0-9_] is rejected.</summary>
public static partial class SqliteIdent
{
    private static readonly Regex Valid = ValidIdent();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$", RegexOptions.None, 100)]
    private static partial Regex ValidIdent();

    public static bool TryQuote(string name, out string quoted)
    {
        quoted = "";
        if (string.IsNullOrEmpty(name) || !Valid.IsMatch(name)) return false;
        quoted = $"\"{name}\"";
        return true;
    }

    public static string Quote(string name) =>
        TryQuote(name, out var q) ? q : throw new ArgumentException($"unsafe sqlite identifier: {name}");
}

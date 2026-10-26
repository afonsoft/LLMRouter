using System.Globalization;
using System.Text;

namespace LLMRouter.Shared.Ux;

public static class CsvWriter
{
    public static string Write<T>(IEnumerable<T> rows, params (string Header, Func<T, object?> Value)[] columns)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", columns.Select(x => Escape(x.Header)))).Append("\r\n");
        foreach (var row in rows)
            sb.Append(string.Join(",", columns.Select(x => Escape(Convert.ToString(x.Value(row), CultureInfo.InvariantCulture))))).Append("\r\n");
        return sb.ToString();
    }

    private static string Escape(string? value)
    {
        value ??= "";
        return value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}

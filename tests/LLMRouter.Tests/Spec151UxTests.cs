using LLMRouter.Shared.Ux;
using Shouldly;

namespace LLMRouter.Tests;

public sealed class Spec151UxTests
{
    [Fact]
    public void Url_filters_round_trip_encoded_values()
    {
        var query = UrlFilters.Build([
            new("q", "model/gpt 4"),
            new("starred", "true"),
            new("empty", null)
        ]);

        var parsed = UrlFilters.Parse(query);
        parsed["q"].ShouldBe("model/gpt 4");
        parsed["starred"].ShouldBe("true");
        parsed.ContainsKey("empty").ShouldBeFalse();
    }

    [Fact]
    public void Compare_window_is_previous_equal_length_period()
    {
        var range = TimeRange.FromQuery("2026-01-10T00:00:00Z", "2026-01-17T00:00:00Z", true);

        range.CompareFrom.ShouldBe(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));
        range.CompareTo.ShouldBe(new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Csv_writer_quotes_commas_quotes_and_newlines()
    {
        var csv = CsvWriter.Write(
            new[] { new Row("a,b", "line\n\"quoted\"") },
            ("Name", (Row x) => x.Name),
            ("Detail", (Row x) => x.Detail));

        csv.ShouldBe("Name,Detail\r\n\"a,b\",\"line\n\"\"quoted\"\"\"\r\n");
    }

    private sealed record Row(string Name, string Detail);
}

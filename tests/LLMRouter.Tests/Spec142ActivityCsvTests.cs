using Shouldly;
using LLMRouter.Core.Data;

namespace LLMRouter.Tests;

/// <summary>SPEC-142: GET /api/usage/timeseries-csv — usage rows as CSV for the activity export button.</summary>
public class Spec142ActivityCsvTests : WebAppTestBase
{
    [Fact]
    public async Task Csv_returns_usage_rows()
    {
        var db = Db();
        db.UsageHistory.Add(new UsageRecord
        {
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            Provider = "openai", Model = "gpt-4o", ApiKey = "k1", Endpoint = "chat",
            Status = "200", Tokens = "12", PromptTokens = 8, CompletionTokens = 4,
            LatencyMs = 42, Cost = 0.001, Meta = "{}",
        });
        await db.SaveChangesAsync();

        await LoginAsync();
        var r = await Client.GetAsync("/api/usage/timeseries-csv?range=week");
        r.EnsureSuccessStatusCode();
        r.Content.Headers.ContentType?.MediaType.ShouldBe("text/csv");
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldStartWith("timestamp,provider,model");
        body.ShouldContain("openai");
        body.ShouldContain("gpt-4o");
    }
}

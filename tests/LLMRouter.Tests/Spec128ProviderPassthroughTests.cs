using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-128: per-provider passthrough fidelity — upstream route-level validation
/// (non-object/non-string-model/invalid-json 400s), cgpt-web 410 retirement,
/// voices POST multipart guard, OPTIONS CORS preflight headers.
/// </summary>
public class Spec128ProviderPassthroughTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec128ProviderPassthroughTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private HttpClient Dash()
    {
        var c = _factory.CreateClient();
        c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    private async Task SeedConn(string provider)
    {
        _ = Dash(); // boot the app so migrations create the schema first
        using var db = Db();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = provider,
            AuthType = "api-key",
            Data = "{}",
            CreatedAt = DateTime.UtcNow.ToString("o"),
            UpdatedAt = DateTime.UtcNow.ToString("o"),
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string> ErrorMessage(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetProperty("message").GetString()!;
    }

    [Fact]
    public async Task Options_ReturnsUpstreamCorsHeaders()
    {
        var c = Dash();
        var req = new HttpRequestMessage(HttpMethod.Options, "/v1/providers/testprov/chat/completions");
        var r = await c.SendAsync(req);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        r.Headers.GetValues("Access-Control-Allow-Methods").ShouldContain("GET, POST, OPTIONS");
        r.Headers.GetValues("Access-Control-Allow-Headers").ShouldContain("*");
    }

    [Fact]
    public async Task CgptWeb_Returns410Retired()
    {
        var c = Dash();
        var r = await c.PostAsJsonAsync("/v1/providers/cgpt-web/chat/completions",
            new { model = "m", messages = Array.Empty<object>() });
        r.StatusCode.ShouldBe(HttpStatusCode.Gone);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var err = doc.RootElement.GetProperty("error");
        err.GetProperty("message").GetString().ShouldBe("Provider is retired and unavailable.");
        err.GetProperty("type").GetString().ShouldBe("provider_error");
        err.GetProperty("code").GetString().ShouldBe("PROVIDER_RETIRED");
    }

    [Fact]
    public async Task NonObjectBody_400()
    {
        await SeedConn("spec128p");
        var c = Dash();
        var r = await c.PostAsync("/v1/providers/spec128p/chat/completions",
            new StringContent("[1,2]", Encoding.UTF8, "application/json"));
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorMessage(r)).ShouldBe("Request body must be a JSON object");
    }

    [Fact]
    public async Task NonStringModel_400()
    {
        await SeedConn("spec128p");
        var c = Dash();
        var r = await c.PostAsync("/v1/providers/spec128p/chat/completions",
            new StringContent("{\"model\":123}", Encoding.UTF8, "application/json"));
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorMessage(r)).ShouldBe("model must be a string");
    }

    [Fact]
    public async Task InvalidJson_400()
    {
        await SeedConn("spec128p");
        var c = Dash();
        var r = await c.PostAsync("/v1/providers/spec128p/chat/completions",
            new StringContent("not-json{", Encoding.UTF8, "application/json"));
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorMessage(r)).ShouldBe("Invalid JSON body");
    }

    [Fact]
    public async Task MismatchedPrefix_400()
    {
        await SeedConn("spec128p");
        var c = Dash();
        var r = await c.PostAsJsonAsync("/v1/providers/spec128p/chat/completions",
            new { model = "other/m", messages = Array.Empty<object>() });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorMessage(r)).ShouldContain("does not belong to provider \"spec128p\"");
    }

    [Fact]
    public async Task UnknownProvider_400()
    {
        var c = Dash();
        var r = await c.PostAsJsonAsync("/v1/providers/definitely-not-a-provider/chat/completions",
            new { model = "m", messages = Array.Empty<object>() });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorMessage(r)).ShouldContain("Unknown provider:");
    }

    [Fact]
    public async Task UnprefixedModel_PassesShapeChecks()
    {
        await SeedConn("spec128p");
        var c = Dash();
        // Valid shape — must NOT fail with any of the route-level 400s. The
        // downstream pipeline errors on missing creds/models, which is fine.
        var r = await c.PostAsJsonAsync("/v1/providers/spec128p/chat/completions",
            new { model = "m", messages = Array.Empty<object>() });
        if (r.StatusCode == HttpStatusCode.BadRequest)
        {
            var msg = await ErrorMessage(r);
            msg.ShouldNotBe("Request body must be a JSON object");
            msg.ShouldNotBe("model must be a string");
            msg.ShouldNotContain("does not belong to provider");
        }
    }

    [Fact]
    public async Task VoicesPost_NonMultipart_400()
    {
        var c = Dash();
        var r = await c.PostAsJsonAsync("/v1/providers/fishaudio/voices", new { title = "t" });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorMessage(r)).ShouldBe("Fish Audio voice creation requires multipart/form-data");
    }
}

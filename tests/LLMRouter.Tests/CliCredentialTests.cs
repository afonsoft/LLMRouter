using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Extras;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-035: CLI credential scan + import.</summary>
public class CliCredentialTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public CliCredentialTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private async Task LoginAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Scanner_finds_credentials_in_known_paths()
    {
        var home = Path.Combine(Path.GetTempPath(), $"cli-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        Directory.CreateDirectory(Path.Combine(home, ".local/share/opencode"));
        try
        {
            File.WriteAllText(Path.Combine(home, ".claude/.credentials.json"),
                """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-FAKEcred1234567890abcdef","expiresAt":9999999999999}}""");
            File.WriteAllText(Path.Combine(home, ".codex/auth.json"),
                """{"OPENAI_API_KEY":"sk-FAKEcodex1234567890abcdef"}""");
            File.WriteAllText(Path.Combine(home, ".local/share/opencode/auth.json"),
                """{"deepseek":{"key":"sk-FAKEdeepseek1234567890abc"},"xai":{"key":"xai-FAKExai1234567890abcdef"}}""");

            var findings = CliCredentialScanner.Scan(home);
            findings.Count.ShouldBeGreaterThanOrEqualTo(4);
            findings.ShouldContain(f => f.Tool == "claude" && f.CredentialType == "access_token");
            findings.ShouldContain(f => f.Tool == "codex" && f.Provider == "openai" && f.CredentialType == "api_key");
            findings.ShouldContain(f => f.Provider == "deepseek");
            findings.ShouldContain(f => f.Provider == "xai");
            findings.All(f => f.Masked.Contains('…') && !f.Masked.Contains(f.Value)).ShouldBeTrue();
            findings.Select(f => f.Value).Distinct().Count().ShouldBe(findings.Count); // dedup by value
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void Scanner_tolerates_missing_and_malformed_files()
    {
        var home = Path.Combine(Path.GetTempPath(), $"cli-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        try
        {
            File.WriteAllText(Path.Combine(home, ".codex/auth.json"), "not json at all");
            CliCredentialScanner.Scan(home).ShouldBeEmpty();
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void Scanner_ignores_short_and_nonsecret_values()
    {
        var home = Path.Combine(Path.GetTempPath(), $"cli-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        try
        {
            File.WriteAllText(Path.Combine(home, ".codex/auth.json"),
                """{"apiKey":"short","theme":"dark","api_key":"sk-FAKEvalid123456789abc"}""");
            var findings = CliCredentialScanner.Scan(home);
            findings.Count.ShouldBe(1);
            findings[0].Value.ShouldBe("sk-FAKEvalid123456789abc");
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task Scan_endpoint_returns_findings_shape()
    {
        await LoginAsync();
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/cli-credentials/scan");
        r.TryGetProperty("findings", out var arr).ShouldBeTrue();
        arr.ValueKind.ShouldBe(JsonValueKind.Array);
        r.TryGetProperty("total", out _).ShouldBeTrue();
        foreach (var f in arr.EnumerateArray())
        {
            f.TryGetProperty("masked", out _).ShouldBeTrue();
            f.TryGetProperty("provider", out _).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Import_with_unknown_ids_is_noop_and_requires_known_provider()
    {
        await LoginAsync();
        var r = await _client.PostAsJsonAsync("/api/cli-credentials/import", new { ids = new[] { "bogus:1" } });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("imported").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Import_creates_provider_connection_and_dedups()
    {
        await LoginAsync();
        // plant a credential file under the REAL home (cleaned up after)
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".codex");
        var file = Path.Combine(dir, "auth.json");
        var preExisting = File.Exists(file) ? File.ReadAllText(file) : null;
        var fakeKey = $"sk-FAKEimport{Guid.NewGuid():N}";
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, $"{{\"OPENAI_API_KEY\":\"{fakeKey}\"}}");
        try
        {
            var r1 = await _client.PostAsJsonAsync("/api/cli-credentials/import", new { });
            var b1 = await r1.Content.ReadFromJsonAsync<JsonElement>();
            b1.GetProperty("imported").GetInt32().ShouldBeGreaterThanOrEqualTo(1);

            var conns = await _client.GetFromJsonAsync<JsonElement>("/api/provider-connections");
            var match = conns.GetProperty("connections").EnumerateArray()
                .Where(c => c.GetProperty("data").GetString()!.Contains(fakeKey)).ToArray();
            match.Length.ShouldBe(1);
            match[0].GetProperty("provider").GetString().ShouldBe("openai");

            // second import → dedup, imported 0 skipped ≥1
            var r2 = await _client.PostAsJsonAsync("/api/cli-credentials/import", new { });
            var b2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
            b2.GetProperty("imported").GetInt32().ShouldBe(0);
            b2.GetProperty("skipped").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            if (preExisting is null) File.Delete(file); else File.WriteAllText(file, preExisting);
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Registry;
using LLMRouter.Server.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Shouldly;

namespace LLMRouter.Tests;

public class CliManifestTests
{
    [Fact]
    public void Manifest_has_coding_clis_with_env_templates()
    {
        CliToolsManifest.Tools.Length.ShouldBeGreaterThanOrEqualTo(8);
        foreach (var t in CliToolsManifest.Tools)
        {
            t.Binary.ShouldNotBeNullOrEmpty();
            t.Env.ShouldNotBeEmpty();
            t.Env.Values.ShouldAllBe(v => v.Contains("{baseUrl}") || v.Contains("{apiKey}"));
        }
    }

    [Theory]
    [InlineData("claude-code", "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN")]
    [InlineData("codex", "OPENAI_BASE_URL", "OPENAI_API_KEY")]
    [InlineData("gemini-cli", "GEMINI_BASE_URL", "GEMINI_API_KEY")]
    public void Config_generation_resolves_placeholders(string id, string urlVar, string keyVar)
    {
        var t = CliToolsManifest.Tools.First(x => x.Id == id);
        var env = CliToolsManifest.EnvFor(t, "http://localhost:5159", "llr_test123");
        env[urlVar].ShouldContain("http://localhost:5159");
        env[keyVar].ShouldBe("llr_test123");
        CliToolsManifest.ExportBlock(t, "http://h:1", "k").ShouldContain("export ");
    }
}

public class SkillParseTests
{
    [Fact]
    public void Frontmatter_parse_extracts_name_and_description()
    {
        var dir = Path.Combine(Path.GetTempPath(), "skills-" + Guid.NewGuid().ToString("N")[..6], "my-skill");
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(f, "---\nname: my-skill\ndescription: does things\n---\n# Body\n");
        var (name, desc) = ToolsEndpoints.ParseSkill(f);
        name.ShouldBe("my-skill");
        desc.ShouldBe("does things");
    }
}

/// <summary>Endpoint-level checks for /api/cli-tools + /api/translator.</summary>
public class ToolsEndpointsRoundTripTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ToolsEndpointsRoundTripTests()
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

    private async Task LoginAsync() =>
        (await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public async Task Translator_endpoint_converts_openai_to_claude()
    {
        await LoginAsync();
        var r = await _client.PostAsJsonAsync("/api/translator", new
        {
            from = "openai", to = "claude", kind = "request", model = "m",
            body = JsonSerializer.Deserialize<JsonElement>("""{"model":"m","messages":[{"role":"user","content":"yo"}]}"""),
        });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).ShouldContain("yo");
    }

    [Fact]
    public async Task Cli_tools_endpoint_lists_manifest_with_detection()
    {
        await LoginAsync();
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/cli-tools");
        var tools = r.GetProperty("tools").EnumerateArray().ToArray();
        tools.Length.ShouldBeGreaterThanOrEqualTo(8);
        tools.All(t => t.TryGetProperty("detected", out _)).ShouldBeTrue();
        tools.First(t => t.GetProperty("id").GetString() == "codex")
            .GetProperty("env").GetProperty("OPENAI_API_KEY").GetString().ShouldBe("<your-api-key>");
    }

    [Fact]
    public async Task Cli_tool_config_endpoint_renders_export_block()
    {
        await LoginAsync();
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/cli-tools/claude-code/config?key=k1");
        r.GetProperty("envBlock").GetString()!.ShouldContain("ANTHROPIC_AUTH_TOKEN=\"k1\"");
    }

    [Fact]
    public async Task Skills_endpoint_lists_bundled_skills()
    {
        await LoginAsync();
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/skills");
        r.GetProperty("skills").EnumerateArray()
            .ShouldContain(s => s.GetProperty("name").GetString() == "token-saver");
    }
}

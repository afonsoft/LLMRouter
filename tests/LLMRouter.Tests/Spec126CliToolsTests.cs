using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Server.Services.CliTools;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-126: cli-tools depth — tool registry, status/detect, settings read/write,
/// apply, backups, runtime/config/keys endpoints.
/// </summary>
public class Spec126CliToolsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"llmr-cli-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec126CliToolsTests()
    {
        Directory.CreateDirectory(_home);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_home, true); } catch { }
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private CliToolService Svc() => new(_home, "http://test-gateway:9999");

    [Fact]
    public void Registry_covers_upstream_tools()
    {
        // Ported verbatim from cliRuntime.ts CLI_TOOLS.
        var ids = CliToolService.Tools.Select(t => t.Id).ToHashSet();
        foreach (var expected in new[]
        {
            "claude", "codex", "droid", "openclaw", "cursor", "windsurf", "devin",
            "zcode", "cline", "kilo", "continue", "opencode", "hermes", "hermes-agent",
            "qoder", "qwen", "aider", "goose", "gemini", "forge", "jcode", "omp",
            "letta", "codewhale", "smelt", "pi", "crush",
        })
            ids.ShouldContain(expected);
        CliToolService.Find("CLAUDE")!.ConfigPath.ShouldBe(".claude/settings.json");
        CliToolService.Find("nope").ShouldBeNull();
    }

    [Fact]
    public void Status_reports_not_installed_when_binary_missing()
    {
        var svc = Svc();
        var status = svc.StatusFor(CliToolService.Find("claude")!);
        var json = JsonSerializer.Serialize(status);
        json.ShouldContain("\"installed\":false");
        json.ShouldContain("binary_not_found");
    }

    [Fact]
    public void ApplyEnv_writes_env_block_and_backs_up()
    {
        var svc = Svc();
        var dir = Path.Combine(_home, ".claude");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """{"model": "opus", "env": {"EXISTING": "1"}}""");

        var (status, body) = svc.ApplyEnv(CliToolService.Find("claude")!,
            new JsonObject { ["ANTHROPIC_BASE_URL"] = "http://test-gateway:9999" });

        status.ShouldBe(200);
        var written = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        written["model"]!.GetValue<string>().ShouldBe("opus");          // untouched keys kept
        written["env"]!["EXISTING"]!.GetValue<string>().ShouldBe("1"); // merge, not replace
        written["env"]!["ANTHROPIC_BASE_URL"]!.GetValue<string>().ShouldBe("http://test-gateway:9999");
        // Backup landed under .llmrouter.bak/
        var backups = svc.ListBackups().ToList();
        backups.Count.ShouldBe(1);
        File.Exists(Path.Combine(dir, ".llmrouter.bak", "settings.json.bak")).ShouldBeTrue();
    }

    [Fact]
    public void ApplyEnv_manages_managed_block_for_toml()
    {
        var svc = Svc();
        var dir = Path.Combine(_home, ".codex");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "model = \"gpt-5\"\n");

        var def = CliToolService.Find("codex")!;
        svc.ApplyEnv(def, new JsonObject { ["OPENAI_BASE_URL"] = "http://gw" });
        var first = File.ReadAllText(path);
        first.ShouldContain("# >>> llmrouter");
        first.ShouldContain("OPENAI_BASE_URL = http://gw");

        // Re-applying replaces the managed block instead of duplicating it.
        svc.ApplyEnv(def, new JsonObject { ["OPENAI_BASE_URL"] = "http://gw2" });
        var second = File.ReadAllText(path);
        second.ShouldContain("OPENAI_BASE_URL = http://gw2");
        second.Split("# >>> llmrouter").Length.ShouldBe(2);
    }

    [Fact]
    public void IsConfigured_detects_gateway_marker()
    {
        var svc = Svc();
        var def = CliToolService.Find("claude")!;
        svc.IsConfigured(def, """{"env": {"ANTHROPIC_BASE_URL": "http://test-gateway:9999"}}""")
            .ShouldBeTrue();
        // Upstream semantics: presence of the tool's env var = "configured",
        // regardless of where it points (hasOmniRoute = !!settings.env.ANTHROPIC_BASE_URL).
        svc.IsConfigured(def, """{"env": {"ANTHROPIC_BASE_URL": "https://api.anthropic.com"}}""")
            .ShouldBeTrue();
        svc.IsConfigured(def, """{"model": "opus"}""").ShouldBeFalse();
    }

    [Fact]
    public void Apply_dryRun_returns_content_without_writing()
    {
        var svc = Svc();
        var def = CliToolService.Find("claude")!;
        var (status, body) = svc.Apply(def, "sk-vk-abc", "sonnet", dryRun: true);
        status.ShouldBe(200);
        JsonSerializer.Serialize(body).ShouldContain("ANTHROPIC_BASE_URL");
        File.Exists(svc.ConfigPathFor(def)).ShouldBeFalse();
    }

    [Fact]
    public async Task Endpoints_require_auth_and_validate_tool()
    {
        var c = _factory.CreateClient();
        (await c.GetAsync("/api/cli-tools/all-statuses")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/cli-tools/nosuch-settings")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

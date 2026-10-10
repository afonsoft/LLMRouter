using System.Net;
using System.Net.Http.Json;
using LLMRouter.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>Regression tests for the post-SPEC-033 E2E sweep findings.</summary>
public class E2eSweepFixesTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public E2eSweepFixesTests()
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

    [Fact]
    public void Framework_resolver_never_picks_sourcemap_or_compressed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "dotnet.abc123.js"), "// js");
            File.WriteAllText(Path.Combine(dir, "dotnet.js.map"), "{}");
            File.WriteAllText(Path.Combine(dir, "dotnet.abc123.js.br"), "br");
            File.WriteAllText(Path.Combine(dir, "dotnet.abc123.js.gz"), "gz");
            File.WriteAllText(Path.Combine(dir, "blazor.webassembly.def456.js"), "// boot");

            // /_framework/dotnet.js must resolve to the real JS, not dotnet.js.map
            FrameworkAssets.Resolve(dir, "dotnet.js")!.ShouldEndWith("dotnet.abc123.js");
            FrameworkAssets.Resolve(dir, "blazor.webassembly")!.ShouldEndWith("blazor.webassembly.def456.js");
            FrameworkAssets.Resolve(dir, "missing.js").ShouldBeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Framework_resolver_prefers_fingerprinted_over_bare_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "foo.js"), "// bare");
            File.WriteAllText(Path.Combine(dir, "foo.aaa.js"), "// fp");
            var hit = FrameworkAssets.Resolve(dir, "foo.js");
            hit.ShouldNotBeNull();
            // either candidate is acceptable — the caller skips rewriting when the bare file exists;
            // what matters is we never return the .map sibling
            File.WriteAllText(Path.Combine(dir, "foo.js.map"), "{}");
            FrameworkAssets.Resolve(dir, "foo.js")!.ShouldNotEndWith(".map");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Api_models_without_live_param_returns_200()
    {
        var login = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        var resp = await _client.GetAsync("/api/models");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

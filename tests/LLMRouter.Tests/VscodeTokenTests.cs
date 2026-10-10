using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-051: vscode tokens — /v1/vscode/{token}/* Ollama + OpenAI surface.</summary>
public class VscodeTokenTests : IDisposable
{
    private sealed class CapturingUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":2,"completion_tokens":3}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public VscodeTokenTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                    c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
                .ConfigureServices(s =>
                    s.AddHttpClient("upstream").ConfigurePrimaryHttpMessageHandler(() => new CapturingUpstream())));
    }

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    private HttpClient LoginClient()
    {
        var c = _factory.CreateClient();
        c.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Result
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return c;
    }

    private async Task<string> SeedAsync(string[]? allowed = null)
    {
        await using var db = Db();
        db.EnsureCreated();
        db.ProviderConnections.Add(new ProviderConnection
        {
            Id = "c1", Provider = "openai", Name = "main", IsActive = true,
            Data = """{"baseUrl":"http://fake"}""", CreatedAt = "x", UpdatedAt = "x",
        });
        db.Combos.Add(new Combo
        {
            Id = "cb1", Name = "mycombo", Models = """["openai/gpt-4o"]""",
            CreatedAt = "x", UpdatedAt = "x",
        });
        db.Combos.Add(new Combo
        {
            Id = "cb2", Name = "other", Models = """["openai/gpt-4o-mini"]""",
            CreatedAt = "x", UpdatedAt = "x",
        });
        var token = "vsc-test1234abcd";
        db.VscodeTokens.Add(new VscodeToken
        {
            Id = "t1", Token = token, Name = "continue",
            AllowedCombos = JsonSerializer.Serialize(allowed ?? Array.Empty<string>()),
            CreatedAt = "x",
        });
        await db.SaveChangesAsync();
        return token;
    }

    [Fact]
    public async Task ApiTags_lists_combos_and_models()
    {
        var token = await SeedAsync();
        var c = _factory.CreateClient();
        var r = await c.GetFromJsonAsync<JsonElement>($"/v1/vscode/{token}/api/tags");
        var names = r.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()).ToArray();
        names.ShouldContain("mycombo");
        names.ShouldContain("other");
        names.ShouldContain("openai/gpt-4o");
    }

    [Fact]
    public async Task ApiTags_scoped_by_allowed_combos()
    {
        var token = await SeedAsync(allowed: ["mycombo"]);
        var c = _factory.CreateClient();
        var r = await c.GetFromJsonAsync<JsonElement>($"/v1/vscode/{token}/api/tags");
        var names = r.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()).ToArray();
        names.ShouldContain("mycombo");
        names.ShouldNotContain("other");
    }

    [Fact]
    public async Task OllamaChat_roundtrips_through_gateway()
    {
        var token = await SeedAsync();
        var c = _factory.CreateClient();
        var r = await c.PostAsJsonAsync($"/v1/vscode/{token}/api/chat", new
        {
            model = "openai/gpt-4o",
            messages = new[] { new { role = "user", content = "hi" } },
            stream = false,
        });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("done").GetBoolean().ShouldBe(true);
        body.GetProperty("message").GetProperty("content").GetString().ShouldBe("ok");
        body.GetProperty("eval_count").GetInt32().ShouldBe(3);
        // usage attributed to vscode:{id}
        await using var db = Db();
        (await db.UsageHistory.AnyAsync(u => u.ApiKey == "vscode:t1")).ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAi_surface_dispatches_and_enforces_combo_scope()
    {
        var token = await SeedAsync(allowed: ["mycombo"]);
        var c = _factory.CreateClient();
        // allowed combo → 200
        var ok = await c.PostAsJsonAsync($"/v1/vscode/{token}/chat/completions", new
        { model = "mycombo", messages = new[] { new { role = "user", content = "hi" } }, stream = false });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        // disallowed combo → 403
        var no = await c.PostAsJsonAsync($"/v1/vscode/{token}/chat/completions", new
        { model = "other", messages = new[] { new { role = "user", content = "hi" } }, stream = false });
        no.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Bad_token_returns_401_everywhere()
    {
        await using (var db = Db()) db.EnsureCreated();
        var c = _factory.CreateClient();
        (await c.GetAsync("/v1/vscode/vsc-nope/api/tags")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await c.PostAsJsonAsync("/v1/vscode/vsc-nope/chat/completions", new { model = "x" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Crud_create_mask_delete()
    {
        await using (var db = Db()) db.EnsureCreated();
        var c = LoginClient();
        var r = await c.PostAsJsonAsync("/api/vscode-tokens",
            new { name = "cline", defaultCombo = "mycombo", allowedCombos = new[] { "mycombo" } });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var b = await r.Content.ReadFromJsonAsync<JsonElement>();
        b.GetProperty("token").GetString()!.ShouldStartWith("vsc-");
        var id = b.GetProperty("view").GetProperty("id").GetString()!;

        var list = await c.GetFromJsonAsync<JsonElement>("api/vscode-tokens");
        var t0 = list.GetProperty("tokens").EnumerateArray().First();
        t0.GetProperty("token").GetString()!.ShouldContain("…"); // masked
        t0.GetProperty("allowedCombos").GetArrayLength().ShouldBe(1);

        (await c.DeleteAsync($"/api/vscode-tokens/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.DeleteAsync($"/api/vscode-tokens/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

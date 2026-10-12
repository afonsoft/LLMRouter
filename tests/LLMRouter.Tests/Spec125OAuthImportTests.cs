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

/// <summary>SPEC-125: local-CLI credential import endpoints — paste-credentials blob,
/// codex import/import-token, cliproxy dir scan, cursor auto-import/import,
/// kiro api-key, trae, agy import/import-bulk/apply-local.</summary>
public class Spec125OAuthImportTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec125OAuthImportTests()
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

    private static string Blob(object payload)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "omniroute-cred-v1." + b64;
    }

    [Fact]
    public async Task PasteCredentials_DecodesBlob_AndPersistsConnection()
    {
        var c = Dash();
        var blob = Blob(new { v = 1, provider = "codex", tokens = new { access_token = "at-1", refresh_token = "rt-1" } });
        var r = await c.PostAsJsonAsync("/api/oauth/codex/paste-credentials", new { blob });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        using var db = Db();
        var conn = await db.ProviderConnections.SingleAsync(x => x.Provider == "codex");
        conn.Data.ShouldContain("at-1");
        conn.Data.ShouldContain("rt-1");
    }

    [Fact]
    public async Task PasteCredentials_RejectsProviderMismatch()
    {
        var c = Dash();
        var blob = Blob(new { v = 1, provider = "codex", tokens = new { access_token = "at" } });
        var r = await c.PostAsJsonAsync("/api/oauth/cursor/paste-credentials", new { blob });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CodexImport_FlattensAuthJsonShape()
    {
        var c = Dash();
        var r = await c.PostAsJsonAsync("/api/oauth/codex/import", new
        {
            accounts = new object[]
            {
                new { auth_mode = "chatgpt", email = "a@b.c", tokens = new { access_token = "a1", refresh_token = "r1" } },
                new { access_token = "flat-1", refresh_token = "fr1", name = "flat-conn" },
            },
        });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var json = await r.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("imported").GetInt32().ShouldBe(2);
        using var db = Db();
        var conns = await db.ProviderConnections.Where(x => x.Provider == "codex").ToListAsync();
        conns.Count.ShouldBe(2);
        conns.ShouldContain(x => x.Data.Contains("a1") && x.Data.Contains("a@b.c"));
        conns.ShouldContain(x => x.Name == "flat-conn");
    }

    [Fact]
    public async Task CodexImportToken_AcceptsSessionString()
    {
        var c = Dash();
        var session = JsonSerializer.Serialize(new { tokens = new { access_token = "sess-at" }, email = "s@x.y" });
        var r = await c.PostAsJsonAsync("/api/oauth/codex/import-token", new { session, name = "sess-conn" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        using var db = Db();
        (await db.ProviderConnections.SingleAsync(x => x.Name == "sess-conn")).Data.ShouldContain("sess-at");
    }

    [Fact]
    public async Task CliproxyImport_ScansDir_PreviewAndImport()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cliproxy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "acc1.json"),
                JsonSerializer.Serialize(new { provider = "codex", type = "oauth", email = "c1@x.y", access_token = "cp-at" }));
            Environment.SetEnvironmentVariable("CLIPROXYAPI_CONFIG_DIR", dir);
            var c = Dash();
            var preview = await (await c.GetAsync("/api/oauth/cliproxy-import"))
                .Content.ReadFromJsonAsync<JsonElement>();
            preview.GetProperty("scanned").GetInt32().ShouldBe(1);
            preview.GetProperty("accounts")[0].GetProperty("email").GetString().ShouldBe("c1@x.y");
            preview.GetProperty("accounts")[0].ToString().ShouldNotContain("cp-at");
            var r = await c.PostAsJsonAsync("/api/oauth/cliproxy-import", new { });
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imported").GetInt32().ShouldBe(1);
            using var db = Db();
            (await db.ProviderConnections.SingleAsync(x => x.Name == "c1@x.y")).Data.ShouldContain("cp-at");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIPROXYAPI_CONFIG_DIR", null);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CursorImport_PersistsTokens()
    {
        var c = Dash();
        var r = await c.PostAsJsonAsync("/api/oauth/cursor/import",
            new { accessToken = "cur-at", refreshToken = "cur-rt", machineId = "m1", email = "u@x.y" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        using var db = Db();
        var conn = await db.ProviderConnections.SingleAsync(x => x.Provider == "cursor");
        conn.Data.ShouldContain("cur-at");
        conn.Data.ShouldContain("m1");
    }

    [Fact]
    public async Task KiroApiKey_CreatesLongLivedConnection()
    {
        var c = Dash();
        var r = await c.PostAsJsonAsync("/api/oauth/kiro/api-key?targetProvider=amazon-q",
            new { apiKey = "kiro-key-123456", region = "us-west-2" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        using var db = Db();
        var conn = await db.ProviderConnections.SingleAsync(x => x.Provider == "amazon-q");
        conn.Name.ShouldContain("us-west-2");
        conn.AuthType.ShouldBe("api-key");
    }

    [Fact]
    public async Task TraeImport_PersistsRefreshToken()
    {
        var c = Dash();
        var r = await c.PostAsJsonAsync("/api/oauth/trae/import",
            new { refreshToken = "trae-rt", userRegion = "BR" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        using var db = Db();
        var conn = await db.ProviderConnections.SingleAsync(x => x.Provider == "trae");
        conn.Data.ShouldContain("trae-rt");
        conn.Data.ShouldContain("BR");
    }

    [Fact]
    public async Task AgyImportBulk_AndApplyLocal()
    {
        var c = Dash();
        var r = await c.PostAsJsonAsync("/api/providers/agy-auth/import-bulk", new
        {
            accounts = new object[]
            {
                new { accessToken = "agy-1", email = "a1@x.y" },
                new { refreshToken = "agy-r2", name = "agy-second" },
            },
        });
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imported").GetInt32().ShouldBe(2);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tokenFile = Path.Combine(home, ".gemini", "antigravity-cli", "antigravity-oauth-token");
        var existed = File.Exists(tokenFile) ? File.ReadAllText(tokenFile) : null;
        try
        {
            var r2 = await c.PostAsJsonAsync("/api/providers/agy-auth/apply-local?name=a1@x.y", new { });
            r2.StatusCode.ShouldBe(HttpStatusCode.OK, await r2.Content.ReadAsStringAsync());
            File.ReadAllText(tokenFile).ShouldContain("agy-1");
        }
        finally
        {
            if (existed is null && File.Exists(tokenFile)) File.Delete(tokenFile);
            else if (existed is not null) File.WriteAllText(tokenFile, existed);
        }
    }

    [Fact]
    public async Task Import_RequiresAuth()
    {
        var c = _factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/oauth/codex/import", new { accounts = Array.Empty<object>() });
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

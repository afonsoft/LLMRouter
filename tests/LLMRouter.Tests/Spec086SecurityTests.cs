using System.Net;
using System.Text.Json;
using LLMRouter.Core.Security;
using LLMRouter.Core.Extras;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-086: headless mode, adminLocalOnly rail, public-creds guard e
/// obsidian vault path clamping.
/// </summary>
public class Spec086SecurityTests
{
    // ---------- public-creds guard ----------

    [Theory]
    [InlineData("sk-ant-oat01-public-0000")]
    [InlineData("your-api-key-here")]
    [InlineData("demo-key-123")]
    [InlineData("EXAMPLE-key")]
    [InlineData("sk-xxxxxxxxxxxxxxxx")]
    public void PublicCredRejection_rejeita_credenciais_publicas(string secret)
    {
        SecurityRails.PublicCredRejection(secret).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("sk-real-Ab1Cd2Ef3Gh4Ij5Kl6Mn7Op8")]
    [InlineData("gsk_9a8b7c6d5e4f3g2h1i")]
    [InlineData(null)]
    [InlineData("")]
    public void PublicCredRejection_aceita_credenciais_reais(string? secret)
    {
        SecurityRails.PublicCredRejection(secret).ShouldBeNull();
    }

    [Fact]
    public void PublicCredRejection_varre_campos_secretos_do_data()
    {
        var data = JsonSerializer.Deserialize<JsonElement>("{\"apiKey\":\"demo-key\",\"other\":\"x\"}");
        SecurityRails.PublicCredRejection(data).ShouldNotBeNull();

        var ok = JsonSerializer.Deserialize<JsonElement>("{\"apiKey\":\"sk-real-Ab1Cd2Ef3Gh4\"}");
        SecurityRails.PublicCredRejection(ok).ShouldBeNull();
    }

    // ---------- adminLocalOnly ----------

    [Theory]
    [InlineData("/api/discovery/scan", true)]
    [InlineData("/api/db-backups/import", true)]
    [InlineData("/api/tunnels/start", true)]
    [InlineData("/api/providers", false)]
    [InlineData("/v1/chat/completions", false)]
    public void IsLocalOnlyPath_marca_rotas_administrativas(string path, bool expected)
    {
        SecurityRails.IsLocalOnlyPath(path).ShouldBe(expected);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("203.0.113.9", false)]
    public void IsLocal_distingue_loopback_e_privado(string ip, bool expected)
    {
        SecurityRails.IsLocal(System.Net.IPAddress.Parse(ip)).ShouldBe(expected);
    }

    // ---------- obsidian vault clamping ----------

    [Theory]
    [InlineData("../evil", "evil")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("/abs/path/x", "x")]
    [InlineData("a\\b\\c", "c")]
    [InlineData("normal-id", "normal-id")]
    public void SafeFileName_clampa_caminhos(string input, string expected)
    {
        SecurityRails.SafeFileName(input).ShouldBe(expected);
    }

    [Fact]
    public void InsideRoot_rejeita_escape_do_vault()
    {
        var root = Path.Combine(Path.GetTempPath(), "vault");
        SecurityRails.InsideRoot(root, Path.Combine(root, "a.md")).ShouldBeTrue();
        SecurityRails.InsideRoot(root, "/etc/passwd").ShouldBeFalse();
        SecurityRails.InsideRoot(root, Path.Combine(root, "..", "outside.md")).ShouldBeFalse();
    }
}

/// <summary>Headless: gateway/APIs respondem, UI 404, /api/status marca headless.</summary>
public class Spec086HeadlessTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hl-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Db:Path"] = _dbPath,
                    ["Db:Headless"] = "true",
                })));
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { File.Delete(_dbPath); } catch { }
        HeadlessMode.Enabled = false;
    }

    [Fact]
    public async Task Headless_api_status_marca_flag()
    {
        var r = await _client.GetAsync("/api/status");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync());
        json.RootElement.GetProperty("headless").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Headless_ui_routes_404()
    {
        (await _client.GetAsync("/")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.GetAsync("/dashboard")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.GetAsync("/login")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Headless_v1_models_responde_com_key_valida()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LLMRouter.Core.Data.LlmRouterDbContext>();
        db.ApiKeys.Add(new LLMRouter.Core.Data.ApiKey { Id = "k1", Key = "sk-hl-1234567890", CreatedAt = "x" });
        await db.SaveChangesAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        req.Headers.Authorization = new("Bearer", "sk-hl-1234567890");
        var r = await _client.SendAsync(req);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

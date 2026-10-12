using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Server.Services.AgentBridge;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-127: AgentBridge — MITM targets registry, bypass/routing, CA lifecycle,
/// per-host leaf signing, and a real CONNECT→TLS-intercept→forward loopback test.
/// </summary>
public class Spec127AgentBridgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"llmr-mitm-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec127AgentBridgeTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private LlmRouterDbContext Db() => new(new DbContextOptionsBuilder<LlmRouterDbContext>()
        .UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public void Targets_cover_all_upstream_agents()
    {
        AgentBridgeTargets.All.Length.ShouldBe(10);
        AgentBridgeTargets.Resolve("api.anthropic.com")!.Id.ShouldBe("claude-code");
        AgentBridgeTargets.Resolve("chatgpt.com")!.Id.ShouldBe("codex");
        AgentBridgeTargets.Resolve("cloudcode-pa.googleapis.com")!.Id.ShouldBe("antigravity");
        AgentBridgeTargets.Resolve("api2.cursor.sh")!.Id.ShouldBe("cursor");
        AgentBridgeTargets.Resolve("example.com").ShouldBeNull();
    }

    [Fact]
    public void GlobMatch_anchors_first_and_last_segments()
    {
        AgentBridgeTargets.GlobMatch("api.openai.com", "*.openai.com").ShouldBeTrue();
        AgentBridgeTargets.GlobMatch("api.openai.com", "api.*").ShouldBeTrue();
        AgentBridgeTargets.GlobMatch("xapi.openai.com", "api.*").ShouldBeFalse();
        AgentBridgeTargets.GlobMatch("openai.com.evil.net", "*.openai.com").ShouldBeFalse();
        AgentBridgeTargets.GlobMatch("a.b.c", "*").ShouldBeTrue();
    }

    [Fact]
    public void RouteConnection_bypass_beats_target_then_passthrough()
    {
        // Default bypass list (bank/gov/okta/auth0)
        AgentBridgeTargets.RouteConnection("login.okta.com", []).kind.ShouldBe("bypass");
        // User glob beats target
        AgentBridgeTargets.RouteConnection("api.anthropic.com", ["*.anthropic.com"]).kind.ShouldBe("bypass");
        // Known target
        AgentBridgeTargets.RouteConnection("api.anthropic.com", []).kind.ShouldBe("target");
        // Unknown → passthrough
        AgentBridgeTargets.RouteConnection("example.com", []).kind.ShouldBe("passthrough");
    }

    [Fact]
    public void Ca_generates_and_signs_verifiable_leaf()
    {
        var ca = new MitmCa(_dir);
        ca.CertExists.ShouldBeFalse();
        var (root, _) = ca.Generate();
        ca.CertExists.ShouldBeTrue();
        File.ReadAllText(ca.CertPath).ShouldContain("BEGIN CERTIFICATE");

        var leaf = ca.LeafFor("api.anthropic.com");
        // Chain: leaf signed by our root — verify with custom trust anchor.
        using var chain = new X509Chain();
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(new X509Certificate2(leaf.Export(X509ContentType.Cert))).ShouldBeTrue();
        leaf.GetNameInfo(X509NameType.DnsName, false).ShouldBe("api.anthropic.com");

        // Regenerate → new thumbprint, cache cleared.
        var oldThumb = root.Thumbprint;
        var (root2, _) = ca.Generate();
        root2.Thumbprint.ShouldNotBe(oldThumb);
    }

    /// <summary>
    /// End-to-end MITM loopback: CONNECT to a target host → TLS with the signed
    /// leaf → intercepted request forwarded to a stub upstream → response
    /// relayed back, with model mapping applied.
    /// </summary>
    [Fact]
    public async Task Mitm_intercepts_connect_tls_and_forwards_with_mapping()
    {
        // Stub "gateway" the proxy forwards to (free ephemeral port).
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var stubPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var stub = new HttpListener();
        stub.Prefixes.Add($"http://127.0.0.1:{stubPort}/");
        stub.Start();
        var stubTask = Task.Run(async () =>
        {
            var ctx = await stub.GetContextAsync();
            var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            var auth = ctx.Request.Headers["authorization"];
            var payload = JsonSerializer.Serialize(new
            {
                got = true, auth, bodyModel = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()
            });
            var buf = Encoding.UTF8.GetBytes(payload);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf);
            ctx.Response.Close();
        });

        var ca = new MitmCa(_dir);
        var (root, _) = ca.Generate();
        await using var proxy = new MitmProxy(ca, $"http://127.0.0.1:{stubPort}",
            bypassProvider: _ => Task.FromResult<IReadOnlyList<string>>([]),
            mappingsProvider: _ => Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["claude-sonnet-4.5"] = "gpt-4o" }),
            apiKeyProvider: () => Task.FromResult<string?>("sk-vk-internal"));
        await proxy.Start(0);
        proxy.Running.ShouldBeTrue();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var ns = client.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes("CONNECT api.anthropic.com:443 HTTP/1.1\r\n\r\n"));
        var ok = new byte[64];
        var n = await ns.ReadAsync(ok);
        Encoding.ASCII.GetString(ok, 0, n).ShouldContain("200");

        // TLS client trusting our CA (agent would install it).
        using var tls = new SslStream(ns, leaveInnerStreamOpen: false,
            (_, cert, chain, errors) =>
            {
                using var c = new X509Chain();
                c.ChainPolicy.CustomTrustStore.Add(root);
                c.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                c.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return c.Build(new X509Certificate2(cert!));
            });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "api.anthropic.com",
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
        });
        new X509Certificate2(tls.RemoteCertificate!).GetNameInfo(X509NameType.DnsName, false)
            .ShouldBe("api.anthropic.com");

        var reqBody = """{"model":"claude-sonnet-4.5","messages":[{"role":"user","content":"hi"}]}""";
        var http = $"POST /v1/chat/completions HTTP/1.1\r\nHost: api.anthropic.com\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(reqBody)}\r\n\r\n{reqBody}";
        await tls.WriteAsync(Encoding.UTF8.GetBytes(http));

        var sb = new StringBuilder();
        var resp = new byte[8192];
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while (!sb.ToString().Contains("\"got\"") && !cts.IsCancellationRequested)
            {
                var rn = await tls.ReadAsync(resp, cts.Token);
                if (rn <= 0) break;
                sb.Append(Encoding.UTF8.GetString(resp, 0, rn));
            }
        }
        var respText = sb.ToString();
        respText.ShouldContain("200");
        respText.ShouldContain("\"got\":true");
        respText.ShouldContain("sk-vk-internal");   // injected internal key
        respText.ShouldContain("gpt-4o");           // model mapping applied
        await stubTask;
        proxy.Intercepted.ShouldBe(1);
    }

    [Fact]
    public async Task Endpoints_require_auth()
    {
        var c = _factory.CreateClient();
        (await c.GetAsync("/api/tools/agent-bridge/state")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/tools/agent-bridge/agents")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

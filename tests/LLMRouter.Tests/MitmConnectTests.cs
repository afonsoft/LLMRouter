using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Shouldly;
using LLMRouter.Server.Mitm;

namespace LLMRouter.Tests;

/// <summary>SPEC-028: CONNECT proxy + per-host CA-signed certs.</summary>
public class MitmConnectTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public MitmConnectTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient();
        _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }).Wait();
    }

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public void CertForHost_signed_by_ca_with_san()
    {
        var cert = ConnectProxy.CertForHost("api.example.com");
        cert.Subject.ShouldContain("api.example.com");
        cert.Issuer.ShouldContain("LLMRouter Local CA");
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        san.ShouldNotBeNull();
        san!.EnumerateDnsNames().ShouldContain("api.example.com");
        cert.HasPrivateKey.ShouldBeTrue();
    }

    [Fact]
    public async Task Connect_blind_tunnel_pipes_bytes()
    {
        // echo server as CONNECT target
        var echo = new TcpListener(System.Net.IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((System.Net.IPEndPoint)echo.LocalEndpoint).Port;
        var echoTask = Task.Run(async () =>
        {
            using var c = await echo.AcceptTcpClientAsync();
            var s = c.GetStream();
            var b = new byte[1024];
            var n = await s.ReadAsync(b);
            await s.WriteAsync(b.AsMemory(0, n)); // echo back
        });

        using var proxy = new TcpClient();
        await proxy.ConnectAsync(System.Net.IPAddress.Loopback, 8889);
        var ps = proxy.GetStream();
        var req = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n");
        await ps.WriteAsync(req);
        var buf = new byte[1024];
        using var cts0 = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var n = await ps.ReadAsync(buf, cts0.Token);
        Encoding.ASCII.GetString(buf, 0, n).ShouldContain("200 Connection Established");

        var payload = Encoding.ASCII.GetBytes("ping-through-tunnel");
        await ps.WriteAsync(payload);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var got = await ps.ReadAsync(buf, cts.Token);
        Encoding.ASCII.GetString(buf, 0, got).ShouldBe("ping-through-tunnel");
        echo.Stop();
    }
}

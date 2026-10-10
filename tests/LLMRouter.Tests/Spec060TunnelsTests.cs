using System.Net;
using System.Net.Http.Json;
using LLMRouter.Core.Tunnels;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-060: supervisores de tunnel — args, parse de URL pública, lifecycle com stub.</summary>
public class Spec060TunnelsTests : WebAppTestBase
{
    [Fact]
    public void Args_e_parse_de_url_por_backend()
    {
        TunnelManager.ArgsFor("cloudflared", [])[..3]
            .ShouldBe(["tunnel", "--url", "http://127.0.0.1:8080"]);
        TunnelManager.ArgsFor("cloudflared", new() { ["token"] = "tk" })
            .ShouldBe(["tunnel", "run", "--token", "tk"]);
        TunnelManager.ArgsFor("ngrok", new() { ["authtoken"] = "at" }).ShouldContain("--authtoken");

        TunnelManager.ParsePublicUrl("cloudflared",
            "INF | https://abc-123.trycloudflare.com").ShouldBe("https://abc-123.trycloudflare.com");
        TunnelManager.ParsePublicUrl("ngrok",
            "forwarding https://xyz.ngrok.io -> http://localhost:8080").ShouldBe("https://xyz.ngrok.io");
        TunnelManager.ParsePublicUrl("tailscale", "funnel at https://host.tail-ts.net").ShouldBe("https://host.tail-ts.net");
        TunnelManager.ParsePublicUrl("cloudflared", "random line").ShouldBeNull();
    }

    [Fact]
    public async Task Start_com_stub_spawn_e_parse_url()
    {
        var stub = Path.Combine(Path.GetTempPath(), $"stub-ngrok-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(stub,
            "#!/bin/bash\necho 'forwarding https://stub-ok.ngrok.io -> http://localhost:8080'\nsleep 30\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserExecute | UnixFileMode.UserRead);
        Environment.SetEnvironmentVariable("LLMROUTER_NGROK_BIN", stub);
        try
        {
            var (ok, detail) = await TunnelManager.StartAsync("ngrok", []);
            ok.ShouldBeTrue(detail);
            await Task.Delay(500); // deixa o stub imprimir
            var st = await TunnelManager.StatusAsync("ngrok");
            st.ToString().ShouldContain("stub-ok.ngrok.io");
            (await TunnelManager.StopAsync("ngrok")).ok.ShouldBeTrue();
        }
        finally { Environment.SetEnvironmentVariable("LLMROUTER_NGROK_BIN", null); File.Delete(stub); }
    }

    [Fact]
    public async Task Endpoints_status_e_config()
    {
        await LoginAsync();
        var r = await Client.GetAsync("/api/tunnels");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await r.Content.ReadAsStringAsync();
        body.ShouldContain("cloudflared"); body.ShouldContain("ngrok"); body.ShouldContain("tailscale");

        (await Client.PutAsJsonAsync("/api/tunnels/ngrok/config", new Dictionary<string, string> { ["authtoken"] = "x" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetStringAsync("/api/tunnels/ngrok/config")).ShouldContain("authtoken");
    }
}

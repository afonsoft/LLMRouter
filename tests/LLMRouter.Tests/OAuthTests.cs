using System.Net;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.OAuth;
using LLMRouter.Core.Registry;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>Stub HttpMessageHandler returning canned JSON per URL substring.</summary>
internal sealed class StubHttp : HttpMessageHandler
{
    private readonly Func<string, HttpContent> _responder;
    public List<string> Calls { get; } = [];
    public StubHttp(Func<string, HttpContent> responder) => _responder = responder;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Calls.Add(req.RequestUri!.ToString());
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = _responder(req.RequestUri!.ToString()) });
    }
    public static HttpClient Client(Func<string, string> json)
    {
        var h = new StubHttp(u => new StringContent(json(u), Encoding.UTF8, "application/json"));
        return new HttpClient(h);
    }
}

public class OAuthFlowTests
{
    private static ProviderEntry P(string id, string oauthJson) => new()
    {
        Id = id, Format = "openai", AuthType = "oauth",
        Oauth = JsonDocument.Parse(oauthJson).RootElement.Clone(),
    };

    [Fact]
    public async Task Device_flow_returns_user_code_and_completes()
    {
        var calls = 0;
        var http = StubHttp.Client(u => u.Contains("/device/code")
            ? """{"device_code":"dc1","user_code":"ABCD-1234","verification_uri":"https://x/activate","interval":1}"""
            : """{"access_token":"at1","refresh_token":"rt1","expires_in":3600}""");
        var prov = P("github", """{"clientIdDefault":"cid","deviceCodeUrl":"https://x/device/code","tokenUrl":"https://x/token"}""");
        var s = await OAuthService.StartAsync(prov, "http://router", http);
        s.Flow.ShouldBe("device");
        s.UserCode.ShouldBe("ABCD-1234");
        s.AuthUrl.ShouldBe("https://x/activate");

        var tokens = await OAuthService.CompleteAsync(s, prov, null, http);
        tokens.ShouldNotBeNull();
        var data = OAuthService.TokenData(tokens.Value);
        data["accessToken"].ShouldBe("at1");
        data["refreshToken"].ShouldBe("rt1");
        data["expiresAt"].ShouldBeOfType<long>();
    }

    [Fact]
    public async Task Device_poll_returns_null_while_pending()
    {
        var http = StubHttp.Client(u => u.Contains("/device/code")
            ? """{"device_code":"dc","user_code":"X","verification_uri":"https://x","interval":1}"""
            : """{"error":"authorization_pending"}""");
        var prov = P("github", """{"clientIdDefault":"c","deviceCodeUrl":"https://x/device/code","tokenUrl":"https://x/token"}""");
        var s = await OAuthService.StartAsync(prov, "http://r", http);
        (await OAuthService.CompleteAsync(s, prov, null, http)).ShouldBeNull();
    }

    [Fact]
    public async Task Pkce_flow_builds_challenge_url_and_exchanges_code()
    {
        var http = StubHttp.Client(u => """{"access_token":"at","expires_in":100}""");
        var prov = P("kiro", """{"authUrl":"https://idp/authorize","tokenUrl":"https://idp/token","clientIdDefault":"kc"}""");
        var s = await OAuthService.StartAsync(prov, "http://router", http);
        s.Flow.ShouldBe("pkce");
        s.AuthUrl.ShouldContain("code_challenge=");
        s.AuthUrl.ShouldContain("state=" + s.State);
        s.AuthUrl.ShouldContain("redirect_uri=");

        var tokens = await OAuthService.CompleteAsync(s, prov, "authcode", http);
        tokens.ShouldNotBeNull();
        OAuthService.TokenData(tokens!.Value)["accessToken"].ShouldBe("at");
    }

    [Fact]
    public async Task Refresh_uses_refreshUrl_and_updates_conn_data()
    {
        var http = StubHttp.Client(u => """{"access_token":"new-at","refresh_token":"new-rt","expires_in":7200}""");
        var prov = P("cline", """{"refreshUrl":"https://x/refresh","tokenUrl":"https://x/token","clientIdDefault":"c"}""");
        var conn = new ProviderConnection
        {
            Id = "c1", Provider = "cline", Name = "n", IsActive = true,
            Data = """{"accessToken":"old","refreshToken":"rt0","expiresAt":1}""",
        };
        (await OAuthService.RefreshAsync(conn, prov, http)).ShouldBeTrue();
        var d = JsonDocument.Parse(conn.Data).RootElement;
        d.GetProperty("accessToken").GetString().ShouldBe("new-at");
    }

    [Theory]
    [InlineData("{}", "none")]
    [InlineData("""{"accessToken":"a"}""", "ok")] // no expiry → ok
    public void HealthOf_basic(string data, string expected)
    {
        var c = new ProviderConnection { Id = "x", Provider = "p", Name = "n", Data = data };
        OAuthService.HealthOf(c).ShouldBe(expected);
    }

    [Fact]
    public void HealthOf_expiring_and_expired()
    {
        var soon = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000; // < 5min lead
        var past = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10_000;
        var later = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000;
        string D(long exp) => $$"""{"accessToken":"a","expiresAt":{{exp}}}""";
        OAuthService.HealthOf(new ProviderConnection { Data = D(soon) }).ShouldBe("expiring");
        OAuthService.HealthOf(new ProviderConnection { Data = D(past) }).ShouldBe("expired");
        OAuthService.HealthOf(new ProviderConnection { Data = D(later) }).ShouldBe("ok");
    }
}

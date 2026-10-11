using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Server.Mitm;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-100: MITM policy — glob matching, ShouldIntercept allow/deny/all,
/// leitura de settings.mitm (captureMode etc.) e shape do /mitm/status.
/// </summary>
public class Spec100Tests : WebAppTestBase
{
    [Theory]
    [InlineData("*.example.com", "api.example.com", true)]
    [InlineData("*.example.com", "example.com", true)]   // apex casa também
    [InlineData("*.example.com", "other.com", false)]
    [InlineData("api.example.com", "api.example.com", true)]
    [InlineData("api.example.com", "API.EXAMPLE.COM", true)]
    [InlineData("*", "anything.dev", true)]
    [InlineData("api.*", "api.evil.dev", true)]
    [InlineData("", "x.dev", false)]
    public void Host_glob_matching(string pattern, string host, bool expected) =>
        MitmPolicy.Match(pattern, host).ShouldBe(expected);

    [Fact]
    public void ShouldIntercept_modes()
    {
        new MitmPolicy.Cfg(true, "all", [], "full").ShouldIntercept("any.dev").ShouldBeTrue();
        new MitmPolicy.Cfg(true, "allow", ["*.example.com"], "full")
            .ShouldIntercept("api.example.com").ShouldBeTrue();
        new MitmPolicy.Cfg(true, "allow", ["*.example.com"], "full")
            .ShouldIntercept("other.dev").ShouldBeFalse();
        new MitmPolicy.Cfg(true, "deny", ["blocked.dev"], "full")
            .ShouldIntercept("blocked.dev").ShouldBeFalse();
        new MitmPolicy.Cfg(true, "deny", ["blocked.dev"], "full")
            .ShouldIntercept("fine.dev").ShouldBeTrue();
    }

    [Fact]
    public async Task Mitm_settings_parse_from_db()
    {
        await using var db = Db();
        var row = await db.Settings.FindAsync(1)
            ?? db.Settings.Add(new LLMRouter.Core.Data.SettingRow { Id = 1 }).Entity;
        row.Data = """{"mitm":{"tlsIntercept":true,"intercept":{"mode":"deny","hosts":["*.ads.com"]},"captureMode":"headers"}}""";
        await db.SaveChangesAsync();

        var cfg = await MitmPolicy.ReadAsync(Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            CancellationToken.None);
        cfg.TlsIntercept.ShouldBeTrue();
        cfg.InterceptMode.ShouldBe("deny");
        cfg.Hosts.ShouldBe(["*.ads.com"]);
        cfg.CaptureMode.ShouldBe("headers");
        cfg.ShouldIntercept("trk.ads.com").ShouldBeFalse();
        cfg.ShouldIntercept("api.dev").ShouldBeTrue();
    }

    [Fact]
    public async Task Mitm_status_reports_configured_fields()
    {
        await LoginAsync();
        await using (var db = Db())
        {
            var row = await db.Settings.FindAsync(1)
                ?? db.Settings.Add(new LLMRouter.Core.Data.SettingRow { Id = 1 }).Entity;
            row.Data = """{"mitm":{"tlsIntercept":true,"intercept":{"mode":"allow","hosts":["a.dev"]},"captureMode":"none"}}""";
            await db.SaveChangesAsync();
        }

        var r = await Client.GetFromJsonAsync<JsonElement>("/api/mitm/status");
        r.GetProperty("tlsIntercept").GetBoolean().ShouldBeTrue();
        r.GetProperty("interceptMode").GetString().ShouldBe("allow");
        r.GetProperty("captureMode").GetString().ShouldBe("none");
        r.GetProperty("interceptHosts")[0].GetString().ShouldBe("a.dev");
        r.GetProperty("port").GetInt32().ShouldBe(8889);
        r.GetProperty("activeConnections").GetInt32().ShouldBe(0);
    }
}

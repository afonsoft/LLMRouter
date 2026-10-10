using System.Net;
using System.Net.Http.Json;
using LLMRouter.Core.Versioning;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-059: version-manager status, parse de release, checksum, power.</summary>
public class Spec059VersionTests : WebAppTestBase
{
    protected override void ConfigureHost(Microsoft.AspNetCore.Hosting.WebHostBuilderContext ctx,
        Microsoft.Extensions.Configuration.IConfigurationBuilder cfg) =>
        VersionManager.ExitOnRequest = false;

    [Fact]
    public async Task Status_retorna_versao()
    {
        await LoginAsync();
        var r = await Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/version-manager/status");
        r.GetProperty("version").GetString().ShouldNotBeNullOrEmpty();
        r.GetProperty("runtime").GetString().ShouldContain(".NET");
    }

    [Fact]
    public void Parse_latest_release()
    {
        var (tag, url, changelog) = VersionManager.ParseLatestRelease(
            """{"tag_name":"v1.2.3","html_url":"https://github.com/x/r/releases/tag/v1.2.3","body":"notes"}""");
        tag.ShouldBe("v1.2.3");
        url.ShouldContain("releases/tag");
        changelog.ShouldBe("notes");
    }

    [Fact]
    public async Task Checksum_rejeita_mismatch()
    {
        var p = Path.Combine(Path.GetTempPath(), $"f-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(p, "data");
        (await VersionManager.VerifySha256Async(p, "deadbeef")).ShouldBeFalse();
        (await VersionManager.VerifySha256Async(p, null)).ShouldBeTrue();
        File.Delete(p);
    }

    [Fact]
    public async Task Restart_e_shutdown_registram_exit()
    {
        await LoginAsync();
        (await Client.PostAsync("/api/restart", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        VersionManager.ExitRequested.ShouldBe((0, "restart"));
        (await Client.PostAsync("/api/shutdown", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        VersionManager.ExitRequested.ShouldBe((0, "shutdown"));
    }
}

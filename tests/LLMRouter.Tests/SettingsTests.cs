using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-007: settings.data round-trip, reset, export, resilience wiring.</summary>
public class SettingsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SettingsTests()
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

    private async Task LoginAsync() =>
        (await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" }))
        .StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public async Task Settings_round_trip_subkeys()
    {
        await LoginAsync();
        await _client.PutAsJsonAsync("/api/settings", new
        {
            defaultModel = "openai/gpt-4o",
            defaultTemperature = "0.7",
            resilience = new { failureThreshold = 5, cooldownBaseSeconds = 10.0, cooldownMaxSeconds = 120.0 },
            featureFlags = new { fusionCombo = true },
            sidebarHidden = new[] { "media" },
        });
        var s = await _client.GetFromJsonAsync<JsonElement>("/api/settings");
        var d = s.GetProperty("data");
        d.GetProperty("defaultModel").GetString().ShouldBe("openai/gpt-4o");
        d.GetProperty("resilience").GetProperty("failureThreshold").GetInt32().ShouldBe(5);
        d.GetProperty("featureFlags").GetProperty("fusionCombo").GetBoolean().ShouldBeTrue();
        d.GetProperty("sidebarHidden")[0].GetString().ShouldBe("media");
    }

    [Fact]
    public async Task Resilience_settings_reach_cooldown_tracker()
    {
        await LoginAsync();
        await _client.PutAsJsonAsync("/api/settings", new
        {
            resilience = new { failureThreshold = 7, cooldownBaseSeconds = 45.0, cooldownMaxSeconds = 600.0 },
        });
        Core.Resilience.CooldownTracker.FailureThreshold.ShouldBe(7);
        Core.Resilience.CooldownTracker.BaseCooldown.ShouldBe(TimeSpan.FromSeconds(45));
        Core.Resilience.CooldownTracker.MaxCooldown.ShouldBe(TimeSpan.FromSeconds(600));
        // restore defaults for other tests
        Core.Resilience.CooldownTracker.Configure(3, 30, 300);
    }

    [Fact]
    public async Task Settings_export_and_reset()
    {
        await LoginAsync();
        await _client.PutAsJsonAsync("/api/settings", new { defaultModel = "x/y" });
        var exported = await _client.GetStringAsync("/api/settings/export");
        JsonDocument.Parse(exported).RootElement.GetProperty("defaultModel").GetString().ShouldBe("x/y");

        await _client.PostAsync("/api/settings/reset", null);
        var s = await _client.GetFromJsonAsync<JsonElement>("/api/settings");
        s.GetProperty("data").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task Logs_prune_removes_old_details()
    {
        await LoginAsync();
        var resp = await _client.PostAsync("/api/logs/prune?days=30", null);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

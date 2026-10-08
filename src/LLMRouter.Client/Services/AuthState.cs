using System.Text.Json;
using System.Net.Http.Json;

namespace LLMRouter.Client.Services;

/// <summary>Dashboard auth state (requireLogin flag + session check).</summary>
public sealed class AuthState(HttpClient http)
{
    public bool Authenticated { get; private set; }
    public bool RequireLogin { get; private set; } = true;
    public bool SetupComplete { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            var req = await http.GetFromJsonAsync<JsonElement>("api/settings/require-login");
            RequireLogin = req.TryGetProperty("requireLogin", out var r) && r.GetBoolean();
            SetupComplete = req.TryGetProperty("setupComplete", out var s) && s.GetBoolean();
            var sess = await http.GetFromJsonAsync<JsonElement>("api/auth/session");
            Authenticated = sess.TryGetProperty("authenticated", out var a) && a.GetBoolean();
        }
        catch { }
    }

    public async Task<bool> LoginAsync(string password)
    {
        var resp = await http.PostAsJsonAsync("api/auth/login", new { password });
        Authenticated = resp.IsSuccessStatusCode;
        return Authenticated;
    }

    public async Task RawChangePassword(string newPassword) =>
        await http.PostAsJsonAsync("api/auth/change-password", new { newPassword });

    public async Task LogoutAsync()
    {
        await http.PostAsync("api/auth/logout", null);
        Authenticated = false;
    }
}

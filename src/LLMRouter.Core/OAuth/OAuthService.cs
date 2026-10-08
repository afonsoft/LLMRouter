using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;

namespace LLMRouter.Core.OAuth;

/// <summary>
/// SPEC-011: OAuth flows (device-code, PKCE/auth-code, poll-based) driven by the
/// provider registry's `oauth` blocks + token refresh/health for stored
/// connections (same data shape as upstream: accessToken/refreshToken/expiresAt).
/// </summary>
public static class OAuthService
{
    public sealed record Session(
        string State, string Provider, string Flow,
        string? AuthUrl, string? UserCode, string? DeviceCode,
        string? Verifier, string? PollUrl, int IntervalSec);

    public static readonly ConcurrentDictionary<string, Session> Sessions = new();

    private static JsonElement OauthBlock(ProviderEntry p) =>
        p.Oauth is JsonElement je ? je
        : p.Oauth is null ? default
        : JsonSerializer.SerializeToElement(p.Oauth);

    private static string? S(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ClientId(ProviderEntry p, JsonElement o) =>
        Environment.GetEnvironmentVariable(S(o, "clientIdEnv") ?? "")
        ?? S(o, "clientIdDefault");

    private static string? ClientSecret(ProviderEntry p, JsonElement o) =>
        Environment.GetEnvironmentVariable(S(o, "clientSecretEnv") ?? "")
        ?? S(o, "clientSecretDefault");

    /// <summary>GitHub-family providers use the device-code endpoint even when not listed.</summary>
    private static string? DeviceCodeUrl(ProviderEntry p, JsonElement o) =>
        S(o, "deviceCodeUrl") ?? p.Id switch
        {
            "github" or "ghe-copilot" => "https://github.com/login/device/code",
            _ => null,
        };

    private static (string Verifier, string Challenge) Pkce()
    {
        var v = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var c = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(v)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (v, c);
    }

    private static async Task<JsonElement> PostForm(HttpClient http, string url, Dictionary<string, string> form)
    {
        var resp = await http.PostAsync(url, new FormUrlEncodedContent(form));
        var txt = await resp.Content.ReadAsStringAsync();
        try { return JsonDocument.Parse(txt).RootElement.Clone(); }
        catch { return JsonSerializer.SerializeToElement(new { error = txt[..Math.Min(300, txt.Length)], status = (int)resp.StatusCode }); }
    }

    /// <summary>Start a flow: device (deviceCodeUrl), pkce (authUrl+tokenUrl), or poll (initiateUrl).</summary>
    public static async Task<Session> StartAsync(ProviderEntry p, string redirectBase, HttpClient http)
    {
        var o = OauthBlock(p);
        var state = Guid.NewGuid().ToString("N");

        if (DeviceCodeUrl(p, o) is { } devUrl)
        {
            var resp = await PostForm(http, devUrl, new()
            {
                ["client_id"] = ClientId(p, o) ?? "",
                ["scope"] = S(o, "scope") ?? "",
            });
            var s = new Session(state, p.Id, "device",
                S(resp, "verification_uri") ?? S(resp, "verificationUri"),
                S(resp, "user_code") ?? S(resp, "userCode"),
                S(resp, "device_code") ?? S(resp, "deviceCode"),
                null, null,
                resp.TryGetProperty("interval", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 5);
            Sessions[state] = s;
            return s;
        }

        if (S(o, "authUrl") is { } authUrl && S(o, "tokenUrl") is not null)
        {
            var (verifier, challenge) = Pkce();
            var redirect = $"{redirectBase.TrimEnd('/')}/api/oauth/callback";
            var sep = authUrl.Contains('?') ? "&" : "?";
            var url = $"{authUrl}{sep}response_type=code&client_id={Uri.EscapeDataString(ClientId(p, o) ?? "")}" +
                $"&redirect_uri={Uri.EscapeDataString(redirect)}&state={state}" +
                $"&code_challenge={challenge}&code_challenge_method=S256" +
                (S(o, "scope") is { } sc ? $"&scope={Uri.EscapeDataString(sc)}" : "");
            var s = new Session(state, p.Id, "pkce", url, null, null, verifier, null, 0);
            Sessions[state] = s;
            return s;
        }

        if (S(o, "initiateUrl") is { } init && S(o, "pollUrlBase") is { } pollBase)
        {
            var resp = await PostForm(http, init, new() { ["state"] = state, ["clientId"] = ClientId(p, o) ?? "" });
            var poll = S(resp, "pollUrl") ?? $"{pollBase.TrimEnd('/')}/{S(resp, "session") ?? state}";
            var s = new Session(state, p.Id, "poll",
                S(resp, "authUrl") ?? S(resp, "url"), S(resp, "userCode"), null, null, poll, 3);
            Sessions[state] = s;
            return s;
        }

        throw new InvalidOperationException($"provider '{p.Id}' has no supported oauth flow");
    }

    /// <summary>
    /// Poll/complete a running session. Returns token JSON on success,
    /// null while still pending.
    /// </summary>
    public static async Task<JsonElement?> CompleteAsync(Session s, ProviderEntry prov, string? code, HttpClient http)
    {
        var o = default(JsonElement);
        switch (s.Flow)
        {
            case "device":
            {
                o = OauthBlock(prov);
                var resp = await PostForm(http, S(o, "tokenUrl") ?? "https://github.com/login/oauth/access_token", new()
                {
                    ["client_id"] = ClientId(prov, o) ?? "",
                    ["device_code"] = s.DeviceCode ?? "",
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                });
                if (S(resp, "error") is "authorization_pending" or "slow_down") return null;
                return resp;
            }
            case "pkce":
            {
                if (code is null) return null;
                o = OauthBlock(prov);
                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["client_id"] = ClientId(prov, o) ?? "",
                    ["code_verifier"] = s.Verifier ?? "",
                };
                if (ClientSecret(prov, o) is { } sec) form["client_secret"] = sec;
                return await PostForm(http, S(o, "tokenUrl")!, form);
            }
            case "poll":
            {
                if (s.PollUrl is null) return null;
                var resp = await http.GetAsync(s.PollUrl);
                if ((int)resp.StatusCode == 202 || resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
                return await resp.Content.ReadFromJsonAsync<JsonElement>();
            }
        }
        return null;
    }

    /// <summary>Extract token fields into the standard conn.data shape.</summary>
    public static Dictionary<string, object?> TokenData(JsonElement tokens)
    {
        var d = new Dictionary<string, object?>();
        if (S(tokens, "access_token") is { } at) d["accessToken"] = at;
        if (S(tokens, "refresh_token") is { } rt) d["refreshToken"] = rt;
        var expiresIn = tokens.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt64() : 3600;
        d["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds();
        if (S(tokens, "token_type") is { } tt) d["tokenType"] = tt;
        if (S(tokens, "scope") is { } sc) d["scope"] = sc;
        return d;
    }

    /// <summary>"ok" | "expiring" (&lt;5min) | "expired" | "none" (no oauth data).</summary>
    public static string HealthOf(ProviderConnection c, long leadMs = 300_000)
    {
        try
        {
            var d = JsonSerializer.Deserialize<JsonElement>(c.Data);
            if (!d.TryGetProperty("accessToken", out _)) return "none";
            if (!d.TryGetProperty("expiresAt", out var e) || e.ValueKind != JsonValueKind.Number) return "ok";
            var left = e.GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return left <= 0 ? "expired" : left <= leadMs ? "expiring" : "ok";
        }
        catch { return "none"; }
    }

    /// <summary>Refresh a connection's access token via refreshUrl/tokenUrl. Mutates conn.Data.</summary>
    public static async Task<bool> RefreshAsync(ProviderConnection c, ProviderEntry p, HttpClient http)
    {
        var o = OauthBlock(p);
        var d = JsonSerializer.Deserialize<JsonElement>(c.Data);
        if (!d.TryGetProperty("refreshToken", out var rt)) return false;
        var url = S(o, "refreshUrl") ?? S(o, "tokenUrl");
        if (url is null) return false;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = rt.GetString()!,
            ["client_id"] = ClientId(p, o) ?? "",
        };
        if (ClientSecret(p, o) is { } sec) form["client_secret"] = sec;
        var resp = await PostForm(http, url, form);
        if (S(resp, "access_token") is not { } at) return false;
        var merged = JsonNode.Parse(c.Data)!.AsObject();
        var td = TokenData(resp);
        foreach (var kv in td) merged[kv.Key] = kv.Value is null ? null : JsonValue.Create(kv.Value);
        c.Data = merged.ToJsonString();
        return true;
    }
}

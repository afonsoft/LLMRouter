using System.Text.Json;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// Connection-level custom upstream headers — port of upstream
/// connectionCustomHeaders.ts + upstreamHeaders.ts (#16108). A connection's
/// `data.customHeaders` are sent on chat AND on model discovery, so a key that
/// needs a routing header on every request (e.g. `anthropic-workspace-id`) can
/// list models, not only chat. Same rules on both paths: auth and hop-by-hop /
/// origin-IP names are skipped, CR/LF is dropped, and a same-named default is
/// replaced case-insensitively instead of duplicated.
/// </summary>
public static class CustomHeaders
{
    /// <summary>Hop-by-hop / framing names + forwarding headers that would leak or spoof the client IP upstream.</summary>
    private static readonly HashSet<string> ForbiddenUpstream = new(StringComparer.Ordinal)
    {
        "host", "connection", "content-length", "keep-alive", "proxy-connection",
        "proxy-authenticate", "proxy-authorization", "transfer-encoding", "te",
        "trailer", "upgrade",
        "x-forwarded-for", "x-forwarded-host", "x-forwarded-proto", "x-forwarded-port",
        "x-forwarded-server", "x-real-ip", "cf-connecting-ip", "true-client-ip",
        "client-ip", "forwarded", "via",
    };

    /// <summary>Auth names owned by the credential layer, never by custom headers.</summary>
    private static readonly HashSet<string> ForbiddenAuth = new(StringComparer.Ordinal)
    {
        "authorization", "x-api-key", "x-goog-api-key", "api-key", "cookie",
    };

    /// <summary>Forbidden for operator-supplied custom headers (hop-by-hop + auth).</summary>
    public static bool IsForbiddenName(string name)
    {
        var n = name.Trim();
        return ForbiddenUpstream.Contains(n) || ForbiddenAuth.Contains(n);
    }

    /// <summary>
    /// Apply a connection Data JSON's `customHeaders` object onto <paramref name="headers"/>.
    /// A same-named existing header is replaced case-insensitively instead of duplicated.
    /// </summary>
    public static void Apply(IDictionary<string, string> headers, string? connectionDataJson)
    {
        if (string.IsNullOrWhiteSpace(connectionDataJson)) return;
        JsonElement custom;
        try
        {
            using var doc = JsonDocument.Parse(connectionDataJson);
            if (!doc.RootElement.TryGetProperty("customHeaders", out var ch)
                || ch.ValueKind != JsonValueKind.Object)
                return;
            custom = ch.Clone();
        }
        catch { return; }

        foreach (var kv in custom.EnumerateObject())
        {
            var name = kv.Name.Trim();
            if (name.Length == 0 || kv.Value.ValueKind != JsonValueKind.String) continue;
            var value = kv.Value.GetString();
            if (value is null) continue;
            if (IsForbiddenName(name)) continue;
            if (name.IndexOfAny(['\r', '\n', '\0']) >= 0
                || value.IndexOfAny(['\r', '\n', '\0']) >= 0) continue;
            // replace any same-named default instead of duplicating
            foreach (var existing in headers.Keys.ToList())
                if (existing.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && !existing.Equals(name, StringComparison.Ordinal))
                    headers.Remove(existing);
            headers[name] = value;
        }
    }
}

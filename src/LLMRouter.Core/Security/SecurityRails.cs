using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMRouter.Core.Security;

/// <summary>
/// SPEC-086 security rail: structural guards ported from the upstream
/// security sweep — public/well-known credential rejection and
/// loopback-only enforcement for destructive/probing admin routes.
/// </summary>
public static partial class SecurityRails
{
    /// <summary>Provider-connection Data fields that hold secrets.</summary>
    private static readonly string[] SecretFields = ["apiKey", "api_key", "key", "token", "accessToken", "secret", "password"];

    /// <summary>Return an error message when the secret looks like a public/demo/placeholder credential; null when acceptable.</summary>
    public static string? PublicCredRejection(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return null;
        var s = secret.Trim();
        if (PublicCredPattern().IsMatch(s))
            return $"refusing to store a public/placeholder credential value ('{s[..Math.Min(24, s.Length)]}…')";
        return null;
    }

    /// <summary>Scan a connection `data` JSON object for secret fields and reject public values.</summary>
    public static string? PublicCredRejection(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        foreach (var field in SecretFields)
            if (data.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                && PublicCredRejection(v.GetString()) is { } err)
                return err;
        return null;
    }

    /// <summary>Route prefixes that must be loopback-only when adminLocalOnly is on (destructive/probing ops).</summary>
    public static readonly string[] LocalOnlyPrefixes =
    [
        "/api/discovery/scan",
        "/api/env",
        "/api/db-backups/import",
        "/api/version-manager",
        "/api/services",
        "/api/tunnels",
    ];

    /// <summary>True when the request path hits a local-only prefix.</summary>
    public static bool IsLocalOnlyPath(string path) =>
        LocalOnlyPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loopback or private-range check (trusted nets behind forwarded headers handled by callers).</summary>
    public static bool IsLocal(System.Net.IPAddress? ip) =>
        ip is null || System.Net.IPAddress.IsLoopback(ip)
        || ip.ToString().StartsWith("10.") || ip.ToString().StartsWith("192.168.")
        || ip.ToString().StartsWith("172.16.") || ip.ToString().StartsWith("::1");

    /// <summary>Clamp a user-supplied note id so the vault file cannot escape `root`.</summary>
    public static string SafeFileName(string id)
    {
        // strip directory parts, dot-segments and absolute paths entirely
        var name = id.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = UnsafeChars().Replace(name, "-");
        if (name.StartsWith('.')) name = name.TrimStart('.');
        return name.Length == 0 ? "note" : name;
    }

    /// <summary>True when a resolved full path stays inside root (both normalized).</summary>
    public static bool InsideRoot(string root, string full) =>
        Path.GetFullPath(full).StartsWith(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?i)(example|demo|sample|changeme|placeholder|your[-_ ]?(api[-_ ]?)?key|sk-[\w-]*public|xxxxx|testtesttest|aaaabbbb|0{8,})")]
    private static partial Regex PublicCredPattern();

    [GeneratedRegex(@"[^\w\-. ]")]
    private static partial Regex UnsafeChars();
}

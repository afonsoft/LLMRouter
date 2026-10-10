using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// SPEC-075 — faithful port of OmniRoute open-sse/utils/errorSanitization.ts +
/// errorPathRedaction.ts + credentialPatterns.ts + shared/utils/upstreamError.ts.
/// Every upstream error field that reaches a client passes here: stack-tail
/// strip, credential redaction, absolute-path redaction, bounded security-escape
/// decode, and an allow-listed { error: { ... } } payload shape.
/// </summary>
public static class ErrorSanitizer
{
    private const int MaxErrorLen = 4096;
    private const int MaxScanHeadroom = 512;
    private const int MaxEscapeLayers = 3;
    private const int MaxDepth = 4;
    private const int MaxUpstreamKeyLen = 256;

    // ---------- credential patterns (credentialPatterns.ts, order matters) ----------
    private static readonly (string Name, Regex Re)[] CredentialPatterns =
    [
        ("openai", new Regex(@"\bsk-proj-[A-Za-z0-9_-]{20,}|\bsk-[A-Za-z0-9]{48}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("anthropic", new Regex(@"\bsk-ant-api[0-9]?-[A-Za-z0-9_-]{20,}|\bsk-ant-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("groq", new Regex(@"\bgsk_[A-Za-z0-9]{20,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("xai", new Regex(@"\bxai-[A-Za-z0-9]{20,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("google", new Regex(@"\bAIza[0-9A-Za-z_-]{20,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("huggingface", new Regex(@"\bhf_[A-Za-z0-9]{34}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("replicate", new Regex(@"\br8_[A-Za-z0-9]{37}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("github", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}|\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("slack", new Regex(@"\bxox[bpoa]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("linear", new Regex(@"\blin_api_[A-Za-z0-9]{40}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("notion", new Regex(@"\bsecret_[A-Za-z0-9]{43}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("npm", new Regex(@"\bnpm_[A-Za-z0-9]{36}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("postman", new Regex(@"\bPMAK-[a-f0-9]{8}-[a-f0-9]{32}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("discord", new Regex(@"\b[MN][A-Za-z0-9]{23}\.[A-Za-z0-9]{6}\.[A-Za-z0-9]{27}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("stripe", new Regex(@"\b(?:sk|rk)_(?:live|test)_[0-9a-zA-Z]{24,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("square", new Regex(@"\bsq0(?:atp-[0-9A-Za-z_-]{22}|csp-[0-9A-Za-z_-]{43})", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("aws", new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("twilio", new Regex(@"\bSK[0-9a-fA-F]{32}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("sendgrid", new Regex(@"\bSG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("mailgun", new Regex(@"\bkey-[a-f0-9]{32}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("private_key", new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("jwt", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("connection_string", new Regex(@"(?:mongodb(?:\+srv)?|postgres(?:ql)?|mysql|redis|amqp)://[^:/@\s""']+:[^:/@\s""']+@", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("gitlab", new Regex(@"\bglpat-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500))),
        ("openai_compatible", new Regex(@"(?<![A-Za-z0-9])(?:[A-Za-z0-9]{3,})?sk[-_][A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500))),
    ];

    private static readonly Regex BearerBasicRe =
        new(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));

    // Labeled assignments: label → failClosed (true = redact to end of string).
    private static readonly (string Label, bool FailClosed)[] CredentialLabels =
    [
        ("__secure-next-auth.session-token", true), ("arena-auth-prod-v1", true),
        ("__cf_bm", true), ("_cfuvid", true), ("_puid", true),
        ("access_token_v2", true), ("token_v2", true), ("tokenv2", true),
        ("cf_clearance", true), ("credentials", true), ("credential", true),
        ("session id", true), ("session-id", true), ("session_id", true), ("sessionid", true),
        ("encryption key", true), ("encryption-key", true), ("encryption_key", true), ("encryptionkey", true),
        ("private key", true), ("private-key", true), ("private_key", true), ("privatekey", true),
        ("session key", true), ("session-key", true), ("session_key", true), ("sessionkey", true),
        ("secret key", true), ("secret-key", true), ("secret_key", true), ("secretkey", true),
        ("signing key", true), ("signing-key", true), ("signing_key", true), ("signingkey", true),
        ("refresh token", false), ("refresh-token", false), ("refresh_token", false), ("refreshtoken", false),
        ("access token", false), ("access-token", false), ("access_token", false), ("accesstoken", false),
        ("authorization", true), ("sso-rw", true), ("session", true), ("sso", true),
        ("api key", false), ("api-key", false), ("api_key", false), ("apikey", false),
        ("password", true), ("cookie", true), ("secret", true), ("token", false),
    ];

    private static readonly Regex StackFrameRe =
        new(@"^\s*(?:at\s+\S+.*\(.*:\d+:\d+\)|at\s+\S+\s+.*:\d+:\d+|File ""[^""]+"", line \d+|at\s+.*\.(?:cs|ts|js|py|go|java|rb|php)[:\s]\d+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));

    private static readonly Regex AbsolutePathRe =
        new(@"(?:^|[\s(""'`])(/[\w.@~+/-][^\s()\[\]{}""'`;,<>:]*|~[/\\][^\s()\[\]{}""'`;,<>:]*|[A-Za-z]:\\[^\s()\[\]{}""'`;,<>:]*)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));

    private static readonly Regex HttpUrlRe = new(@"https?://[^\s""'`<>\)\]\},;]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
    private static readonly Regex UrlQueryParamRe = new(@"([?&])([^=&#]+)=([^&#]*)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
    private static readonly Regex DataUrlRe = new(@"data:([\w.+-]+/[\w.+-]+)?;base64,[A-Za-z0-9+/=_-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));

    private static readonly Regex BlockedKeys =
        new(@"stack|trace|path|file|cwd|dir|password|secret|token|key|authorization|cookie|credential|session(?!_?(?:count|status)$)", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
    private static readonly Regex BlockedCredentialAliasKeys =
        new(@"^(?:cf_clearance|__cf_bm|_cfuvid|_puid|sso|sso-rw|arena-auth-prod-v1(?:\.\d+)?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
    private static readonly HashSet<string> PrototypeKeys = new(["__proto__", "constructor", "prototype"]);

    private static readonly string[] SensitiveUrlQueryKeys =
    [
        "sig", "signature", "key", "apikey", "token", "accesstoken", "refreshtoken",
        "credential", "password", "secret", "awsaccesskeyid", "googleaccessid",
        "xamzcredential", "xamzsignature", "xamzsecuritytoken", "xgoogcredential", "xgoogsignature",
    ];

    private static readonly HashSet<string> SafeErrorFields = new(["type", "code", "param", "reason", "status"]);

    /// <summary>True when the string carries a strong credential token shape.</summary>
    public static bool ContainsStrongCredentialToken(string value) =>
        CredentialPatterns.Any(p => p.Re.IsMatch(value)) || BearerBasicRe.IsMatch(value);

    // ---------- escape normalization ----------
    private static bool IsSecurityWhitespace(char c) => c is '\b' or '\t' or '\n' or '\f' or '\r';

    private static string DecodeSecurityEscapesOnce(string value, bool decodeQuotes, int maxLength)
    {
        var sb = new StringBuilder(value.Length);
        var changed = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { sb.Append(value[i]); continue; }
            var runStart = i;
            while (i < value.Length && value[i] == '\\') i++;
            if (i >= value.Length) { sb.Append(value, runStart, i - runStart); break; }
            var escaped = value[i];
            if (escaped is 'u' or 'U' && i + 4 < value.Length)
            {
                if (int.TryParse(value.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code)
                    && (IsSecurityWhitespace((char)code) || (code >= 0x20 && code <= 0x7e && (decodeQuotes || (code != 0x22 && code != 0x27)))))
                {
                    sb.Append(IsSecurityWhitespace((char)code) ? ' ' : (char)code);
                    i += 4; changed = true; continue;
                }
                sb.Append(value, runStart, i - runStart); sb.Append(value, i, 5); i += 4; continue;
            }
            if (escaped is 'b' or 'f' or 'n' or 'r' or 't') { sb.Append(' '); changed = true; continue; }
            if (escaped == '/' || (decodeQuotes && escaped is '"' or '\'')) { sb.Append(escaped); changed = true; continue; }
            sb.Append(value, runStart, i - runStart); sb.Append(escaped);
        }
        return changed ? sb.ToString()[..Math.Min(sb.Length, maxLength)] : value;
    }

    private static bool HasResidualSecurityEscape(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') continue;
            while (i < value.Length && value[i] == '\\') i++;
            if (i >= value.Length) return false;
            var e = value[i];
            if (e is 'b' or 'f' or 'n' or 'r' or 't' or '/' or '"' or '\'') return true;
            if (e is 'u' or 'U' && i + 4 < value.Length
                && int.TryParse(value.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code)
                && (IsSecurityWhitespace((char)code) || (code >= 0x20 && code <= 0x7e))) return true;
        }
        return false;
    }

    private static string NormalizeSecurityEscapes(string value, bool decodeQuotes, int maxLength = MaxErrorLen)
    {
        var normalized = value.Length > maxLength ? value[..maxLength] : value;
        for (var layer = 0; layer < MaxEscapeLayers; layer++)
        {
            var decoded = DecodeSecurityEscapesOnce(normalized, decodeQuotes, maxLength);
            if (decoded == normalized) break;
            normalized = decoded.Length > maxLength ? decoded[..maxLength] : decoded;
        }
        return normalized;
    }

    // ---------- credential redaction ----------
    private static string RedactKnownCredentialPatterns(string value)
    {
        var redacted = value;
        foreach (var (name, re) in CredentialPatterns)
            redacted = re.Replace(redacted, $"[REDACTED:{name}]");
        return redacted;
    }

    private static bool IsLabelBoundary(char c) => !char.IsLetterOrDigit(c) && c is not '_' and not '-';

    /// <summary>Redact `label(:|=| )value` credential assignments, incl. JSON-quoted and --flag forms.</summary>
    private static string RedactLabeledCredentialAssignments(string value)
    {
        var sb = new StringBuilder(value.Length);
        var copyStart = 0;
        var i = 0;
        while (i < value.Length)
        {
            var matched = false;
            foreach (var (label, failClosed) in CredentialLabels)
            {
                var labelStart = i;
                var keyQuoted = value[i] is '"' or '\'';
                if (keyQuoted) labelStart++;
                if (labelStart + label.Length > value.Length) continue;
                if (!value.AsSpan(labelStart, label.Length).Equals(label, StringComparison.OrdinalIgnoreCase)) continue;
                var j = labelStart + label.Length;
                if (keyQuoted)
                {
                    if (j >= value.Length || value[j] != value[i]) continue;
                    j++;
                }
                else if (j < value.Length && !IsLabelBoundary(value[j])) continue;
                var sepStart = j;
                while (j < value.Length && char.IsWhiteSpace(value[j])) j++;
                var cliFlag = !keyQuoted && labelStart >= 2
                    && value.AsSpan(labelStart - 2, 2).SequenceEqual("--")
                    && (labelStart == 2 || IsLabelBoundary(value[labelStart - 3]));
                if (j < value.Length && (value[j] == ':' || value[j] == '='))
                {
                    j++;
                    while (j < value.Length && char.IsWhiteSpace(value[j])) j++;
                }
                else if (!(cliFlag && j > sepStart)) continue;

                // j = value start
                if (j < value.Length && value[j] is '"' or '\'')
                {
                    var quote = value[j];
                    var k = j + 1;
                    while (k < value.Length && value[k] != quote)
                    {
                        if (value[k] == '\\') k++;
                        k++;
                    }
                    sb.Append(value, copyStart, j + 1 - copyStart).Append("[REDACTED]");
                    if (k < value.Length) { sb.Append(quote); copyStart = k + 1; }
                    else copyStart = value.Length;
                    i = copyStart; matched = true; break;
                }
                var end = failClosed || (j < value.Length && value[j] == '\\')
                    ? value.Length
                    : j;
                if (end < value.Length)
                    while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] is not '"' and not '\'' and not ',' and not '}')
                        end++;
                sb.Append(value, copyStart, j - copyStart).Append("[REDACTED]");
                copyStart = end;
                i = Math.Max(end, j + 1); matched = true; break;
            }
            if (!matched) i++;
        }
        if (copyStart == 0) return value;
        sb.Append(value, copyStart, value.Length - copyStart);
        return sb.ToString();
    }

    private static string RedactPrivateKeyPemBlocks(string value) =>
        Regex.Replace(value, @"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----[\s\S]*?(?:-----END (?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?-----|$)",
            "[REDACTED]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));

    private static string RedactBase64DataUrls(string value) =>
        DataUrlRe.Replace(value, "[REDACTED_DATA_URL]");

    private static bool IsSensitiveUrlQueryKey(string key)
    {
        var normalized = new string(key.Replace('+', ' ')
            .Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return SensitiveUrlQueryKeys.Contains(normalized);
    }

    private static string RedactUrlSegment(string url)
    {
        var schemeEnd = url.IndexOf("//", StringComparison.Ordinal) + 2;
        var authorityEnd = url.Length;
        foreach (var d in new[] { "/", "?", "#" })
        {
            var c = url.IndexOf(d, schemeEnd, StringComparison.Ordinal);
            if (c >= 0) authorityEnd = Math.Min(authorityEnd, c);
        }
        var redacted = url;
        var userInfoEnd = url.LastIndexOf('@', authorityEnd - 1);
        if (userInfoEnd >= schemeEnd)
            redacted = $"{url[..schemeEnd]}[REDACTED]@{url[(userInfoEnd + 1)..]}";
        return UrlQueryParamRe.Replace(redacted, m =>
            IsSensitiveUrlQueryKey(m.Groups[2].Value) ? $"{m.Groups[1].Value}redacted=[REDACTED]" : m.Value);
    }

    private static string RedactSensitiveUrlCredentials(string value) =>
        HttpUrlRe.Replace(value, m => RedactUrlSegment(m.Value));

    /// <summary>redactSensitiveErrorText — full credential pass.</summary>
    public static string RedactSensitiveErrorText(string value)
    {
        var normalized = NormalizeSecurityEscapes(value, false, MaxErrorLen + MaxScanHeadroom);
        var catalog = RedactKnownCredentialPatterns(RedactSensitiveUrlCredentials(normalized));
        var common = RedactBase64DataUrls(RedactPrivateKeyPemBlocks(catalog));
        common = BearerBasicRe.Replace(common, "$1 [REDACTED]");
        return RedactLabeledCredentialAssignments(common);
    }

    // ---------- stack tail + path redaction (errorPathRedaction.ts) ----------
    /// <summary>Cut the message at the first line that looks like a stack frame.</summary>
    public static string StripRecognizedErrorStackTail(string value)
    {
        var idx = value.IndexOf('\n');
        while (idx >= 0)
        {
            var next = idx + 1;
            while (next < value.Length && (value[next] == '\r' || value[next] == '\n' || value[next] == ' ' || value[next] == '\t'))
                next++;
            if (next >= value.Length) break;
            if (StackFrameRe.IsMatch(value[next..])) return value[..idx];
            idx = value.IndexOf('\n', next);
        }
        // inline " at foo (file.ts:1:2)" tail
        var inline = Regex.Match(value, @"\s+at\s+[^\s]+\s+\([^\s()]+:\d+:\d+\)", RegexOptions.None, TimeSpan.FromMilliseconds(500));
        return inline.Success ? value[..inline.Index] : value;
    }

    /// <summary>Fail-closed variant: only the first physical line survives.</summary>
    public static string StripErrorStackTail(string value)
    {
        var firstEnd = value.IndexOfAny(['\r', '\n']);
        var first = firstEnd < 0 ? value : value[..firstEnd];
        return StripRecognizedErrorStackTail(first);
    }

    /// <summary>Redact absolute filesystem paths (URLs and API routes preserved).</summary>
    public static string RedactErrorPaths(string value) =>
        AbsolutePathRe.Replace(value, m =>
        {
            var token = m.Groups[1].Value;
            if (token.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return m.Value;
            if (token.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return m.Value;
            var sep = token.Replace('\\', '/');
            var name = sep[(sep.LastIndexOf('/') + 1)..];
            return m.Value[..^token.Length] + (name.Length > 0 ? $"<path>/{name}" : "<path>");
        });

    // ---------- public message sanitizer ----------
    /// <summary>sanitizeErrorMessage — the multi-pass pipeline upstream applies to client-visible errors.</summary>
    public static string SanitizeMessage(object? message)
    {
        var str = message as string ?? message?.ToString() ?? "";
        if (str.Length > MaxErrorLen + MaxScanHeadroom)
            str = str[..(MaxErrorLen + MaxScanHeadroom)];
        str = RedactLabeledCredentialAssignments(
            RedactKnownCredentialPatterns(RedactSensitiveUrlCredentials(StripErrorStackTail(str))));
        str = RedactErrorPaths(str);
        str = RedactSensitiveErrorText(str);
        str = TruncateSanitized(str);
        str = NormalizeSecurityEscapes(str, false);
        str = RedactSensitiveErrorText(RedactErrorPaths(StripErrorStackTail(str)));
        str = NormalizeSecurityEscapes(str, true);
        str = RedactSensitiveErrorText(RedactErrorPaths(StripErrorStackTail(str)));
        return HasResidualSecurityEscape(str) ? "[REDACTED]" : str.TrimEnd();
    }

    private static string TruncateSanitized(string value)
    {
        if (value.Length <= MaxErrorLen) return value;
        var markerStart = value.LastIndexOf("[REDACTED", MaxErrorLen, StringComparison.Ordinal);
        var markerEnd = markerStart >= 0 ? value.IndexOf(']', markerStart) : -1;
        if (markerStart >= 0 && markerStart < MaxErrorLen && markerEnd >= MaxErrorLen && markerEnd - markerStart <= 128)
        {
            var marker = value[markerStart..(markerEnd + 1)];
            return $"{value[..(MaxErrorLen - marker.Length)]}{marker}";
        }
        return value[..MaxErrorLen];
    }

    // ---------- upstream detail records (sanitizeUpstreamDetails) ----------
    /// <summary>Recursively drop credential/path-shaped keys and sanitize strings, mirroring sanitizeUpstreamDetails.</summary>
    public static object? SanitizeUpstreamDetails(JsonElement value, int depth = 0)
    {
        if (depth > MaxDepth) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => SanitizeMessage(value.GetString()),
            JsonValueKind.Array => value.EnumerateArray()
                .Select(e => SanitizeUpstreamDetails(e, depth + 1))
                .Where(v => v is not null).ToList(),
            JsonValueKind.Object => SanitizeUpstreamObject(value, depth),
            JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static Dictionary<string, object?> SanitizeUpstreamObject(JsonElement obj, int depth)
    {
        var result = new Dictionary<string, object?>();
        foreach (var prop in obj.EnumerateObject())
        {
            var key = prop.Name;
            if (key.Length > MaxUpstreamKeyLen || PrototypeKeys.Contains(key)) continue;
            if (BlockedKeys.IsMatch(key) || BlockedCredentialAliasKeys.IsMatch(key)) continue;
            var sanitized = SanitizeUpstreamDetails(prop.Value, depth + 1);
            if (sanitized is not null) result[key] = sanitized;
        }
        return result;
    }

    // ---------- toJsonErrorPayload ----------
    /// <summary>
    /// toJsonErrorPayload: normalize a raw upstream error body (JSON or text) into
    /// a sanitized { error: { message, type?, code?, ... } } dictionary.
    /// `message` is ALWAYS present (falls back to <paramref name="fallback"/>).
    /// </summary>
    public static Dictionary<string, object?> ToJsonErrorPayload(string? rawBody, string fallback = "Upstream request failed.")
    {
        var fallbackErr = new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?>
            {
                ["message"] = fallback, ["type"] = "upstream_error", ["code"] = "upstream_error",
            },
        };
        if (string.IsNullOrWhiteSpace(rawBody)) return fallbackErr;
        var trimmed = rawBody.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return fallbackErr;
            if (root.TryGetProperty("error", out var errEl))
            {
                if (errEl.ValueKind == JsonValueKind.String)
                    return new Dictionary<string, object?>
                    {
                        ["error"] = new Dictionary<string, object?>
                        {
                            ["message"] = SafeField(errEl.GetString()) ?? fallback,
                            ["type"] = "upstream_error", ["code"] = "upstream_error",
                        },
                    };
                if (errEl.ValueKind == JsonValueKind.Object)
                    return new Dictionary<string, object?> { ["error"] = BuildErrorRecord(errEl, fallback) };
            }
            return new Dictionary<string, object?> { ["error"] = BuildErrorRecord(root, fallback) };
        }
        catch (JsonException)
        {
            var msg = SafeField(trimmed);
            if (msg is null) return fallbackErr;
            return new Dictionary<string, object?>
            {
                ["error"] = new Dictionary<string, object?>
                {
                    ["message"] = msg, ["type"] = "upstream_error", ["code"] = "upstream_error",
                },
            };
        }
    }

    private static string? SafeField(string? v)
    {
        if (v is null) return null;
        var s = SanitizeMessage(v).Trim();
        return s.Length > 0 ? s : null;
    }

    /// <summary>buildErrorRecord — allow-listed fields only; message always present.</summary>
    private static Dictionary<string, object?> BuildErrorRecord(JsonElement record, string fallback)
    {
        var result = new Dictionary<string, object?>();
        foreach (var prop in record.EnumerateObject())
        {
            if (!SafeErrorFields.Contains(prop.Name) && prop.Name != "message") continue;
            if (prop.Name == "message") continue;
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => SafeField(prop.Value.GetString()),
                JsonValueKind.Number when prop.Value.TryGetInt32(out var n) => n,
                JsonValueKind.Number when prop.Value.TryGetDouble(out var d) => d,
                JsonValueKind.True or JsonValueKind.False => prop.Value.GetBoolean(),
                _ => null,
            };
            if (result[prop.Name] is null) result.Remove(prop.Name);
        }
        string? message = null;
        if (record.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            message = SafeField(m.GetString());
        result["message"] = message ?? fallback;
        return result;
    }

    /// <summary>Serialize a sanitized error payload for a client response.</summary>
    public static string ErrorPayloadJson(string? rawBody, string fallback = "Upstream request failed.") =>
        JsonSerializer.Serialize(ToJsonErrorPayload(rawBody, fallback));

    /// <summary>Extract just the sanitized message for shaping per-protocol errors.</summary>
    public static string SanitizedMessage(string? rawBody, string fallback = "Upstream request failed.")
    {
        var payload = ToJsonErrorPayload(rawBody, fallback);
        return (payload["error"] as Dictionary<string, object?>)?["message"] as string ?? fallback;
    }
}

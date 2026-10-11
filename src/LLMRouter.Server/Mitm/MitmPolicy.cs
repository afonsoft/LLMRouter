using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace LLMRouter.Server.Mitm;

/// <summary>
/// SPEC-100: leitura de `settings.mitm` — tlsIntercept, intercept policy
/// ({mode: allow|deny|all, hosts: []} com globs `*.example.com`) e
/// captureMode (full|headers|none). Defaults preservam o comportamento
/// atual (intercept tudo, capture full).
/// </summary>
public static class MitmPolicy
{
    public sealed record Cfg(bool TlsIntercept, string InterceptMode, string[] Hosts, string CaptureMode)
    {
        /// <summary>Intercepta este host? (all = sempre; allow = só glob; deny = tudo menos glob).</summary>
        public bool ShouldIntercept(string host) => InterceptMode switch
        {
            "allow" => Hosts.Any(h => Match(h, host)),
            "deny" => !Hosts.Any(h => Match(h, host)),
            _ => true, // "all" e desconhecido = intercept (default upstream)
        };
    }

    /// <summary>Glob host matcher: `*.example.com` casa subdomains+apex, exato = string eq.</summary>
    public static bool Match(string pattern, string host)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(host)) return false;
        if (pattern == "*") return true;
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..]; // ".example.com"
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.EndsWith('*'))
            return host.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lê settings.mitm do db (falha → defaults conservadores: intercept off, capture full).</summary>
    public static async Task<Cfg> ReadAsync(IServiceScopeFactory sf, CancellationToken ct)
    {
        try
        {
            using var scope = sf.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
            var row = await db.Settings.FindAsync([1], ct);
            if (row?.Data is null) return new(false, "all", [], "full");
            var d = JsonDocument.Parse(row.Data).RootElement;
            if (!d.TryGetProperty("mitm", out var m)) return new(false, "all", [], "full");
            var tls = m.TryGetProperty("tlsIntercept", out var t) && t.ValueKind == JsonValueKind.True;
            var mode = "all";
            string[] hosts = [];
            if (m.TryGetProperty("intercept", out var i) && i.ValueKind == JsonValueKind.Object)
            {
                if (i.TryGetProperty("mode", out var mm) && mm.GetString() is { Length: > 0 } s) mode = s;
                if (i.TryGetProperty("hosts", out var hh) && hh.ValueKind == JsonValueKind.Array)
                    hosts = hh.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToArray();
            }
            var capture = m.TryGetProperty("captureMode", out var c) && c.GetString() is { Length: > 0 } cs
                ? cs : "full";
            return new(tls, mode, hosts, capture);
        }
        catch { return new(false, "all", [], "full"); }
    }
}

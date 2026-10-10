using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LLMRouter.Core.Data;
using LLMRouter.Core.Usage;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>SPEC-047: ip-filter CIDR match, payload rules, reasoning routing,
/// task routing, free-proxy import, oneproxy state.</summary>
public static class RoutingOps
{
    // ---------- ip filter ----------

    /// <summary>True when <paramref name="ip"/> falls inside <paramref name="cidr"/>
    /// (IPv4/IPv6; a bare address means /32 or /128).</summary>
    public static bool IpMatch(string ip, string cidr)
    {
        if (!IPAddress.TryParse(ip.Trim(), out var addr)) return false;
        var parts = cidr.Trim().Split('/');
        if (!IPAddress.TryParse(parts[0], out var net)) return false;
        if (addr.AddressFamily != net.AddressFamily) return false;
        var a = addr.GetAddressBytes();
        var n = net.GetAddressBytes();
        var bits = parts.Length > 1 && int.TryParse(parts[1], out var p)
            ? p : n.Length * 8;
        if (bits < 0 || bits > n.Length * 8) return false;
        for (var i = 0; i < a.Length; i++)
        {
            var rem = bits - i * 8;
            if (rem <= 0) return true;
            if (rem >= 8)
            {
                if (a[i] != n[i]) return false;
            }
            else
            {
                var mask = (byte)(0xFF << (8 - rem));
                return (a[i] & mask) == (n[i] & mask);
            }
        }
        return true;
    }

    /// <summary>settings.ipFilter {mode:"allow"|"deny", cidrs:[]} →
    /// null = no filter; otherwise whether the remote ip may proceed.</summary>
    public static bool IpAllowed(JsonElement sdata, string? remoteIp)
    {
        if (remoteIp is null || Section(sdata, "ipFilter") is not { } f) return true;
        if (f.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) return true;
        var cidrs = f.TryGetProperty("cidrs", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];
        if (cidrs.Count == 0) return true;
        var mode = f.TryGetProperty("mode", out var m) ? m.GetString() : "deny";
        var hit = cidrs.Any(c => IpMatch(remoteIp, c));
        return mode == "allow" ? hit : !hit;
    }

    // ---------- payload rules ----------

    /// <summary>Apply settings.payloadRules [{match:{provider?,model?,pattern?},
    /// action:set|remove|redact|cap, field:"a.b", value?}] to the request body.</summary>
    public static JsonElement ApplyPayloadRules(JsonElement sdata, JsonElement body,
        string? provider, string model)
    {
        if (Section(sdata, "payloadRules") is not { } rules || rules.ValueKind != JsonValueKind.Array)
            return body;
        JsonObject? node = null;
        foreach (var rule in rules.EnumerateArray())
        {
            var match = rule.TryGetProperty("match", out var mm) && mm.ValueKind == JsonValueKind.Object
                ? mm : (JsonElement?)null;
            if (match is { } mx)
            {
                if (mx.TryGetProperty("provider", out var pv) && pv.GetString() is { } prov
                    && prov.Length > 0 && prov != provider) continue;
                if (mx.TryGetProperty("model", out var mo) && mo.GetString() is { } pat
                    && pat.Length > 0 && !(model.Contains(pat, StringComparison.OrdinalIgnoreCase)
                        || Regex.IsMatch(model, pat, RegexOptions.IgnoreCase))) continue;
            }
            var field = rule.TryGetProperty("field", out var fl) ? fl.GetString() : null;
            if (string.IsNullOrEmpty(field)) continue;
            var action = rule.TryGetProperty("action", out var ac) ? ac.GetString() : "set";
            node ??= JsonNode.Parse(body.GetRawText())!.AsObject();
            ApplyRule(node, field!, action ?? "set", rule);
        }
        return node is null ? body : JsonDocument.Parse(node.ToJsonString()).RootElement;
    }

    private static void ApplyRule(JsonObject root, string field, string action, JsonElement rule)
    {
        var segs = field.Split('.', StringSplitOptions.RemoveEmptyEntries);
        JsonObject cur = root;
        for (var i = 0; i < segs.Length - 1; i++)
        {
            if (cur[segs[i]] is JsonObject next) cur = next;
            else if (action is "set")
            {
                var o = new JsonObject();
                cur[segs[i]] = o;
                cur = o;
            }
            else return;
        }
        var last = segs[^1];
        switch (action)
        {
            case "remove":
                cur.Remove(last);
                break;
            case "redact":
                if (cur.ContainsKey(last)) cur[last] = "***";
                break;
            case "cap":
                if (cur[last] is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    var cap = rule.TryGetProperty("value", out var c) && c.TryGetInt32(out var n) ? n : 4000;
                    if (s.Length > cap) cur[last] = s[..cap];
                }
                break;
            default: // set
                cur[last] = rule.TryGetProperty("value", out var val)
                    ? JsonNode.Parse(val.GetRawText()) : null;
                break;
        }
    }

    // ---------- reasoning routing ----------

    /// <summary>Match settings.reasoningRoutingRules [{pattern, effort,
    /// provider?, budgetTokens?}] against the model; apply effort mapping:
    /// budget→thinking.budget_tokens, max→reasoning_effort=high, off→strip.</summary>
    public static (JsonElement Body, string? MatchedPattern, string? Effort) ApplyReasoningRules(
        JsonElement sdata, JsonElement body, string model)
    {
        if (Section(sdata, "reasoningRoutingRules") is not { } rules
            || rules.ValueKind != JsonValueKind.Array)
            return (body, null, null);
        foreach (var rule in rules.EnumerateArray())
        {
            var pattern = rule.TryGetProperty("pattern", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(pattern)) continue;
            var matched = model.Contains(pattern!, StringComparison.OrdinalIgnoreCase);
            if (!matched)
                try { matched = Regex.IsMatch(model, pattern!, RegexOptions.IgnoreCase); }
                catch { }
            if (!matched) continue;
            var effort = rule.TryGetProperty("effort", out var e) ? e.GetString() : null;
            var node = JsonNode.Parse(body.GetRawText())!.AsObject();
            switch (effort)
            {
                case "off":
                    node.Remove("reasoning_effort");
                    node.Remove("reasoning");
                    node.Remove("thinking");
                    break;
                case "max":
                    node["reasoning_effort"] = "high";
                    break;
                default: // budget
                    var tokens = rule.TryGetProperty("budgetTokens", out var bt) && bt.TryGetInt32(out var n)
                        ? n : 1024;
                    var thinking = node["thinking"] as JsonObject ?? new JsonObject();
                    thinking["budget_tokens"] = tokens;
                    node["thinking"] = thinking;
                    break;
            }
            return (JsonDocument.Parse(node.ToJsonString()).RootElement, pattern, effort ?? "budget");
        }
        return (body, null, null);
    }

    // ---------- task routing ----------

    /// <summary>settings.taskRouting {taskType: modelOrCombo} → mapped model or null.</summary>
    public static string? ResolveTaskModel(JsonElement sdata, string taskType)
    {
        if (Section(sdata, "taskRouting") is not { } tr) return null;
        return tr.TryGetProperty(taskType, out var m) ? m.GetString() : null;
    }

    // ---------- free proxies ----------

    /// <summary>Parse a free-proxy list (plain "host:port" lines or JSON array
    /// of {ip,port}/{host,port}) into "http://host:port" urls.</summary>
    public static List<string> ParseFreeProxies(string text)
    {
        var urls = new List<string>();
        var trimmed = text.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                foreach (var el in JsonDocument.Parse(trimmed).RootElement.EnumerateArray())
                {
                    var host = el.TryGetProperty("ip", out var ip) ? ip.GetString()
                        : el.TryGetProperty("host", out var h) ? h.GetString() : null;
                    var port = el.TryGetProperty("port", out var po)
                        ? (po.ValueKind == JsonValueKind.Number ? po.GetInt32().ToString() : po.GetString())
                        : null;
                    if (host is not null && port is not null)
                        urls.Add($"http://{host}:{port}");
                }
            }
            catch { }
            return urls;
        }
        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var l = line.Trim();
            if (l.Length > 7 && l.Contains(':'))
                urls.Add(l.StartsWith("http") ? l : $"http://{l}");
        }
        return urls;
    }

    // ---------- oneproxy ----------

    /// <summary>settings.oneproxy {urls:[], rotateOnFail} → current proxy url
    /// tracked in kv "oneproxy" (first url when unset); rotate advances.</summary>
    public static async Task<string?> OneProxyCurrentAsync(LlmRouterDbContext db, JsonElement sdata)
    {
        if (Section(sdata, "oneproxy") is not { } op) return null;
        var urls = op.TryGetProperty("urls", out var u) && u.ValueKind == JsonValueKind.Array
            ? u.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];
        if (urls.Count == 0)
        {
            var single = op.TryGetProperty("url", out var su) ? su.GetString() : null;
            if (!string.IsNullOrEmpty(single)) urls.Add(single!);
        }
        if (urls.Count == 0) return null;
        var cur = (await db.Kv.FindAsync("oneproxy", "current"))?.Value;
        return cur is not null && urls.Contains(cur) ? cur : urls[0];
    }

    /// <summary>Advance kv "oneproxy".current to the next url (rotate-on-fail).</summary>
    public static async Task<string?> OneProxyRotateAsync(LlmRouterDbContext db, JsonElement sdata)
    {
        if (Section(sdata, "oneproxy") is not { } op) return null;
        var urls = op.TryGetProperty("urls", out var u) && u.ValueKind == JsonValueKind.Array
            ? u.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];
        if (urls.Count < 2) return null;
        if (op.TryGetProperty("rotateOnFail", out var ro) && ro.ValueKind == JsonValueKind.False)
            return null;
        var row = await db.Kv.FindAsync("oneproxy", "current")
            ?? db.Kv.Add(new KvEntry { Scope = "oneproxy", Key = "current", Value = urls[0] }).Entity;
        var i = urls.IndexOf(row.Value);
        row.Value = urls[(i + 1) % urls.Count];
        await db.SaveChangesAsync();
        return row.Value;
    }

    // ---------- shared ----------

    internal static JsonElement? Section(JsonElement sdata, string key) =>
        sdata.ValueKind == JsonValueKind.Object && sdata.TryGetProperty(key, out var s)
            ? s : null;
}

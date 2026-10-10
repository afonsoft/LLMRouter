using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.ProviderOps;

/// <summary>
/// SPEC-054: per-connection upstream rewrite rules stored on
/// providerConnection.Data — interceptionRules (match → rewrite model/params),
/// paramFilters (drop params the provider rejects), ccAlias (model alias map).
/// Applied verbatim to the outgoing JSON body inside the gateway forwarder.
/// </summary>
public static class ProviderRules
{
    /// <summary>Parse conn.Data into a dictionary of JsonElement (empty on failure).</summary>
    public static Dictionary<string, JsonElement> DataOf(ProviderConnection c)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(c.Data) ?? [];
        }
        catch { return []; }
    }

    /// <summary>Write a JSON field back onto conn.Data.</summary>
    public static void SetData(ProviderConnection c, string key, JsonElement value)
    {
        var d = DataOf(c);
        d[key] = value;
        c.Data = JsonSerializer.Serialize(d);
        c.UpdatedAt = DateTime.UtcNow.ToString("o");
    }

    /// <summary>
    /// Apply this connection's rewrite rules to a request body.
    /// Returns the (possibly unchanged) body bytes; non-JSON bodies pass through.
    /// </summary>
    public static byte[]? ApplyRewrites(ProviderConnection conn, string path, byte[]? rawBody)
    {
        if (rawBody is null) return null;
        var data = DataOf(conn);
        var hasRules = data.ContainsKey("interceptionRules")
            || data.ContainsKey("paramFilters") || data.ContainsKey("ccAlias");
        if (!hasRules) return rawBody;

        JsonNode? node;
        try { node = JsonNode.Parse(rawBody); }
        catch { return rawBody; }
        if (node is not JsonObject obj) return rawBody;

        // ccAlias: body.model is an alias key → rewrite to the real model
        if (data.TryGetValue("ccAlias", out var al) && al.ValueKind == JsonValueKind.Object
            && obj["model"] is JsonValue mv && mv.TryGetValue<string>(out var m) && m is not null
            && al.TryGetProperty(m, out var target) && target.ValueKind == JsonValueKind.String)
            obj["model"] = target.GetString();

        // interceptionRules: [{match:{model?, pathContains?}, rewrite:{model?, set?:{}, drop?:[]}}]
        if (data.TryGetValue("interceptionRules", out var ir) && ir.ValueKind == JsonValueKind.Array)
        {
            var model = obj["model"] is JsonValue mm && mm.TryGetValue<string>(out var m2) ? m2 : null;
            foreach (var rule in ir.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object) continue;
                if (!Matches(rule, model, path)) continue;
                if (!rule.TryGetProperty("rewrite", out var rw) || rw.ValueKind != JsonValueKind.Object) continue;
                if (rw.TryGetProperty("model", out var rm) && rm.ValueKind == JsonValueKind.String)
                    obj["model"] = rm.GetString();
                if (rw.TryGetProperty("set", out var set) && set.ValueKind == JsonValueKind.Object)
                    foreach (var kv in set.EnumerateObject())
                        obj[kv.Name] = JsonNode.Parse(kv.Value.GetRawText());
                if (rw.TryGetProperty("drop", out var drop) && drop.ValueKind == JsonValueKind.Array)
                    foreach (var d in drop.EnumerateArray())
                        if (d.ValueKind == JsonValueKind.String) obj.Remove(d.GetString());
            }
        }

        // paramFilters: [names] — strip from the outgoing body
        if (data.TryGetValue("paramFilters", out var pf) && pf.ValueKind == JsonValueKind.Array)
            foreach (var f in pf.EnumerateArray())
                if (f.ValueKind == JsonValueKind.String) obj.Remove(f.GetString());

        return JsonSerializer.SerializeToUtf8Bytes(obj);
    }

    private static bool Matches(JsonElement rule, string? model, string path)
    {
        if (!rule.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Object)
            return true; // match-all rule
        if (match.TryGetProperty("model", out var mm) && mm.ValueKind == JsonValueKind.String)
        {
            var want = mm.GetString();
            if (want != "*" && want != model) return false;
        }
        if (match.TryGetProperty("pathContains", out var pc) && pc.ValueKind == JsonValueKind.String)
        {
            var frag = pc.GetString();
            if (frag is not null && !path.Contains(frag, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}

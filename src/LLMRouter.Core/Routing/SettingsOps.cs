using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>SPEC-046: settings-driven ops — purge, system prompt,
/// thinking budget, tiers, connection auto-disable.</summary>
public static class SettingsOps
{
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private static JsonElement? Section(JsonElement sdata, string key) =>
        sdata.ValueKind == JsonValueKind.Object
        && sdata.TryGetProperty(key, out var s) && s.ValueKind == JsonValueKind.Object ? s : null;

    // ---------- purge ----------

    /// <summary>Row-count targets for /api/settings/purge/{target}.</summary>
    public static async Task<int> PurgeAsync(LlmRouterDbContext db, string target, string? before)
    {
        var cutoff = before is { Length: > 0 } ? before : Now();
        return target switch
        {
            "call-logs" or "detailed-logs" or "logs" =>
                await db.RequestDetails.Where(r => r.Timestamp.CompareTo(cutoff) < 0)
                    .ExecuteDeleteAsync(),
            "request-history" or "usage-history" =>
                await db.UsageHistory.Where(r => r.Timestamp.CompareTo(cutoff) < 0)
                    .ExecuteDeleteAsync(),
            "quota-snapshots" =>
                await db.Kv.Where(k => k.Scope == "quotaSnapshots" && k.Key.CompareTo(cutoff) < 0)
                    .ExecuteDeleteAsync(),
            _ => -1,
        };
    }

    // ---------- system prompt ----------

    /// <summary>
    /// Inject settings.systemPrompt {text, providers?} as the first system
    /// message when the request has none. providers is an allow-list of
    /// provider ids (empty = all). Returns the (possibly) rewritten body.
    /// </summary>
    public static JsonElement InjectSystemPrompt(
        JsonElement sdata, JsonElement body, string? providerId)
    {
        if (Section(sdata, "systemPrompt") is not { } sp) return body;
        var text = sp.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(text)) return body;
        if (sp.TryGetProperty("providers", out var provs)
            && provs.ValueKind == JsonValueKind.Array && provs.GetArrayLength() > 0)
        {
            var allowed = provs.EnumerateArray().Select(p => p.GetString()).ToList();
            if (providerId is null || !allowed.Contains(providerId)) return body;
        }
        if (body.ValueKind != JsonValueKind.Object) return body;
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        if (node["messages"] is JsonArray msgs)
        {
            // already carries a system message → leave alone (exactly-once rule)
            if (msgs.Any(m => m is JsonObject o && o["role"]?.GetValue<string>() == "system"))
                return body;
            msgs.Insert(0, JsonNode.Parse(
                JsonSerializer.Serialize(new { role = "system", content = text })));
        }
        else return body; // input/contents formats left untouched
        return JsonSerializer.SerializeToElement(node);
    }

    // ---------- thinking budget ----------

    /// <summary>
    /// settings.thinkingBudget {default,max,models:{substring:budget}} —
    /// clamp thinking.budget_tokens to max; when the request has no thinking
    /// block and a default is configured for the model, inject it.
    /// </summary>
    public static JsonElement ClampThinkingBudget(JsonElement sdata, JsonElement body, string model)
    {
        if (Section(sdata, "thinkingBudget") is not { } tb || body.ValueKind != JsonValueKind.Object)
            return body;
        var max = tb.TryGetProperty("max", out var mx) && mx.TryGetInt32(out var mxi) ? mxi : 0;
        var budget = 0;
        if (tb.TryGetProperty("models", out var mm) && mm.ValueKind == JsonValueKind.Object)
            foreach (var p in mm.EnumerateObject())
                if (model.Contains(p.Name, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt32(out var v))
                    budget = v;
        if (budget == 0 && tb.TryGetProperty("default", out var d) && d.TryGetInt32(out var di)) budget = di;
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        var changed = false;
        if (node["thinking"] is JsonObject th)
        {
            var cur = th["budget_tokens"]?.GetValue<int>() ?? 0;
            var cap = max > 0 && max < cur ? max : budget > 0 && budget < cur ? budget : cur;
            if (cap != cur) { th["budget_tokens"] = cap; changed = true; }
        }
        else if (budget > 0)
        {
            node["thinking"] = JsonNode.Parse(
                JsonSerializer.Serialize(new { type = "enabled", budget_tokens = budget }));
            changed = true;
        }
        return changed ? JsonSerializer.SerializeToElement(node) : body;
    }

    // ---------- tier config ----------

    /// <summary>Assigned tier name for an api key (kv scope "keytiers").</summary>
    public static async Task<string?> TierForAsync(LlmRouterDbContext db, string apiKey) =>
        await db.Kv.Where(k => k.Scope == "keytiers" && k.Key == apiKey)
            .Select(k => k.Value).FirstOrDefaultAsync();

    /// <summary>
    /// Tier rules for an api key: {models:[], dailyTokens}. Returns the tier's
    /// model allow-list (null = unrestricted) and daily cap (0 = none).
    /// </summary>
    public static async Task<(List<string>? models, int dailyTokens)> TierRulesAsync(
        LlmRouterDbContext db, JsonElement sdata, string apiKey)
    {
        var tierName = await TierForAsync(db, apiKey);
        if (tierName is null || Section(sdata, "tierConfig") is not { } tc) return (null, 0);
        if (!tc.TryGetProperty("tiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array)
            return (null, 0);
        var tier = tiers.EnumerateArray()
            .FirstOrDefault(t => t.TryGetProperty("name", out var n) && n.GetString() == tierName);
        if (tier.ValueKind == JsonValueKind.Undefined) return (null, 0);
        List<string>? models = tier.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array
            ? ms.EnumerateArray().Select(m => m.GetString()!).Where(m => m.Length > 0).ToList() : null;
        var daily = tier.TryGetProperty("limits", out var li) && li.ValueKind == JsonValueKind.Object
            && li.TryGetProperty("dailyTokens", out var dt) && dt.TryGetInt32(out var dti) ? dti : 0;
        return (models is { Count: 0 } ? null : models, daily);
    }

    // ---------- auto-disable accounts ----------

    /// <summary>
    /// Consecutive-error tracker per connection (kv scope "connErrors").
    /// On ≥threshold failures marks the connection inactive; on success resets.
    /// </summary>
    public static async Task ReportConnectionAsync(
        LlmRouterDbContext db, JsonElement sdata, string connectionId, bool success)
    {
        if (Section(sdata, "autoDisableAccounts") is not { } cfg) return;
        var threshold = cfg.TryGetProperty("threshold", out var t) && t.TryGetInt32(out var ti) ? ti : 5;
        if (success)
        {
            if (await db.Kv.FindAsync("connErrors", connectionId) is { } row)
            {
                db.Kv.Remove(row);
                await db.SaveChangesAsync();
            }
            return;
        }
        var cur = await db.Kv.FindAsync("connErrors", connectionId);
        var n = (cur is not null && int.TryParse(cur.Value, out var c) ? c : 0) + 1;
        if (cur is null) db.Kv.Add(new KvEntry { Scope = "connErrors", Key = connectionId, Value = n.ToString() });
        else cur.Value = n.ToString();
        if (n >= threshold)
        {
            if (await db.ProviderConnections.FindAsync(connectionId) is { } conn && conn.IsActive)
            {
                conn.IsActive = false;
                conn.UpdatedAt = Now();
                db.Kv.Add(new KvEntry { Scope = "connDisabledAt", Key = connectionId, Value = Now() });
            }
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Re-enable connections auto-disabled ≥ reenableMinutes ago.</summary>
    public static async Task<int> ReenableDisabledAsync(LlmRouterDbContext db, JsonElement sdata)
    {
        if (Section(sdata, "autoDisableAccounts") is not { } cfg) return 0;
        var mins = cfg.TryGetProperty("reenableMinutes", out var r) && r.TryGetInt32(out var ri) ? ri : 0;
        if (mins <= 0) return 0;
        var cutoff = DateTime.UtcNow.AddMinutes(-mins).ToString("yyyy-MM-dd HH:mm:ss");
        var rows = await db.Kv.Where(k => k.Scope == "connDisabledAt"
                && string.Compare(k.Value, cutoff) < 0).ToListAsync();
        var n = 0;
        foreach (var row in rows)
        {
            if (await db.ProviderConnections.FindAsync(row.Key) is { } conn && !conn.IsActive)
            {
                conn.IsActive = true; conn.UpdatedAt = Now(); n++;
            }
            db.Kv.Remove(row);
        }
        if (rows.Count > 0) await db.SaveChangesAsync();
        return n;
    }
}

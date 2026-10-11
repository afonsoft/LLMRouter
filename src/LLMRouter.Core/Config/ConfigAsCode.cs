using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Config;

/// <summary>SPEC-104: config-as-code — export/import/diff of the routing config
/// surface (settings, combos, mappings, rateLimits, keys, providers, guardrails).
/// No secrets (providers export redacted), no usage data — one versionable doc.</summary>
public static class ConfigAsCode
{
    public const string Format = "llmr-config";
    public const int Version = 1;
    public static readonly string[] Scopes =
        ["settings", "combos", "mappings", "rateLimits", "keys", "providers", "guardrails"];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Secret-looking JSON keys redacted from ProviderConnection.Data on export.</summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    { "apiKey", "accessToken", "token", "key", "secret", "password", "clientSecret", "privateKey", "refreshToken" };

    // ---- export ----

    public static async Task<JsonObject> ExportAsync(LlmRouterDbContext db, string scopeFilter, CancellationToken ct)
    {
        var wanted = ParseScopes(scopeFilter);
        var scopes = new JsonObject();
        foreach (var s in wanted)
            scopes[s] = s switch
            {
                "settings" => await ExportSettingsAsync(db, ct),
                "combos" => ToArr(await db.Combos.ToListAsync(ct)),
                "mappings" => await ExportKvAsync(db, Routing.ComboMappings.Scope, ct),
                "rateLimits" => ToArr(await db.RateLimits.ToListAsync(ct)),
                "keys" => ToArr(await db.ApiKeys.ToListAsync(ct)),
                "providers" => await ExportProvidersAsync(db, ct),
                "guardrails" => await ExportKvAsync(db, "guardrails", ct),
                _ => null,
            };
        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["exportedAt"] = DateTime.UtcNow.ToString("O"),
            ["scopes"] = scopes,
        };
    }

    private static async Task<JsonNode?> ExportSettingsAsync(LlmRouterDbContext db, CancellationToken ct)
    {
        var s = await db.Settings.FirstOrDefaultAsync(ct);
        return s is null ? new JsonObject() : JsonNode.Parse(s.Data);
    }

    private static async Task<JsonArray> ExportKvAsync(LlmRouterDbContext db, string scope, CancellationToken ct)
    {
        var rows = await db.Kv.Where(k => k.Scope == scope).ToListAsync(ct);
        return new JsonArray(rows.Select(r =>
            (JsonNode)new JsonObject { ["key"] = r.Key, ["value"] = TryJson(r.Value) }).ToArray());
    }

    private static async Task<JsonArray> ExportProvidersAsync(LlmRouterDbContext db, CancellationToken ct)
    {
        var rows = await db.ProviderConnections.ToListAsync(ct);
        var arr = new JsonArray();
        foreach (var r in rows)
        {
            var node = JsonSerializer.SerializeToNode(r, JsonOpts)!.AsObject();
            node["data"] = Redact(r.Data);
            arr.Add(node);
        }
        return arr;
    }

    private static JsonNode? Redact(string? data)
    {
        if (string.IsNullOrEmpty(data)) return new JsonObject();
        try
        {
            var obj = JsonNode.Parse(data)!.AsObject();
            foreach (var kv in obj.ToList())
                if (SecretKeys.Contains(kv.Key) && kv.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                    obj[kv.Key] = new JsonObject { ["$secret"] = true };
            return obj;
        }
        catch { return new JsonObject(); }
    }

    // ---- validate ----

    public static async Task<Dictionary<string, List<string>>> ValidateAsync(
        JsonObject doc, LlmRouterDbContext db, ProviderRegistry registry, CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>();
        void Err(string scope, string msg) => (errors.TryGetValue(scope, out var l) ? l : errors[scope] = []).Add(msg);

        if (doc["format"]?.GetValue<string>() != Format) Err("_doc", "format must be llmr-config");
        if (doc["version"]?.GetValue<int>() != Version) Err("_doc", $"version must be {Version}");
        if (doc["scopes"] is not JsonObject scopes) { Err("_doc", "scopes object required"); return errors; }

        foreach (var kv in scopes)
        {
            if (!Scopes.Contains(kv.Key)) { Err(kv.Key, "unknown scope"); continue; }
            switch (kv.Key)
            {
                case "settings" when kv.Value is not JsonObject: Err(kv.Key, "must be an object"); break;
                case "combos":
                    if (kv.Value is not JsonArray combos) { Err(kv.Key, "must be an array"); break; }
                    foreach (var c in combos)
                    {
                        var name = c?["name"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(name)) { Err(kv.Key, "combo missing name"); continue; }
                        var modelsJson = c["models"]?.GetValue<string>() ?? "[]";
                        List<string>? steps = null;
                        try { steps = JsonSerializer.Deserialize<List<string>>(modelsJson); } catch { }
                        if (steps is null) { Err(kv.Key, $"{name}: models is not a JSON array"); continue; }
                        var comboNames = combos.Select(x => x?["name"]?.GetValue<string>())
                            .Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var dbComboNames = (await db.Combos.Select(x => x.Name).ToListAsync(ct))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var step in steps)
                        {
                            var pm = step.Trim();
                            if (comboNames.Contains(pm) || dbComboNames.Contains(pm)
                                || Routing.AutoCombos.IsAuto(pm)) continue;
                            var slash = pm.IndexOf('/');
                            var prov = slash > 0 ? pm[..slash] : pm;
                            if (pm.EndsWith("/*")) continue;
                            if (registry.GetProvider(prov) is null
                                && await Routing.NodeResolver.ResolveAsync(db, prov, ct) is null)
                                Err(kv.Key, $"{name}: dangling step '{pm}'");
                        }
                    }
                    break;
                default:
                    if (kv.Value is not JsonArray) Err(kv.Key, "must be an array");
                    break;
            }
        }
        return errors;
    }

    // ---- diff ----

    public static async Task<Dictionary<string, JsonObject>> DiffAsync(
        JsonObject doc, LlmRouterDbContext db, CancellationToken ct)
    {
        var result = new Dictionary<string, JsonObject>();
        if (doc["scopes"] is not JsonObject scopes) return result;
        foreach (var kv in scopes)
        {
            var (added, removed, changed) = (new JsonArray(), new JsonArray(), new JsonArray());
            switch (kv.Key)
            {
                case "settings":
                {
                    var cur = (await db.Settings.FirstOrDefaultAsync(ct))?.Data ?? "{}";
                    if (JsonNode.DeepEquals(JsonNode.Parse(cur), kv.Value) == false)
                        changed.Add("settings");
                    break;
                }
                case "combos": DiffRows(kv.Value as JsonArray, await db.Combos.ToListAsync(ct), x => x.Id, added, removed, changed); break;
                case "rateLimits": DiffRows(kv.Value as JsonArray, await db.RateLimits.ToListAsync(ct), x => x.Id, added, removed, changed); break;
                case "keys": DiffRows(kv.Value as JsonArray, await db.ApiKeys.ToListAsync(ct), x => x.Id, added, removed, changed); break;
                case "providers": DiffRows(kv.Value as JsonArray, await db.ProviderConnections.ToListAsync(ct), x => x.Id, added, removed, changed, redactData: true); break;
                case "mappings" or "guardrails":
                    await DiffKvAsync(db, kv.Key == "mappings" ? Routing.ComboMappings.Scope : "guardrails",
                        kv.Value as JsonArray, added, removed, changed, ct);
                    break;
            }
            result[kv.Key] = new JsonObject { ["added"] = added, ["removed"] = removed, ["changed"] = changed };
        }
        return result;
    }

    private static void DiffRows<T>(JsonArray? incoming, List<T> current, Func<T, string> idOf,
        JsonArray added, JsonArray removed, JsonArray changed, bool redactData = false)
    {
        var curById = current.ToDictionary(idOf, e => JsonSerializer.SerializeToNode(e, JsonOpts)!.AsObject());
        var seen = new HashSet<string>();
        foreach (var item in incoming ?? [])
        {
            if (item is not JsonObject o) continue;
            var id = o["id"]?.GetValue<string>();
            if (id is null) continue;
            seen.Add(id);
            if (!curById.TryGetValue(id, out var cur)) { added.Add(id); continue; }
            var cmp = (JsonObject)o.DeepClone();
            if (redactData && cur["data"] is { } cd)
            {
                // compare with secrets stripped both sides — a $secret marker is "unchanged"
                cmp["data"] = StripSecrets(cmp["data"]?.DeepClone());
                var c2 = cur.DeepClone().AsObject();
                c2["data"] = StripSecrets(c2["data"]?.DeepClone());
                if (!JsonNode.DeepEquals(cmp, c2)) changed.Add(id);
            }
            else if (!JsonNode.DeepEquals(cmp, cur.DeepClone())) changed.Add(id);
        }
        foreach (var id in curById.Keys.Where(k => !seen.Contains(k))) removed.Add(id);
    }

    private static JsonNode? StripSecrets(JsonNode? node)
    {
        if (node is JsonObject o)
            foreach (var kv in o.ToList())
                if (SecretKeys.Contains(kv.Key)) o[kv.Key] = null;
        return node;
    }

    private static async Task DiffKvAsync(LlmRouterDbContext db, string scope, JsonArray? incoming,
        JsonArray added, JsonArray removed, JsonArray changed, CancellationToken ct)
    {
        var cur = (await db.Kv.Where(k => k.Scope == scope).ToListAsync(ct))
            .ToDictionary(k => k.Key, k => k.Value);
        var seen = new HashSet<string>();
        foreach (var item in incoming ?? [])
        {
            var key = item?["key"]?.GetValue<string>();
            if (key is null) continue;
            seen.Add(key);
            var val = item["value"] is JsonValue v && v.GetValueKind() == JsonValueKind.String
                ? v.GetValue<string>() : item["value"]?.ToJsonString();
            if (!cur.TryGetValue(key, out var cv)) added.Add(key);
            else if (!SameValue(cv, val)) changed.Add(key);
        }
        foreach (var key in cur.Keys.Where(k => !seen.Contains(k))) removed.Add(key);
    }

    private static bool SameValue(string cur, string? inc) =>
        cur == inc || (TryJson(cur)?.ToJsonString() == TryJson(inc ?? "")?.ToJsonString());

    // ---- import ----

    public sealed record ImportResult(Dictionary<string, int> Counts, Dictionary<string, List<string>> Errors);

    public static async Task<ImportResult> ImportAsync(
        JsonObject doc, string[]? scopes, bool dryRun, string redactedSecrets,
        LlmRouterDbContext db, ProviderRegistry registry, CancellationToken ct)
    {
        var errors = await ValidateAsync(doc, db, registry, ct);
        var counts = new Dictionary<string, int>();
        if (errors.Count > 0) return new(counts, errors);
        if (doc["scopes"] is not JsonObject docScopes) return new(counts, errors);

        foreach (var kv in docScopes)
        {
            if (scopes is { Length: > 0 } && !scopes.Contains(kv.Key)) continue;
            if (dryRun) { counts[kv.Key] = CountOf(kv.Value); continue; }
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            counts[kv.Key] = kv.Key switch
            {
                "settings" => await ImportSettingsAsync(db, kv.Value, ct),
                "combos" => await UpsertAsync(db, db.Combos, kv.Value, c => c.Id, ct),
                "rateLimits" => await UpsertAsync(db, db.RateLimits, kv.Value, c => c.Id, ct),
                "keys" => await UpsertAsync(db, db.ApiKeys, kv.Value, c => c.Id, ct),
                "providers" => await ImportProvidersAsync(db, kv.Value, redactedSecrets, ct),
                "mappings" => await ImportKvAsync(db, Routing.ComboMappings.Scope, kv.Value, ct),
                "guardrails" => await ImportKvAsync(db, "guardrails", kv.Value, ct),
                _ => 0,
            };
            await tx.CommitAsync(ct);
        }
        return new(counts, errors);
    }

    private static int CountOf(JsonNode? node) => node switch
    {
        JsonArray a => a.Count,
        JsonObject o => o.Count,
        _ => 0,
    };

    private static async Task<int> ImportSettingsAsync(LlmRouterDbContext db, JsonNode? node, CancellationToken ct)
    {
        var s = await db.Settings.FirstOrDefaultAsync(ct)
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        s.Data = node?.ToJsonString() ?? "{}";
        await db.SaveChangesAsync(ct);
        return 1;
    }

    private static async Task<int> UpsertAsync<T>(LlmRouterDbContext db, DbSet<T> set, JsonNode? node,
        Func<T, string> idOf, CancellationToken ct) where T : class
    {
        if (node is not JsonArray arr) return 0;
        var n = 0;
        foreach (var item in arr)
        {
            var entity = item.Deserialize<T>(JsonOpts);
            if (entity is null) continue;
            var id = idOf(entity);
            var existing = await set.FindAsync([id], ct);
            if (existing is null) set.Add(entity);
            else db.Entry(existing).CurrentValues.SetValues(entity);
            n++;
        }
        await db.SaveChangesAsync(ct);
        return n;
    }

    private static async Task<int> ImportProvidersAsync(
        LlmRouterDbContext db, JsonNode? node, string redactedSecrets, CancellationToken ct)
    {
        if (node is not JsonArray arr) return 0;
        var n = 0;
        foreach (var item in arr)
        {
            // exported docs carry `data` as a JSON object; the entity stores it as a string
            if (item is JsonObject jo && jo["data"] is JsonObject dataObj)
                jo["data"] = dataObj.ToJsonString();
            var incoming = item.Deserialize<ProviderConnection>(JsonOpts);
            if (incoming is null || incoming.Id.Length == 0) continue;
            var existing = await db.ProviderConnections.FindAsync([incoming.Id], ct);
            if (redactedSecrets == "keep")
            {
                // merge: {"$secret":true} markers keep the stored value; "clear" drops them
                incoming.Data = MergeSecrets(existing?.Data, incoming.Data);
            }
            else if (redactedSecrets == "clear")
            {
                incoming.Data = StripRedacted(incoming.Data);
            }
            if (existing is null) db.ProviderConnections.Add(incoming);
            else db.Entry(existing).CurrentValues.SetValues(incoming);
            n++;
        }
        await db.SaveChangesAsync(ct);
        return n;
    }

    private static string MergeSecrets(string? existingData, string? incomingData)
    {
        if (string.IsNullOrEmpty(incomingData)) return existingData ?? "{}";
        try
        {
            var inc = JsonNode.Parse(incomingData)!.AsObject();
            var cur = string.IsNullOrEmpty(existingData) ? null : JsonNode.Parse(existingData)!.AsObject();
            foreach (var kv in inc.ToList())
                if (kv.Value is JsonObject marker && marker["$secret"] is not null)
                {
                    if (cur is not null && cur.TryGetPropertyValue(kv.Key, out var keep) && keep is not null)
                        inc[kv.Key] = keep.DeepClone();
                    else
                        inc.Remove(kv.Key); // no stored value to keep — drop the marker
                }
            return inc.ToJsonString();
        }
        catch { return incomingData; }
    }

    private static string StripRedacted(string? incomingData)
    {
        if (string.IsNullOrEmpty(incomingData)) return "{}";
        try
        {
            var inc = JsonNode.Parse(incomingData)!.AsObject();
            foreach (var kv in inc.ToList())
                if (kv.Value is JsonObject marker && marker["$secret"] is not null)
                    inc.Remove(kv.Key);
            return inc.ToJsonString();
        }
        catch { return incomingData; }
    }

    private static async Task<int> ImportKvAsync(LlmRouterDbContext db, string scope, JsonNode? node, CancellationToken ct)
    {
        if (node is not JsonArray arr) return 0;
        var n = 0;
        foreach (var item in arr)
        {
            var key = item?["key"]?.GetValue<string>();
            if (key is null) continue;
            var value = item["value"] is JsonValue v && v.GetValueKind() == JsonValueKind.String
                ? v.GetValue<string>() : item["value"]?.ToJsonString() ?? "";
            var row = await db.Kv.FindAsync([scope, key], ct);
            if (row is null) db.Kv.Add(new KvEntry { Scope = scope, Key = key, Value = value });
            else row.Value = value;
            n++;
        }
        await db.SaveChangesAsync(ct);
        return n;
    }

    // ---- helpers ----

    private static string[] ParseScopes(string filter) =>
        string.IsNullOrEmpty(filter) || filter == "all"
            ? Scopes
            : filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Scopes.Contains).ToArray();

    private static JsonArray ToArr<T>(List<T> rows) =>
        new(rows.Select(r => (JsonNode?)JsonSerializer.SerializeToNode(r, JsonOpts)).ToArray());

    private static JsonNode? TryJson(string s)
    {
        try { return JsonNode.Parse(s); } catch { return JsonValue.Create(s); }
    }
}

using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Extras;

/// <summary>
/// SPEC-031: registered plugins (kv scope "plugins", key "registered") as
/// [{id,name,enabled,hooks:[{on,action,value}]}]. Hooks run in the gateway
/// pipeline: onRequest actions mutate the request body before model resolution
/// and/or inject headers into every upstream call; onResponse actions rewrite
/// the translated non-stream response.
/// Actions: setModel (value=string), addHeaders (value={k:v}),
/// annotate (adds "x_plugins" with plugin names).
/// </summary>
public static class PluginHooks
{
    public static async Task<JsonArray> RegisteredAsync(LlmRouterDbContext db)
    {
        var raw = (await db.Kv.FindAsync("plugins", "registered"))?.Value;
        try { return raw is null ? [] : JsonNode.Parse(raw)!.AsArray(); }
        catch { return []; }
    }

    /// <summary>Apply onRequest hooks; mutates body, returns headers to inject upstream.</summary>
    public static async Task<Dictionary<string, string>> ApplyRequestAsync(
        LlmRouterDbContext db, JsonObject body, JsonArray? plugins = null)
    {
        var headers = new Dictionary<string, string>();
        foreach (var p in plugins ?? await RegisteredAsync(db))
        {
            if (p?["enabled"]?.GetValue<bool>() == false) continue;
            foreach (var h in p?["hooks"]?.AsArray() ?? [])
            {
                if (h?["on"]?.GetValue<string>() != "onRequest") continue;
                switch (h?["action"]?.GetValue<string>())
                {
                    case "setModel":
                        if (h?["value"]?.GetValue<string>() is { Length: > 0 } v) body["model"] = v;
                        break;
                    case "addHeaders":
                        foreach (var kv in h?["value"]?.AsObject() ?? [])
                            if (kv.Value is not null) headers[kv.Key] = kv.Value.GetValue<string>();
                        break;
                }
            }
        }
        return headers;
    }

    /// <summary>Apply onResponse hooks to the translated response object.</summary>
    public static void ApplyResponse(JsonArray plugins, JsonObject resp)
    {
        var annotators = new List<string>();
        foreach (var p in plugins)
        {
            if (p?["enabled"]?.GetValue<bool>() == false) continue;
            var name = p?["name"]?.GetValue<string>() ?? p?["id"]?.GetValue<string>() ?? "?";
            foreach (var h in p?["hooks"]?.AsArray() ?? [])
            {
                if (h?["on"]?.GetValue<string>() != "onResponse") continue;
                switch (h?["action"]?.GetValue<string>())
                {
                    case "setModel":
                        if (h?["value"]?.GetValue<string>() is { Length: > 0 } v) resp["model"] = v;
                        break;
                    case "annotate": annotators.Add(name); break;
                }
            }
        }
        if (annotators.Count > 0)
            resp["x_plugins"] = new JsonArray(annotators.Select(a => (JsonNode)a).ToArray());
    }
}

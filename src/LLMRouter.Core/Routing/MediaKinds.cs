using System.Text.Json;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Routing;

/// <summary>
/// Media/service kinds (SPEC-010): maps gateway paths to media kinds and filters
/// resolved targets to connections opted into that kind via data.mediaKinds[].
/// </summary>
public static class MediaKinds
{
    /// <summary>Kinds shown on the media-providers page (upstream VISIBLE_MEDIA_KINDS).</summary>
    public static readonly string[] Visible =
        ["embedding", "image", "video", "tts", "stt", "search", "fetch", "systemone"];

    /// <summary>Map a /v1 path to its media kind, or null for chat-ish paths.</summary>
    public static string? KindForPath(string path)
    {
        var p = path.ToLowerInvariant();
        return p switch
        {
            _ when p.Contains("/audio/speech") || p.Contains("/audio/voices") => "tts",
            _ when p.Contains("/audio/transcriptions") || p.Contains("/audio/translations") => "stt",
            _ when p.Contains("/images/") => "image",
            _ when p.Contains("/videos") => "video",
            _ when p.Contains("/embeddings") => "embedding",
            _ when p.Contains("/search") => "search",
            _ when p.Contains("/web/fetch") || p.Contains("/fetch") => "fetch",
            _ when p.Contains("/systemone") => "systemone",
            _ => null,
        };
    }

    /// <summary>Kinds a connection declares in data.mediaKinds (array) or data.mediaKind (string).</summary>
    public static HashSet<string> KindsOf(ProviderConnection c)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (c.Data is not { Length: > 0 }) return set;
        try
        {
            var d = JsonSerializer.Deserialize<JsonElement>(c.Data);
            if (d.TryGetProperty("mediaKinds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) set.Add(e.GetString()!);
            if (d.TryGetProperty("mediaKind", out var one) && one.ValueKind == JsonValueKind.String)
                set.Add(one.GetString()!);
        }
        catch { }
        return set;
    }

    /// <summary>
    /// Filter resolved targets to connections opted into <paramref name="kind"/>.
    /// If no connection declares that kind, all targets are kept (a generic
    /// connection can still serve the request — chat-model resolution applies).
    /// </summary>
    public static List<T> Filter<T>(List<T> targets, string? kind, Func<T, ProviderConnection> conn)
    {
        if (kind is null || targets.Count == 0) return targets;
        var matching = targets.Where(t => KindsOf(conn(t)).Contains(kind)).ToList();
        return matching.Count > 0 ? matching : targets;
    }
}

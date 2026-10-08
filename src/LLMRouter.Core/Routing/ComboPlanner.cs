using System.Collections.Concurrent;
using System.Text.Json;
using LLMRouter.Core.Registry;

namespace LLMRouter.Core.Routing;

/// <summary>
/// Ported from open-sse/services/combo.js — combo model rotation (fallback /
/// round-robin with sticky limits), capability-based auto-switch reordering,
/// and the trailing-user-turn capability detector.
/// </summary>
public class ComboPlanner
{
    private static readonly HashSet<string> HardCaps = new(StringComparer.Ordinal)
        { "vision", "pdf", "audioInput", "videoInput" };

    private static readonly ConcurrentDictionary<string, (int Index, int Count)> RotationState = new();

    private readonly ProviderRegistry _registry;
    public ComboPlanner(ProviderRegistry registry) => _registry = registry;

    /// <summary>Rotate the model list per the combo strategy.</summary>
    public static List<string> GetRotatedModels(IReadOnlyList<string> models, string? comboName, string? strategy, int stickyLimit = 1)
    {
        if (models.Count <= 1 || !string.Equals(strategy, "round-robin", StringComparison.OrdinalIgnoreCase))
            return models.ToList();
        if (stickyLimit < 1) stickyLimit = 1;

        var key = comboName ?? "__default__";
        var state = RotationState.GetOrAdd(key, _ => (0, 0));
        var currentIndex = state.Index % models.Count;

        var rotated = new List<string>(models.Count);
        for (var i = 0; i < models.Count; i++)
            rotated.Add(models[(currentIndex + i) % models.Count]);

        var nextCount = state.Count + 1;
        RotationState[key] = nextCount >= stickyLimit
            ? ((currentIndex + 1) % models.Count, 0)
            : (currentIndex, nextCount);

        return rotated;
    }

    public static void ResetRotation(string? comboName = null)
    {
        if (comboName is null) RotationState.Clear();
        else RotationState.TryRemove(comboName, out _);
    }

    /// <summary>
    /// Stable tier sort: tier 0 = satisfies hard + soft caps, tier 1 = hard only,
    /// tier 2 = rest. Never drops a model (fallback intact).
    /// </summary>
    public List<string> ReorderByCapabilities(IReadOnlyList<string> models, HashSet<string> required)
    {
        if (required.Count == 0 || models.Count <= 1) return models.ToList();
        var hard = required.Where(HardCaps.Contains).ToList();
        var soft = required.Where(c => !HardCaps.Contains(c)).ToList();

        int TierOf(string m)
        {
            var caps = GetCapabilitiesForModel(m);
            if (!hard.All(c => caps.Contains(c))) return 2;
            return soft.All(c => caps.Contains(c)) ? 0 : 1;
        }

        return models
            .Select((m, i) => (m, i, t: TierOf(m)))
            .OrderBy(x => x.t).ThenBy(x => x.i)
            .Select(x => x.m)
            .ToList();
    }

    /// <summary>Capability lookup for a "provider/model" ref from the embedded registry.</summary>
    public HashSet<string> GetCapabilitiesForModel(string providerModel)
    {
        var caps = new HashSet<string>(StringComparer.Ordinal);
        var slash = providerModel.IndexOf('/');
        var providerId = slash > 0 ? providerModel[..slash] : "";
        var modelId = slash > 0 ? providerModel[(slash + 1)..] : providerModel;

        var provider = _registry.GetProvider(providerId);
        var model = provider?.Models?.FirstOrDefault(m =>
            string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is not null)
        {
            if (model.Vision == true || model.ImageToText == true) caps.Add("vision");
            foreach (var c in model.Capabilities ?? [])
            {
                caps.Add(c);
                if (c is "image" or "vision") caps.Add("vision");
                if (c is "pdf") caps.Add("pdf");
            }
        }
        return caps;
    }

    /// <summary>
    /// Detect capabilities required by the request's trailing user turn
    /// (vision/pdf/audioInput/videoInput). Ported from detectRequiredCapabilities.
    /// </summary>
    public static HashSet<string> DetectRequiredCapabilities(JsonElement body)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object) return required;

        void AddByMime(string? mime)
        {
            if (string.IsNullOrEmpty(mime)) return;
            if (mime.StartsWith("image/")) required.Add("vision");
            else if (mime == "application/pdf") required.Add("pdf");
            else if (mime.StartsWith("audio/")) required.Add("audioInput");
            else if (mime.StartsWith("video/")) required.Add("videoInput");
        }

        void ScanBlock(JsonElement b)
        {
            if (b.ValueKind != JsonValueKind.Object) return;
            var t = GetStr(b, "type");
            if (t is "image_url" or "image" or "input_image")
            {
                string? imime = null;
                if (b.TryGetProperty("image_url", out var iu))
                    imime = iu.ValueKind == JsonValueKind.String ? DataUriMime(iu.GetString())
                        : iu.TryGetProperty("url", out var u) ? DataUriMime(u.GetString()) : null;
                else if (b.TryGetProperty("url", out var bu)) imime = DataUriMime(bu.GetString());
                else if (b.TryGetProperty("source", out var isrc))
                {
                    if (isrc.TryGetProperty("media_type", out var mt)) imime = mt.GetString();
                    else if (isrc.TryGetProperty("data", out var dd)) imime = DataUriMime(dd.GetString());
                }
                if (imime is not null && !imime.StartsWith("image/")) AddByMime(imime);
                else required.Add("vision");
            }
            if (t is "input_audio" or "audio_url" or "audio") required.Add("audioInput");
            if (t is "input_video" or "video_url" or "video") required.Add("videoInput");
            if (t is "file" or "document" or "input_file")
            {
                string? fmime = null;
                if (b.TryGetProperty("input_audio", out var ia) && ia.TryGetProperty("format", out var f))
                    fmime = $"audio/{f.GetString()}";
                else if (b.TryGetProperty("file", out var ff) && ff.TryGetProperty("file_data", out var fd))
                    fmime = DataUriMime(fd.GetString());
                else if (b.TryGetProperty("source", out var src))
                {
                    if (src.TryGetProperty("media_type", out var mt)) fmime = mt.GetString();
                    else if (src.TryGetProperty("data", out var sd)) fmime = DataUriMime(sd.GetString());
                }
                if (fmime is not null) AddByMime(fmime); else required.Add("pdf");
            }
            if (b.TryGetProperty("inlineData", out var idata) && idata.TryGetProperty("mimeType", out var imt)) AddByMime(imt.GetString());
            if (b.TryGetProperty("fileData", out var fdata) && fdata.TryGetProperty("mimeType", out var fmt)) AddByMime(fmt.GetString());
        }

        void ScanContent(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.Array)
                foreach (var b in content.EnumerateArray()) ScanBlock(b);
        }

        void ScanMessage(JsonElement m)
        {
            if (m.ValueKind != JsonValueKind.Object) return;
            if (m.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
                required.Add("vision");
            var attachments = m.TryGetProperty("experimental_attachments", out var ea) ? ea
                : m.TryGetProperty("attachments", out var at) ? at : default;
            if (attachments.ValueKind == JsonValueKind.Array)
            {
                foreach (var att in attachments.EnumerateArray())
                {
                    if (att.ValueKind != JsonValueKind.Object) continue;
                    var mime = GetStr(att, "contentType") ?? GetStr(att, "mediaType")
                        ?? (att.TryGetProperty("url", out var u) ? DataUriMime(u.GetString()) : null);
                    if (mime is not null) AddByMime(mime);
                    else if (att.TryGetProperty("url", out _) || att.TryGetProperty("data", out _)) required.Add("vision");
                }
            }
            if (m.TryGetProperty("image_url", out _) || m.TryGetProperty("image", out _)) required.Add("vision");
            if (m.TryGetProperty("audio_url", out _) || m.TryGetProperty("audio", out _)) required.Add("audioInput");
            if (m.TryGetProperty("content", out var content))
            {
                ScanContent(content);
                if (content.ValueKind == JsonValueKind.String)
                {
                    var s = content.GetString() ?? "";
                    if (s.Contains("data:image/")) required.Add("vision");
                    else if (s.Contains("data:audio/")) required.Add("audioInput");
                    else if (s.Contains("data:application/pdf")) required.Add("pdf");
                }
            }
        }

        if (body.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            foreach (var m in TrailingUserItems(messages, "role")) ScanMessage(m);
        if (body.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
            foreach (var it in TrailingUserItems(input, "role"))
                if (it.TryGetProperty("content", out var c)) ScanContent(c);
        var contents = body.TryGetProperty("contents", out var gc) ? gc
            : body.TryGetProperty("request", out var req) && req.TryGetProperty("contents", out var rc) ? rc : default;
        if (contents.ValueKind == JsonValueKind.Array)
            foreach (var c in TrailingUserItems(contents, "role"))
                if (c.TryGetProperty("parts", out var parts)) ScanContent(parts);

        return required;
    }

    /// <summary>Items after the last assistant/model turn = the current user turn.</summary>
    private static IEnumerable<JsonElement> TrailingUserItems(JsonElement arr, string roleProp)
    {
        var items = arr.EnumerateArray().ToList();
        var lastAssistant = -1;
        for (var i = items.Count - 1; i >= 0; i--)
            if (IsAssistant(items[i], roleProp)) { lastAssistant = i; break; }
        return items.Skip(lastAssistant + 1);
    }

    private static bool IsAssistant(JsonElement el, string roleProp)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(roleProp, out var r)
           && GetStr(el, roleProp) is "assistant" or "model";

    private static string? GetStr(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? DataUriMime(string? s)
    {
        if (s is null || !s.StartsWith("data:")) return null;
        var end = s.IndexOfAny([';', ',']);
        return end > 5 ? s[5..end] : null;
    }
}

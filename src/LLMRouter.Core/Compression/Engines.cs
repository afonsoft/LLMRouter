using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LLMRouter.Core.Extras;

namespace LLMRouter.Core.Compression;

/// <summary>Base helpers for engines.</summary>
public abstract class EngineBase : ICompressionEngine
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual string Icon => "compress";
    public virtual bool Stackable => true;
    public abstract int StackPriority { get; }
    public virtual bool Stable => true;
    public abstract CompressionResult Apply(JsonObject body, EngineOptions options);
    public abstract IReadOnlyList<EngineConfigField> ConfigSchema { get; }

    protected CompressionResult Done(JsonObject original, JsonObject result, string[] techniques)
    {
        var before = TextOps.BodyTextChars(original);
        var after = TextOps.BodyTextChars(result);
        var compressed = techniques.Length > 0 && after < before;
        return new CompressionResult(result, compressed,
            compressed ? new EngineRunStats(Id, before, after, techniques) : null);
    }

    protected static string? Opt(JsonObject? cfg, string key) =>
        cfg?.TryGetPropertyValue(key, out var v) == true ? v?.GetValue<string>() : null;

    protected static bool OptBool(JsonObject? cfg, string key, bool fallback) =>
        cfg?.TryGetPropertyValue(key, out var v) == true && v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : fallback;

    protected static int OptInt(JsonObject? cfg, string key, int fallback) =>
        cfg?.TryGetPropertyValue(key, out var v) == true && v is JsonValue jv && jv.TryGetValue<int>(out var i) ? i : fallback;

    protected static double OptDouble(JsonObject? cfg, string key, double fallback) =>
        cfg?.TryGetPropertyValue(key, out var v) == true && v is JsonValue jv && jv.TryGetValue<double>(out var d) ? d : fallback;

    protected static string[] OptArr(JsonObject? cfg, string key, string[] fallback) =>
        cfg?.TryGetPropertyValue(key, out var v) == true && v is JsonArray a
            ? a.Select(x => x?.GetValue<string>() ?? "").Where(x => x != "").ToArray()
            : fallback;
}

/// <summary>lite — whitespace collapse, system-prompt dedup, tool-result truncation, redundant-content removal, image placeholder.</summary>
public sealed class LiteEngine : EngineBase
{
    public override string Id => "lite";
    public override string Name => "Lite";
    public override string Description => "Lossless whitespace cleanup, system dedup and old tool-result truncation.";
    public override int StackPriority => 25;
    public override IReadOnlyList<EngineConfigField> ConfigSchema =>
    [
        new("enabled", "boolean", "Enabled", true),
        new("maxToolLength", "number", "Max tool result length", 2000, Min: 256, Max: 1000000),
        new("preserveSystemPrompt", "boolean", "Preserve system prompt", true),
    ];

    private const int DefaultMaxToolLength = 2000;
    private const int TruncationLookback = 80;

    private static int BackOffToWordBoundary(string content, int cutIndex)
    {
        bool IsWord(int i) => i >= 0 && i < content.Length && !char.IsWhiteSpace(content[i]);
        if (!IsWord(cutIndex - 1) || !IsWord(cutIndex)) return cutIndex;
        var windowStart = Math.Max(0, cutIndex - TruncationLookback);
        for (var i = cutIndex; i > windowStart; i--)
            if (!IsWord(i - 1)) return i - 1;
        var windowEnd = Math.Min(content.Length, cutIndex + TruncationLookback);
        for (var i = cutIndex; i < windowEnd; i++)
            if (!IsWord(i)) return i;
        return cutIndex;
    }

    public override CompressionResult Apply(JsonObject body, EngineOptions options)
    {
        var messages = TextOps.Messages(body);
        if (messages is null || messages.Count == 0)
            return new CompressionResult(body, false, null);

        var cfg = options.StepConfig;
        if (!OptBool(cfg, "enabled", true)) return new CompressionResult(body, false, null);
        var preserveSystem = OptBool(cfg, "preserveSystemPrompt", true);
        var maxTool = Math.Clamp(OptInt(cfg, "maxToolLength", DefaultMaxToolLength), 256, 1_000_000);

        var techniques = new List<string>();
        var currentTurnStart = TextOps.CurrentTurnStart(messages);
        var seenSystem = new HashSet<string>();
        var outMsgs = new JsonArray();
        var idx = 0;
        foreach (var m in messages)
        {
            var msg = m as JsonObject;
            if (msg is null) { outMsgs.Add(m?.DeepClone()); idx++; continue; }
            msg = (JsonObject)msg.DeepClone(); // detach — original node is still parented in `messages`
            var role = TextOps.Role(msg);

            if (!(preserveSystem && role == "system"))
            {
                // whitespace collapse
                msg = TextOps.MapText(msg, t => t == "" ? t
                    : Regex.Replace(
                        Regex.Replace(
                            Regex.Replace(t, @"\n{3,}", "\n\n"),
                            @"[ \t]+$", "", RegexOptions.Multiline),
                        @"[^\S\n]{2,}", " "));
                if (TextOps.ExtractText(msg) != TextOps.ExtractText(m)) { if (!techniques.Contains("whitespace")) techniques.Add("whitespace"); }

                // system dedup (only when system isn't preserved)
                if (role == "system" && !preserveSystem)
                {
                    var key = TextOps.ExtractText(msg).Trim();
                    key = key[..Math.Min(200, key.Length)];
                    if (!seenSystem.Add(key)) { idx++; techniques.Add("system-dedup"); continue; }
                }

                // tool-result truncation (never the current turn)
                if (idx < currentTurnStart && role is "tool" or "function")
                {
                    var t = TextOps.ExtractText(msg);
                    if (t.Length > maxTool)
                    {
                        var cut = BackOffToWordBoundary(t, maxTool);
                        msg = TextOps.SetText(msg, t[..cut] + "\n...[truncated]");
                        techniques.Add("tool-compress");
                    }
                }

                // redundant consecutive identical non-tool messages
                if (outMsgs.Count > 0 && role != "tool")
                {
                    var prev = outMsgs[^1] as JsonObject;
                    if (prev is not null && TextOps.Role(prev) == role
                        && TextOps.ExtractText(prev) == TextOps.ExtractText(msg)
                        && TextOps.ExtractText(msg) != "")
                    { idx++; techniques.Add("redundant-remove"); continue; }
                }
            }

            // image placeholder when the model can't see images
            if (options.SupportsVision == false && msg["content"] is JsonArray parts)
            {
                var newParts = new JsonArray();
                var changed = false;
                foreach (var p in parts)
                {
                    if (p is JsonObject po && po["type"]?.GetValue<string>() == "image_url"
                        && po["image_url"] is JsonObject iu && iu["url"]?.GetValue<string>() is { } url
                        && url.StartsWith("data:image/", StringComparison.Ordinal))
                    {
                        var fmt = url.Split('/', ';') is { Length: > 1 } seg ? seg[1] : "unknown";
                        newParts.Add(new JsonObject { ["type"] = "text", ["text"] = $"[image: {fmt}]" });
                        changed = true;
                    }
                    else newParts.Add(p?.DeepClone());
                }
                if (changed)
                {
                    var clone = (JsonObject)msg.DeepClone();
                    clone["content"] = newParts;
                    msg = clone;
                    techniques.Add("image-placeholder");
                }
            }

            outMsgs.Add(msg);
            idx++;
        }

        return Done(body, TextOps.NewBody(body, outMsgs), techniques.Distinct().ToArray());
    }
}

/// <summary>session-dedup — content-addressed cross-turn suffix-block dedup → [dedup:ref sha=…] markers.</summary>
public sealed class SessionDedupEngine : EngineBase
{
    public override string Id => "session-dedup";
    public override string Name => "Session Dedup";
    public override string Description => "Content-addressed cross-turn deduplication of repeated text blocks.";
    public override int StackPriority => 3;
    public override IReadOnlyList<EngineConfigField> ConfigSchema =>
    [
        new("enabled", "boolean", "Enabled", true),
        new("minBlockChars", "number", "Minimum block characters", 80, Min: 1, Max: 100000,
            Description: "Minimum character count for a suffix block to be a dedup candidate."),
        new("fuzzy", "boolean", "Fuzzy near-duplicate dedup", false),
    ];

    private const int MinBlockLines = 3;
    private const int MaxSuffixStarts = 2000;
    private const int MaxTotalBlockBytes = 8 * 1024 * 1024;

    private static string HashBlock(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();

    private static List<(string Block, int StartLine)> FindSuffixBlocks(string[] lines, int minBlockChars)
    {
        var results = new List<(string, int)>();
        var seen = new HashSet<string>();
        var n = lines.Length;
        var totalBytes = 0;
        for (var start = 0; start < Math.Min(n, MaxSuffixStarts); start++)
        {
            var block = string.Join("\n", lines[start..]);
            if (n - start >= MinBlockLines && block.Length >= minBlockChars && seen.Add(block))
            {
                results.Add((block, start));
                totalBytes += block.Length;
                if (totalBytes >= MaxTotalBlockBytes) break;
            }
        }
        return results;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0; var offset = 0;
        while (needle.Length > 0 && (offset = text.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        { count++; offset += needle.Length; }
        return count;
    }

    private static string ReplaceAfterFirst(string text, string needle, string replacement)
    {
        var first = text.IndexOf(needle, StringComparison.Ordinal);
        if (first < 0) return text;
        var sb = new StringBuilder(text[..(first + needle.Length)]);
        var offset = first + needle.Length;
        while (true)
        {
            var i = text.IndexOf(needle, offset, StringComparison.Ordinal);
            if (i < 0) { sb.Append(text[offset..]); return sb.ToString(); }
            sb.Append(text[offset..i]).Append(replacement);
            offset = i + needle.Length;
        }
    }

    private static (string Deduped, bool Changed) DedupeWithinMessage(string text, int minBlockChars)
    {
        var blocks = FindSuffixBlocks(text.Split('\n'), minBlockChars);
        if (blocks.Count < 2) return (text, false);
        var freq = blocks.GroupBy(b => b.Block).ToDictionary(g => g.Key, g => g.Count());
        var result = text; var changed = false;
        foreach (var (block, _) in blocks.OrderByDescending(b => freq[b.Block]).ThenByDescending(b => b.Block.Length))
        {
            if (CountOccurrences(result, block) < 2) continue;
            result = ReplaceAfterFirst(result, block, $"[dedup:ref sha={HashBlock(block)}]");
            changed = true;
        }
        return (result, changed);
    }

    public override CompressionResult Apply(JsonObject body, EngineOptions options)
    {
        var messages = TextOps.Messages(body);
        if (messages is null || messages.Count == 0)
            return new CompressionResult(body, false, null);
        var cfg = options.StepConfig;
        if (!OptBool(cfg, "enabled", true)) return new CompressionResult(body, false, null);
        var minChars = Math.Max(1, OptInt(cfg, "minBlockChars", 80));
        var fuzzy = OptBool(cfg, "fuzzy", false);

        // collect (key, msgIndex, partIndex?, text) for non-system text parts
        var texts = new List<(long Key, int MsgIndex, string Text)>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject msg || TextOps.Role(msg) == "system") continue;
            if (msg["content"] is JsonValue v && v.TryGetValue<string>(out var s))
                texts.Add((i, i, s));
            else if (msg["content"] is JsonArray arr)
                for (var p = 0; p < arr.Count; p++)
                    if (arr[p] is JsonObject po && po["type"]?.GetValue<string>() == "text"
                        && po["text"] is JsonValue tv && tv.TryGetValue<string>(out var t))
                        texts.Add((i * 100000L + p + 1, i, t));
        }
        if (texts.Count == 0) return new CompressionResult(body, false, null);

        var currentTurnStart = TextOps.CurrentTurnStart(messages);
        var deduped = new Dictionary<long, string>();
        var dedupCount = 0;

        if (texts.Count == 1)
        {
            var (dedupedText, changed) = DedupeWithinMessage(texts[0].Text, minChars);
            if (changed) { deduped[texts[0].Key] = dedupedText; dedupCount++; }
        }
        else
        {
            // pass 1: first ownership of each block hash
            var firstSeen = new Dictionary<string, (long OwnerKey, string Block)>();
            foreach (var (key, _, text) in texts)
                foreach (var (block, _) in FindSuffixBlocks(text.Split('\n'), minChars))
                {
                    var sha = HashBlock(block);
                    firstSeen.TryAdd(sha, (key, block));
                }
            // pass 2: replace blocks first owned by an earlier text (never the current turn)
            foreach (var (key, msgIndex, text) in texts)
            {
                if (msgIndex >= currentTurnStart) continue;
                var dupBlocks = new List<(string Block, string Sha)>();
                foreach (var (block, _) in FindSuffixBlocks(text.Split('\n'), minChars))
                {
                    var sha = HashBlock(block);
                    if (firstSeen.TryGetValue(sha, out var owner) && owner.OwnerKey < key && owner.Block == block)
                        dupBlocks.Add((block, sha));
                }
                if (dupBlocks.Count == 0) continue;
                var result = text;
                var replaced = new List<string>();
                foreach (var (block, sha) in dupBlocks.OrderByDescending(d => d.Block.Length))
                {
                    if (replaced.Any(r => r.Contains(block))) continue;
                    var i = result.IndexOf(block, StringComparison.Ordinal);
                    if (i < 0) continue;
                    result = result[..i] + $"[dedup:ref sha={sha}]" + result[(i + block.Length)..];
                    replaced.Add(block);
                    break; // one replacement per message pass (upstream parity)
                }
                if (result != text) { deduped[key] = result; dedupCount++; }
            }
        }

        // fuzzy pass: whole-message near-dup → CCR marker (opt-in)
        if (fuzzy)
        {
            var principal = options.PrincipalId ?? "__anon__";
            for (var i = 1; i < texts.Count; i++)
            {
                var (key, msgIndex, text) = texts[i];
                if (msgIndex >= currentTurnStart || text.Length < 200) continue;
                var earlier = texts.Take(i).Where(t => t.MsgIndex < currentTurnStart)
                    .FirstOrDefault(t => Similarity(t.Text, text) >= 0.85);
                if (earlier.Text is null) continue;
                var hash = CcrStore.Put(text, principal);
                if (hash is null) continue;
                deduped[key] = $"[CCR retrieve hash={hash} chars={text.Length}]";
                dedupCount++;
            }
        }

        if (dedupCount == 0) return new CompressionResult(body, false, null);

        var outMsgs = new JsonArray();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject msg || TextOps.Role(msg) == "system")
            { outMsgs.Add(messages[i]?.DeepClone()); continue; }
            if (msg["content"] is JsonValue)
            {
                outMsgs.Add(deduped.TryGetValue(i, out var rep) ? TextOps.SetText(msg, rep) : msg.DeepClone());
            }
            else if (msg["content"] is JsonArray arr)
            {
                var clone = (JsonObject)msg.DeepClone();
                var newArr = (JsonArray)clone["content"]!;
                var changed = false;
                for (var p = 0; p < arr.Count; p++)
                {
                    var key = i * 100000L + p + 1;
                    if (newArr[p] is JsonObject po && po["type"]?.GetValue<string>() == "text"
                        && deduped.TryGetValue(key, out var rep)) { po["text"] = rep; changed = true; }
                }
                outMsgs.Add(changed ? clone : msg.DeepClone());
            }
            else outMsgs.Add(msg.DeepClone());
        }

        return Done(body, TextOps.NewBody(body, outMsgs), ["session-dedup"]);
    }

    /// <summary>Trigram Jaccard similarity — enough for the 85% fuzzy gate.</summary>
    private static double Similarity(string a, string b)
    {
        static HashSet<int> Trigrams(string s)
        {
            var set = new HashSet<int>();
            for (var i = 0; i + 3 <= s.Length; i += 3)
                set.Add(s.AsSpan(i, 3).GetHashCode());
            if (set.Count == 0 && s.Length > 0) set.Add(s.GetHashCode());
            return set;
        }
        var ta = Trigrams(a); var tb = Trigrams(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var inter = ta.Count(tb.Contains);
        return (double)inter / (ta.Count + tb.Count - inter);
    }
}

/// <summary>ccr — large contiguous blocks → [CCR retrieve hash=… chars=N] markers backed by CcrStore.</summary>
public sealed class CcrEngine : EngineBase
{
    public override string Id => "ccr";
    public override string Name => "CCR";
    public override string Description => "Replaces large text blocks with content-addressed retrieve markers.";
    public override int StackPriority => 4;
    public override IReadOnlyList<EngineConfigField> ConfigSchema =>
    [
        new("enabled", "boolean", "Enabled", true),
        new("minChars", "number", "Minimum block chars", 600, Min: 50, Max: 2000000),
        new("retrievalRampFactor", "number", "Retrieval ramp factor", 2, Min: 0, Max: 20),
    ];

    private static readonly Regex BlockSplit = new(@"\n{2,}", RegexOptions.Compiled);

    public override CompressionResult Apply(JsonObject body, EngineOptions options)
    {
        var messages = TextOps.Messages(body);
        if (messages is null || messages.Count == 0)
            return new CompressionResult(body, false, null);
        var cfg = options.StepConfig;
        if (!OptBool(cfg, "enabled", true)) return new CompressionResult(body, false, null);
        var minChars = Math.Max(50, OptInt(cfg, "minChars", 600));
        var ramp = OptInt(cfg, "retrievalRampFactor", 2);
        var principal = options.PrincipalId ?? "__anon__";
        var currentTurnStart = TextOps.CurrentTurnStart(messages);

        var compressedAny = false;
        var outMsgs = new JsonArray();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject msg || TextOps.Role(msg) == "system" || i >= currentTurnStart)
            { outMsgs.Add(messages[i]?.DeepClone()); continue; }

            var changed = false;
            var newMsg = TextOps.MapText(msg, text =>
            {
                var blocks = BlockSplit.Split(text);
                if (blocks.All(b => b.Length < minChars)) return text;
                var sb = new StringBuilder();
                for (var b = 0; b < blocks.Length; b++)
                {
                    var block = blocks[b];
                    var hash = block.Length >= minChars ? CcrStore.HashOf(block) : null;
                    var effective = hash is not null
                        ? CcrStore.EffectiveMinChars(hash, principal, minChars, ramp)
                        : minChars;
                    if (hash is not null && block.Length >= effective
                        && !CcrStore.ShouldSkipCompression(hash, principal))
                    {
                        var marker = $"[CCR retrieve hash={hash} chars={block.Length}]";
                        if (marker.Length < block.Length)
                        {
                            CcrStore.Put(block, principal);
                            sb.Append(marker);
                            compressedAny = true;
                        }
                        else sb.Append(block);
                    }
                    else sb.Append(block);
                    if (b < blocks.Length - 1) sb.Append("\n\n");
                }
                var outText = sb.ToString();
                if (outText != text) changed = true;
                return outText;
            });
            _ = changed;
            outMsgs.Add(newMsg);
        }

        return compressedAny ? Done(body, TextOps.NewBody(body, outMsgs), ["ccr-markers"])
            : new CompressionResult(body, false, null);
    }
}

/// <summary>headroom — compact uniform JSON-array tables into omni-tabular fenced blocks.</summary>
public sealed class HeadroomEngine : EngineBase
{
    public override string Id => "headroom";
    public override string Name => "Headroom";
    public override string Description => "Tabular compaction of repeated row structures (JSON arrays / markdown tables).";
    public override int StackPriority => 15;
    public override IReadOnlyList<EngineConfigField> ConfigSchema =>
    [
        new("enabled", "boolean", "Enabled", true),
        new("minRows", "number", "Minimum rows", 8, Min: 2, Max: 10000,
            Description: "Only arrays/tables with at least this many uniform rows are compacted."),
    ];

    private static readonly Regex JsonArrayRe = new(@"\[\s*\{[\s\S]{40,}?\}\s*(?:,\s*\{[\s\S]*?\}\s*)+\]", RegexOptions.Compiled);
    private static readonly Regex MdTableRe = new(@"(?:^\|.+\|[ \t]*\r?\n){3,}", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Compact columnar encoding: typed cells, one row per line.</summary>
    internal static string EncodeRows(List<Dictionary<string, JsonNode?>> rows, List<string> cols)
    {
        var sb = new StringBuilder("```omni-tabular\ncols:");
        sb.AppendLine(string.Join(',', cols.Select(c => c.Replace(",", "\\,"))));
        foreach (var r in rows)
        {
            var cells = cols.Select(c =>
            {
                if (!r.TryGetValue(c, out var v) || v is null) return "null";
                return v switch
                {
                    JsonValue jv when jv.TryGetValue<bool>(out var b) => b ? "t" : "f",
                    JsonValue jv when jv.TryGetValue<double>(out var d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    JsonValue => v.ToJsonString().Replace("\n", " "),
                    _ => "null",
                };
            });
            sb.AppendLine(string.Join('|', cells));
        }
        sb.Append("```");
        return sb.ToString();
    }

    public override CompressionResult Apply(JsonObject body, EngineOptions options)
    {
        var messages = TextOps.Messages(body);
        if (messages is null || messages.Count == 0)
            return new CompressionResult(body, false, null);
        var cfg = options.StepConfig;
        if (!OptBool(cfg, "enabled", true)) return new CompressionResult(body, false, null);
        var minRows = Math.Max(2, OptInt(cfg, "minRows", 8));

        var outMsgs = new JsonArray();
        var applied = false;
        foreach (var m in messages)
        {
            if (m is not JsonObject msg || TextOps.Role(msg) == "system") { outMsgs.Add(m?.DeepClone()); continue; }
            var newMsg = TextOps.MapText(msg, text =>
            {
                // JSON arrays of uniform objects
                var result = JsonArrayRe.Replace(text, match =>
                {
                    try
                    {
                        var arr = JsonNode.Parse(match.Value) as JsonArray;
                        if (arr is null || arr.Count < minRows) return match.Value;
                        var rows = arr.OfType<JsonObject>().Select(o =>
                            o.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone())).ToList();
                        if (rows.Count != arr.Count || rows.Count < minRows) return match.Value;
                        var cols = rows.SelectMany(r => r.Keys).Distinct()
                            .Where(c => rows.Count(r => r.ContainsKey(c)) >= rows.Count * 0.8).ToList();
                        if (cols.Count < 2 || cols.Count > 64) return match.Value;
                        var enc = EncodeRows(rows, cols);
                        return enc.Length < match.Value.Length ? Tap(() => applied = true, enc) : match.Value;
                    }
                    catch { return match.Value; }
                });
                return result;
            });
            outMsgs.Add(newMsg);
        }

        return applied ? Done(body, TextOps.NewBody(body, outMsgs), ["headroom-tabular"])
            : new CompressionResult(body, false, null);

        static T Tap<T>(Action a, T v) { a(); return v; }
    }
}

/// <summary>caveman — regex rule packs at lite/full/ultra intensity with preserved-block tombstoning.</summary>
public sealed class CavemanEngine : EngineBase
{
    public override string Id => "caveman";
    public override string Name => "Caveman";
    public override string Description => "Filler/hedging/politeness removal via intensity-gated rule packs.";
    public override int StackPriority => 20;
    public override IReadOnlyList<EngineConfigField> ConfigSchema =>
    [
        new("intensity", "select", "Intensity", "full", Options: ["lite", "full", "ultra"]),
        new("minMessageLength", "number", "Minimum message length", 50, Min: 0, Max: 10000),
        new("enabled", "boolean", "Enabled", true),
    ];

    private static readonly Regex CleanupSpaces = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunct = new(@"[ \t]+([,.;:!?])", RegexOptions.Compiled);
    private static readonly Regex MultiPunct = new(@"([.!?]){2,}", RegexOptions.Compiled);
    private static readonly Regex TrailWs = new(@"[ \t]+$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex MultiNl = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex SentenceStart = new(@"(^|[.!?][ \t]|\n[ \t]*)([a-z])", RegexOptions.Compiled);

    private static string CleanupArtifacts(string text) => MultiNl.Replace(
        TrailWs.Replace(MultiPunct.Replace(
            SpaceBeforePunct.Replace(CleanupSpaces.Replace(text, " "), "$1"),
            m => m.Value[^1].ToString()), ""), "\n\n")
        .Trim('\n');

    private static string Recapitalize(string text) => SentenceStart.Replace(text,
        m => m.Groups[1].Value + char.ToUpperInvariant(m.Groups[2].Value[0]));

    /// <summary>Strong majority of non-empty lines look like code → skip prose normalization.</summary>
    private static bool IsCodeDominant(string text)
    {
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length < 3) return false;
        var codeLike = lines.Count(l =>
            Regex.IsMatch(l, @"^\s*(?:[#/]{1,2}|[{}()\[\];]|=>|->|::|\w+\s*[=({]|if|for|while|return|function|def |class |import |using |var |let |const |public |private |#include|#define)\b")
            || l.TrimStart().StartsWith("//") || l.TrimStart().StartsWith('#'));
        return (double)codeLike / lines.Length >= 0.3;
    }

    public override CompressionResult Apply(JsonObject body, EngineOptions options)
    {
        var messages = TextOps.Messages(body);
        if (messages is null || messages.Count == 0)
            return new CompressionResult(body, false, null);
        var cfg = options.StepConfig;
        if (!OptBool(cfg, "enabled", true)) return new CompressionResult(body, false, null);
        var intensity = Opt(cfg, "intensity") ?? "full";
        var minLen = Math.Max(0, OptInt(cfg, "minMessageLength", 50));
        var compressRoles = OptArr(cfg, "compressRoles", ["user", "assistant", "system"]);
        var skipRules = OptArr(cfg, "skipRules", []);
        var preservePatterns = OptArr(cfg, "preservePatterns", [])
            .Select(p => { try { return new Regex(p, RegexOptions.Compiled); } catch { return null; } })
            .Where(r => r is not null).Cast<Regex>().ToList();

        var appliedRules = new List<string>();
        var outMsgs = new JsonArray();
        foreach (var m in messages)
        {
            if (m is not JsonObject msg) { outMsgs.Add(m?.DeepClone()); continue; }
            var role = TextOps.Role(msg);
            if (!compressRoles.Contains(role, StringComparer.OrdinalIgnoreCase)) { outMsgs.Add(msg.DeepClone()); continue; }
            var text = TextOps.ExtractText(msg);
            if (text.Length < minLen) { outMsgs.Add(msg.DeepClone()); continue; }

            var newMsg = TextOps.MapText(msg, part =>
            {
                if (part.Length < minLen) return part;
                var (prose, blocks) = Preservation.Extract(part, preservePatterns);
                var rules = CavemanRules.ForContext(role, intensity, skipRules).ToList();
                var result = prose;
                var lower = result.ToLowerInvariant();
                foreach (var rule in rules)
                {
                    if (!rule.Pattern.IsMatch(lower) && !rule.Pattern.IsMatch(result)) continue;
                    var before = result;
                    result = rule.Pattern.Replace(result, m2 => rule.Apply(m2));
                    if (result != before) appliedRules.Add(rule.Name);
                    lower = result.ToLowerInvariant();
                }
                var normalized = IsCodeDominant(result) ? result : Recapitalize(CleanupArtifacts(result));
                var cleaned = blocks.Count > 0 ? CleanupArtifacts(Preservation.Restore(normalized, blocks)) : normalized;
                return cleaned;
            });
            outMsgs.Add(newMsg);
        }

        return Done(body, TextOps.NewBody(body, outMsgs), appliedRules.Distinct().ToArray());
    }
}

/// <summary>aggressive — tool-result compression + progressive aging + summarizer-lite, with caveman/lite fallback.</summary>
public sealed class AggressiveEngine : EngineBase
{
    public override string Id => "aggressive";
    public override string Name => "Aggressive";
    public override string Description => "Tool-result compression, progressive aging and per-message summarization.";
    public override int StackPriority => 30;
    public override IReadOnlyList<EngineConfigField> ConfigSchema =>
    [
        new("summarizerEnabled", "boolean", "Summarizer enabled", true),
        new("maxTokensPerMessage", "number", "Max tokens per message", 2048, Min: 256, Max: 32768),
        new("minSavingsThreshold", "number", "Minimum savings threshold", 0.05, Min: 0, Max: 1),
    ];

    private static readonly Regex CompressedMarker = new(@"^\[COMPRESSED:", RegexOptions.Compiled);

    public override CompressionResult Apply(JsonObject body, EngineOptions options)
    {
        var messages = TextOps.Messages(body);
        if (messages is null || messages.Count == 0)
            return new CompressionResult(body, false, null);
        var cfg = options.StepConfig;
        var summarizerEnabled = OptBool(cfg, "summarizerEnabled", true);
        var maxTok = Math.Max(256, OptInt(cfg, "maxTokensPerMessage", 2048));
        var minSavings = OptDouble(cfg, "minSavingsThreshold", 0.05);
        var thresholds = (fullSummary: 5, moderate: 3, light: 2, verbatim: 2);

        var lastUserIdx = TextOps.LastIndexOfRole(messages, "user");
        var total = messages.Count;
        var outMsgs = new JsonArray();
        var saved = 0;
        var techniques = new List<string>();

        for (var i = 0; i < total; i++)
        {
            var msg = messages[i] as JsonObject;
            if (msg is null) { outMsgs.Add(messages[i]?.DeepClone()); continue; }
            var role = TextOps.Role(msg);
            var text = TextOps.ExtractText(msg);
            if (role == "system" || i == lastUserIdx || CompressedMarker.IsMatch(text))
            { outMsgs.Add(msg.DeepClone()); continue; }

            // Step 1: tool/function result compression (truncate at word boundary)
            if (role is "tool" or "function")
            {
                var maxTool = Math.Max(256, Math.Min(maxTok * 4, 4096));
                if (text.Length > maxTool)
                {
                    var cut = text.LastIndexOfAny([' ', '\n', '\t'], Math.Min(maxTool, text.Length - 1));
                    if (cut < maxTool / 2) cut = maxTool;
                    var tagged = $"[COMPRESSED:tool] {text[..cut]}\n...[truncated]";
                    saved += text.Length - tagged.Length;
                    techniques.Add("toolResult");
                    outMsgs.Add(TextOps.SetText(msg, tagged));
                    continue;
                }
            }

            // Step 2: progressive aging by distance from end
            var distance = total - 1 - i;
            if (distance <= thresholds.verbatim) { outMsgs.Add(msg.DeepClone()); continue; }

            string? aged = null;
            if (distance <= thresholds.light)
            {
                // light = lite whitespace on this message
                var liteBody = new JsonObject { ["messages"] = new JsonArray(msg.DeepClone()) };
                var r = new LiteEngine().Apply(liteBody, new EngineOptions());
                var t = TextOps.ExtractText((TextOps.Messages(r.Body) ?? [null])[0]);
                if (t.Length < text.Length) aged = $"[COMPRESSED:aging:light] {t}";
            }
            else if (distance <= thresholds.moderate)
            {
                // moderate = caveman full on this message
                var cBody = new JsonObject { ["messages"] = new JsonArray(msg.DeepClone()) };
                var r = new CavemanEngine().Apply(cBody, new EngineOptions(
                    StepConfig: new JsonObject { ["minMessageLength"] = 1 }));
                var t = TextOps.ExtractText((TextOps.Messages(r.Body) ?? [null])[0]);
                if (t.Length < text.Length) aged = $"[COMPRESSED:aging:moderate] {t}";
            }
            else
            {
                // fullSummary: assistant → first-200-chars summary; user → first line
                var summary = role == "assistant"
                    ? text[..Math.Min(200, text.Length)]
                    : (text.Split('\n')[0][..Math.Min(120, text.Split('\n')[0].Length)]);
                if (summary.Length < text.Length) aged = $"[COMPRESSED:aging:fullSummary] {summary}";
            }

            if (aged is not null)
            {
                saved += TextOps.EstimateTokens(text) - TextOps.EstimateTokens(aged);
                if (!techniques.Contains("aging")) techniques.Add("aging");
                outMsgs.Add(TextOps.SetText(msg, aged));
                continue;
            }
            outMsgs.Add(msg.DeepClone());
        }

        // Step 3: fallback summarizer for remaining long messages
        if (summarizerEnabled)
        {
            for (var i = 0; i < outMsgs.Count; i++)
            {
                if (outMsgs[i] is not JsonObject msg || i == lastUserIdx) continue;
                var text = TextOps.ExtractText(msg);
                if (text.Length <= maxTok * 4 || CompressedMarker.IsMatch(text) || TextOps.Role(msg) == "system") continue;
                var summary = text[..Math.Min(maxTok * 4, text.Length)];
                if (summary.Length < text.Length)
                {
                    saved += TextOps.EstimateTokens(text) - TextOps.EstimateTokens(summary);
                    techniques.Add("summarizer");
                    outMsgs[i] = TextOps.SetText(msg, $"[COMPRESSED:summary] {summary}");
                }
            }
        }

        var result = Done(body, TextOps.NewBody(body, outMsgs), techniques.Distinct().ToArray());
        // downgrade chain: below threshold → try caveman on the whole body
        if (result.Stats is null || result.Stats.SavingsPercent < minSavings * 100)
        {
            var cave = new CavemanEngine().Apply(body, new EngineOptions(
                StepConfig: new JsonObject { ["intensity"] = "full" }));
            if (cave.Stats is not null && (result.Stats is null || cave.Stats.SavingsPercent > result.Stats.SavingsPercent))
                return new CompressionResult(cave.Body, true,
                    new EngineRunStats(Id, cave.Stats.BeforeChars, cave.Stats.AfterChars, ["caveman-fallback"]));
        }
        return result;
    }
}

/// <summary>ultra — Tier-A heuristic token pruning by information score (prose only).</summary>
public sealed class UltraEngine : EngineBase
{
    public override string Id => "ultra";
    public override string Name => "Ultra";
    public override string Description => "Heuristic token pruning by information-density score.";
    public override int StackPriority => 40;
    public override IReadOnlyList<EngineConfigField> ConfigSchema =>
    [
        new("enabled", "boolean", "Enabled", true),
        new("compressionRate", "number", "Compression rate", 0.5, Min: 0, Max: 1),
        new("minScoreThreshold", "number", "Minimum score threshold", 0.3, Min: 0, Max: 1),
        new("maxTokensPerMessage", "number", "Max tokens per message", 0, Min: 0, Max: 32768),
    ];

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","is","are","was","were","be","been","being","have","has","had",
        "will","would","could","may","might","shall","dare","ought","used",
        "i","we","you","he","she","it","they","me","us","him","her","them",
        "my","our","your","his","its","their","this","that","these","those",
        "and","but","or","for","yet","so","as","at","by","in","of","on","to","up","via","with",
        "from","into","onto","upon","about","just","very","really","quite","rather",
        "also","too","even","still","already","often","usually","sometimes","here","there",
    };

    private static readonly HashSet<string> PolarityWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "never","always","no","not","nor","must","shall","do","does","did",
        "don't","doesn't","didn't","can","cannot","can't","should","shouldn't",
        "need","needs","mustn't","won't","wouldn't","could","couldn't",
    };

    private static readonly Regex ForcePreserve = new(@"\d|https?://|[._/\\]|Error:|Exception:|```", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"^\s+$", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"[ \t]{2,}", RegexOptions.Compiled);

    private static double ScoreToken(string token)
    {
        if (ForcePreserve.IsMatch(token)) return 1.0;
        if (PolarityWords.Contains(token)) return 1.0;
        if (Stopwords.Contains(token)) return 0.1;
        if (token.Length <= 2) return 0.2;
        if (char.IsUpper(token[0])) return 0.8;
        if (token.Length >= 6) return 0.7;
        return 0.5;
    }

    internal static string PruneByScore(string text, double keepRate = 0.5, double minScore = 0.3)
    {
        if (string.IsNullOrEmpty(text) || keepRate >= 1) return text;
        var tokens = Regex.Split(text, @"(\s+)");
        var wordIdx = tokens.Select((t, i) => (t, i)).Where(x => !Whitespace.IsMatch(x.t)).Select(x => x.i).ToList();
        var targetKeep = (int)Math.Ceiling(wordIdx.Count * keepRate);
        var scored = wordIdx.Select(i => (i, score: ScoreToken(tokens[i]))).ToList();
        var toPrune = new HashSet<int>();
        var pruned = 0;
        foreach (var (i, score) in scored.OrderBy(x => x.score))
        {
            if (pruned >= wordIdx.Count - targetKeep) break;
            if (score < minScore) { toPrune.Add(i); pruned++; }
        }
        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Length; i++)
            if (Whitespace.IsMatch(tokens[i]) || !toPrune.Contains(i)) sb.Append(tokens[i]);
        return MultiSpace.Replace(sb.ToString(), " ").Trim();
    }

    public override CompressionResult Apply(JsonObject body, EngineOptions options)
    {
        var messages = TextOps.Messages(body);
        if (messages is null || messages.Count == 0)
            return new CompressionResult(body, false, null);
        var cfg = options.StepConfig;
        if (!OptBool(cfg, "enabled", true)) return new CompressionResult(body, false, null);
        var rate = Math.Clamp(OptDouble(cfg, "compressionRate", 0.5), 0, 1);
        var minScore = Math.Clamp(OptDouble(cfg, "minScoreThreshold", 0.3), 0, 1);
        var maxTok = OptInt(cfg, "maxTokensPerMessage", 0);

        var outMsgs = new JsonArray();
        var applied = false;
        foreach (var m in messages)
        {
            if (m is not JsonObject msg || TextOps.Role(msg) == "system") { outMsgs.Add(m?.DeepClone()); continue; }
            var text = TextOps.ExtractText(msg);
            if (text.Length == 0 || text.StartsWith("[COMPRESSED:", StringComparison.Ordinal)) { outMsgs.Add(msg.DeepClone()); continue; }
            if (maxTok > 0 && TextOps.EstimateTokens(text) <= maxTok) { outMsgs.Add(msg.DeepClone()); continue; }
            var newMsg = TextOps.MapText(msg, part =>
            {
                if (part.StartsWith("[COMPRESSED:", StringComparison.Ordinal)) return part;
                var pruned = Preservation.TransformProseOnly(part, p => PruneByScore(p, rate, minScore));
                if (pruned != part) applied = true;
                return pruned;
            });
            outMsgs.Add(newMsg);
        }

        return applied ? Done(body, TextOps.NewBody(body, outMsgs), ["ultra-heuristic-pruning"])
            : new CompressionResult(body, false, null);
    }
}

/// <summary>rtk — named text filters (wraps SPEC-033 RtkFilters), intensity → filter set.</summary>
public sealed class RtkEngine : EngineBase
{
    public override string Id => "rtk";
    public override string Name => "RTK";
    public override string Description => "Deterministic line/text filters (whitespace, dedup, ANSI, comments, secrets).";
    public override int StackPriority => 45;
    public override IReadOnlyList<EngineConfigField> ConfigSchema =>
    [
        new("intensity", "select", "Intensity", "standard", Options: ["minimal", "standard", "aggressive"]),
        new("maxLineLength", "number", "Max line length", 500, Min: 50, Max: 100000),
    ];

    private static readonly string[] Minimal = ["collapseWhitespace", "dropEmptyLines"];
    private static readonly string[] Standard = ["collapseWhitespace", "dedupLines", "dropEmptyLines", "stripAnsi", "truncateLines"];
    private static readonly string[] Aggressive = ["collapseWhitespace", "dedupLines", "dropEmptyLines", "stripAnsi", "truncateLines", "stripComments", "minifyJson", "redactSecrets"];

    public override CompressionResult Apply(JsonObject body, EngineOptions options)
    {
        var messages = TextOps.Messages(body);
        if (messages is null || messages.Count == 0)
            return new CompressionResult(body, false, null);
        var cfg = options.StepConfig;
        if (!OptBool(cfg, "enabled", true)) return new CompressionResult(body, false, null);
        var filters = Opt(cfg, "intensity") switch
        {
            "minimal" => Minimal,
            "aggressive" => Aggressive,
            _ => Standard,
        };
        var rcfg = new RtkFilters.Config(true, filters, Math.Clamp(OptInt(cfg, "maxLineLength", 500), 50, 100000),
            OptArr(cfg, "compressRoles", ["system", "user", "assistant"]),
            OptArr(cfg, "skipRules", []), OptArr(cfg, "preservePatterns", []));

        var outMsgs = new JsonArray();
        var applied = false;
        foreach (var m in messages)
        {
            if (m is not JsonObject msg || !rcfg.CompressRoles.Contains(TextOps.Role(msg), StringComparer.OrdinalIgnoreCase))
            { outMsgs.Add(m?.DeepClone()); continue; }
            var newMsg = TextOps.MapText(msg, t =>
            {
                var c = RtkFilters.Apply(t, rcfg);
                if (c != t) applied = true;
                return c;
            });
            outMsgs.Add(newMsg);
        }

        return applied ? Done(body, TextOps.NewBody(body, outMsgs), ["rtk-filters"])
            : new CompressionResult(body, false, null);
    }
}

/// <summary>llmlingua — registered for catalog parity; the SLM backend isn't bundled, Apply is fail-open pass-through.</summary>
public sealed class LlmlinguaEngine : EngineBase
{
    public override string Id => "llmlingua";
    public override string Name => "LLMLingua";
    public override string Description => "Semantic pruning via a local SLM backend (not bundled — engine is fail-open).";
    public override int StackPriority => 35;
    public override bool Stable => false;
    public override IReadOnlyList<EngineConfigField> ConfigSchema =>
    [
        new("compressionRate", "number", "Compression rate", 0.5, Min: 0, Max: 1),
        new("modelPath", "string", "Model path", ""),
    ];
    public override CompressionResult Apply(JsonObject body, EngineOptions options) =>
        new(body, false, new EngineRunStats(Id, TextOps.BodyTextChars(body), TextOps.BodyTextChars(body), [], "backend_unavailable"));
}

/// <summary>omniglyph — registered for catalog parity; needs the renderer + byte-preserving provider transport.</summary>
public sealed class OmniglyphEngine : EngineBase
{
    public override string Id => "omniglyph";
    public override string Name => "OmniGlyph";
    public override string Description => "Context-as-image compression (requires direct byte-preserving provider transport — fail-open).";
    public override int StackPriority => 50;
    public override bool Stackable => false;
    public override bool Stable => false;
    public override IReadOnlyList<EngineConfigField> ConfigSchema =>
    [
        new("enabled", "boolean", "Enabled", false),
    ];
    public override CompressionResult Apply(JsonObject body, EngineOptions options) =>
        new(body, false, new EngineRunStats(Id, TextOps.BodyTextChars(body), TextOps.BodyTextChars(body), [], "renderer_unavailable"));
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMRouter.Core.Translation;

/// <summary>
/// Extended translator helpers (SPEC-003): Claude request hygiene
/// (cache_control, tool ordering, trailing user turn, thinking, tool_choice),
/// OpenAI-side normalization, Responses API ↔ chat.completions, and
/// maxTokens ceiling rules — ported from upstream open-sse/translator/*.
/// </summary>
public static class ExtendedTranslators
{
    /// <summary>
    /// Prepare a request destined for a claude-format upstream: drop empty
    /// messages, fix tool_use ordering (tool_use must be followed by tool_result),
    /// ensure trailing user turn, normalize tool_choice, reconcile max_tokens
    /// with thinking budgets.
    /// </summary>
    public static void ClaudePrepare(JsonObject o)
    {
        if (o["messages"] is JsonArray msgs)
        {
            // drop empty messages (no content or empty string/array)
            for (var i = msgs.Count - 1; i >= 0; i--)
            {
                var c = msgs[i]?["content"];
                var empty = c is null
                    || (c is JsonValue v && (!v.TryGetValue<string>(out var s) || s.Length == 0))
                    || (c is JsonArray { Count: 0 });
                if (empty) msgs.RemoveAt(i);
            }
            FixToolUseOrdering(msgs);
            EnsureTrailingUserTurn(msgs);
        }
        // system: string → array so cache_control anchoring can apply
        if (o["system"] is JsonValue sv && sv.TryGetValue<string>(out var sText))
            o["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = sText });
        // normalize string elements inside a system array to text blocks
        if (o["system"] is JsonArray sysArr)
            for (var i = 0; i < sysArr.Count; i++)
                if (sysArr[i] is JsonValue el && el.TryGetValue<string>(out var t))
                    sysArr[i] = new JsonObject { ["type"] = "text", ["text"] = t };
        // normalize tool_choice: {type:"function",...} → {type:"tool",name:...}
        if (o["tool_choice"] is JsonObject tc && tc["type"]?.GetValue<string>() == "function")
            o["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = tc["function"]?["name"]?.DeepClone() };
        // thinking budget < max_tokens
        if (o["thinking"] is JsonObject th
            && th["type"]?.GetValue<string>() == "enabled"
            && th["budget_tokens"] is { } bt
            && o["max_tokens"] is { } mt
            && bt.GetValue<int>() >= mt.GetValue<int>())
            o["max_tokens"] = Math.Max(bt.GetValue<int>() + 1, 1024);
        ApplyCacheBreakpoints(o);
    }

    /// <summary>
    /// SPEC-018: upstream prepareClaudeRequest — when the client sent no
    /// cache_control markers, anchor up to 4 ephemeral breakpoints (last
    /// system block, last tool, last block of each of the last two messages).
    /// Existing client markers are preserved untouched (passthrough).
    /// </summary>
    private static void ApplyCacheBreakpoints(JsonObject o)
    {
        if (HasCacheControl(o)) return;
        var anchors = new List<JsonNode>();
        if (o["system"] is JsonArray sys && sys.Count > 0 && sys[^1] is { } lastSys)
            anchors.Add(lastSys);
        if (o["tools"] is JsonArray tools && tools.Count > 0 && tools[^1] is { } lastTool)
            anchors.Add(lastTool);
        if (o["messages"] is JsonArray msgs)
            for (var i = msgs.Count - 1; i >= 0 && anchors.Count < 4; i--)
                if (msgs[i]?["content"] is JsonArray { Count: > 0 } c && c[^1] is { } lastBlock)
                    anchors.Add(lastBlock);
        foreach (var a in anchors.Take(4))
            if (a is JsonObject ao)
                ao["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
    }

    private static bool HasCacheControl(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                if (o.ContainsKey("cache_control")) return true;
                return o.Any(kv => HasCacheControl(kv.Value));
            case JsonArray a:
                return a.Any(HasCacheControl);
            default:
                return false;
        }
    }

    /// <summary>tool_use block must be immediately followed by a tool_result in the next message.</summary>
    private static void FixToolUseOrdering(JsonArray msgs)
    {
        for (var i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (m?["role"]?.GetValue<string>() != "assistant") continue;
            if (m["content"] is not JsonArray blocks) continue;
            var hasToolUse = blocks.Any(b => b?["type"]?.GetValue<string>() == "tool_use");
            if (!hasToolUse) continue;
            var next = i + 1 < msgs.Count ? msgs[i + 1] : null;
            var hasResult = next?["content"] is JsonArray nb
                && nb.Any(b => b?["type"]?.GetValue<string>() == "tool_result");
            if (!hasResult)
            {
                // synthesize empty tool_result for each tool_use
                var tr = new JsonArray();
                foreach (var b in blocks)
                    if (b?["type"]?.GetValue<string>() == "tool_use")
                        tr.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = b["id"]?.DeepClone(),
                            ["content"] = "",
                        });
                msgs.Insert(i + 1, new JsonObject { ["role"] = "user", ["content"] = tr });
                i++; // skip the message we just inserted
            }
        }
    }

    /// <summary>Claude requires the last message to be a user turn.</summary>
    private static void EnsureTrailingUserTurn(JsonArray msgs)
    {
        if (msgs.Count == 0 || msgs[^1]?["role"]?.GetValue<string>() != "user")
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = "Continue." });
    }

    /// <summary>
    /// Normalize an inbound claude request for openai transport: tools → tools[].function,
    /// tool_result blocks → role:"tool" messages, tool_use blocks → tool_calls.
    /// Mutates a chat-completions-shaped object already produced by the base translator.
    /// </summary>
    public static void NormalizeClaudeToolsForOpenAi(JsonObject claudeReq, JsonObject openAiReq)
    {
        if (claudeReq["tools"] is JsonArray tools)
        {
            var oTools = new JsonArray();
            foreach (var t in tools)
                oTools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t?["name"]?.DeepClone(),
                        ["description"] = t?["description"]?.DeepClone(),
                        ["parameters"] = t?["input_schema"]?.DeepClone(),
                    },
                });
            openAiReq["tools"] = oTools;
        }
        if (claudeReq["tool_choice"] is { } tc)
        {
            openAiReq["tool_choice"] = tc["type"]?.GetValue<string>() switch
            {
                "auto" => "auto",
                "none" => "none",
                "any" => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "auto" } },
                "tool" => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = tc["name"]?.DeepClone() } },
                _ => "auto",
            };
        }
        // rewrite tool_use/tool_result blocks inside messages
        if (openAiReq["messages"] is JsonArray msgs)
        {
            for (var i = 0; i < msgs.Count; i++)
            {
                var m = msgs[i];
                if (m?["content"] is not JsonArray content) continue;
                if (m["role"]?.GetValue<string>() == "assistant")
                {
                    var toolCalls = new JsonArray();
                    var textParts = new JsonArray();
                    foreach (var b in content)
                    {
                        if (b?["type"]?.GetValue<string>() == "tool_use")
                            toolCalls.Add(new JsonObject
                            {
                                ["id"] = b["id"]?.DeepClone(),
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = b["name"]?.DeepClone(),
                                    ["arguments"] = b["input"] is JsonObject inp ? inp.ToJsonString() : "{}",
                                },
                            });
                        else if (b?["type"]?.GetValue<string>() == "text")
                            textParts.Add(new JsonObject { ["type"] = "text", ["text"] = b["text"]?.DeepClone() });
                    }
                    if (toolCalls.Count > 0)
                    {
                        m["tool_calls"] = toolCalls;
                        m["content"] = textParts.Count == 1 ? textParts[0]!["text"]!.DeepClone() : textParts;
                    }
                }
                else if (m["role"]?.GetValue<string>() == "user")
                {
                    // split tool_result blocks into role:"tool" messages
                    var results = content.Where(b => b?["type"]?.GetValue<string>() == "tool_result").ToList();
                    if (results.Count > 0)
                    {
                        var toolMsgs = results.Select(b => (JsonNode)new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = b?["tool_use_id"]?.DeepClone(),
                            ["content"] = ExtractToolResultText(b),
                        }).ToList();
                        // SPEC-018 hoistToolResultImages: openai tool messages are
                        // text-only — move image blocks into a following user msg
                        var images = results.SelectMany(b => ExtractToolResultImages(b)).ToList();
                        var remaining = content.Where(b => b?["type"]?.GetValue<string>() != "tool_result").ToList();
                        msgs.RemoveAt(i);
                        foreach (var tm in toolMsgs) msgs.Insert(i++, tm);
                        if (images.Count > 0)
                            msgs.Insert(i++, new JsonObject { ["role"] = "user", ["content"] = new JsonArray(images.ToArray()) });
                        if (remaining.Count > 0)
                            msgs.Insert(i, new JsonObject { ["role"] = "user", ["content"] = new JsonArray(remaining.Select(r => r!.DeepClone()).ToArray()) });
                    }
                }
            }
        }
    }

    private static string ExtractToolResultText(JsonNode? block)
    {
        var c = block?["content"];
        if (c is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (c is JsonArray arr)
            return string.Join("\n", arr.Select(p => p?["text"]?.GetValue<string>()).Where(t => t is not null)!);
        return c?.ToJsonString() ?? "";
    }

    /// <summary>claude image blocks inside a tool_result → openai image_url parts.</summary>
    private static IEnumerable<JsonNode> ExtractToolResultImages(JsonNode? block)
    {
        if (block?["content"] is not JsonArray arr) yield break;
        foreach (var p in arr)
        {
            if (p?["type"]?.GetValue<string>() != "image") continue;
            var src = p["source"];
            var url = src?["type"]?.GetValue<string>() == "base64"
                ? $"data:{src["media_type"]?.GetValue<string>()};base64,{src["data"]?.GetValue<string>()}"
                : src?["url"]?.GetValue<string>();
            if (url is not null)
                yield return new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = url },
                };
        }
    }

    /// <summary>
    /// Normalize openai request hygiene: drop empty messages and empty
    /// tools array, strip thinking blocks from assistant content.
    /// </summary>
    public static void OpenAiPrepare(JsonObject o)
    {
        if (o["tools"] is JsonArray { Count: 0 }) o.Remove("tools");
        if (o["messages"] is JsonArray msgs)
            for (var i = msgs.Count - 1; i >= 0; i--)
            {
                var c = msgs[i]?["content"];
                if (c is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0
                    && msgs[i]?["tool_calls"] is null
                    && msgs[i]?["role"]?.GetValue<string>() != "tool")
                    msgs.RemoveAt(i);
            }
        // thinking blocks in assistant content → strip (openai has no thinking blocks)
        if (o["messages"] is JsonArray ms)
            foreach (var m in ms)
                if (m?["content"] is JsonArray arr)
                    for (var i = arr.Count - 1; i >= 0; i--)
                        if (arr[i]?["type"]?.GetValue<string>() is "thinking" or "redacted_thinking")
                            arr.RemoveAt(i);
    }

    /// <summary>maxTokens ceiling rules (ported from upstream maxTokens.js).</summary>
    public static int AdjustMaxTokens(int requested, string? model, string format)
    {
        // upstream rule of thumb: claude models cap lower than openai defaults
        var ceiling = format switch
        {
            "claude" => model?.Contains("opus") == true || model?.Contains("sonnet") == true ? 64000 : 8192,
            "gemini" => 65536,
            _ => int.MaxValue,
        };
        return Math.Min(requested, ceiling);
    }

    // ---------- Responses API (openai-responses) ----------

    /// <summary>chat.completions request → Responses API request body.</summary>
    public static JsonObject ChatToResponsesRequest(JsonObject req)
    {
        var o = new JsonObject();
        var input = new JsonArray();
        var instructions = new List<string>();
        foreach (var m in req["messages"]?.AsArray() ?? [])
        {
            var role = m?["role"]?.GetValue<string>() ?? "user";
            if (role is "system" or "developer")
            {
                var t = ExtractText(m?["content"]);
                if (t.Length > 0) instructions.Add(t);
                continue;
            }
            if (m?["tool_calls"] is JsonArray tc)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "message", ["role"] = "assistant",
                    ["content"] = ToResponsesContent(m?["content"]),
                });
                foreach (var call in tc)
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call?["id"]?.DeepClone(),
                        ["name"] = call?["function"]?["name"]?.DeepClone(),
                        ["arguments"] = call?["function"]?["arguments"]?.DeepClone(),
                    });
                continue;
            }
            if (role == "tool")
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = m["tool_call_id"]?.DeepClone(),
                    ["output"] = ExtractText(m["content"]),
                });
                continue;
            }
            input.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = role,
                ["content"] = ToResponsesContent(m?["content"]),
            });
        }
        o["input"] = input;
        if (instructions.Count > 0) o["instructions"] = string.Join("\n", instructions);
        foreach (var k in new[] { "temperature", "top_p", "stream", "tools" })
            if (req[k] is { } v) o[k] = v.DeepClone();
        if (req["max_tokens"] is { } mt || req["max_completion_tokens"] is { })
            o["max_output_tokens"] = (req["max_tokens"] ?? req["max_completion_tokens"])!.DeepClone();
        return o;
    }

    /// <summary>Responses API request → chat.completions request body.</summary>
    public static JsonObject ResponsesToChatRequest(JsonObject req)
    {
        var o = new JsonObject();
        var messages = new JsonArray();
        if (req["instructions"] is { } ins)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = ins.DeepClone() });
        var input = req["input"];
        if (input is JsonValue iv && iv.TryGetValue<string>(out var isText))
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = isText });
        else if (input is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var type = item?["type"]?.GetValue<string>();
                // bare {role,content} items (no type) are messages per upstream
                type ??= item?["role"] is not null ? "message" : null;
                switch (type)
                {
                    case "message":
                        messages.Add(new JsonObject
                        {
                            ["role"] = item?["role"]?.DeepClone(),
                            ["content"] = ResponsesContentToOpenAi(item?["content"]),
                        });
                        break;
                    case "function_call":
                        messages.Add(new JsonObject
                        {
                            ["role"] = "assistant",
                            ["tool_calls"] = new JsonArray(new JsonObject
                            {
                                ["id"] = item?["call_id"]?.DeepClone() ?? item?["id"]?.DeepClone(),
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = item?["name"]?.DeepClone(),
                                    ["arguments"] = item?["arguments"]?.DeepClone(),
                                },
                            }),
                        });
                        break;
                    case "function_call_output":
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = item?["call_id"]?.DeepClone(),
                            ["content"] = item?["output"]?.DeepClone(),
                        });
                        break;
                }
            }
        }
        o["messages"] = messages;
        foreach (var k in new[] { "temperature", "top_p", "stream", "tools" })
            if (req[k] is { } v) o[k] = v.DeepClone();
        if (req["max_output_tokens"] is { } mo) o["max_tokens"] = mo.DeepClone();
        return o;
    }

    /// <summary>Responses API response → chat.completion response.</summary>
    public static JsonNode ResponsesToChatResponse(JsonNode? res, string model)
    {
        var r = res?.AsObject();
        var text = "";
        var toolCalls = new JsonArray();
        if (r?["output"] is JsonArray output)
            foreach (var item in output)
            {
                var type = item?["type"]?.GetValue<string>();
                if (type == "message")
                    foreach (var c in item?["content"]?.AsArray() ?? [])
                        if (c?["type"]?.GetValue<string>() is "output_text" or "text")
                            text += c["text"]?.GetValue<string>();
                if (type == "function_call")
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = item?["call_id"]?.DeepClone() ?? item?["id"]?.DeepClone(),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = item?["name"]?.DeepClone(),
                            ["arguments"] = item?["arguments"]?.DeepClone(),
                        },
                    });
            }
        else if (r?["output_text"] is { } ot)
            text = ot.GetValue<string>();
        var msg = new JsonObject { ["role"] = "assistant", ["content"] = text };
        if (toolCalls.Count > 0) msg["tool_calls"] = toolCalls;
        var usage = r?["usage"];
        return new JsonObject
        {
            ["id"] = r?["id"]?.GetValue<string>() ?? $"chatcmpl-{Guid.NewGuid():N}",
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = msg,
                ["finish_reason"] = toolCalls.Count > 0 ? "tool_calls" : "stop",
            }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = usage?["input_tokens"]?.GetValue<int>() ?? 0,
                ["completion_tokens"] = usage?["output_tokens"]?.GetValue<int>() ?? 0,
                ["total_tokens"] = (usage?["input_tokens"]?.GetValue<int>() ?? 0) + (usage?["output_tokens"]?.GetValue<int>() ?? 0),
            },
        };
    }

    /// <summary>chat.completion response → Responses API response.</summary>
    public static JsonNode ChatToResponsesResponse(JsonNode? res, string model)
    {
        var r = res?.AsObject();
        var choice = r?["choices"]?.AsArray().FirstOrDefault();
        var text = choice?["message"]?["content"]?.GetValue<string>() ?? "";
        var output = new JsonArray();
        if (text.Length > 0)
            output.Add(new JsonObject
            {
                ["type"] = "message", ["role"] = "assistant", ["status"] = "completed",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
            });
        foreach (var tc in choice?["message"]?["tool_calls"]?.AsArray() ?? [])
            output.Add(new JsonObject
            {
                ["type"] = "function_call",
                ["call_id"] = tc?["id"]?.DeepClone(),
                ["name"] = tc?["function"]?["name"]?.DeepClone(),
                ["arguments"] = tc?["function"]?["arguments"]?.DeepClone(),
            });
        var usage = r?["usage"];
        return new JsonObject
        {
            ["id"] = r?["id"]?.GetValue<string>() ?? $"resp_{Guid.NewGuid():N}",
            ["object"] = "response",
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["status"] = "completed",
            ["output"] = output,
            ["output_text"] = text,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = usage?["prompt_tokens"]?.GetValue<int>() ?? 0,
                ["output_tokens"] = usage?["completion_tokens"]?.GetValue<int>() ?? 0,
                ["total_tokens"] = usage?["total_tokens"]?.DeepClone() ?? (usage?["prompt_tokens"]?.GetValue<int>() ?? 0) + (usage?["completion_tokens"]?.GetValue<int>() ?? 0),
            },
        };
    }

    private static JsonArray ToResponsesContent(JsonNode? content)
    {
        var parts = new JsonArray();
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
        {
            parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = s });
            return parts;
        }
        foreach (var b in content as JsonArray ?? [])
        {
            var type = b?["type"]?.GetValue<string>();
            if (type == "text")
                parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = b?["text"]?.DeepClone() });
            else if (type == "image_url")
                parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = b?["image_url"]?["url"]?.DeepClone() });
        }
        return parts;
    }

    private static JsonNode ResponsesContentToOpenAi(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is not JsonArray arr) return content?.DeepClone() ?? "";
        var parts = new JsonArray();
        foreach (var b in arr)
        {
            var type = b?["type"]?.GetValue<string>();
            if (type is "input_text" or "output_text" or "text")
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = b?["text"]?.DeepClone() });
            else if (type == "input_image")
                parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = b?["image_url"]?.DeepClone() } });
        }
        return parts.Count == 1 && parts[0]?["type"]?.GetValue<string>() == "text"
            ? parts[0]!["text"]!.DeepClone() : parts;
    }

    private static string ExtractText(JsonNode? content) => content switch
    {
        JsonValue v => v.TryGetValue<string>(out var s) ? s : "",
        JsonArray arr => string.Join("\n", arr.Select(b => b?["text"]?.GetValue<string>()).Where(t => t is not null)!),
        _ => "",
    };

    // ---------- SPEC-075: bedrock same-role merge (open-sse/executors/bedrock.ts) ----------

    private static bool IsEmptyTurnFiller(JsonNode? block) =>
        block is JsonObject o && o.Count == 1
        && o["text"] is JsonValue v && v.TryGetValue<string>(out var s) && s == " ";

    private static bool HasToolResultBlock(JsonNode? message) =>
        message?["content"] is JsonArray arr && arr.Any(b =>
            b?["type"]?.GetValue<string>() == "tool_result" ||
            b?["tool_result"] is not null || b?["toolResult"] is not null);

    /// <summary>
    /// Merge consecutive same-role messages (bedrock requires strict role
    /// alternation). A plain user turn must never absorb a following
    /// tool-result turn; the other direction still merges. Empty turn fillers
    /// ({ "text": " " }) are dropped when real content exists.
    /// </summary>
    public static void MergeBedrockSameRoleMessages(JsonObject req)
    {
        if (req["messages"] is not JsonArray msgs || msgs.Count < 2) return;
        var merged = new List<JsonNode>();
        foreach (var message in msgs)
        {
            var previous = merged.Count > 0 ? merged[^1] : null;
            var sameRole = previous?["role"]?.GetValue<string>() == message?["role"]?.GetValue<string>()
                && previous?["content"] is JsonArray && message?["content"] is JsonArray;
            var plainUserBeforeToolResult = sameRole
                && previous!["role"]?.GetValue<string>() == "user"
                && HasToolResultBlock(message) && !HasToolResultBlock(previous);
            if (sameRole && !plainUserBeforeToolResult)
            {
                var content = new JsonArray();
                foreach (var b in (JsonArray)previous!["content"]!) content.Add(b?.DeepClone());
                foreach (var b in (JsonArray)message!["content"]!) content.Add(b?.DeepClone());
                var real = content.Where(b => !IsEmptyTurnFiller(b)).ToList();
                previous["content"] = real.Count > 0
                    ? new JsonArray(real.Select(b => b?.DeepClone()).ToArray())
                    : new JsonArray(content[0]?.DeepClone());
                continue;
            }
            merged.Add(message!.DeepClone());
        }
        msgs.Clear();
        foreach (var m in merged) msgs.Add(m);
    }
}

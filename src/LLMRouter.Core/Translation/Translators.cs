using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMRouter.Core.Translation;

/// <summary>
/// Format translators (openai / claude / gemini / responsesApi) ported from
/// upstream open-sse/translator/*.js. Request: inbound → outbound.
/// Response: outbound → inbound (both non-streaming and SSE event payloads).
/// </summary>
public static class Translators
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    /// <summary>Mutable state carried across SSE events of one response.</summary>
    public sealed class SseState
    {
        public int InputTokens;
        public bool Started;
    }

    /// <summary>Translate an inbound chat request body to the outbound provider format.</summary>
    public static JsonNode Translate(JsonElement body, string inbound, string outbound, string model, bool stream)
    {
        var doc = JsonNode.Parse(body.GetRawText())!.AsObject();
        doc.Remove("model");
        doc["model"] = model;
        JsonNode result = (inbound, outbound) switch
        {
            ("openai", "claude") or ("responsesApi", "claude") => OpenAiToClaudeRequest(doc),
            ("openai", "gemini") or ("responsesApi", "gemini") => OpenAiToGeminiRequest(doc),
            ("claude", "openai") or ("claude", "responsesApi") => ClaudeToOpenAiRequest(doc),
            ("claude", "gemini") => ClaudeToOpenAiThenGemini(doc),
            ("gemini", "openai") or ("gemini", "responsesApi") => GeminiToOpenAiRequest(doc),
            ("gemini", "claude") => GeminiToClaudeRequest(doc),
            (_, "responsesApi") => doc,   // responses inbound/outbound pass through
            _ => doc,
        };
        if (result is JsonObject o)
        {
            o["model"] = model;
            if (stream) ApplyStreamFlag(o, outbound);
        }
        return result;
    }

    /// <summary>Translate an upstream (non-stream) response body to the inbound format.</summary>
    public static JsonNode TranslateResponse(JsonElement upstreamBody, string outbound, string inbound, string model)
    {
        var node = JsonNode.Parse(upstreamBody.GetRawText());
        return (outbound, inbound) switch
        {
            ("claude", "openai") or ("claude", "responsesApi") => ClaudeToOpenAiResponse(node, model),
            ("gemini", "openai") or ("gemini", "responsesApi") => GeminiToOpenAiResponse(node, model),
            ("openai", "claude") or ("responsesApi", "claude") => OpenAiToClaudeResponse(node, model),
            _ => node!,
        };
    }

    /// <summary>
    /// Translate one SSE event payload (the JSON after "data:") from outbound
    /// to inbound streaming format. Event "[DONE]" means emit the openai terminator.
    /// </summary>
    public static IEnumerable<(string? Event, string Data)> TranslateSse(
        string data, string outbound, string inbound, string model, SseState state)
    {
        if (outbound == inbound)
        {
            yield return (null, data);
            yield break;
        }
        JsonObject? chunk;
        try { chunk = JsonNode.Parse(data)?.AsObject(); }
        catch { yield break; }
        if (chunk is null) yield break;

        foreach (var r in (outbound, inbound) switch
        {
            ("claude", _) => ClaudeSseToOpenAi(chunk, model, state),
            ("gemini", _) => GeminiSseToOpenAi(chunk, model, state),
            (_, "claude") => OpenAiSseToClaude(chunk, model, state),
            (_, "gemini") => OpenAiSseToGemini(chunk, model, state),
            _ => [(null, data)],
        })
            yield return r;
    }

    // ---------- request translations ----------

    private static JsonObject OpenAiToClaudeRequest(JsonObject req)
    {
        var o = new JsonObject();
        var system = new JsonArray();
        var messages = new JsonArray();
        foreach (var m in req["messages"]?.AsArray() ?? [])
        {
            var role = m?["role"]?.GetValue<string>();
            if (role is "system" or "developer")
            {
                system.Add(ExtractText(m?["content"]));
                continue;
            }
            var msg = new JsonObject
            {
                ["role"] = role == "assistant" ? "assistant" : "user",
                ["content"] = ToClaudeContent(m?["content"]),
            };
            messages.Add(msg);
        }
        if (system.Count > 0) o["system"] = system;
        o["messages"] = messages;
        if (req["max_tokens"] is { } mt) o["max_tokens"] = mt.DeepClone();
        else if (req["max_completion_tokens"] is { } mct) o["max_tokens"] = mct.DeepClone();
        else o["max_tokens"] = 1024;
        foreach (var k in new[] { "temperature", "top_p", "stream" })
            if (req[k] is { } v) o[k] = v.DeepClone();
        if (req["stop"] is { } stop) o["stop_sequences"] = stop.DeepClone();
        return o;
    }

    private static JsonObject ClaudeToOpenAiRequest(JsonObject req)
    {
        var o = new JsonObject();
        var messages = new JsonArray();
        if (req["system"] is { } sys)
        {
            var text = sys is JsonArray arr
                ? string.Join("\n", arr.Select(b => b?["text"]?.GetValue<string>()).Where(t => t is not null))
                : sys.GetValue<string>();
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = text });
        }
        foreach (var m in req["messages"]?.AsArray() ?? [])
        {
            messages.Add(new JsonObject
            {
                ["role"] = m?["role"]?.GetValue<string>() ?? "user",
                ["content"] = ToOpenAiContent(m?["content"]),
            });
        }
        o["messages"] = messages;
        foreach (var k in new[] { "max_tokens", "temperature", "top_p", "stream" })
            if (req[k] is { } v) o[k] = v.DeepClone();
        if (req["stop_sequences"] is { } ss) o["stop"] = ss.DeepClone();
        return o;
    }

    private static JsonObject OpenAiToGeminiRequest(JsonObject req)
    {
        var o = new JsonObject();
        var contents = new JsonArray();
        JsonArray? system = null;
        foreach (var m in req["messages"]?.AsArray() ?? [])
        {
            var role = m?["role"]?.GetValue<string>();
            var parts = ToGeminiParts(m?["content"]);
            if (role is "system" or "developer")
            {
                system ??= [];
                foreach (var p in parts) system.Add(p?.DeepClone());
                continue;
            }
            contents.Add(new JsonObject
            {
                ["role"] = role == "assistant" ? "model" : "user",
                ["parts"] = parts,
            });
        }
        o["contents"] = contents;
        if (system is { Count: > 0 }) o["systemInstruction"] = new JsonObject { ["parts"] = system };
        var gen = new JsonObject();
        if (req["max_tokens"] is { } mt) gen["maxOutputTokens"] = mt.DeepClone();
        if (req["temperature"] is { } tp) gen["temperature"] = tp.DeepClone();
        if (req["top_p"] is { } top) gen["topP"] = top.DeepClone();
        if (req["stop"] is { } st) gen["stopSequences"] = st.DeepClone();
        if (gen.Count > 0) o["generationConfig"] = gen;
        return o;
    }

    private static JsonObject ClaudeToOpenAiThenGemini(JsonObject req)
        => OpenAiToGeminiRequest(ClaudeToOpenAiRequest(req));

    private static JsonObject GeminiToOpenAiRequest(JsonObject req)
    {
        var o = new JsonObject();
        var messages = new JsonArray();
        if (req["systemInstruction"]?["parts"] is JsonArray sp)
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = string.Join("\n", sp.Select(p => p?["text"]?.GetValue<string>())),
            });
        foreach (var c in req["contents"]?.AsArray() ?? [])
        {
            var role = c?["role"]?.GetValue<string>() == "model" ? "assistant" : "user";
            var parts = new JsonArray();
            foreach (var p in c?["parts"]?.AsArray() ?? [])
            {
                if (p?["text"] is { } t)
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = t.DeepClone() });
                else if (p?["inlineData"] is { } id)
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = $"data:{id["mimeType"]?.GetValue<string>()};base64,{id["data"]?.GetValue<string>()}",
                        },
                    });
            }
            messages.Add(new JsonObject { ["role"] = role, ["content"] = parts });
        }
        o["messages"] = messages;
        var gen = req["generationConfig"];
        if (gen?["maxOutputTokens"] is { } mt) o["max_tokens"] = mt.DeepClone();
        if (gen?["temperature"] is { } tp) o["temperature"] = tp.DeepClone();
        if (gen?["topP"] is { } top) o["top_p"] = top.DeepClone();
        if (gen?["stopSequences"] is { } ss) o["stop"] = ss.DeepClone();
        return o;
    }

    private static JsonObject GeminiToClaudeRequest(JsonObject req)
        => OpenAiToClaudeRequest(GeminiToOpenAiRequest(req));

    private static void ApplyStreamFlag(JsonObject o, string outbound)
    {
        switch (outbound)
        {
            case "claude": o["stream"] = true; break;
            case "gemini": o["alt"] ??= "sse"; break; // appended as query param actually; flag noted for caller
            default: o["stream"] = true; break;
        }
    }

    // ---------- response translations ----------

    private static JsonNode ClaudeToOpenAiResponse(JsonNode? res, string model)
    {
        var r = res?.AsObject();
        var text = r?["content"]?.AsArray()
            .Where(b => b?["type"]?.GetValue<string>() == "text")
            .Select(b => b?["text"]?.GetValue<string>())
            .Aggregate(string.Empty, (a, b) => a + b) ?? "";
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
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
                ["finish_reason"] = MapClaudeStop(r?["stop_reason"]?.GetValue<string>()),
            }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = usage?["input_tokens"]?.GetValue<int>() ?? 0,
                ["completion_tokens"] = usage?["output_tokens"]?.GetValue<int>() ?? 0,
                ["total_tokens"] = (usage?["input_tokens"]?.GetValue<int>() ?? 0) + (usage?["output_tokens"]?.GetValue<int>() ?? 0),
            },
        };
    }

    private static JsonNode GeminiToOpenAiResponse(JsonNode? res, string model)
    {
        var r = res?.AsObject();
        var cand = r?["candidates"]?.AsArray().FirstOrDefault();
        var text = cand?["content"]?["parts"]?.AsArray()
            .Select(p => p?["text"]?.GetValue<string>())
            .Where(t => t is not null)
            .Aggregate(string.Empty, (a, b) => a + b) ?? "";
        var usage = r?["usageMetadata"];
        return new JsonObject
        {
            ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
                ["finish_reason"] = cand?["finishReason"]?.GetValue<string>()?.ToLowerInvariant() ?? "stop",
            }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = usage?["promptTokenCount"]?.GetValue<int>() ?? 0,
                ["completion_tokens"] = usage?["candidatesTokenCount"]?.GetValue<int>() ?? 0,
                ["total_tokens"] = usage?["totalTokenCount"]?.GetValue<int>() ?? 0,
            },
        };
    }

    private static JsonNode OpenAiToClaudeResponse(JsonNode? res, string model)
    {
        var r = res?.AsObject();
        var choice = r?["choices"]?.AsArray().FirstOrDefault();
        var text = choice?["message"]?["content"]?.GetValue<string>() ?? "";
        var usage = r?["usage"];
        return new JsonObject
        {
            ["id"] = r?["id"]?.GetValue<string>() ?? $"msg_{Guid.NewGuid():N}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["stop_reason"] = MapOpenAiFinish(choice?["finish_reason"]?.GetValue<string>()),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = usage?["prompt_tokens"]?.GetValue<int>() ?? 0,
                ["output_tokens"] = usage?["completion_tokens"]?.GetValue<int>() ?? 0,
            },
        };
    }

    // ---------- SSE translations ----------

    private static IEnumerable<(string?, string)> ClaudeSseToOpenAi(JsonObject ev, string model, SseState state)
    {
        var type = ev["type"]?.GetValue<string>();
        switch (type)
        {
            case "content_block_delta":
                var delta = ev["delta"];
                var text = delta?["text"]?.GetValue<string>();
                if (text is null) yield break;
                yield return (null, OpenAiChunk(model, text, null));
                break;
            case "message_delta":
            case "message_stop":
                var usage = ev["usage"];
                var outTok = usage?["output_tokens"]?.GetValue<int>() ?? 0;
                yield return (null, OpenAiChunk(model, null, "stop", state.InputTokens, outTok));
                yield return (null, "[DONE]");
                break;
            case "message_start":
                state.InputTokens = ev["message"]?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
                break;
        }
    }

    private static IEnumerable<(string?, string)> GeminiSseToOpenAi(JsonObject ev, string model, SseState state)
    {
        var cand = ev["candidates"]?.AsArray().FirstOrDefault();
        var text = cand?["content"]?["parts"]?.AsArray()
            .Select(p => p?["text"]?.GetValue<string>())
            .Where(t => t is not null)
            .Aggregate(string.Empty, (a, b) => a + b);
        var usage = ev["usageMetadata"];
        var finish = cand?["finishReason"]?.GetValue<string>()?.ToLowerInvariant();
        if (text is { Length: > 0 })
            yield return (null, OpenAiChunk(model, text, null));
        if (finish is not null || usage is not null)
        {
            yield return (null, OpenAiChunk(model, null, finish ?? "stop",
                usage?["promptTokenCount"]?.GetValue<int>() ?? 0,
                usage?["candidatesTokenCount"]?.GetValue<int>() ?? 0));
            yield return (null, "[DONE]");
        }
    }

    private static IEnumerable<(string?, string)> OpenAiSseToClaude(JsonObject ev, string model, SseState state)
    {
        var delta = ev["choices"]?.AsArray().FirstOrDefault()?["delta"];
        var finish = ev["choices"]?.AsArray().FirstOrDefault()?["finish_reason"]?.GetValue<string>();
        var text = delta?["content"]?.GetValue<string>();
        if (!state.Started)
        {
            state.Started = true;
            yield return ("message_start", JsonSerializer.Serialize(new
            {
                type = "message_start",
                message = new
                {
                    id = $"msg_{Guid.NewGuid():N}",
                    type = "message",
                    role = "assistant",
                    model,
                    content = Array.Empty<object>(),
                    stop_reason = (string?)null,
                    usage = new { input_tokens = 0, output_tokens = 0 },
                },
            }));
            yield return ("content_block_start", JsonSerializer.Serialize(new
            {
                type = "content_block_start",
                index = 0,
                content_block = new { type = "text", text = "" },
            }));
        }
        if (text is { Length: > 0 })
            yield return ("content_block_delta", JsonSerializer.Serialize(new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "text_delta", text },
            }));
        if (finish is not null)
        {
            yield return ("content_block_stop", JsonSerializer.Serialize(new { type = "content_block_stop", index = 0 }));
            var usage = ev["usage"];
            yield return ("message_delta", JsonSerializer.Serialize(new
            {
                type = "message_delta",
                delta = new { stop_reason = MapOpenAiFinish(finish) },
                usage = new
                {
                    output_tokens = usage?["completion_tokens"]?.GetValue<int>() ?? 0,
                    input_tokens = usage?["prompt_tokens"]?.GetValue<int>() ?? 0,
                },
            }));
            yield return ("message_stop", JsonSerializer.Serialize(new { type = "message_stop" }));
        }
    }

    private static IEnumerable<(string?, string)> OpenAiSseToGemini(JsonObject ev, string model, SseState state)
    {
        var delta = ev["choices"]?.AsArray().FirstOrDefault()?["delta"];
        var finish = ev["choices"]?.AsArray().FirstOrDefault()?["finish_reason"]?.GetValue<string>();
        var text = delta?["content"]?.GetValue<string>();
        if (text is null && finish is null) yield break;
        var cand = new JsonObject
        {
            ["content"] = new JsonObject
            {
                ["role"] = "model",
                ["parts"] = text is null ? new JsonArray() : new JsonArray(new JsonObject { ["text"] = text }),
            },
        };
        if (finish is not null) cand["finishReason"] = "STOP";
        var usage = ev["usage"];
        var payload = new JsonObject { ["candidates"] = new JsonArray(cand) };
        if (usage is not null)
            payload["usageMetadata"] = new JsonObject
            {
                ["promptTokenCount"] = usage["prompt_tokens"]?.GetValue<int>() ?? 0,
                ["candidatesTokenCount"] = usage["completion_tokens"]?.GetValue<int>() ?? 0,
                ["totalTokenCount"] = usage["total_tokens"]?.GetValue<int>() ?? 0,
            };
        yield return (null, payload.ToJsonString());
    }

    // ---------- content helpers ----------

    private static string ExtractText(JsonNode? content) => content switch
    {
        JsonValue v => v.TryGetValue<string>(out var s) ? s : "",
        JsonArray arr => string.Join("\n", arr.Select(b => b?["text"]?.GetValue<string>()).Where(t => t is not null)),
        _ => "",
    };

    private static JsonNode ToClaudeContent(JsonNode? content) => content switch
    {
        JsonValue => new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ExtractText(content) }),
        JsonArray arr => new JsonArray(arr.Select<JsonNode?, JsonNode>(b =>
        {
            var type = b?["type"]?.GetValue<string>();
            return type switch
            {
                "text" => new JsonObject { ["type"] = "text", ["text"] = b!["text"]!.DeepClone() },
                "image_url" => DataUrlToClaudeSource(b?["image_url"]?["url"]?.GetValue<string>()),
                _ => new JsonObject { ["type"] = "text", ["text"] = b?.ToJsonString() ?? "" },
            };
        }).ToArray()),
        _ => content?.DeepClone() ?? "",
    };

    private static JsonNode ToOpenAiContent(JsonNode? content) => content switch
    {
        JsonValue => content.DeepClone(),
        JsonArray arr => new JsonArray(arr.Select<JsonNode?, JsonNode>(b =>
        {
            var type = b?["type"]?.GetValue<string>();
            return type switch
            {
                "text" => new JsonObject { ["type"] = "text", ["text"] = b!["text"]!.DeepClone() },
                "image" => new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:{b?["source"]?["media_type"]?.GetValue<string>()};base64,{b?["source"]?["data"]?.GetValue<string>()}",
                    },
                },
                _ => new JsonObject { ["type"] = "text", ["text"] = b?["text"]?.GetValue<string>() ?? b?.ToJsonString() ?? "" },
            };
        }).ToArray()),
        _ => content?.DeepClone() ?? "",
    };

    private static JsonArray ToGeminiParts(JsonNode? content)
    {
        var parts = new JsonArray();
        switch (content)
        {
            case JsonValue:
                parts.Add(new JsonObject { ["text"] = ExtractText(content) });
                break;
            case JsonArray arr:
                foreach (var b in arr)
                {
                    var type = b?["type"]?.GetValue<string>();
                    if (type == "text")
                        parts.Add(new JsonObject { ["text"] = b!["text"]!.DeepClone() });
                    else if (type == "image_url")
                    {
                        var url = b?["image_url"]?["url"]?.GetValue<string>() ?? "";
                        var semi = url.IndexOf(';');
                        var comma = url.IndexOf(',');
                        if (url.StartsWith("data:") && semi > 0 && comma > semi)
                            parts.Add(new JsonObject
                            {
                                ["inlineData"] = new JsonObject
                                {
                                    ["mimeType"] = url[5..semi],
                                    ["data"] = url[(comma + 1)..],
                                },
                            });
                    }
                }
                break;
        }
        return parts;
    }

    private static JsonNode DataUrlToClaudeSource(string? url)
    {
        var u = url ?? "";
        var semi = u.IndexOf(';');
        var comma = u.IndexOf(',');
        if (u.StartsWith("data:") && semi > 0 && comma > semi)
            return new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = u[5..semi],
                    ["data"] = u[(comma + 1)..],
                },
            };
        return new JsonObject { ["type"] = "text", ["text"] = "" };
    }

    private static string OpenAiChunk(string model, string? text, string? finish, int? inTok = null, int? outTok = null)
    {
        var chunk = new JsonObject
        {
            ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = text is null
                    ? new JsonObject()
                    : new JsonObject { ["role"] = "assistant", ["content"] = text },
                ["finish_reason"] = finish is null ? null : finish,
            }),
        };
        if (inTok is not null || outTok is not null)
            chunk["usage"] = new JsonObject
            {
                ["prompt_tokens"] = inTok ?? 0,
                ["completion_tokens"] = outTok ?? 0,
                ["total_tokens"] = (inTok ?? 0) + (outTok ?? 0),
            };
        return chunk.ToJsonString();
    }

    private static string MapClaudeStop(string? stop) => stop switch
    {
        "end_turn" or "stop_sequence" or "pause_turn" => "stop",
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        _ => "stop",
    };

    private static string MapOpenAiFinish(string? finish) => finish switch
    {
        "stop" => "end_turn",
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        "content_filter" => "refusal",
        _ => "end_turn",
    };
}

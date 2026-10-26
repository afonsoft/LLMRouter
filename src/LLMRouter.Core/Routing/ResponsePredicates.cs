using System.Text.Json;

namespace LLMRouter.Core.Routing;

public static class ResponsePredicates
{
    public sealed record Result(bool Passed, string? FailedPredicate);

    public static Result Evaluate(JsonElement response, IReadOnlyCollection<string> predicates,
        long latencyMs)
    {
        foreach (var predicate in predicates)
        {
            var passed = predicate.Equals("finish_reason", StringComparison.OrdinalIgnoreCase)
                ? HasFinishReason(response)
                : predicate.Equals("nonempty_content", StringComparison.OrdinalIgnoreCase)
                    ? HasContent(response)
                    : predicate.StartsWith("max_latency_ms:", StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(predicate["max_latency_ms:".Length..], out var max)
                        && latencyMs <= max;
            if (!passed) return new Result(false, predicate);
        }
        return new Result(true, null);
    }

    private static bool HasFinishReason(JsonElement response)
    {
        if (response.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array)
            return choices.EnumerateArray().Any(c =>
                c.TryGetProperty("finish_reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(reason.GetString()));

        return response.TryGetProperty("finish_reason", out var direct)
            && direct.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(direct.GetString());
    }

    private static bool HasContent(JsonElement response)
    {
        if (response.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array)
            return choices.EnumerateArray().Any(ChoiceHasContent);

        if (response.TryGetProperty("output_text", out var output)
            && output.ValueKind == JsonValueKind.String)
            return !string.IsNullOrWhiteSpace(output.GetString());

        return response.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(content.GetString());
    }

    private static bool ChoiceHasContent(JsonElement choice)
    {
        if (choice.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var messageContent)
            && messageContent.ValueKind == JsonValueKind.String)
            return !string.IsNullOrWhiteSpace(messageContent.GetString());

        return choice.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(text.GetString());
    }
}

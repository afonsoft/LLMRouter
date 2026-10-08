using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

/// <summary>
/// Custom provider nodes (ported from open-sse/services/provider.js): user-defined
/// endpoints whose provider id follows the naming convention
/// openai-compatible-{chat|responses}-{id}, anthropic-compatible-{id},
/// claude-code-{id}. Each maps a providerNodes row to a synthetic registry entry.
/// </summary>
public static class NodeResolver
{
    /// <summary>"chat" | "responses" | null — the openai-compatible transport for an id.</summary>
    public static string? ResolveOpenAICompatibleApiType(string providerId)
    {
        if (providerId.StartsWith("openai-compatible-chat-", StringComparison.Ordinal)) return "chat";
        if (providerId.StartsWith("openai-compatible-responses-", StringComparison.Ordinal)) return "responses";
        return null;
    }

    public static bool IsNodeProviderId(string id)
        => ResolveOpenAICompatibleApiType(id) is not null
           || id.StartsWith("anthropic-compatible-", StringComparison.Ordinal)
           || id.StartsWith("claude-code-", StringComparison.Ordinal);

    /// <summary>Build a synthetic ProviderEntry for a node provider id, loading its data row.</summary>
    public static async Task<ProviderEntry?> ResolveAsync(LlmRouterDbContext db, string providerId, CancellationToken ct = default)
    {
        if (!IsNodeProviderId(providerId)) return null;

        // Node rows are keyed by their provider id or store it inside data.providerId.
        var node = await db.ProviderNodes
            .Where(n => n.Id == providerId || n.Id == providerId.Substring(providerId.LastIndexOf('-') + 1))
            .FirstOrDefaultAsync(ct);
        node ??= await db.ProviderNodes
            .Where(n => n.Data.Contains(providerId))
            .FirstOrDefaultAsync(ct);

        var (format, baseUrl, modelsUrl) = providerId switch
        {
            _ when ResolveOpenAICompatibleApiType(providerId) == "responses" => ("responsesApi", "", "v1/models"),
            _ when ResolveOpenAICompatibleApiType(providerId) == "chat" => ("openai", "", "v1/models"),
            _ when providerId.StartsWith("anthropic-compatible-") => ("claude", "", "v1/models"),
            _ => ("claude", "", "v1/models"), // claude-code-*
        };

        string? nodeBaseUrl = null, nodeName = null;
        List<RegistryModel>? models = null;
        if (node is not null && node.Data is { Length: > 0 } d)
        {
            try
            {
                var el = JsonDocument.Parse(d).RootElement;
                nodeBaseUrl = Str(el, "baseUrl") ?? Str(el, "base_url") ?? Str(el, "url");
                nodeName = node.Name ?? Str(el, "name");
                if (el.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array)
                    models = m.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String
                            ? new RegistryModel { Id = x.GetString()! }
                            : new RegistryModel { Id = Str(x, "id") ?? "", Name = Str(x, "name") })
                        .Where(x => x.Id.Length > 0).ToList();
            }
            catch { }
        }

        return new ProviderEntry
        {
            Id = providerId,
            Format = format,
            BaseUrl = (nodeBaseUrl ?? "").TrimEnd('/'),
            ModelsUrl = modelsUrl,
            AuthType = "apikey",
            Models = models,
            // keep node name discoverable via extra headers map (display only)
            Headers = nodeName is null ? null : new Dictionary<string, string> { ["x-llmrouter-node-name"] = nodeName },
        };
    }

    private static string? Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

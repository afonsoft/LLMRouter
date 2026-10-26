using Microsoft.JSInterop;

namespace LLMRouter.Client.Services;

public sealed class FavoritesService(IJSRuntime js)
{
    public async Task<bool> IsStarredAsync(string type, string id) =>
        (await GetAsync(type)).Contains(id, StringComparer.OrdinalIgnoreCase);

    public async Task ToggleAsync(string type, string id)
    {
        var values = (await GetAsync(type)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!values.Add(id)) values.Remove(id);
        await js.InvokeVoidAsync("localStorage.setItem", Key(type), string.Join("\n", values));
    }

    private async Task<string[]> GetAsync(string type)
    {
        var raw = await js.InvokeAsync<string?>("localStorage.getItem", Key(type));
        return raw?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
    }

    private static string Key(string type) => $"llmr_starred_{type.ToLowerInvariant()}";
}

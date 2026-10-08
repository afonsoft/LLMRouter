using Microsoft.JSInterop;

namespace LLMRouter.Client.Services;

/// <summary>Theme service — same localStorage contract as upstream (zustand JSON under "theme").</summary>
public sealed class ThemeService(IJSRuntime js)
{
    public string Theme { get; private set; } = "system";
    public event Action? Changed;

    public async Task InitializeAsync() => Theme = await js.InvokeAsync<string>("llmrouter.getTheme");

    public async Task SetAsync(string theme)
    {
        Theme = theme;
        await js.InvokeVoidAsync("llmrouter.setTheme", theme);
        Changed?.Invoke();
    }
}

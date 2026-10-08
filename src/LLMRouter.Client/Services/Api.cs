using System.Net.Http.Json;
using System.Text.Json;

namespace LLMRouter.Client.Services;

/// <summary>Thin typed wrapper over the management API.</summary>
public sealed class Api(HttpClient http)
{
    public async Task<JsonElement> GetAsync(string url) =>
        (await http.GetFromJsonAsync<JsonElement>(url))!;

    public async Task<JsonElement> PostAsync(string url, object body) =>
        (await (await http.PostAsJsonAsync(url, body)).Content.ReadFromJsonAsync<JsonElement>())!;

    public async Task<JsonElement> PutAsync(string url, object body) =>
        (await (await http.PutAsJsonAsync(url, body)).Content.ReadFromJsonAsync<JsonElement>())!;

    public async Task DeleteAsync(string url) => await http.DeleteAsync(url);

    public HttpClient Raw => http;
}

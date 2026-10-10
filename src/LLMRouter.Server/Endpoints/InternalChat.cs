using System.Text;
using System.Text.Json;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// In-process chat call: fabricates a minimal HttpContext over a memory
/// stream and drives GatewayEndpoints.Chat with a forced key — no socket,
/// works identically under TestServer and in production. Used by the A2A
/// executor and the conductor decomposer (SPEC-053).
/// </summary>
internal static class InternalChat
{
    /// <returns>assistant text, or an "error: ..." string on failure</returns>
    internal static async Task<string> CallAsync(
        IServiceProvider sp, string model, string prompt, string forcedKey)
    {
        await using var scope = sp.CreateAsyncScope();
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var body = JsonSerializer.Serialize(new
        {
            model, stream = false,
            messages = new[] { new { role = "user", content = prompt } },
        });
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Request.ContentType = "application/json";
        var ms = new MemoryStream();
        ctx.Response.Body = ms;
        try
        {
            await GatewayEndpoints.Chat(ctx, "openai", forcedKey);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
        var raw = Encoding.UTF8.GetString(ms.ToArray());
        if (ctx.Response.StatusCode != 200)
            return $"error: {ctx.Response.StatusCode} {(raw.Length > 300 ? raw[..300] : raw)}";
        try
        {
            var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
                && ch[0].TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c)
                ? c.GetString() ?? "" : "";
        }
        catch { return raw; }
    }
}

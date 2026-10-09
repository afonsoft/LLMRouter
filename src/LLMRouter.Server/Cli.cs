using System.Security.Cryptography;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server;

/// <summary>SPEC-016: llmrouter reset-password verb — writes the PBKDF2 hash into the settings row.</summary>
public static class Cli
{
    private const int Iterations = 100_000;

    public static void ResetPassword(string newPassword, string? dbPath = null)
    {
        if (string.IsNullOrEmpty(newPassword))
        {
            Console.Error.WriteLine("usage: llmrouter reset-password <new-password>");
            Environment.ExitCode = 2;
            return;
        }
        dbPath ??= Environment.GetEnvironmentVariable("LLMROUTER_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LLMRouter", "llmrouter.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var opts = new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        using var db = new LlmRouterDbContext(opts);
        db.EnsureCreated();
        var s = db.Settings.FirstOrDefault() ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var d = string.IsNullOrEmpty(s.Data)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.Data) ?? [];
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(newPassword, salt, Iterations, HashAlgorithmName.SHA256, 32);
        d["adminPasswordHash"] = JsonSerializer.SerializeToElement(
            $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
        d["setupComplete"] = JsonSerializer.SerializeToElement(true);
        s.Data = JsonSerializer.Serialize(d);
        db.SaveChanges();
        Console.WriteLine($"Password reset ({dbPath})");
    }

    /// <summary>SPEC-025: `llmrouter mcp-stdio` — stdin/stdout JSON-RPC bridge to
    /// the running server's POST /mcp. Env: LLMROUTER_MCP_URL (default
    /// http://localhost:20128/mcp), LLMROUTER_API_KEY (forwarded as x-api-key).</summary>
    public static async Task<int> McpStdioAsync()
    {
        var url = Environment.GetEnvironmentVariable("LLMROUTER_MCP_URL") ?? "http://localhost:20128/mcp";
        var apiKey = Environment.GetEnvironmentVariable("LLMROUTER_API_KEY");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        string? line;
        var stdout = Console.OpenStandardOutput();
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new StringContent(line, System.Text.Encoding.UTF8, "application/json") };
            if (apiKey is not null) req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            try
            {
                using var res = await http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                var bytes = System.Text.Encoding.UTF8.GetBytes(body + "\n");
                await stdout.WriteAsync(bytes);
                await stdout.FlushAsync();
            }
            catch (Exception ex)
            {
                var err = $"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":-32000,\"message\":{JsonSerializer.Serialize(ex.Message)}}}}}\n";
                await stdout.WriteAsync(System.Text.Encoding.UTF8.GetBytes(err));
                await stdout.FlushAsync();
            }
        }
        return 0;
    }
}

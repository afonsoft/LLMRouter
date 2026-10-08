using System.Security.Cryptography;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server;

/// <summary>SPEC-016: `llmrouter reset-password <pw>` — writes the PBKDF2 hash into the settings row.</summary>
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
}

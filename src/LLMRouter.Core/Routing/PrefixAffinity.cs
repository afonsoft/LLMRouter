using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Routing;

public static class PrefixAffinity
{
    public const string Scope = "routing";

    public static string ComputeHash(JsonElement body)
    {
        var text = body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array
                ? string.Join(" ", messages.EnumerateArray().Select(MessageText))
                : body.GetRawText();
        var prefix = text[..Math.Min(200, text.Length)];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix)))[..16];
    }

    public static string ComputeKey(JsonElement body) => $"affinity:{ComputeHash(body)}";

    public static async Task PutAsync(LlmRouterDbContext db, string hash, string deploymentId,
        TimeSpan ttl, CancellationToken ct)
    {
        var key = hash.StartsWith("affinity:", StringComparison.Ordinal) ? hash : $"affinity:{hash}";
        var value = JsonSerializer.Serialize(new
        {
            deploymentId,
            expiresAt = DateTimeOffset.UtcNow.Add(ttl),
        });
        var row = await db.Kv.FirstOrDefaultAsync(x => x.Scope == Scope && x.Key == key, ct);
        if (row is null) db.Kv.Add(new KvEntry { Scope = Scope, Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync(ct);
    }

    public static async Task<string?> GetAsync(LlmRouterDbContext db, string hash,
        CancellationToken ct)
    {
        var key = hash.StartsWith("affinity:", StringComparison.Ordinal) ? hash : $"affinity:{hash}";
        var row = await db.Kv.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Scope == Scope && x.Key == key, ct);
        if (row is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(row.Value);
            var expires = doc.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
            if (expires <= DateTimeOffset.UtcNow)
            {
                var expired = await db.Kv.FirstOrDefaultAsync(
                    x => x.Scope == Scope && x.Key == key, ct);
                if (expired is not null)
                {
                    db.Kv.Remove(expired);
                    await db.SaveChangesAsync(ct);
                }
                return null;
            }
            return doc.RootElement.GetProperty("deploymentId").GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MessageText(JsonElement message) =>
        message.TryGetProperty("content", out var content)
            ? content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : content.GetRawText()
            : "";
}

using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-035: scan local CLI tools' credential files and import them as provider connections.</summary>
public static class CliEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Dictionary<string, string> ProviderAlias = new()
    {
        ["anthropic"] = "anthropic", ["openai"] = "openai", ["gemini"] = "gemini",
        ["xai"] = "xai", ["groq"] = "groq", ["deepseek"] = "deepseek",
        ["qwen"] = "qwen", ["mistral"] = "mistral", ["aws-bedrock"] = "bedrock",
    };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/cli-credentials/scan", () =>
        {
            var findings = Core.Extras.CliCredentialScanner.Scan();
            return Results.Json(new
            {
                findings = findings.Select(f => new
                {
                    id = f.Id, tool = f.Tool, provider = f.Provider,
                    type = f.CredentialType, masked = f.Masked, label = f.Label, path = f.Path,
                }),
                total = findings.Count,
            }, JsonOpts);
        });

        g.MapPost("/cli-credentials/import", async (HttpContext ctx, LlmRouterDbContext db, ProviderRegistry reg) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var ids = req.ValueKind == JsonValueKind.Object && req.TryGetProperty("ids", out var ii) && ii.ValueKind == JsonValueKind.Array
                ? ii.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet()
                : null;

            var findings = Core.Extras.CliCredentialScanner.Scan();
            var selected = ids is null ? findings : findings.Where(f => ids.Contains(f.Id)).ToList();
            var imported = 0; var skipped = 0;
            var errors = new List<string>();
            var existing = await db.ProviderConnections.ToListAsync();

            foreach (var f in selected)
            {
                var provider = ProviderAlias.GetValueOrDefault(f.Provider, f.Provider);
                var p = reg.GetProvider(provider)
                    ?? (NodeResolver.IsNodeProviderId(provider) ? await NodeResolver.ResolveAsync(db, provider) : null);
                if (p is null) { errors.Add($"{f.Tool}: unknown provider '{f.Provider}'"); continue; }

                // dedup: same provider + same credential value already stored
                if (existing.Any(c => c.Provider == provider && c.Data.Contains(f.Value)))
                { skipped++; continue; }

                var data = f.CredentialType == "api_key"
                    ? JsonSerializer.Serialize(new Dictionary<string, string> { ["apiKey"] = f.Value })
                    : JsonSerializer.Serialize(new Dictionary<string, string> { ["accessToken"] = f.Value, ["credentialType"] = f.CredentialType });
                var c = new ProviderConnection
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Provider = provider,
                    AuthType = p.AuthType,
                    Name = $"{provider} ({f.Tool})",
                    IsActive = true,
                    Data = data,
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    UpdatedAt = DateTime.UtcNow.ToString("o"),
                };
                db.ProviderConnections.Add(c);
                existing.Add(c);
                imported++;
            }
            await db.SaveChangesAsync();
            if (imported > 0)
                await Core.Extras.Extras.AuditAsync(db, "cli.import", $"{imported} credential(s)");
            return Results.Json(new { imported, skipped, errors }, JsonOpts);
        });
    }
}

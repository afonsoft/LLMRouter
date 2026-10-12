using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.OAuth;
using LLMRouter.Core.Registry;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// SPEC-130: provider auth apply-local/export + refresh — faithful port of
/// OmniRoute's /api/providers/{id}/{codex-auth,claude-auth}/{apply-local,export}
/// and /api/providers/{id}/refresh. apply-local writes the CLI's auth file under
/// the user's home (~/.codex/auth.json, ~/.claude/.credentials.json) — gated by
/// LLMR_ALLOW_CONFIG_WRITES=false → 403 {error, code:"writes_disabled"}. Codex
/// apply never clobbers a fresh existing auth.json unless {force:true}
/// ("skipped_present_fresh"); a .bak sibling backup is always taken on write.
public static class ProviderAuthEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static string Now() => DateTime.UtcNow.ToString("O");
    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static JsonObject DataOf(ProviderConnection c)
        => JsonNode.Parse(c.Data)?.AsObject() ?? new JsonObject();

    private static string? S(JsonObject d, string k) => d[k]?.GetValue<string>();

    private static bool WritesDisabled() =>
        string.Equals(Environment.GetEnvironmentVariable("LLMR_ALLOW_CONFIG_WRITES"), "false",
            StringComparison.OrdinalIgnoreCase);

    private static IResult? WriteGuard()
        => WritesDisabled()
            ? Results.Json(new { error = "CLI config writes are disabled (LLMR_ALLOW_CONFIG_WRITES=false)", code = "writes_disabled" },
                JsonOpts, statusCode: 403)
            : null;

    // ---------- auth-file builders ----------
    private static (JsonObject file, string fileName) BuildCodexAuthFile(ProviderConnection c)
    {
        var d = DataOf(c);
        if (S(d, "accessToken") is not { } at)
            throw new InvalidOperationException("connection has no accessToken");
        var tokens = new JsonObject { ["access_token"] = at };
        if (S(d, "refreshToken") is { } rt) tokens["refresh_token"] = rt;
        if (S(d, "accountId") is { } aid) tokens["account_id"] = aid;
        return (new JsonObject { ["tokens"] = tokens, ["last_refresh"] = Now() }, "codex-auth.json");
    }

    private static (JsonObject payload, string fileName) BuildClaudeAuthFile(ProviderConnection c)
    {
        var d = DataOf(c);
        if (S(d, "accessToken") is not { } at)
            throw new InvalidOperationException("connection has no accessToken");
        var oauth = new JsonObject { ["accessToken"] = at };
        if (S(d, "refreshToken") is { } rt) oauth["refreshToken"] = rt;
        if (S(d, "expiresAt") is { } ea) oauth["expiresAt"] = ea;
        if (d["providerSpecificData"] is JsonObject psd)
        {
            if (S(psd, "subscriptionType") is { } st) oauth["subscriptionType"] = st;
            if (S(psd, "rateLimitTier") is { } rlt) oauth["rateLimitTier"] = rlt;
            if (psd["scopes"] is JsonArray sc) oauth["scopes"] = sc.DeepClone();
        }
        return (new JsonObject { ["claudeAiOauth"] = oauth }, "claude-credentials.json");
    }

    private static IResult ExportAttachment((JsonObject file, string fileName) built)
        => Results.File(
            System.Text.Encoding.UTF8.GetBytes(built.file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"),
            "application/json; charset=utf-8",
            built.fileName);

    // write file with sibling .bak; returns savedBakPath or null when nothing existed
    private static async Task<string?> WriteWithBackupAsync(string path, string content)
    {
        string? bak = null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            bak = path + ".bak";
            File.Copy(path, bak, overwrite: true);
        }
        await File.WriteAllTextAsync(path, content);
        return bak;
    }

    // codex auth.json considered fresh when its access_token matches the connection's
    private static bool CodexFileFresh(string path, string accessToken)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var t = JsonNode.Parse(File.ReadAllText(path))?["tokens"]?["access_token"]?.GetValue<string>();
            return t == accessToken;
        }
        catch { return false; }
    }

    public static void MapProviderAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---------- codex-auth ----------
        g.MapPost("/providers/{id}/codex-auth/apply-local", async (HttpContext ctx, LlmRouterDbContext db, string id) =>
        {
            if (WriteGuard() is { } denied) return denied;
            var conn = await db.ProviderConnections.FindAsync(id);
            if (conn is null || !conn.Provider.Equals("codex", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "codex connection not found", code = "not_found" });
            var force = false;
            try
            {
                if (await ctx.Request.ReadFromJsonAsync<JsonObject>() is { } b && b["force"]?.GetValue<bool>() == true)
                    force = true;
            }
            catch { /* no body — default force=false */ }

            var d = DataOf(conn);
            var at = S(d, "accessToken");
            if (at is null) return Results.BadRequest(new { error = "connection has no accessToken", code = "no_access_token" });
            var authPath = Path.Combine(Home(), ".codex", "auth.json");

            if (!force && CodexFileFresh(authPath, at))
            {
                return Results.Json(new
                {
                    success = true, connectionId = conn.Id, decision = "skipped_present_fresh",
                    connectionLabel = conn.Name, authPath, savedBakPath = (string?)null,
                    centralizedBackupPath = (string?)null, writtenAt = Now(),
                }, JsonOpts);
            }

            var (file, _) = BuildCodexAuthFile(conn);
            var bak = await WriteWithBackupAsync(authPath,
                file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return Results.Json(new
            {
                success = true, connectionId = conn.Id, decision = "written",
                connectionLabel = conn.Name, authPath, savedBakPath = bak,
                centralizedBackupPath = (string?)null, writtenAt = Now(),
            }, JsonOpts);
        });

        g.MapPost("/providers/{id}/codex-auth/export", async (LlmRouterDbContext db, string id) =>
        {
            var conn = await db.ProviderConnections.FindAsync(id);
            if (conn is null || !conn.Provider.Equals("codex", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "codex connection not found", code = "not_found" });
            try { return ExportAttachment(BuildCodexAuthFile(conn)); }
            catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message, code = "no_access_token" }); }
        });

        // ---------- claude-auth ----------
        g.MapPost("/providers/{id}/claude-auth/apply-local", async (LlmRouterDbContext db, string id) =>
        {
            if (WriteGuard() is { } denied) return denied;
            var conn = await db.ProviderConnections.FindAsync(id);
            if (conn is null || !conn.Provider.Equals("claude", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "claude connection not found", code = "not_found" });
            var authPath = Path.Combine(Home(), ".claude", ".credentials.json");
            try
            {
                var (payload, _) = BuildClaudeAuthFile(conn);
                // merge into existing doc so unrelated keys (e.g. mcpOAuth) are preserved
                JsonObject doc;
                try { doc = File.Exists(authPath) ? JsonNode.Parse(await File.ReadAllTextAsync(authPath))?.AsObject() ?? new JsonObject() : new JsonObject(); }
                catch { doc = new JsonObject(); }
                var mcpPreserved = doc.ContainsKey("mcpOAuth");
                doc["claudeAiOauth"] = payload["claudeAiOauth"]!.DeepClone();
                var bak = await WriteWithBackupAsync(authPath,
                    doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                return Results.Json(new
                {
                    success = true, connectionId = conn.Id, connectionLabel = conn.Name,
                    authPath, savedBakPath = bak, centralizedBackupPath = (string?)null,
                    mcpOAuthPreserved = mcpPreserved, writtenAt = Now(),
                }, JsonOpts);
            }
            catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message, code = "no_access_token" }); }
        });

        g.MapPost("/providers/{id}/claude-auth/export", async (LlmRouterDbContext db, string id) =>
        {
            var conn = await db.ProviderConnections.FindAsync(id);
            if (conn is null || !conn.Provider.Equals("claude", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "claude connection not found", code = "not_found" });
            try { return ExportAttachment(BuildClaudeAuthFile(conn)); }
            catch (InvalidOperationException e) { return Results.BadRequest(new { error = e.Message, code = "no_access_token" }); }
        });

        // ---------- refresh ----------
        g.MapPost("/providers/{id}/refresh", async (LlmRouterDbContext db, ProviderRegistry reg, IHttpClientFactory hf, string id) =>
        {
            var conn = await db.ProviderConnections.FindAsync(id);
            if (conn is null) return Results.NotFound(new { error = "connection not found" });
            var p = reg.GetProvider(conn.Provider);
            if (p is null) return Results.BadRequest(new { error = $"unknown provider: {conn.Provider}" });
            var ok = await OAuthService.RefreshAsync(conn, p, hf.CreateClient("oauth-refresh"));
            if (!ok) return Results.Json(new { error = "refresh failed", connectionId = id }, JsonOpts, statusCode: 502);
            conn.UpdatedAt = Now();
            await db.SaveChangesAsync();
            return Results.Json(new { success = true, connectionId = id, refreshedAt = Now() }, JsonOpts);
        });
    }
}

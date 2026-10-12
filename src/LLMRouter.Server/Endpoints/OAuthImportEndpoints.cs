using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// SPEC-125: local-CLI credential import — faithful port of OmniRoute's
/// /api/oauth/{codex-import, codex-import-token, cliproxy-import, cursor/*, kiro/api-key,
/// trae/import, paste-credentials} and /api/providers/agy-auth/* routes. Imports OAuth
/// credentials produced by local CLI tools (Codex CLI, Cursor, Kiro, Trae, Antigravity,
/// CLIProxyAPI) as ProviderConnection rows — same Data shape as OAuthService.TokenData
/// (accessToken / refreshToken / expiresAt / email / providerSpecificData).
public static class OAuthImportEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static string Now() => DateTime.UtcNow.ToString("O");
    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ConnId() => Guid.NewGuid().ToString("N")[..12];

    private static async Task<ProviderConnection> UpsertAsync(
        LlmRouterDbContext db, string provider, string? connectionId, string name,
        JsonObject data, string authType = "oauth")
    {
        ProviderConnection? conn = connectionId is not null
            ? await db.ProviderConnections.FindAsync(connectionId)
            : await db.ProviderConnections.FirstOrDefaultAsync(c =>
                c.Provider == provider && c.Name == name);
        if (conn is null)
        {
            conn = new ProviderConnection
            {
                Id = ConnId(), Provider = provider, Name = name,
                IsActive = true, AuthType = authType, Data = "{}",
                CreatedAt = Now(), UpdatedAt = Now(),
            };
            db.ProviderConnections.Add(conn);
        }
        conn.Name = name;
        conn.AuthType = authType;
        conn.Data = data.ToJsonString();
        conn.IsActive = true;
        conn.UpdatedAt = Now();
        return conn;
    }

    private static JsonObject TokenData(JsonElement tokens)
    {
        var d = new JsonObject();
        string? S(JsonElement e, string k) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        if (S(tokens, "access_token") is { } at) d["accessToken"] = at;
        if (S(tokens, "refresh_token") is { } rt) d["refreshToken"] = rt;
        if (S(tokens, "id_token") is { } it) d["idToken"] = it;
        if (S(tokens, "expires_at") is { } ea) d["expiresAt"] = ea;
        if (S(tokens, "expiry_date") is { } ed) d["expiresAt"] = ed;
        if (S(tokens, "account_id") is { } aid) d["accountId"] = aid;
        return d;
    }

    // Flatten Codex auth.json shape: {auth_mode, OPENAI_API_KEY, tokens:{access_token,refresh_token,...}}
    private static JsonObject NormalizeCodexRecord(JsonElement rec)
    {
        if (rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty("tokens", out var t)
            && t.ValueKind == JsonValueKind.Object)
        {
            var merged = JsonNode.Parse(rec.GetRawText())!.AsObject();
            foreach (var kv in JsonNode.Parse(t.GetRawText())!.AsObject())
                merged[kv.Key] = kv.Value?.DeepClone();
            rec = JsonSerializer.Deserialize<JsonElement>(merged.ToJsonString());
        }
        var d = TokenData(rec);
        if (rec.ValueKind == JsonValueKind.Object)
        {
            if (rec.TryGetProperty("OPENAI_API_KEY", out var k) && k.ValueKind == JsonValueKind.String)
                d["apiKey"] = k.GetString();
            if (rec.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String)
                d["email"] = em.GetString();
            if (rec.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                d["connName"] = nm.GetString();
        }
        return d;
    }

    public static void MapOAuthImportEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---------- paste-credentials: `omniroute-cred-v1.<base64url>` blob ----------
        g.MapPost("/oauth/{provider}/paste-credentials", async (
            string provider, HttpContext ctx, LlmRouterDbContext db, ProviderRegistry registry) =>
        {
            if (registry.GetProvider(provider) is null && await NodeResolver.ResolveAsync(db, provider) is null)
                return Results.NotFound(new { error = "unknown provider" });
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(JsonOpts); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
            if (body.ValueKind != JsonValueKind.Object
                || !body.TryGetProperty("blob", out var bl) || bl.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "missing blob" });
            const string prefix = "omniroute-cred-v1.";
            var blob = bl.GetString()!;
            if (!blob.StartsWith(prefix))
                return Results.BadRequest(new { error = $"invalid blob — must start with \"{prefix}\"" });
            JsonElement parsed;
            try
            {
                var b64 = blob[prefix.Length..].Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(b64, "^[A-Za-z0-9_-]+$"))
                    return Results.BadRequest(new { error = "invalid blob payload — not base64url" });
                var pad = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=').Replace('-', '+').Replace('_', '/');
                parsed = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(pad));
            }
            catch { return Results.BadRequest(new { error = "invalid blob payload" }); }
            if (!parsed.TryGetProperty("v", out var v) || v.GetInt32() != 1)
                return Results.BadRequest(new { error = "unsupported blob version" });
            if (!parsed.TryGetProperty("provider", out var bp) || bp.GetString()?.Trim() != provider)
                return Results.BadRequest(new { error = $"blob provider does not match \"{provider}\"" });
            if (!parsed.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object
                || !tokens.TryGetProperty("access_token", out _))
                return Results.BadRequest(new { error = "blob missing tokens.access_token" });
            var data = TokenData(tokens);
            string? cid = body.TryGetProperty("connectionId", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            var email = parsed.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (email is not null) data["email"] = email;
            var name = parsed.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                ? nm.GetString()! : $"{provider}-pasted";
            var conn = await UpsertAsync(db, provider, cid, name, data);
            await db.SaveChangesAsync();
            return Results.Json(new { connection = new { conn.Id, conn.Provider, conn.Name } }, JsonOpts);
        });

        // ---------- codex/import: bulk {accounts: obj|[]} ----------
        g.MapPost("/oauth/codex/import", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(JsonOpts); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
            if (!body.TryGetProperty("accounts", out var acc)) return Results.BadRequest(new { error = "missing accounts" });
            var items = acc.ValueKind == JsonValueKind.Array ? acc.EnumerateArray().ToList() : new List<JsonElement> { acc };
            var results = new List<object>();
            var imported = 0;
            var idx = 0;
            foreach (var rec in items)
            {
                idx++;
                try
                {
                    if (rec.ValueKind != JsonValueKind.Object) { results.Add(new { index = idx, ok = false, error = "not an object" }); continue; }
                    var d = NormalizeCodexRecord(rec);
                    if (!d.ContainsKey("accessToken") && !d.ContainsKey("apiKey"))
                    { results.Add(new { index = idx, ok = false, error = "no access_token/OPENAI_API_KEY" }); continue; }
                    var name = d["connName"]?.GetValue<string>()
                        ?? d["email"]?.GetValue<string>()
                        ?? $"codex-imported-{idx}";
                    d.Remove("connName");
                    await UpsertAsync(db, "codex", null, name, d);
                    imported++;
                    results.Add(new { index = idx, ok = true, name });
                }
                catch (Exception ex) { results.Add(new { index = idx, ok = false, error = ex.Message }); }
            }
            await db.SaveChangesAsync();
            return Results.Json(new { imported, total = items.Count, results }, JsonOpts);
        });

        // ---------- codex/import-token: {accessToken,name?} or {session: "<auth.json>"} ----------
        g.MapPost("/oauth/codex/import-token", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(JsonOpts); }
            catch { return Results.BadRequest(new { error = "Invalid or empty JSON body" }); }
            JsonObject d;
            string? name = null;
            if (body.TryGetProperty("session", out var sess) && sess.ValueKind == JsonValueKind.String)
            {
                JsonElement rec;
                try { rec = JsonSerializer.Deserialize<JsonElement>(sess.GetString()!); }
                catch { return Results.BadRequest(new { error = "session is not valid JSON" }); }
                d = NormalizeCodexRecord(rec);
            }
            else if (body.TryGetProperty("accessToken", out var at) && at.ValueKind == JsonValueKind.String)
            {
                d = new JsonObject { ["accessToken"] = at.GetString() };
                if (body.TryGetProperty("refreshToken", out var rt) && rt.ValueKind == JsonValueKind.String)
                    d["refreshToken"] = rt.GetString();
            }
            else return Results.BadRequest(new { error = "provide accessToken or session" });
            if (body.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
            name ??= d["email"]?.GetValue<string>() ?? $"codex-{DateTime.UtcNow:yyyyMMddHHmmss}";
            d.Remove("connName");
            var conn = await UpsertAsync(db, "codex", null, name, d);
            await db.SaveChangesAsync();
            return Results.Json(new { connection = new { conn.Id, conn.Provider, conn.Name } }, JsonOpts);
        });

        // ---------- cliproxy-import: scan ~/.cli-proxy-api ----------
        var cliproxyDir = () => Environment.GetEnvironmentVariable("CLIPROXYAPI_CONFIG_DIR")
            ?? Path.Combine(Home(), ".cli-proxy-api");
        (List<JsonObject> cands, int scanned, List<string> skipped) ScanCliProxy()
        {
            var cands = new List<JsonObject>(); var skipped = new List<string>(); var scanned = 0;
            var dir = cliproxyDir();
            if (!Directory.Exists(dir)) return (cands, scanned, skipped);
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                scanned++;
                try
                {
                    var rec = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(f));
                    if (rec.ValueKind != JsonValueKind.Object) { skipped.Add(Path.GetFileName(f)); continue; }
                    var provider = rec.TryGetProperty("provider", out var pp) ? pp.GetString()
                        : rec.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    if (string.IsNullOrEmpty(provider)) { skipped.Add(Path.GetFileName(f)); continue; }
                    var d = TokenData(rec);
                    if (!d.ContainsKey("accessToken") && !d.ContainsKey("refreshToken")) { skipped.Add(Path.GetFileName(f)); continue; }
                    var email = rec.TryGetProperty("email", out var em) ? em.GetString() : null;
                    if (email is not null) d["email"] = email;
                    cands.Add(new JsonObject
                    {
                        ["provider"] = provider.ToLowerInvariant(),
                        ["type"] = rec.TryGetProperty("type", out var tt) ? tt.GetString() : "oauth",
                        ["email"] = email,
                        ["name"] = email ?? Path.GetFileNameWithoutExtension(f),
                        ["data"] = d,
                    });
                }
                catch { skipped.Add(Path.GetFileName(f)); }
            }
            return (cands, scanned, skipped);
        }

        g.MapGet("/oauth/cliproxy-import", () =>
        {
            var (cands, scanned, skipped) = ScanCliProxy();
            return Results.Json(new
            {
                dir = cliproxyDir(), scanned, skipped,
                accounts = cands.Select(c => new
                {
                    provider = c["provider"]!.GetValue<string>(),
                    type = c["type"]?.GetValue<string>(),
                    email = c["email"]?.GetValue<string>(),
                }),
            }, JsonOpts);
        });

        g.MapPost("/oauth/cliproxy-import", async (LlmRouterDbContext db) =>
        {
            var (cands, scanned, skipped) = ScanCliProxy();
            var imported = 0;
            var results = new List<object>();
            foreach (var c in cands)
            {
                var provider = c["provider"]!.GetValue<string>();
                var email = c["email"]?.GetValue<string>();
                try
                {
                    await UpsertAsync(db, provider, null, c["name"]!.GetValue<string>(), (JsonObject)c["data"]!);
                    imported++;
                    results.Add(new { provider, email, ok = true });
                }
                catch (Exception ex) { results.Add(new { provider, email, ok = false, error = ex.Message }); }
            }
            await db.SaveChangesAsync();
            return Results.Json(new { scanned, skipped, imported, results }, JsonOpts);
        });

        // ---------- cursor: auto-import (scan local creds) + import (persist) ----------
        g.MapPost("/oauth/cursor/auto-import", () =>
        {
            // Cursor IDE: state.vscdb sqlite — keys cursorAuth/accessToken|refreshToken + storage machineId
            var vscdb = Path.Combine(Home(), ".config", "Cursor", "User", "globalStorage", "state.vscdb");
            if (File.Exists(vscdb))
            {
                try
                {
                    using var cn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={vscdb};Mode=ReadOnly");
                    cn.Open();
                    string? Get(string k)
                    {
                        using var cmd = cn.CreateCommand();
                        cmd.CommandText = "SELECT value FROM ItemTable WHERE key=$k";
                        cmd.Parameters.AddWithValue("$k", k);
                        return cmd.ExecuteScalar() as string;
                    }
                    var at = Get("cursorAuth/accessToken");
                    if (at is not null)
                        return Results.Json(new
                        {
                            found = true, accessToken = at,
                            refreshToken = Get("cursorAuth/refreshToken"),
                            machineId = Get("storage.machineId") ?? Get("telemetry.machineId"),
                            source = "cursor-ide",
                        }, JsonOpts);
                }
                catch { /* fall through to CLI auth */ }
            }
            var cliAuth = Path.Combine(Home(), ".cursor", "cli-auth.json");
            if (File.Exists(cliAuth))
            {
                try
                {
                    var rec = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(cliAuth));
                    if (rec.TryGetProperty("accessToken", out var at) || rec.TryGetProperty("access_token", out at))
                        return Results.Json(new { found = true, accessToken = at.GetString(), source = "cursor-agent" }, JsonOpts);
                }
                catch { }
            }
            return Results.Json(new { found = false, error = "No Cursor credentials found. Install Cursor IDE or login with cursor-agent." }, JsonOpts);
        });

        g.MapPost("/oauth/cursor/import", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(JsonOpts); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
            string? Get(string k) => body.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var at = Get("accessToken");
            if (at is null) return Results.BadRequest(new { error = "missing accessToken" });
            var d = new JsonObject { ["accessToken"] = at };
            if (Get("refreshToken") is { } rt) d["refreshToken"] = rt;
            if (Get("machineId") is { } mid) d["machineId"] = mid;
            if (Get("email") is { } em) d["email"] = em;
            var conn = await UpsertAsync(db, "cursor", null, Get("name") ?? Get("email") ?? "cursor-imported", d);
            await db.SaveChangesAsync();
            return Results.Json(new { connection = new { conn.Id, conn.Provider, conn.Name } }, JsonOpts);
        });

        // ---------- kiro/api-key?targetProvider=kiro|amazon-q ----------
        g.MapPost("/oauth/kiro/api-key", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var target = ctx.Request.Query["targetProvider"].FirstOrDefault() == "amazon-q" ? "amazon-q" : "kiro";
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(JsonOpts); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
            var apiKey = body.TryGetProperty("apiKey", out var k) ? k.GetString() : null;
            if (string.IsNullOrWhiteSpace(apiKey)) return Results.BadRequest(new { error = "missing apiKey" });
            var region = body.TryGetProperty("region", out var r) ? r.GetString() : null;
            region = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region;
            var d = new JsonObject
            {
                ["apiKey"] = apiKey,
                ["accessToken"] = apiKey,
                // long-lived key: future timestamp so health paths don't flag as expired
                ["expiresAt"] = DateTime.UtcNow.AddDays(365).ToString("O"),
                ["providerSpecificData"] = new JsonObject
                {
                    ["region"] = region, ["authMethod"] = "api_key", ["provider"] = "API Key",
                },
            };
            var tail = apiKey.Length >= 6 ? apiKey[^6..] : apiKey;
            var conn = await UpsertAsync(db, target, null, $"{target}-{region}-{tail}", d, "api-key");
            await db.SaveChangesAsync();
            return Results.Json(new { connection = new { conn.Id, conn.Provider, conn.Name } }, JsonOpts);
        });

        // ---------- trae/import {refreshToken,userRegion?} ----------
        g.MapPost("/oauth/trae/import", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(JsonOpts); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
            var rt = body.TryGetProperty("refreshToken", out var r) ? r.GetString() : null;
            if (string.IsNullOrWhiteSpace(rt)) return Results.BadRequest(new { error = "missing refreshToken" });
            var region = body.TryGetProperty("userRegion", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() : "US";
            var d = new JsonObject
            {
                ["refreshToken"] = rt,
                ["providerSpecificData"] = new JsonObject { ["userRegion"] = region },
            };
            var conn = await UpsertAsync(db, "trae", null, $"trae-{region!.ToLowerInvariant()}", d);
            await db.SaveChangesAsync();
            return Results.Json(new { connection = new { conn.Id, conn.Provider, conn.Name } }, JsonOpts);
        });

        // ---------- providers/agy-auth: antigravity CLI creds ----------
        var agyPath = () => Path.Combine(Home(), ".gemini", "antigravity-cli", "antigravity-oauth-token");
        JsonObject AgyData(JsonElement b)
        {
            var d = new JsonObject();
            string? Get(string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Get("accessToken") is { } at) d["accessToken"] = at;
            if (Get("refreshToken") is { } rt) d["refreshToken"] = rt;
            if (Get("expiresAt") is { } ea) d["expiresAt"] = ea;
            if (Get("email") is { } em) d["email"] = em;
            return d;
        }

        g.MapPost("/providers/agy-auth/import", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(JsonOpts); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
            var d = AgyData(body);
            if (!d.ContainsKey("accessToken") && !d.ContainsKey("refreshToken"))
                return Results.BadRequest(new { error = "missing accessToken/refreshToken" });
            var name = body.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()! : d["email"]?.GetValue<string>() ?? "antigravity-imported";
            var conn = await UpsertAsync(db, "antigravity", null, name, d);
            await db.SaveChangesAsync();
            return Results.Json(new { connection = new { conn.Id, conn.Provider, conn.Name } }, JsonOpts);
        });

        g.MapPost("/providers/agy-auth/import-bulk", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(JsonOpts); }
            catch { return Results.BadRequest(new { error = "Invalid JSON body" }); }
            if (!body.TryGetProperty("accounts", out var acc) || acc.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "missing accounts" });
            var imported = 0; var idx = 0; var results = new List<object>();
            foreach (var rec in acc.EnumerateArray())
            {
                idx++;
                try
                {
                    var d = AgyData(rec);
                    if (!d.ContainsKey("accessToken") && !d.ContainsKey("refreshToken"))
                    { results.Add(new { index = idx, ok = false, error = "missing tokens" }); continue; }
                    var name = rec.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString()! : d["email"]?.GetValue<string>() ?? $"antigravity-{idx}";
                    await UpsertAsync(db, "antigravity", null, name, d);
                    imported++;
                    results.Add(new { index = idx, ok = true, name });
                }
                catch (Exception ex) { results.Add(new { index = idx, ok = false, error = ex.Message }); }
            }
            await db.SaveChangesAsync();
            return Results.Json(new { imported, results }, JsonOpts);
        });

        // apply-local: write connection's accessToken to ~/.gemini/antigravity-cli/antigravity-oauth-token
        g.MapPost("/providers/agy-auth/apply-local", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var name = ctx.Request.Query["name"].FirstOrDefault();
            var conn = name is not null
                ? await db.ProviderConnections.FirstOrDefaultAsync(c => c.Provider == "antigravity" && c.Name == name)
                : await db.ProviderConnections.Where(c => c.Provider == "antigravity" && c.IsActive)
                    .OrderByDescending(c => c.UpdatedAt).FirstOrDefaultAsync();
            if (conn is null) return Results.NotFound(new { error = "no antigravity connection" });
            var data = JsonNode.Parse(conn.Data)?.AsObject();
            var at = data?["accessToken"]?.GetValue<string>();
            if (at is null) return Results.BadRequest(new { error = "connection has no accessToken" });
            var path = agyPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { accessToken = at, appliedAt = Now() }));
            return Results.Json(new { ok = true, path, connection = conn.Name }, JsonOpts);
        });
    }
}

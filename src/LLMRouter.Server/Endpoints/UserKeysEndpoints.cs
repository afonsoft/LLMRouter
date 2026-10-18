using System.Security.Claims;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-111: admin de usuários + virtual keys + ledger de crédito (cookie),
/// e self-serve estilo OpenRouter em /api/account/* autenticado por vk-…
/// (chave admin com UserId null recebe 403 no self-serve).
/// </summary>
public static class UserKeysEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static string Now() => DateTime.UtcNow.ToString("O");

    private static string Mask(string key) =>
        key.Length <= 8 ? "vk-…" : $"{key[..5]}…{key[^2..]}";

    private static object KeyDto(UserKey k) => new
    {
        k.Id, k.UserId, key = Mask(k.Key), k.Name, k.ModelsAllow, k.BudgetUsd,
        k.BudgetPeriod, k.RateLimitGroupId, k.IsActive, k.SpendUsd, k.CreatedAt,
    };

    private static async Task<double> Balance(LlmRouterDbContext db, string userId)
    {
        var last = await db.CreditLedger.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        return last?.BalanceAfter ?? 0;
    }

    private static object UserDto(User u, double balance) => new
    {
        u.Id, u.Email, u.Name, u.Role, u.IsActive, u.CreatedAt, u.Plan,
        hasPassword = u.PasswordHash != null, u.OidcSubject, u.OidcIssuer, balance,
    };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();
        var ip = (HttpContext c) => c.Connection.RemoteIpAddress?.ToString();

        // ---------- admin: users ----------
        g.MapGet("/users", async (LlmRouterDbContext db) =>
        {
            var users = await db.Users.AsNoTracking().ToListAsync();
            var list = new List<object>();
            foreach (var u in users) list.Add(UserDto(u, await Balance(db, u.Id)));
            return Results.Json(new { users = list }, JsonOpts);
        });

        g.MapPost("/users", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var email = b.TryGetProperty("email", out var e) ? e.GetString() : null;
            if (string.IsNullOrWhiteSpace(email))
                return Results.BadRequest(new { error = "email obrigatório" });
            var u = new User
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Email = email.Trim(),
                Name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Role = b.TryGetProperty("role", out var r) ? r.GetString() ?? "member" : "member",
                CreatedAt = Now(),
            };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            await AuditLog.RecordAsync(db, "admin", "user.create", u.Id, new { u.Email }, ip(ctx));
            return Results.Json(UserDto(u, 0), JsonOpts, statusCode: 201);
        });

        g.MapGet("/users/{id}", async (string id, LlmRouterDbContext db) =>
            await db.Users.FindAsync(id) is { } u
                ? Results.Json(UserDto(u, await Balance(db, u.Id)), JsonOpts)
                : Results.NotFound(new { error = "user não encontrado" }));

        g.MapPatch("/users/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            if (await db.Users.FindAsync(id) is not { } u)
                return Results.NotFound(new { error = "user não encontrado" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("email", out var e)) u.Email = e.GetString() ?? u.Email;
            if (b.TryGetProperty("name", out var n)) u.Name = n.GetString() ?? u.Name;
            if (b.TryGetProperty("role", out var r)) u.Role = r.GetString() ?? u.Role;
            if (b.TryGetProperty("isActive", out var a)) u.IsActive = a.GetBoolean();
            // SPEC-144: admin sets billing plan (free|standard)
            if (b.TryGetProperty("plan", out var p) && p.GetString() is { Length: > 0 } plan
                && plan is "free" or "standard") u.Plan = plan;
            await db.SaveChangesAsync();
            await AuditLog.RecordAsync(db, "admin", "user.update", id, null, ip(ctx));
            return Results.Json(UserDto(u, await Balance(db, id)), JsonOpts);
        });

        g.MapDelete("/users/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            if (await db.Users.FindAsync(id) is not { } u)
                return Results.NotFound(new { error = "user não encontrado" });
            u.IsActive = false;
            foreach (var k in await db.UserKeys.Where(x => x.UserId == id).ToListAsync())
                Core.Gateway.HotReads.InvalidateUserKey(k.Key);
            await db.SaveChangesAsync();
            await AuditLog.RecordAsync(db, "admin", "user.deactivate", id, null, ip(ctx));
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---------- admin: user keys ----------
        g.MapGet("/user-keys", async (string? userId, LlmRouterDbContext db) =>
        {
            var q = db.UserKeys.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(userId)) q = q.Where(k => k.UserId == userId);
            var keys = (await q.ToListAsync()).Select(KeyDto);
            return Results.Json(new { keys }, JsonOpts);
        });

        g.MapPost("/user-keys", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var userId = b.TryGetProperty("userId", out var uid) ? uid.GetString() : null;
            if (string.IsNullOrEmpty(userId) || await db.Users.FindAsync(userId) is null)
                return Results.BadRequest(new { error = "userId inválido" });
            var k = new UserKey
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                UserId = userId,
                Key = $"vk-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}",
                Name = b.TryGetProperty("name", out var n) ? n.GetString() : null,
                ModelsAllow = b.TryGetProperty("modelsAllow", out var ma) ? ma.GetRawText() : "[]",
                BudgetUsd = b.TryGetProperty("budgetUsd", out var bu) ? bu.GetDouble() : 0,
                BudgetPeriod = b.TryGetProperty("budgetPeriod", out var bp) ? bp.GetString() ?? "none" : "none",
                RateLimitGroupId = b.TryGetProperty("rateLimitGroupId", out var rg) ? rg.GetString() : null,
                CreatedAt = Now(),
            };
            db.UserKeys.Add(k);
            await db.SaveChangesAsync();
            await AuditLog.RecordAsync(db, "admin", "userkey.create", k.Id, new { userId }, ip(ctx));
            // chave completa retornada uma única vez
            return Results.Json(new { key = k.Key, item = KeyDto(k) }, JsonOpts, statusCode: 201);
        });

        g.MapPatch("/user-keys/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            if (await db.UserKeys.FindAsync(id) is not { } k)
                return Results.NotFound(new { error = "key não encontrada" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out var n)) k.Name = n.GetString();
            if (b.TryGetProperty("modelsAllow", out var ma)) k.ModelsAllow = ma.GetRawText();
            if (b.TryGetProperty("budgetUsd", out var bu)) k.BudgetUsd = bu.GetDouble();
            if (b.TryGetProperty("budgetPeriod", out var bp)) k.BudgetPeriod = bp.GetString() ?? k.BudgetPeriod;
            if (b.TryGetProperty("rateLimitGroupId", out var rg)) k.RateLimitGroupId = rg.GetString();
            if (b.TryGetProperty("isActive", out var a)) k.IsActive = a.GetBoolean();
            await db.SaveChangesAsync();
            Core.Gateway.HotReads.InvalidateUserKey(k.Key);
            await AuditLog.RecordAsync(db, "admin", "userkey.update", id, null, ip(ctx));
            return Results.Json(KeyDto(k), JsonOpts);
        });

        g.MapDelete("/user-keys/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            if (await db.UserKeys.FindAsync(id) is not { } k)
                return Results.NotFound(new { error = "key não encontrada" });
            k.IsActive = false;
            await db.SaveChangesAsync();
            Core.Gateway.HotReads.InvalidateUserKey(k.Key);
            await AuditLog.RecordAsync(db, "admin", "userkey.deactivate", id, null, ip(ctx));
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---------- admin: credit ledger ----------
        g.MapGet("/credit-ledger", async (string? userId, int? limit, LlmRouterDbContext db) =>
        {
            var q = db.CreditLedger.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(userId)) q = q.Where(r => r.UserId == userId);
            var rows = await q.OrderByDescending(r => r.Id).Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync();
            return Results.Json(new { entries = rows }, JsonOpts);
        });

        g.MapPost("/credit-ledger/topup", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var userId = b.TryGetProperty("userId", out var uid) ? uid.GetString() : null;
            var amount = b.TryGetProperty("amount", out var am) ? am.GetDouble() : 0;
            if (string.IsNullOrEmpty(userId) || await db.Users.FindAsync(userId) is null)
                return Results.BadRequest(new { error = "userId inválido" });
            if (amount <= 0)
                return Results.BadRequest(new { error = "amount deve ser > 0" });
            var bal = await Balance(db, userId) + amount;
            var row = new CreditLedgerRow
            {
                UserId = userId, Delta = amount,
                Reason = b.TryGetProperty("reason", out var rr) ? rr.GetString() ?? "topup" : "topup",
                BalanceAfter = bal, CreatedAt = Now(),
            };
            db.CreditLedger.Add(row);
            await db.SaveChangesAsync();
            await AuditLog.RecordAsync(db, "admin", "credit.topup", userId, new { amount, bal }, ip(ctx));
            return Results.Json(row, JsonOpts, statusCode: 201);
        });

        // ---------- self-serve /api/account/* (auth por vk, UserId obrigatório) ----------
        var self = app.MapGroup("/api/account");

        async Task<(User? U, UserKey? Uk, IResult? Reject)> Self(HttpContext ctx, LlmRouterDbContext db)
        {
            // SPEC-135: cookie session carrying a userId claim (email+password login) counts as the user
            if (ctx.User.FindFirst("userId")?.Value is { Length: > 0 } uid
                && await db.Users.FindAsync(uid) is { } cu)
                return (cu, null, null);
            var auth = await GatewayEndpoints.AuthenticatedKey(ctx, db);
            if (!auth.Authed || auth.UserId is null)
                return (null, null, Results.Json(new { error = new { message = "virtual key required", type = "forbidden" } }, JsonOpts, statusCode: 403));
            var u = await db.Users.FindAsync(auth.UserId);
            return u is null ? (null, null, Results.NotFound(new { error = "user não encontrado" })) : (u, auth.UserKey, null);
        }

        self.MapGet("/me", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var (u, uk, reject) = await Self(ctx, db);
            if (reject is not null) return reject;
            return Results.Json(new
            {
                user = UserDto(u!, await Balance(db, u!.Id)),
                key = uk is null ? null : new { uk.Id, uk.Name, uk.BudgetUsd, uk.BudgetPeriod, uk.SpendUsd },
            }, JsonOpts);
        });

        self.MapGet("/me/usage", async (int? days, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var (u, _, reject) = await Self(ctx, db);
            if (reject is not null) return reject;
            var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 30, 1, 365)).ToString("O");
            var rows = await db.UsageHistory.AsNoTracking()
                .Where(r => r.UserId == u!.Id && string.Compare(r.Timestamp, since) > 0)
                .OrderByDescending(r => r.Id).Take(500).ToListAsync();
            return Results.Json(new { usage = rows }, JsonOpts);
        });

        // SPEC-141: CSV export of the user's own generations (same 500-row window)
        self.MapGet("/me/usage.csv", async (int? days, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var (u, _, reject) = await Self(ctx, db);
            if (reject is not null) return reject;
            var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 30, 1, 365)).ToString("O");
            var rows = await db.UsageHistory.AsNoTracking()
                .Where(r => r.UserId == u!.Id && string.Compare(r.Timestamp, since) > 0)
                .OrderByDescending(r => r.Id).Take(500).ToListAsync();
            var sb = new System.Text.StringBuilder("timestamp,model,provider,prompt_tokens,completion_tokens,cost_usd,status,latency_ms\n");
            foreach (var r in rows)
                sb.AppendLine($"{r.Timestamp},{Esc(r.Model)},{Esc(r.Provider)},{r.PromptTokens},{r.CompletionTokens},{r.Cost:F6},{r.Status},{r.LatencyMs}");
            return Results.Text(sb.ToString(), "text/csv");
            static string Esc(string? s) => s is null ? "" : $"\"{s.Replace("\"", "\"\"")}\"";
        });

        self.MapGet("/me/ledger", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var (u, _, reject) = await Self(ctx, db);
            if (reject is not null) return reject;
            var rows = await db.CreditLedger.AsNoTracking()
                .Where(r => r.UserId == u!.Id).OrderByDescending(r => r.Id).Take(500).ToListAsync();
            return Results.Json(new { entries = rows, balance = rows.FirstOrDefault()?.BalanceAfter ?? 0 }, JsonOpts);
        });

        self.MapGet("/me/keys", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var (u, _, reject) = await Self(ctx, db);
            if (reject is not null) return reject;
            var keys = (await db.UserKeys.AsNoTracking().Where(k => k.UserId == u!.Id).ToListAsync()).Select(KeyDto);
            return Results.Json(new { keys }, JsonOpts);
        });

        self.MapPost("/me/keys", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var (u, _, reject) = await Self(ctx, db);
            if (reject is not null) return reject;
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var k = new UserKey
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                UserId = u!.Id,
                Key = $"vk-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}",
                Name = b.TryGetProperty("name", out var n) ? n.GetString() : null,
                ModelsAllow = b.TryGetProperty("modelsAllow", out var ma) ? ma.GetRawText() : "[]",
                BudgetUsd = b.TryGetProperty("budgetUsd", out var bu) ? bu.GetDouble() : 0,
                BudgetPeriod = b.TryGetProperty("budgetPeriod", out var bp) ? bp.GetString() ?? "none" : "none",
                CreatedAt = Now(),
            };
            db.UserKeys.Add(k);
            await db.SaveChangesAsync();
            await AuditLog.RecordAsync(db, u.Email, "userkey.self-create", k.Id, null, ip(ctx));
            return Results.Json(new { key = k.Key, item = KeyDto(k) }, JsonOpts, statusCode: 201);
        });

        self.MapPatch("/me/keys/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var (u, _, reject) = await Self(ctx, db);
            if (reject is not null) return reject;
            if (await db.UserKeys.FindAsync(id) is not { } k || k.UserId != u!.Id)
                return Results.NotFound(new { error = "key não encontrada" });
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (b.TryGetProperty("name", out var n)) k.Name = n.GetString();
            if (b.TryGetProperty("modelsAllow", out var ma)) k.ModelsAllow = ma.GetRawText();
            // SPEC-138: OpenRouter-style "edit limit" — budget fields are mutable
            if (b.TryGetProperty("budgetUsd", out var bud) && bud.ValueKind == JsonValueKind.Number)
                k.BudgetUsd = Math.Max(0, bud.GetDouble());
            if (b.TryGetProperty("budgetPeriod", out var bper) && bper.ValueKind == JsonValueKind.String)
                k.BudgetPeriod = bper.GetString() is { Length: > 0 } p ? p : k.BudgetPeriod;
            if (b.TryGetProperty("isActive", out var a)) k.IsActive = a.GetBoolean();
            await db.SaveChangesAsync();
            Core.Gateway.HotReads.InvalidateUserKey(k.Key);
            await AuditLog.RecordAsync(db, u.Email, "userkey.self-update", id, null, ip(ctx));
            return Results.Json(KeyDto(k), JsonOpts);
        });

        self.MapDelete("/me/keys/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var (u, _, reject) = await Self(ctx, db);
            if (reject is not null) return reject;
            if (await db.UserKeys.FindAsync(id) is not { } k || k.UserId != u!.Id)
                return Results.NotFound(new { error = "key não encontrada" });
            k.IsActive = false;
            await db.SaveChangesAsync();
            Core.Gateway.HotReads.InvalidateUserKey(k.Key);
            await AuditLog.RecordAsync(db, u.Email, "userkey.self-deactivate", id, null, ip(ctx));
            return Results.Json(new { ok = true }, JsonOpts);
        });
    }
}

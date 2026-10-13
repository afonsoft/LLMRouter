using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>Dashboard auth — mirrors upstream /api/auth/* + /api/settings/require-login.</summary>
public static class AuthEndpoints
{
    private const int Iterations = 100_000;

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/settings/require-login", async (LlmRouterDbContext db) =>
        {
            var s = await db.Settings.FirstOrDefaultAsync();
            var data = Parse(s?.Data);
            var requireLogin = GetBool(data, "requireLogin", true);
            var setupComplete = GetBool(data, "setupComplete", false);
            return Results.Json(new { requireLogin, setupComplete });
        });

        g.MapPost("/auth/login", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var password = body.TryGetProperty("password", out var p) ? p.GetString() : null;
            var email = body.TryGetProperty("email", out var em) ? em.GetString()?.Trim() : null;

            // SPEC-135: user login — email+password against User.PasswordHash (pbkdf2, same format)
            if (!string.IsNullOrEmpty(email))
            {
                var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email && x.IsActive);
                if (u?.PasswordHash is not null && VerifyPassword(u.PasswordHash, password))
                {
                    var claims = new[]
                    {
                        new Claim(ClaimTypes.Name, u.Email),
                        new Claim("userId", u.Id),
                        new Claim(ClaimTypes.Role, u.Role),
                    };
                    await ctx.SignInAsync("cookie", new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie")));
                    await Core.Extras.AuditLog.RecordAsync(db, u.Email, "auth.login.user", u.Id, null,
                        ctx.Connection.RemoteIpAddress?.ToString());
                    return Results.Json(new { success = true, userId = u.Id, role = u.Role });
                }
                await Core.Extras.AuditLog.RecordAsync(db, email, "auth.login.fail",
                    ip: ctx.Connection.RemoteIpAddress?.ToString());
                return Results.Json(new { success = false, error = "Invalid email or password" }, statusCode: 401);
            }

            var s = await db.Settings.FirstOrDefaultAsync();
            var data = Parse(s?.Data);
            var requireLogin = GetBool(data, "requireLogin", true);

            if (!requireLogin || CheckPassword(data, password))
            {
                var claims = new[] { new Claim(ClaimTypes.Name, "admin") };
                await ctx.SignInAsync("cookie",
                    new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie")));
                // mark setup complete on first successful login
                if (!GetBool(data, "setupComplete", false))
                {
                    s ??= db.Settings.Add(new LLMRouter.Core.Data.SettingRow { Id = 1, Data = "{}" }).Entity;
                    var d = data.Deserialize<Dictionary<string, JsonElement>>() ?? [];
                    d["setupComplete"] = JsonSerializer.SerializeToElement(true);
                    s.Data = JsonSerializer.Serialize(d);
                    await db.SaveChangesAsync();
                }
                await Core.Extras.AuditLog.RecordAsync(db, "dashboard", "auth.login",
                    ip: ctx.Connection.RemoteIpAddress?.ToString());
                return Results.Json(new { success = true });
            }
            await Core.Extras.AuditLog.RecordAsync(db, "anonymous", "auth.login.fail",
                ip: ctx.Connection.RemoteIpAddress?.ToString());
            return Results.Json(new { success = false, error = "Invalid password" }, statusCode: 401);
        });

        g.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync("cookie");
            return Results.Json(new { success = true });
        });

        g.MapGet("/auth/session", (HttpContext ctx) =>
            Results.Json(new { authenticated = ctx.User.Identity?.IsAuthenticated == true }));

        g.MapPost("/auth/change-password", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var newPw = body.TryGetProperty("newPassword", out var np) ? np.GetString() : null;
            if (string.IsNullOrEmpty(newPw)) return Results.BadRequest(new { error = "newPassword required" });
            var s = await db.Settings.FirstOrDefaultAsync() ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
            var d = Parse(s.Data).Deserialize<Dictionary<string, JsonElement>>() ?? [];
            d["adminPasswordHash"] = JsonSerializer.SerializeToElement(HashPassword(newPw));
            s.Data = JsonSerializer.Serialize(d);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true });
        });
    }

    internal static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    internal static bool VerifyPassword(string encoded, string? password)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iters)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iters, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    internal static bool CheckPassword(JsonElement data, string? password)
    {
        if (!data.TryGetProperty("adminPasswordHash", out var h) || h.ValueKind != JsonValueKind.String)
            return !string.IsNullOrEmpty(password); // first-run: any non-empty password becomes the credential
        return VerifyPassword(h.GetString()!, password);
    }

    internal static JsonElement Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return JsonSerializer.SerializeToElement(new { });
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return JsonSerializer.SerializeToElement(new { }); }
    }

    internal static bool GetBool(JsonElement el, string name, bool fallback) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;
}

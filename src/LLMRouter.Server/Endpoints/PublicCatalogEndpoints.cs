using System.Collections.Concurrent;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-112: superfície pública estilo OpenRouter — catálogo de modelos com
/// preço de venda (cost × markup ou "sell" explícito) e signup→key em uma
/// chamada. Ambos fora do RequireAuthorization: o catálogo é gated por
/// settings.publicCatalog (default true) e o signup por
/// settings.signup.enabled (default false).
/// </summary>
public static class PublicCatalogEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, Queue<long>> SignupIps = new();

    private static string Now() => DateTime.UtcNow.ToString("O");

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>settings.markup {defaultPercent, perModel{"provider/model": percent}}.</summary>
    private static double MarkupPercent(JsonElement sdata, string id)
    {
        if (sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty("markup", out var mk) && mk.ValueKind == JsonValueKind.Object)
        {
            if (mk.TryGetProperty("perModel", out var pm) && pm.ValueKind == JsonValueKind.Object
                && pm.TryGetProperty(id, out var pp) && pp.ValueKind == JsonValueKind.Number)
                return pp.GetDouble();
            if (mk.TryGetProperty("defaultPercent", out var dp) && dp.ValueKind == JsonValueKind.Number)
                return dp.GetDouble();
        }
        return 0;
    }

    /// <summary>Preço de venda USD/1M tokens: "sell" explícito vence markup.</summary>
    private static (double input, double output)? SellPrice(
        JsonElement sdata, ProviderRegistry registry, string provider, string model, double markupPct)
    {
        var id = $"{provider}/{model}";
        if (sdata.ValueKind == JsonValueKind.Object
            && sdata.TryGetProperty(Core.Usage.PricingService.OverridesKey, out var pr) && pr.ValueKind == JsonValueKind.Object
            && pr.TryGetProperty(id, out var e) && e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty("sell", out var sv) && sv.ValueKind == JsonValueKind.Object)
        {
            var i = Num(sv, "input") ?? Num(sv, "prompt") ?? 0;
            var o = Num(sv, "output") ?? Num(sv, "completion") ?? 0;
            return (i, o);
        }
        var cost = Core.Usage.PricingService.GetPrice(sdata, registry, provider, model);
        if (cost is null) return null;
        var f = 1 + markupPct / 100.0;
        return (cost.Value.input * f, cost.Value.output * f);
    }

    public static void Map(WebApplication app)
    {
        // ---------- catálogo público ----------
        app.MapGet("/api/public/models", async (LlmRouterDbContext db, ProviderRegistry registry) =>
        {
            var sdata = await Core.Gateway.HotReads.SettingsDataAsync(db);
            // flag lida fresca do DB — toggle não pode ficar stale no cache de 10s
            var srow = await db.Settings.AsNoTracking().FirstOrDefaultAsync();
            var fresh = srow is null ? default
                : AuthEndpoints.Parse(srow.Data);
            if (fresh.ValueKind == JsonValueKind.Object
                && fresh.TryGetProperty("publicCatalog", out var pc) && pc.ValueKind == JsonValueKind.False)
                return Results.NotFound();

            var disabled = await Core.Gateway.HotReads.DisabledModelsAsync(db);
            var data = new List<object>();
            foreach (var c in await db.ProviderConnections.AsNoTracking()
                .Where(x => x.IsActive).ToListAsync())
            {
                var p = registry.GetProvider(c.Provider);
                if (p?.Models is null) continue;
                foreach (var m in p.Models)
                {
                    var id = $"{p.Id}/{m.Id}";
                    if (DisabledModels.IsDisabled(disabled, p.Id, m.Id)) continue;
                    var markup = MarkupPercent(sdata, id);
                    var sell = SellPrice(sdata, registry, p.Id, m.Id, markup);
                    var caps = new List<string>();
                    if (m.Vision == true || m.SupportsVision == true) caps.Add("vision");
                    if (m.ImageToText == true) caps.Add("image_to_text");
                    if (m.SupportsReasoning == true) caps.Add("reasoning");
                    if (m.ToolCalling == true) caps.Add("tools");
                    if (m.SupportsAudio == true) caps.Add("audio");
                    data.Add(new
                    {
                        id,
                        name = m.Name ?? id,
                        context_length = m.ContextLength,
                        capabilities = caps,
                        pricing = sell is null ? null : new
                        {
                            prompt = sell.Value.input / 1_000_000.0,
                            completion = sell.Value.output / 1_000_000.0,
                            currency = "USD",
                        },
                        top_provider = new { context_length = m.ContextLength },
                    });
                }
            }
            foreach (var combo in await db.Combos.AsNoTracking().ToListAsync())
                data.Add(new { id = combo.Name, name = combo.Name, context_length = (int?)null,
                    capabilities = new[] { "combo" }, pricing = (object?)null,
                    top_provider = (object?)null });
            return Results.Json(new { data }, JsonOpts);
        });

        // ---------- signup → key (default off) ----------
        app.MapPost("/api/signup", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var srow = await db.Settings.AsNoTracking().FirstOrDefaultAsync();
            var signup = srow is not null
                && AuthEndpoints.Parse(srow.Data) is { ValueKind: JsonValueKind.Object } fres
                && fres.TryGetProperty("signup", out var su) ? su : default;
            var enabled = signup.ValueKind == JsonValueKind.Object
                && signup.TryGetProperty("enabled", out var se) && se.ValueKind == JsonValueKind.True;
            if (!enabled)
                return Results.Json(new { error = "signup disabled" }, JsonOpts, statusCode: 403);

            // rate-limit primitivo: 5 signups/hora por IP
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var q = SignupIps.GetOrAdd(ip, _ => new Queue<long>());
            lock (q) { while (q.Count > 0 && now - q.Peek() > 3600) q.Dequeue(); }
            if (q.Count >= 5)
                return Results.Json(new { error = "too many signups, try later" }, JsonOpts, statusCode: 429);

            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var email = b.TryGetProperty("email", out var e) ? e.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return Results.BadRequest(new { error = "email inválido" });
            if (await db.Users.AnyAsync(u => u.Email == email))
                return Results.Json(new { error = "email já cadastrado" }, JsonOpts, statusCode: 409);

            lock (q) { q.Enqueue(now); }
            var u = new User
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Email = email,
                Name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Role = "member", CreatedAt = Now(),
            };
            db.Users.Add(u);
            var k = new UserKey
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                UserId = u.Id,
                Key = $"vk-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}",
                Name = "signup",
                CreatedAt = Now(),
            };
            db.UserKeys.Add(k);
            var credit = signup.ValueKind == JsonValueKind.Object
                && signup.TryGetProperty("initialCreditUsd", out var ic) && ic.ValueKind == JsonValueKind.Number
                ? ic.GetDouble() : 0;
            if (credit > 0)
                db.CreditLedger.Add(new CreditLedgerRow
                {
                    UserId = u.Id, Delta = credit, Reason = "topup",
                    BalanceAfter = credit, CreatedAt = Now(),
                });
            await db.SaveChangesAsync();
            await AuditLog.RecordAsync(db, email, "user.signup", u.Id, new { credit }, ip);
            return Results.Json(new { key = k.Key, userId = u.Id, creditUsd = credit }, JsonOpts, statusCode: 201);
        });
    }
}

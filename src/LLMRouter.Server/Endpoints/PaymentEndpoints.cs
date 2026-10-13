using System.Security.Claims;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using LLMRouter.Core.Payments;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-136: payment endpoints — POST /api/payments/checkout creates a hosted
/// checkout with the configured provider; /api/payments/webhooks/{provider}
/// settles it (signature-verified) into a creditLedger topup, idempotent on
/// PaymentTransaction.ExternalId. Provider config: settings.data.payments.*.
/// </summary>
public static class PaymentEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static string Now() => DateTime.UtcNow.ToString("O");

    public static void Map(WebApplication app)
    {
        // list configured providers (public-ish — used by the billing page)
        app.MapGet("/api/payments/providers", async (LlmRouterDbContext db) =>
        {
            var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync();
            var providers = PaymentProviders.Load(AuthEndpoints.Parse(s?.Data)).Keys;
            return Results.Json(new { providers }, JsonOpts);
        });

        // create a checkout — requires a user session (cookie userId) or vk user
        app.MapPost("/api/payments/checkout", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var userId = ctx.User.FindFirst("userId")?.Value;
            if (userId is null)
            {
                var auth = await GatewayEndpoints.AuthenticatedKey(ctx, db);
                if (auth.Authed) userId = auth.UserId;
            }
            if (userId is null || await db.Users.FindAsync(userId) is not { } user)
                return Results.Unauthorized();

            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var amount = b.TryGetProperty("amountUsd", out var a) ? a.GetDouble() : 0;
            if (amount is < 1 or > 10000)
                return Results.BadRequest(new { error = "amountUsd fora de 1..10000" });
            var want = b.TryGetProperty("provider", out var pr) ? pr.GetString() ?? "auto" : "auto";

            var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync();
            var providers = PaymentProviders.Load(AuthEndpoints.Parse(s?.Data));
            var name = want == "auto" ? providers.Keys.FirstOrDefault() : want;
            if (name is null || !providers.TryGetValue(name, out var provider))
                return Results.Json(new { error = "nenhum payment provider configurado" }, JsonOpts, statusCode: 503);

            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var (extId, url, currency) = await provider.CreateCheckoutAsync(hf.CreateClient(),
                new PaymentCheckoutRequest(user.Id, user.Email, amount,
                    $"{origin}/account/billing?paid=1", $"{origin}/account/billing?canceled=1"), ct);

            var tx = new PaymentTransaction
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                UserId = user.Id, Provider = name, ExternalId = extId,
                AmountUsd = amount, Currency = currency, CheckoutUrl = url, CreatedAt = Now(),
            };
            db.PaymentTransactions.Add(tx);
            await db.SaveChangesAsync();
            await AuditLog.RecordAsync(db, user.Email, "payment.checkout", tx.Id,
                new { provider = name, amount }, ctx.Connection.RemoteIpAddress?.ToString());
            return Results.Json(new { checkoutUrl = url, transactionId = tx.Id }, JsonOpts, statusCode: 201);
        });

        // signed webhook — anonymous but signature-verified inside the provider
        app.MapPost("/api/payments/webhooks/{provider}", async (string provider, HttpRequest req, LlmRouterDbContext db, IHttpClientFactory hf, CancellationToken ct) =>
        {
            var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync();
            if (!PaymentProviders.Load(AuthEndpoints.Parse(s?.Data)).TryGetValue(provider, out var pp))
                return Results.NotFound(new { error = "provider desconhecido" });
            (string extId, bool paid) parsed;
            try
            {
                var payload = await new StreamReader(req.Body).ReadToEndAsync(ct);
                parsed = await pp.ParseWebhookAsync(payload,
                    req.Headers["Stripe-Signature"].ToString(), ct);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (parsed.extId == "") return Results.Ok(new { ok = true, ignored = true });

            var tx = await db.PaymentTransactions.FirstOrDefaultAsync(t => t.ExternalId == parsed.extId, ct);
            if (tx is null) return Results.NotFound(new { error = "tx desconhecida" });
            if (tx.Status == "paid") return Results.Ok(new { ok = true, idempotent = true });
            if (!parsed.paid) { tx.Status = "failed"; await db.SaveChangesAsync(); return Results.Ok(new { ok = true }); }

            // settle: mark paid + credit the user exactly once (serial under the same db scope)
            tx.Status = "paid"; tx.SettledAt = Now();
            var last = await db.CreditLedger.Where(r => r.UserId == tx.UserId)
                .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
            var bal = (last?.BalanceAfter ?? 0) + tx.AmountUsd;
            db.CreditLedger.Add(new CreditLedgerRow
            {
                UserId = tx.UserId, Delta = tx.AmountUsd,
                Reason = "topup", BalanceAfter = bal, CreatedAt = Now(),
            });
            await db.SaveChangesAsync();
            await AuditLog.RecordAsync(db, "payments", "payment.settled", tx.Id,
                new { provider, amount = tx.AmountUsd, bal }, req.HttpContext.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { ok = true });
        });

        // user's own transactions (billing page history)
        app.MapGet("/api/payments/transactions", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var userId = ctx.User.FindFirst("userId")?.Value;
            if (userId is null)
            {
                var auth = await GatewayEndpoints.AuthenticatedKey(ctx, db);
                if (auth.Authed) userId = auth.UserId;
            }
            if (userId is null) return Results.Unauthorized();
            var rows = await db.PaymentTransactions.AsNoTracking()
                .Where(t => t.UserId == userId).OrderByDescending(t => t.CreatedAt).Take(50).ToListAsync();
            return Results.Json(new { transactions = rows }, JsonOpts);
        });
    }
}

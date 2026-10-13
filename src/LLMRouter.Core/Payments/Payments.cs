using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LLMRouter.Core.Payments;

/// <summary>
/// SPEC-136/137: payment provider abstraction. A provider creates a hosted
/// checkout and later settles it via a signed webhook/callback → topup row in
/// creditLedger (settle is idempotent on PaymentTransaction.ExternalId).
/// Config lives in settings.data under payments.&lt;name&gt; — e.g.
/// payments.stripe = {secretKey, webhookSecret, enabled}.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }
    /// <summary>Create a hosted checkout; returns (externalId, checkoutUrl, currency).</summary>
    Task<(string ExternalId, string CheckoutUrl, string Currency)> CreateCheckoutAsync(
        HttpClient http, PaymentCheckoutRequest req, CancellationToken ct = default);
    /// <summary>Verify + parse a webhook payload → (externalId, paid). Throws on bad signature.</summary>
    Task<(string ExternalId, bool Paid)> ParseWebhookAsync(
        string payload, string signatureHeader, CancellationToken ct = default);
}

public sealed record PaymentCheckoutRequest(
    string UserId, string UserEmail, double AmountUsd, string SuccessUrl, string CancelUrl);

/// <summary>Stripe — Checkout Session (hosted page) + signed webhook.</summary>
public sealed class StripePaymentProvider(JsonElement cfg) : IPaymentProvider
{
    private readonly string _secret = cfg.TryGetProperty("secretKey", out var s) ? s.GetString() ?? "" : "";
    private readonly string _whsec = cfg.TryGetProperty("webhookSecret", out var w) ? w.GetString() ?? "" : "";

    public string Name => "stripe";

    public async Task<(string, string, string)> CreateCheckoutAsync(
        HttpClient http, PaymentCheckoutRequest req, CancellationToken ct = default)
    {
        var cents = (long)Math.Round(req.AmountUsd * 100);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["success_url"] = req.SuccessUrl,
            ["cancel_url"] = req.CancelUrl,
            ["client_reference_id"] = req.UserId,
            ["customer_email"] = req.UserEmail,
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = "usd",
            ["line_items[0][price_data][unit_amount]"] = cents.ToString(),
            ["line_items[0][price_data][product_data][name]"] = $"LLMRouter credit ${req.AmountUsd:0.00}",
            ["metadata[userId]"] = req.UserId,
        });
        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/checkout/sessions")
        { Content = form };
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _secret);
        var res = await http.SendAsync(msg, ct);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"stripe error: {body.GetProperty("error").GetProperty("message").GetString()}");
        return (body.GetProperty("id").GetString()!, body.GetProperty("url").GetString()!, "usd");
    }

    public Task<(string, bool)> ParseWebhookAsync(string payload, string signatureHeader, CancellationToken ct = default)
    {
        VerifySignature(payload, signatureHeader);
        var ev = JsonSerializer.Deserialize<JsonElement>(payload);
        if (ev.GetProperty("type").GetString() != "checkout.session.completed")
            return Task.FromResult(("", false));
        var o = ev.GetProperty("data").GetProperty("object");
        var paid = o.TryGetProperty("payment_status", out var ps) && ps.GetString() == "paid";
        return Task.FromResult((o.GetProperty("id").GetString()!, paid));
    }

    private void VerifySignature(string payload, string header)
    {
        // Stripe-Signature: t=<ts>,v1=<sig> — HMAC-SHA256("t.payload") with whsec
        var t = header.Split(',').Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        if (!t.TryGetValue("t", out var ts) || !t.TryGetValue("v1", out var sig))
            throw new InvalidOperationException("bad Stripe-Signature");
        var expected = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(_whsec), Encoding.UTF8.GetBytes($"{ts}.{payload}"))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(sig)))
            throw new InvalidOperationException("invalid stripe signature");
        // 5min replay window
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(ts)) > 300)
            throw new InvalidOperationException("stale stripe signature");
    }
}

/// <summary>Reads settings.data.payments and instantiates enabled providers.</summary>
public static class PaymentProviders
{
    public static Dictionary<string, IPaymentProvider> Load(JsonElement settingsData)
    {
        var map = new Dictionary<string, IPaymentProvider>();
        if (!settingsData.TryGetProperty("payments", out var p) || p.ValueKind != JsonValueKind.Object)
            return map;
        foreach (var prop in p.EnumerateObject())
        {
            var c = prop.Value;
            if (!c.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.True) continue;
            IPaymentProvider? provider = prop.Name switch
            {
                "stripe" => new StripePaymentProvider(c),
                _ => null,
            };
            if (provider is not null) map[prop.Name] = provider;
        }
        return map;
    }
}

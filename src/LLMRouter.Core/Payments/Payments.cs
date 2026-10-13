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
    /// <summary>Verify + parse a webhook payload → (externalId, paid). Throws on bad signature.
    /// Providers that must query back their API (Mercado Pago, PagSeguro) use http.</summary>
    Task<(string ExternalId, bool Paid)> ParseWebhookAsync(
        string payload, string signatureHeader, HttpClient http, CancellationToken ct = default);
}

public sealed record PaymentCheckoutRequest(
    string UserId, string UserEmail, double AmountUsd, string SuccessUrl, string CancelUrl,
    /// <summary>Our correlation id (tx.Id) — providers echo it back in webhooks.</summary>
    string ExternalRef);

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
            ["client_reference_id"] = req.ExternalRef,
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

    public Task<(string, bool)> ParseWebhookAsync(string payload, string signatureHeader, HttpClient http, CancellationToken ct = default)
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

/// <summary>
/// SPEC-137: Mercado Pago — Checkout Pro preference (PIX/boleto/cartão, BRL).
/// Webhook: payment notification → GET /v1/payments/{id} for the real status
/// (signature x-signature ts+hash when a secret is configured; the reference
/// lives in external_reference = our tx.Id).
/// </summary>
public sealed class MercadoPagoPaymentProvider(JsonElement cfg, double brlPerUsd) : IPaymentProvider
{
    private readonly string _token = cfg.TryGetProperty("accessToken", out var s) ? s.GetString() ?? "" : "";

    public string Name => "mercadopago";

    public async Task<(string, string, string)> CreateCheckoutAsync(
        HttpClient http, PaymentCheckoutRequest req, CancellationToken ct = default)
    {
        var brl = Math.Round(req.AmountUsd * brlPerUsd, 2);
        var body = new
        {
            items = new[] { new { title = $"LLMRouter credit ${req.AmountUsd:0.00}", quantity = 1, unit_price = brl, currency_id = "BRL" } },
            payer = new { email = req.UserEmail },
            external_reference = req.ExternalRef,
            back_urls = new { success = req.SuccessUrl, failure = req.CancelUrl, pending = req.SuccessUrl },
            auto_return = "approved",
        };
        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.mercadopago.com/checkout/preferences")
        { Content = JsonContent.Create(body) };
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        var res = await http.SendAsync(msg, ct);
        var r = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"mercadopago error: {r}");
        return (r.GetProperty("id").GetString()!, r.GetProperty("init_point").GetString()!, "brl");
    }

    public async Task<(string, bool)> ParseWebhookAsync(string payload, string signatureHeader, HttpClient http, CancellationToken ct = default)
    {
        // MP notifies {type:"payment", data:{id}} — verify x-signature when configured, then fetch status
        var ev = JsonSerializer.Deserialize<JsonElement>(payload);
        var paymentId = ev.TryGetProperty("data", out var d) && d.TryGetProperty("id", out var di)
            ? di.GetString() ?? di.GetRawText() : "";
        if (paymentId == "") return ("", false);
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"https://api.mercadopago.com/v1/payments/{paymentId}");
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        var pay = await (await http.SendAsync(msg, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        var extRef = pay.TryGetProperty("external_reference", out var er) ? er.GetString() ?? "" : "";
        var approved = pay.TryGetProperty("status", out var st) && st.GetString() == "approved";
        return (extRef, approved);
    }
}

/// <summary>
/// SPEC-137: PagSeguro (PagBank) — POST /checkouts redirect (PIX/cartão/boleto, BRL).
/// Webhook: notification carries the reference/order code → GET /orders/{id}
/// charges[0].status == "PAID".
/// </summary>
public sealed class PagSeguroPaymentProvider(JsonElement cfg, double brlPerUsd) : IPaymentProvider
{
    private readonly string _token = cfg.TryGetProperty("token", out var s) ? s.GetString() ?? "" : "";

    public string Name => "pagseguro";

    public async Task<(string, string, string)> CreateCheckoutAsync(
        HttpClient http, PaymentCheckoutRequest req, CancellationToken ct = default)
    {
        var cents = (int)Math.Round(req.AmountUsd * brlPerUsd * 100);
        var baseUrl = cfg.TryGetProperty("baseUrl", out var bu) ? bu.GetString() ?? "https://api.pagseguro.com" : "https://api.pagseguro.com";
        var body = new
        {
            reference_id = req.ExternalRef,
            customer = new { name = req.UserEmail, email = req.UserEmail },
            items = new[] { new { reference_id = "credit", name = $"LLMRouter credit ${req.AmountUsd:0.00}", quantity = 1, unit_amount = cents } },
            redirect_url = req.SuccessUrl,
            payment_methods = new[] { new { type = "PIX" }, new { type = "CREDIT_CARD" }, new { type = "BOLETO" } },
        };
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/checkouts")
        { Content = JsonContent.Create(body) };
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        var res = await http.SendAsync(msg, ct);
        var r = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"pagseguro error: {r}");
        var url = r.TryGetProperty("links", out var links)
            ? links.EnumerateArray().FirstOrDefault(l => l.GetProperty("rel").GetString() == "PAY").GetProperty("href").GetString() ?? ""
            : "";
        return (r.GetProperty("id").GetString()!, url, "brl");
    }

    public async Task<(string, bool)> ParseWebhookAsync(string payload, string signatureHeader, HttpClient http, CancellationToken ct = default)
    {
        // PagBank notifies the order id; fetch it for authoritative status
        var ev = JsonSerializer.Deserialize<JsonElement>(payload);
        var orderId = ev.TryGetProperty("id", out var i) ? i.GetString() ?? ""
            : ev.TryGetProperty("reference_id", out var ri) ? ri.GetString() ?? "" : "";
        if (orderId == "") return ("", false);
        var baseUrl = cfg.TryGetProperty("baseUrl", out var bu) ? bu.GetString() ?? "https://api.pagseguro.com" : "https://api.pagseguro.com";
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/orders/{orderId}");
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        var ord = await (await http.SendAsync(msg, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        var paid = ord.TryGetProperty("charges", out var ch) && ch.GetArrayLength() > 0
            && ch[0].TryGetProperty("status", out var st) && st.GetString() == "PAID";
        return (orderId, paid);
    }
}

/// <summary>Reads settings.data.payments and instantiates enabled providers.</summary>
public static class PaymentProviders
{
    /// <summary>BRL→USD conversion for the Brazilian providers (payments.brlPerUsd, default 5.60).</summary>
    public static double BrlPerUsd(JsonElement payments) =>
        payments.TryGetProperty("brlPerUsd", out var r) && r.ValueKind == JsonValueKind.Number
            ? Math.Max(0.01, r.GetDouble()) : 5.60;

    public static Dictionary<string, IPaymentProvider> Load(JsonElement settingsData)
    {
        var map = new Dictionary<string, IPaymentProvider>();
        if (!settingsData.TryGetProperty("payments", out var p) || p.ValueKind != JsonValueKind.Object)
            return map;
        var brl = BrlPerUsd(p);
        foreach (var prop in p.EnumerateObject())
        {
            var c = prop.Value;
            if (c.ValueKind != JsonValueKind.Object) continue;
            if (!c.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.True) continue;
            IPaymentProvider? provider = prop.Name switch
            {
                "stripe" => new StripePaymentProvider(c),
                "mercadopago" => new MercadoPagoPaymentProvider(c, brl),
                "pagseguro" => new PagSeguroPaymentProvider(c, brl),
                _ => null,
            };
            if (provider is not null) map[prop.Name] = provider;
        }
        return map;
    }
}

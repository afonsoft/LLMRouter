using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Payments;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-136/137: payment providers — signatures, checkout shapes, BRL conversion.</summary>
public class PaymentTests
{
    private static readonly JsonElement Cfg = JsonDocument.Parse(
        """{"enabled":true,"secretKey":"sk_test_x","webhookSecret":"whsec_test"}""").RootElement;
    private static readonly HttpClient NoHttp = new();

    private static string Sign(string payload, long t) =>
        $"t={t},v1={Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("whsec_test"), Encoding.UTF8.GetBytes($"{t}.{payload}"))).ToLowerInvariant()}";

    private sealed class StubHandler(string body, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static PaymentCheckoutRequest Req(string ext = "tx123") =>
        new("u1", "u@x.com", 10, "https://app/ok", "https://app/cancel", ext);

    // ---- Stripe (SPEC-136)

    [Fact]
    public async Task Webhook_ValidSignature_ParsesSessionId()
    {
        var p = new StripePaymentProvider(Cfg);
        var payload = """{"type":"checkout.session.completed","data":{"object":{"id":"cs_1","payment_status":"paid"}}}""";
        var (extId, paid) = await p.ParseWebhookAsync(payload, Sign(payload, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), NoHttp);
        Assert.Equal("cs_1", extId);
        Assert.True(paid);
    }

    [Fact]
    public async Task Webhook_BadSignature_Throws()
    {
        var p = new StripePaymentProvider(Cfg);
        var payload = """{"type":"checkout.session.completed","data":{"object":{"id":"cs_1","payment_status":"paid"}}}""";
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => p.ParseWebhookAsync(payload, $"t={t},v1=deadbeef", NoHttp));
    }

    [Fact]
    public async Task Webhook_StaleTimestamp_Throws()
    {
        var p = new StripePaymentProvider(Cfg);
        var payload = """{"type":"checkout.session.completed","data":{"object":{"id":"cs_1","payment_status":"paid"}}}""";
        var stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => p.ParseWebhookAsync(payload, Sign(payload, stale), NoHttp));
    }

    [Fact]
    public async Task Webhook_OtherEvent_Ignored()
    {
        var p = new StripePaymentProvider(Cfg);
        var payload = """{"type":"payment_intent.created","data":{"object":{"id":"pi_1"}}}""";
        var (extId, _) = await p.ParseWebhookAsync(payload, Sign(payload, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), NoHttp);
        Assert.Equal("", extId);
    }

    // ---- loading (136 + 137)

    [Fact]
    public void Providers_DisabledOrUnknown_NotLoaded()
    {
        var cfg = JsonDocument.Parse("""
            {"payments":{"stripe":{"enabled":false,"secretKey":"x"},"bogus":{"enabled":true}}}
            """).RootElement;
        Assert.Empty(PaymentProviders.Load(cfg));
        var none = JsonDocument.Parse("{}").RootElement;
        Assert.Empty(PaymentProviders.Load(none));
    }

    [Fact]
    public void Providers_AllThree_Loaded()
    {
        var cfg = JsonDocument.Parse("""
            {"payments":{"brlPerUsd":6.0,
             "stripe":{"enabled":true,"secretKey":"x"},
             "mercadopago":{"enabled":true,"accessToken":"x"},
             "pagseguro":{"enabled":true,"token":"x"}}}
            """).RootElement;
        var loaded = PaymentProviders.Load(cfg);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(6.0, PaymentProviders.BrlPerUsd(cfg.GetProperty("payments")));
    }

    // ---- Mercado Pago (SPEC-137)

    [Fact]
    public async Task Mp_Checkout_BrlAmountAndInitPoint()
    {
        var h = new StubHandler("""{"id":"pref-1","init_point":"https://mp.com/checkout"}""");
        var p = new MercadoPagoPaymentProvider(
            JsonDocument.Parse("""{"enabled":true,"accessToken":"tok"}""").RootElement, brlPerUsd: 5.0);
        var (extId, url, cur) = await p.CreateCheckoutAsync(new HttpClient(h), Req());
        Assert.Equal("pref-1", extId);
        Assert.Equal("https://mp.com/checkout", url);
        Assert.Equal("brl", cur);
        var sent = h.LastBody!;
        Assert.Contains("\"unit_price\":50", sent);          // 10 USD * 5.0 BRL
        Assert.Contains("\"external_reference\":\"tx123\"", sent);
        Assert.Equal("tok", h.Last.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Mp_Webhook_ApprovedPayment_ReturnsOurRef()
    {
        var h = new StubHandler("""{"status":"approved","external_reference":"tx123"}""");
        var p = new MercadoPagoPaymentProvider(
            JsonDocument.Parse("""{"enabled":true,"accessToken":"tok"}""").RootElement, 5.0);
        var (extId, paid) = await p.ParseWebhookAsync(
            """{"type":"payment","data":{"id":999}}""", "", new HttpClient(h));  // MP sends numeric data.id
        Assert.Equal("tx123", extId);
        Assert.True(paid);
        Assert.Contains("/v1/payments/999", h.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Mp_Webhook_PendingPayment_NotPaid()
    {
        var h = new StubHandler("""{"status":"pending","external_reference":"tx123"}""");
        var p = new MercadoPagoPaymentProvider(
            JsonDocument.Parse("""{"enabled":true,"accessToken":"tok"}""").RootElement, 5.0);
        var (_, paid) = await p.ParseWebhookAsync(
            """{"type":"payment","data":{"id":"999"}}""", "", new HttpClient(h));
        Assert.False(paid);
    }

    // ---- PagSeguro (SPEC-137)

    [Fact]
    public async Task Ps_Checkout_CentsAndPayLink()
    {
        var h = new StubHandler(
            """{"id":"ord-1","links":[{"rel":"PAY","href":"https://pay.pagseguro.com/x"}]}""");
        var p = new PagSeguroPaymentProvider(
            JsonDocument.Parse("""{"enabled":true,"token":"tok","baseUrl":"https://sandbox.test"}""").RootElement, brlPerUsd: 6.0);
        var (extId, url, cur) = await p.CreateCheckoutAsync(new HttpClient(h), Req());
        Assert.Equal("ord-1", extId);
        Assert.Equal("https://pay.pagseguro.com/x", url);
        Assert.Equal("brl", cur);
        Assert.Equal("https://sandbox.test/checkouts", h.Last!.RequestUri!.ToString());
        var sent = h.LastBody!;
        Assert.Contains("\"unit_amount\":6000", sent);       // 10 USD * 6.0 BRL * 100
        Assert.Contains("\"reference_id\":\"tx123\"", sent);
    }

    [Fact]
    public async Task Ps_Webhook_PaidCharge_ReturnsOrderId()
    {
        var h = new StubHandler("""{"id":"ord-1","charges":[{"status":"PAID"}]}""");
        var p = new PagSeguroPaymentProvider(
            JsonDocument.Parse("""{"enabled":true,"token":"tok","baseUrl":"https://sandbox.test"}""").RootElement, 6.0);
        var (extId, paid) = await p.ParseWebhookAsync("""{"id":"ord-1"}""", "", new HttpClient(h));
        Assert.Equal("ord-1", extId);
        Assert.True(paid);
        Assert.Equal("https://sandbox.test/orders/ord-1", h.Last!.RequestUri!.ToString());
    }
}

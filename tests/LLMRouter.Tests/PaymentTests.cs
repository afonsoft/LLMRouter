using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMRouter.Core.Payments;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-136: Stripe provider — webhook signature + payload parsing.</summary>
public class PaymentTests
{
    private static readonly JsonElement Cfg = JsonDocument.Parse(
        """{"enabled":true,"secretKey":"sk_test_x","webhookSecret":"whsec_test"}""").RootElement;

    private static string Sign(string payload, long t) =>
        $"t={t},v1={Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("whsec_test"), Encoding.UTF8.GetBytes($"{t}.{payload}"))).ToLowerInvariant()}";

    [Fact]
    public async Task Webhook_ValidSignature_ParsesSessionId()
    {
        var p = new StripePaymentProvider(Cfg);
        var payload = """{"type":"checkout.session.completed","data":{"object":{"id":"cs_1","payment_status":"paid"}}}""";
        var (extId, paid) = await p.ParseWebhookAsync(payload, Sign(payload, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
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
            () => p.ParseWebhookAsync(payload, $"t={t},v1=deadbeef"));
    }

    [Fact]
    public async Task Webhook_StaleTimestamp_Throws()
    {
        var p = new StripePaymentProvider(Cfg);
        var payload = """{"type":"checkout.session.completed","data":{"object":{"id":"cs_1","payment_status":"paid"}}}""";
        var stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => p.ParseWebhookAsync(payload, Sign(payload, stale)));
    }

    [Fact]
    public async Task Webhook_OtherEvent_Ignored()
    {
        var p = new StripePaymentProvider(Cfg);
        var payload = """{"type":"payment_intent.created","data":{"object":{"id":"pi_1"}}}""";
        var (extId, _) = await p.ParseWebhookAsync(payload, Sign(payload, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Assert.Equal("", extId);
    }

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
    public void Providers_Enabled_Loaded()
    {
        var cfg = JsonDocument.Parse("""{"payments":{"stripe":{"enabled":true,"secretKey":"x"}}}""").RootElement;
        Assert.Contains("stripe", PaymentProviders.Load(cfg).Keys);
    }
}

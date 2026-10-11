using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// SPEC-101: scrapeable gateway metrics. Single System.Diagnostics.Metrics
/// source ("llmr") consumed by the OTel Prometheus exporter at GET /metrics
/// and by the optional OTLP exporter (settings.observability.otlp).
/// </summary>
public static class GatewayMetrics
{
    public const string MeterName = "llmr";
    private const string LblProvider = "provider";
    private const string LblModel = "model";
    public static readonly ActivitySource ActivitySource = new(MeterName);
    private static readonly Meter M = new(MeterName, "1.0");

    public static readonly Counter<long> Requests = M.CreateCounter<long>(
        "llmr_requests_total", description: "Gateway requests by provider/model/status.");
    public static readonly Histogram<double> RequestDurationMs = M.CreateHistogram<double>(
        "llmr_request_duration_ms", description: "End-to-end gateway latency.");
    public static readonly Counter<long> Tokens = M.CreateCounter<long>(
        "llmr_tokens_total", description: "Tokens by provider/model/kind.");
    public static readonly Counter<double> CostUsd = M.CreateCounter<double>(
        "llmr_cost_usd_total", description: "Estimated cost by provider/model.");
    public static readonly Histogram<double> UpstreamTtfbMs = M.CreateHistogram<double>(
        "llmr_upstream_ttfb_ms", description: "Time to upstream response headers.");
    public static readonly Histogram<double> StreamDurationMs = M.CreateHistogram<double>(
        "llmr_stream_duration_ms", description: "Stream body relay duration.");
    public static readonly Counter<long> RateLimitViolations = M.CreateCounter<long>(
        "llmr_ratelimit_violations_total", description: "Rejected/skipped requests by scope.");
    public static readonly Counter<long> ComboSteps = M.CreateCounter<long>(
        "llmr_combo_steps_selected_total", description: "Combo step dispatches.");
    public static readonly Counter<long> UsageDropped = M.CreateCounter<long>(
        "llmr_usage_dropped_total", description: "Usage writes dropped on a full sink channel.");

    private static int _gaugesRegistered;

    /// <summary>
    /// Observable gauges against live static sources (breaker, cooldowns,
    /// lockouts, prompt-cache ratio). Idempotent — call once at startup.
    /// (o Meter estático mantém os instrumentos vivos)
    /// </summary>
    public static void RegisterRuntimeGauges()
    {
        if (Interlocked.Exchange(ref _gaugesRegistered, 1) == 1) return;
        _ = M.CreateObservableGauge<int>("llmr_breaker_state",
            () => Resilience.ProviderBreaker.Snapshot().Select(s =>
                new Measurement<int>(BreakerValue(s.State),
                    new KeyValuePair<string, object?>(LblProvider, s.ProviderId))));
        _ = M.CreateObservableGauge<int>("llmr_cooldowns_active",
            () => Resilience.CooldownTracker.Snapshot().Count);
        _ = M.CreateObservableGauge<int>("llmr_lockouts_active",
            () => Resilience.ModelLockout.Snapshot().Count);
        _ = M.CreateObservableGauge<double>("llmr_promptcache_hit_ratio",
            () => Cache.PromptCache.HitRatio);
    }

    // closed=0, degraded=1, half_open=2, open=3 — documented in docs/observability.md
    private static int BreakerValue(string state) => state switch
    {
        "DEGRADED" => 1,
        "HALF_OPEN" => 2,
        "OPEN" => 3,
        _ => 0,
    };

    /// <summary>Short api-key fingerprint for the keyhash label (never the raw key).</summary>
    public static string KeyHash(string apiKey) =>
        apiKey.Length == 0 ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..12];

    /// <summary>One completed (or failed) gateway request for RecordRequest.</summary>
    public sealed record RequestMetric(
        string Provider, string Model, string? ConnectionId, string ApiKey,
        string Inbound, int PromptTokens, int CompletionTokens,
        double Cost, long LatencyMs, string Status);

    /// <summary>Record a completed (or failed) gateway request — called once per LogUsage.</summary>
    public static void RecordRequest(RequestMetric m)
    {
        var tags = new TagList
        {
            { LblProvider, m.Provider },
            { LblModel, m.Model },
            { "connection", m.ConnectionId ?? "" },
            { "keyhash", KeyHash(m.ApiKey) },
            { "status", m.Status },
            { "inbound", m.Inbound },
        };
        Requests.Add(1, tags);
        RequestDurationMs.Record(m.LatencyMs, tags);
        if (m.PromptTokens > 0)
            Tokens.Add(m.PromptTokens, Tag(LblProvider, m.Provider), Tag(LblModel, m.Model), Tag("kind", "prompt"));
        if (m.CompletionTokens > 0)
            Tokens.Add(m.CompletionTokens, Tag(LblProvider, m.Provider), Tag(LblModel, m.Model), Tag("kind", "completion"));
        if (m.Cost > 0) CostUsd.Add(m.Cost, Tag(LblProvider, m.Provider), Tag(LblModel, m.Model));
    }

    public static void RateLimitViolation(string scope) =>
        RateLimitViolations.Add(1, Tag("scope", scope));

    public static void ComboStepSelected(string combo) =>
        ComboSteps.Add(1, Tag("combo", combo));

    public static void UpstreamTtfb(string provider, string model, double ms) =>
        UpstreamTtfbMs.Record(ms, Tag(LblProvider, provider), Tag(LblModel, model));

    public static void StreamDuration(string provider, string model, double ms) =>
        StreamDurationMs.Record(ms, Tag(LblProvider, provider), Tag(LblModel, model));

    private static KeyValuePair<string, object?> Tag(string k, string v) => new(k, v);
}

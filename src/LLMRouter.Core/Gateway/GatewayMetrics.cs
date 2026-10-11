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

    // Live gauges — kept as field references so the instruments are not GC'd.
    private static ObservableGauge<int>? _breaker;
    private static ObservableGauge<int>? _cooldowns;
    private static ObservableGauge<int>? _lockouts;
    private static ObservableGauge<double>? _cacheRatio;

    /// <summary>
    /// Observable gauges against live static sources (breaker, cooldowns,
    /// lockouts, prompt-cache ratio). Idempotent — call once at startup.
    /// </summary>
    public static void RegisterRuntimeGauges()
    {
        if (Interlocked.Exchange(ref _gaugesRegistered, 1) == 1) return;
        _breaker = M.CreateObservableGauge<int>("llmr_breaker_state",
            () => Resilience.ProviderBreaker.Snapshot().Select(s =>
                new Measurement<int>(BreakerValue(s.State),
                    new KeyValuePair<string, object?>("provider", s.ProviderId))));
        _cooldowns = M.CreateObservableGauge<int>("llmr_cooldowns_active",
            () => Resilience.CooldownTracker.Snapshot().Count);
        _lockouts = M.CreateObservableGauge<int>("llmr_lockouts_active",
            () => Resilience.ModelLockout.Snapshot().Count);
        _cacheRatio = M.CreateObservableGauge<double>("llmr_promptcache_hit_ratio",
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

    /// <summary>Record a completed (or failed) gateway request — called once per LogUsage.</summary>
    public static void RecordRequest(
        string provider, string model, string? connectionId, string apiKey,
        string inbound, int promptTokens, int completionTokens,
        double costUsd, long latencyMs, string status)
    {
        var tags = new TagList
        {
            { "provider", provider },
            { "model", model },
            { "connection", connectionId ?? "" },
            { "keyhash", KeyHash(apiKey) },
            { "status", status },
            { "inbound", inbound },
        };
        Requests.Add(1, tags);
        RequestDurationMs.Record(latencyMs, tags);
        if (promptTokens > 0)
            Tokens.Add(promptTokens, Tag("provider", provider), Tag("model", model), Tag("kind", "prompt"));
        if (completionTokens > 0)
            Tokens.Add(completionTokens, Tag("provider", provider), Tag("model", model), Tag("kind", "completion"));
        if (costUsd > 0) CostUsd.Add(costUsd, Tag("provider", provider), Tag("model", model));
    }

    public static void RateLimitViolation(string scope) =>
        RateLimitViolations.Add(1, Tag("scope", scope));

    public static void ComboStepSelected(string combo) =>
        ComboSteps.Add(1, Tag("combo", combo));

    public static void UpstreamTtfb(string provider, string model, double ms) =>
        UpstreamTtfbMs.Record(ms, Tag("provider", provider), Tag("model", model));

    public static void StreamDuration(string provider, string model, double ms) =>
        StreamDurationMs.Record(ms, Tag("provider", provider), Tag("model", model));

    private static KeyValuePair<string, object?> Tag(string k, string v) => new(k, v);
}

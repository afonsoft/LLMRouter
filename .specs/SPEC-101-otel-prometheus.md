# SPEC-101 — Observability export: `/metrics` (Prometheus) + OTLP

Origin: `docs/analysis/comparativo-mercado-2026-10-11.md` §8.1 — biggest audit/
observability gap vs every enterprise gateway (LiteLLM, Kong, Bifrost all export
Prometheus/OTEL). Today we only have the internal `/api/telemetry/summary` and
provider-metrics aggregates; nothing scrapeable.

## Work

- Add `OpenTelemetry` SDK + `OpenTelemetry.Exporter.Prometheus.AspNetCore` to
  `LLMRouter.Server`; expose `GET /metrics` (Prometheus text format) on the same
  port — unauthenticated or apiKey-scoped behind
  `settings.observability.metricsAuth` (default off for local scrape).
- Meters (`System.Diagnostics.Metrics`) in `GatewayPipeline`: `llmr_requests_total`
  {provider,model,connection,apiKey-hash,status,inbound}, `llmr_request_duration`
  histogram, `llmr_tokens_total` {prompt|completion}, `llmr_cost_usd_total`,
  `llmr_upstream_ttfb`, `llmr_stream_duration`.
- Gauges from existing services: `llmr_breaker_state` {provider,connection}
  (ProviderBreaker), `llmr_cooldowns_active`, `llmr_lockouts_active`
  (ModelLockout), `llmr_promptcache_hit_ratio`, `llmr_ratelimit_violations_total`
  (RateLimiter.CheckAndConsume rejects), `llmr_combo_steps_selected` {combo}.
- Optional OTLP exporter: `settings.observability.otlp = {endpoint, protocol,
  headers}` — traces (`ActivitySource` per upstream call: resolve → translate →
  dispatch → stream) + logs. Disabled by default; Prometheus always on.
- Doc `docs/observability.md`: scrape config example + dashboard fields.

## Tests (≥4)

- `/metrics` returns text format with `llmr_requests_total` after a gateway call;
- counter labels carry provider/model/status for a stubbed upstream call;
- breaker trip changes `llmr_breaker_state` sample;
- `otlp.endpoint` unset → no OTLP registration (settings parse test).

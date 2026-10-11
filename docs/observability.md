# Observabilidade — `/metrics` (Prometheus) + OTLP

SPEC-101: métricas scrapeáveis via OpenTelemetry. Fonte única: `System.Diagnostics.Metrics`
no meter `"llmr"` (`LLMRouter.Core.Gateway.GatewayMetrics`). O exporter Prometheus
serve `GET /metrics` na mesma porta do gateway; OTLP é opcional.

## Scrape (Prometheus)

```yaml
scrape_configs:
  - job_name: llmrouter
    scrape_interval: 15s
    static_configs:
      - targets: ["localhost:20128"]
```

Autenticação: por padrão `/metrics` é aberto (scrape local). Para exigir uma
api key do gateway: `settings.data.observability.metricsAuth = true` — o scrape
então precisa de `Authorization: Bearer <key>` ou `?key=<key>` válida. O flag é
lido a cada request (não precisa restart).

## Métricas

| Métrica | Tipo | Labels |
|---|---|---|
| `llmr_requests_total` | counter | `provider, model, connection, keyhash, status, inbound` |
| `llmr_request_duration_ms` | histogram | idem |
| `llmr_tokens_total` | counter | `provider, model, kind=prompt\|completion` |
| `llmr_cost_usd_total` | counter (USD) | `provider, model` |
| `llmr_upstream_ttfb_ms` | histogram | `provider, model` |
| `llmr_stream_duration_ms` | histogram | `provider, model` |
| `llmr_ratelimit_violations_total` | counter | `scope` |
| `llmr_combo_steps_selected_total` | counter | `combo` |
| `llmr_usage_dropped_total` | counter | — (sink SPEC-108 cheio) |
| `llmr_breaker_state` | gauge | `provider` → 0 closed, 1 degraded, 2 half_open, 3 open |
| `llmr_cooldowns_active` | gauge | conexões em cooldown |
| `llmr_lockouts_active` | gauge | lockouts de modelo ativos |
| `llmr_promptcache_hit_ratio` | gauge | 0..1 |

`keyhash` = 12 primeiros hex do SHA256 da api key (nunca a chave em claro).
O exporter OTel adiciona `otel_scope_name="llmr"`/`otel_scope_version` em toda série.

Dashboard útil: `rate(llmr_requests_total[5m])` por provider/status,
`histogram_quantile(0.95, llmr_request_duration_ms)`, `llmr_breaker_state > 0`,
`llmr_usage_dropped_total` crescente = sink saturado (ver `settings.usageSink`).

## OTLP (opcional)

`settings.data.observability.otlp` liga o exporter para métricas **e** traces:

```json
{
  "observability": {
    "otlp": {
      "endpoint": "http://otel-collector:4317",
      "protocol": "grpc",            // ou "http_protobuf"
      "headers": { "x-api-key": "..." }
    }
  }
}
```

Lido uma vez no boot (`ObservabilitySettings.LoadEarly`); ausente ou inválido →
OTLP desligado, Prometheus continua ativo. Traces: `ActivitySource "llmr"` emite
`llmr.dispatch` por chamada upstream (tags provider/model/connection/status) +
instrumentação ASP.NET Core das requests de entrada.

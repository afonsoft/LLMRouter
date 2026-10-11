# SPEC-107 — Response guardrails + PII redaction stage

Origin: `comparativo-mercado` §8.7 — `PromptGuard` (SPEC-023) is input-only
regex; Kong ships response guard, Portkey 50+ checks incl. PII. Add an
output-side stage in `GatewayPipeline` after the upstream response is
assembled (and per-SSE-chunk for streams).

## Work

- `Guardrails.Response` module next to `PromptGuard`: patterns for credential
  shapes (api keys, private key blocks, JWTs), PII (email/phone/CC via
  Luhn-aware check), plus upstream-style denylist patterns reusing the
  `PromptGuard` severity model (low/medium/high + block/warn/log).
- `settings.guardrails.response = {enabled, mode: block|redact|warn|log,
  patterns[], threshold}` — `redact` replaces matches with `[REDACTED:<name>]`
  (upstream has transcript redaction precedent); `block` fails the request
  with a `content_filter`-style error.
- Streaming: apply per-chunk on translated SSE payloads — buffer only the
  scanned text delta, flush clean chunks immediately; on `block` mid-stream,
  emit error event + close (same contract as existing mid-stream abort).
- Wire `requestDetails`/`usageHistory.meta.guardrails` with
  `{stage: response, detections: [{name,severity,count}]}` — same shape as
  input detections today; count in SPEC-101 metrics (`llmr_guardrail_hits_total`).
- `/api/guardrails` gets `response` section alongside existing input config
  (SPEC-092 surface).

## Tests (≥5)

- response containing `sk-…`-shaped token → redacted in body;
- `block` mode returns content_filter error (non-stream);
- SSE chunk with detection mid-stream → error event, no raw leak;
- `warn`/`log` pass body through and record detection meta;
- Luhn check rejects false-positive CC pattern on random digits.

# Response guardrails (SPEC-107)

Output-side scanning of upstream responses before they reach the client —
credential shapes (API keys, private keys, JWTs) and PII (email, phone,
Luhn-validated credit cards) are detected, and depending on the mode the
response is passed through, rewritten, or rejected with a `content_filter`
error. This complements the input-side `PromptGuard` injection rules and
reuses its severity model (`low` / `medium` / `high` + block threshold).

## Configuration

`settings.guardrails.response`:

```json
{
  "guardrails": {
    "response": {
      "enabled": true,
      "mode": "redact",
      "threshold": "high",
      "patterns": []
    }
  }
}
```

| Field | Values | Default |
|-------|--------|---------|
| `enabled` | bool | `false` |
| `mode` | `block` \| `redact` \| `warn` \| `log` | `log` |
| `threshold` | `low` \| `medium` \| `high` — minimum severity that triggers `block` | `high` |
| `patterns` | extra regex strings (always severity `high`, reported as `custom:<pattern>`) | `[]` |

## Modes

- **`block`** — a detection at or above `threshold` fails the request with a
  `content_filter`-style error (`400`, `error.code = "content_filter"`).
  Mid-stream it emits an `error` SSE event followed by `[DONE]` and closes
  the relay — no raw payload leaks past the detection point.
- **`redact`** — every match is replaced with `[REDACTED:<name>]` in the
  served body. The redacted form is also what the prompt cache stores —
  secrets are never cached.
- **`warn`** / **`log`** — body passes through unchanged; detections are
  recorded in `usageHistory.meta.guardrails` and the metrics counter.

## Built-in patterns

| Name | Severity | Matches |
|------|----------|---------|
| `api_key` | high | `sk-…`, `AKIA…`, `ghp_`/`xox…` tokens, generic `key-…`/`token-…` |
| `private_key` | high | `-----BEGIN … PRIVATE KEY-----` blocks |
| `jwt` | medium | `eyJ…` JWT triples |
| `email` | low | email addresses |
| `phone` | low | E.164-ish phone numbers |
| `credit_card` | medium | 13–19 digit runs **Luhn-validated** (random digits don't fire) |

## Observability

- `llmr_guardrail_hits_total{stage, name, action}` — counter per detection
  (SPEC-101 metric family).
- `usageHistory.meta.guardrails` — `{stage: "response", mode, detections:
  [{name, severity, count}]}`, merged with any token-saver meta.
- `GET /api/guardrails` — now returns `{items, response}`: input rules plus
  the response section config and the builtin pattern list.

## Scope

- Runs **after** response translation and plugin hooks, before serving and
  caching — the scan sees the client-shaped (inbound) body.
- Non-stream requests scan the whole assembled body once; streaming scans
  each translated SSE payload (tokens split across chunk boundaries may
  evade detection in one chunk — the pattern surface still catches
  self-contained credentials, which are the meaningful leaks).
- Dashboard `prompt redaction` (`mgmtBatch` redaction) still applies on top,
  so redaction stacks safely.

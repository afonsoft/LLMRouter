# SPEC-088 — Gateway `/v1/providers/{p}/*` + container probes

Gap §3.1 + §3.2 of `docs/analysis/paridade-2026-10-10.md`. Docker-focus:
unauthenticated probes for orchestrators + upstream's provider-scoped call surface.

## Container probes

Upstream ships `/healthz`, `/livez`, `/readyz` (and `/api/health/ping`) — required
for docker/k8s/systemd healthchecks. Today only `/api/status` exists (authed).

- `GET /healthz` — liveness: process up → `{ok:true}` (no auth, no checks).
- `GET /livez` — same as healthz (upstream treats them identically).
- `GET /readyz` — readiness: DB reachable (`db.Database.CanConnect()`) +
  registry loaded → 200 `{ok:true}` else 503 `{ok:false,error}`.
- `GET /api/health/ping` — `{pong:true}` inside the authed group for parity.
- All three probes exempt from auth middleware, rate limiting, headless-mode
  exclusion (must work in `LLMROUTER_HEADLESS=1`), and StatusCodePages rewrite.

## Provider-scoped gateway calls

Upstream lets clients call one provider directly (bypassing combos/routing) via
`/v1/providers/{providerId}/...`. Port all 8 + the suggestion helper:

- `POST /v1/providers/{p}/chat/completions` — same OpenAI chat contract as
  `/v1/chat/completions` but forced to provider `p` (model passes through
  verbatim; 404 if provider unknown/disabled, 502 passthrough of upstream error).
- `POST /v1/providers/{p}/embeddings` — forced-provider embeddings.
- `POST /v1/providers/{p}/images/generations` — forced-provider image gen.
- `GET  /v1/providers/{p}/models` — provider's own model list (registry models
  for that provider + `syncedModels` rows, OpenAI `{data:[...]}` shape).
- `GET  /v1/providers/{p}/voices` + `GET /v1/providers/{p}/voices/{voice}` —
  TTS voice list/detail for audio-capable providers; empty list when unsupported.
- `GET  /v1/providers/{p}/limits` — provider's rate/quota limits snapshot
  (rateLimits rows scoped to that provider + breaker state + cooldowns).
- `GET  /v1/providers/suggested-models` — `{provider, models[]}` for every
  enabled provider (models flagged recommended/default in the registry,
  else first N registry models).

All mounted on both `/v1` and `/api/v1` prefixes (foreach mount like the rest
of the v1 surface). Auth = `V1Authed` (api key or dashboard cookie) — same as
`/v1/chat/completions`. Streaming (`stream:true`) supported on chat/completions
and goes through the same `GatewayEngine` pipeline with the provider forced.

## Tests (≥8)

- /healthz + /livez unauthenticated 200 in headless mode;
- /readyz 200 with seeded DB, 503 when DB unreachable (test via bad path or
  a failing CanConnect stub — pick the cheaper seam);
- providers/{p}/chat/completions reaches only provider p (stub two providers,
  assert the request hit p's baseUrl);
- providers/{p}/chat/completions stream:true SSE frames pass through;
- unknown provider → 404;
- providers/{p}/models returns registry+synced models in OpenAI shape;
- providers/{p}/limits returns breaker+cooldown+rateLimits snapshot;
- suggested-models returns one entry per enabled provider;
- /api/v1/providers/{p}/chat/completions dual-mount works.

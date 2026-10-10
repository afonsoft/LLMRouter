# SPEC-093 — Upstream delta v3.8.52 wave-2 (sync caps, floors, SSE/translator follow-ups)

Gap §3.5 of `docs/analysis/paridade-2026-10-10.md` — portable fixes/features in
the ~34 upstream commits after `048d9e2f2` (headless/security wave already done
in SPEC-086). Faithful port: same semantics as upstream, not rewrites.

## Registry caps persisted on sync

Upstream `fix(models)`: synced model rows keep provider-reported output caps +
vision metadata instead of dropping them.

- `syncedModels` rows capture `maxOutputTokens`, `vision`, `tools`, `reasoning`
  fields when the provider model-list response exposes them.
- `ComboPlanner.GetCapabilitiesForModel` merges: capabilityOverrides > synced
  caps > registry `supports*` fields (SPEC-087 mapped registry fields; this
  adds the synced layer).
- Migration: existing syncedModels rows keep working (missing caps → registry
  fallback).

## No fabricated max_tokens floor

Upstream `fix(providers)`: don't inject `max_tokens:4096` when the caller
didn't set one — send upstream what was requested (a missing cap means the
provider default applies).

- Remove/never-introduce the 4096 floor in chat/messages/responses paths; only
  clamp when `maxOutputTokens` cap is known from registry/synced row.
- Keep honoring caller `max_tokens`/`max_completion_tokens` verbatim.

## Reasoning efforts discovery

Upstream `feat(providers)`: OpenAI-like providers advertise supported
`reasoning_effort` values during model discovery.

- Model discovery (sync-models) captures `reasoning_efforts`/`reasoning.efforts`
  from provider model metadata → stored on syncedModels/registry caps.
- `GET /v1/models/{p}` + catalog surfaces them (`reasoning_efforts: [...]`).
- Requests passing `reasoning_effort` outside the advertised set → warn header
  (upstream logs a warning, keeps the request).

## SSE/stream follow-ups

Port the upstream recovery fixes that apply to our SSE pipeline:

- Opaque reasoning deltas: upstream-only reasoning content surfaces as
  progress events, never as user-visible text (no `[REASONING]` leak into
  content).
- Buffered Responses failures classify: a non-2xx buffered Responses-API
  reply is classified by its JSON `error.type` (not generic 502) so the
  breaker gets the right status semantics.
- Empty stop is a valid turn: upstream emits `finish_reason:"stop"` with
  empty content → forward as a valid completion, don't retry/flag failure.
- Dedup/reset with breaker: network-level dedup must not hold a request while
  its provider breaker is open — skip straight to fallback.

## Translator/bedrock/claude follow-ups

- Claude passthrough: system messages that appear mid-conversation (not just
  position 0) are preserved — upstream fix for Sonnet/Haiku 5.5 system prompts
  injected between user/assistant turns.
- Bedrock translator: merge consecutive same-role messages before dispatch
  (bedrock rejects alternating violations) + map cache token fields
  (`cacheReadInputTokens`/`cacheWriteInputTokens` → usage).
- Translator: never serialize `stop_sequences:[null]` / `stop:[null]` — drop
  null entries upstream-side (bedrock/claude 400 on them).

## ToS + misc

- Uncataloged models used in requests get a ToS-style warning log (upstream
  `feat(auto-combo)` posture): warn once per model, don't block.
- `/v1/search` internal-exception → `buildErrorBody` shape; kie speech/images
  proxy errors sanitized (no upstream stack/URL leak) — extend the SPEC-079/086
  sanitizer sweep to these routes.

## Tests (≥9)

- syncedModels row with vision/maxOutputTokens feeds planner caps;
- request without max_tokens passes through without a floor;
- known cap clamps, unknown cap doesn't;
- reasoning_efforts surfaced in /v1/models/{p}; out-of-set value → warn header;
- opaque reasoning chunk → progress event, content clean;
- buffered 429 Responses → breaker records 429 semantics;
- empty-stop SSE → success path, usage logged;
- same-role merge in bedrock translator; null stop_sequences dropped;
- /v1/search upstream error → sanitized error body.

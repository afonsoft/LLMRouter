# SPEC-097 — Upstream delta wave-3 (post-pin `61e07fb7e` → `release/v3.8.52` tip)

Third portability sweep over the ~192 commits upstream landed after our contract pin.
Wave-1 covered by SPEC-086/087; wave-2 by SPEC-093. This wave covers the remaining
behavioral deltas. Verified already-done in earlier specs: `nadir` strategy,
combo auto-prune (`settings.data.combos.autoPruneStaleSteps`), custom headers on
model discovery (SPEC-077), bedrock same-role merge (SPEC-075), reasoning_effort
clamp (SPEC-093).

## Delta items to port

### 1. Typed provider availability (`upstream providerAvailability.ts`)
`/api/resilience/connections` classifies each connection into a typed state:
`AVAILABLE | NO_CREDENTIAL | AUTH_EXPIRED (reauthenticate) | QUOTA_EXHAUSTED
(nextEligibleRecheckAt) | DISABLED | STALE_TERMINAL (previousState) | UNHEALTHY
(retryable)`. Terminal statuses `credits_exhausted|banned|expired` confirmed
>24h ago report `STALE_TERMINAL`, never silently `AVAILABLE`. Pure classifier,
no mutation. Tests per state + staleness boundary.

### 2. Vision requirement: nested tool-output images (`upstream #15977`)
Vision requirement detection (used by ComboPlanner caps + guardrail reroute
decision) must count images nested in Responses `function_call_output` /
`custom_tool_call_output` items, not only top-level `image_url`/`image` parts.

### 3. Responses readable compaction (`upstream #16101`)
Responses input normalization: an item of type `compaction` /
`compaction_summary` / `context_compaction` whose `encrypted_content` starts
with the bridge prefix `ocx1:` decodes strict base64 → UTF-8 and is restored as
a `user` message `input_text` prefixed by the summary marker; malformed payloads
raise `unsupported_feature`; a bare `{"type":"context_compaction","id"?}` marker
passes through unchanged.

### 4. Provider-reported search cost, opt-in (`upstream #16088`)
New opt-in flag `settings.data.search.providerReportedCost` (default off).
When on, a successful `/v1/search`+`/api/v1/search` response that carries a
`cost` number (body or header) bills that USD amount into the usage ledger and
per-key USD limit accounting instead of the neutral 0. Off → unchanged.

### 5. Empty stop = valid empty turn (`upstream #16091`)
A streaming upstream response that terminates with a normal `stop`/`end_turn`
finish reason but zero content bytes is a **valid** completion (finishReason
`stop`), not a provider failure — no lockout/cooldown/breaker signal, no
cascade retry on that ground. A stream dying before any terminal stays an error.

### 6. Proxy-subscriptions trio (`upstream #15852/#16104/#16067`)
- Refresh of a **disabled** subscription still upserts fetched nodes but no
  longer deletes rows the feed omits (skip stale-node removal while disabled).
- A throttled selector repeat is recorded on the subscription row
  (`lastThrottledAt`, count) without raising a standing failure warning.
- Selector ordering prefers thrifty/free rungs before paid for auto selection.

### 7. Unannotated synced models route by endpoint type (`upstream #16017`)
Image-generation requests for a synced model without capability annotations
follow the provider node's `endpointType` (openai-compatible image endpoint →
generate; otherwise skip), instead of being excluded. Embeddings routing
unchanged.

### 8. No replay of inference POSTs after response-start (`upstream #15787`)
The gateway fetch retry policy must not re-POST an inference request once any
response bytes (headers/body) were received before the timeout/failure — replay
only pre-response transport failures. Applies to cascade retries inside the
pipeline.

### 9. API-key model policy post-resolution (`upstream #13052`)
Restricted keys (`AccessRestricted`) are re-checked **after** combo/connection
resolution: when the resolved concrete model differs from the requested alias,
the resolved model must also match `AccessAllow` (globs/combos as SPEC-083).

### 10. Registry additions (`upstream f966efb9ba` + registry diffs)
- `codebuddy-intl` (apikey gateway, `cbai` alias, `apiAuth.device` flow same as
  codebuddy-cn), plus registry additions: `venice-web` catalog entry,
  `cursor/grok47`, `stepfun` new models, `opencode` zen/go additions,
  `poixe-ai`, `kie` imageModels, `openai` additions — catalog/UI entries only,
  matching the fields our provider schema supports.

### 11. Anthropic key-test fix (`upstream #16146`)
Provider key test must not mark a key valid when every attempt fails — port the
guard (test requests that all return non-2xx ⇒ `invalid`).

## Explicitly NOT ported (architecture N/A)

- Async video-job poll retry + dual-consent STT + raw transcript retention —
  upstream runs a server-side multi-step video job runner; ours is a
  passthrough + kv stub surface (`/api/modality-bridge/*`). Marked backlog.
- 9router executor recovery + kiro opaque-reasoning/CodeWhisperer fallback —
  no 9router/kiro dedicated executors in our runtime (9router is a catalog UI
  entry only). Backlog.
- Upstream-internal: typed strategy registry refactor, rail tooling,
  i18n waves, release infra.

## Acceptance

- Each ported item has ≥1 targeted unit test; behavior verified against the
  upstream diff semantics, not just the commit message.
- `dotnet test` full suite green; endpoints keep their mounted shape.
- Doc `paridade-2026-10-10.md` updated after merge (wave-3 line amended).

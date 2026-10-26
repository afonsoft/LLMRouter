# SPEC-160 — Routing decision observability

Origin: OmniRoute route-decision transparency, request details, and quota-aware routing surfaces.

## Goal

Make every routed request explainable from the LLMRouter Logs screen.

## Scope

- Persist a compact attempt ledger: candidate order, strategy, skips, status, latency, cooldown,
  predicate result, and selected target.
- Expose a stable route-decision endpoint keyed by request id.
- Link the request log row to attempts, cost ledger, affinity, hedge, and shadow outcomes.
- Keep prompt/response payloads opt-in under the existing I/O logging setting.

## Acceptance criteria

- A completed request can be explained without reading server logs.
- Failed candidates and their reason are visible even when a later fallback succeeds.
- The detail shape is stable and redacts credentials/payloads by default.

## Tests

- Integration tests for fallback, hedge, predicate rejection, and shadow divergence.
- Authorization/redaction tests.
- UI contract tests for request detail rendering.

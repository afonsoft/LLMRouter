# SPEC-077 — Connection custom headers on every upstream call (incl. model discovery)

Ports upstream #16108: `providerSpecificData.customHeaders` (our `connection.Data.customHeaders`) is applied on model discovery too — a key that needs a routing header on every request (e.g. `anthropic-workspace-id` for a workspace-unscoped Anthropic key) can list models, not only chat.

## Scope

- `Core/Gateway/CustomHeaders.cs` — port of `connectionCustomHeaders.ts` + `upstreamHeaders.ts` denylists:
  - Forbidden set: hop-by-hop/framing (`host`, `connection`, `content-length`, `keep-alive`, `proxy-*`, `transfer-encoding`, `te`, `trailer`, `upgrade`) + origin-IP (`x-forwarded-*`, `x-real-ip`, `cf-connecting-ip`, `true-client-ip`, `client-ip`, `forwarded`, `via`) + auth (`authorization`, `x-api-key`, `x-goog-api-key`, `api-key`, `cookie`).
  - CR/LF/NUL in name or value dropped; non-string values skipped; same-named defaults replaced case-insensitively.
- Applied on: `GatewayEngine.BuildUrlAndAuth` (chat), `FetchModelIdsAsync` (`/api/synced-available-models` discovery), `POST /api/provider-connections/{id}/test`, `GET .../usage-quota` probe.

## Invariants

- Auth headers always come from the credential layer — a custom header can never override them.
- Discovery behaves identically to chat for custom headers.

## Tests

`tests/LLMRouter.Tests/CustomHeadersTests.cs` — 5 cases covering apply/deny/CRLF/replace/noop.

# SPEC-075 — Gateway protocol parity (OmniRoute 3.8.52 delta)

Close the remaining protocol-level gaps vs OmniRoute 3.8.52, ported faithfully
from `open-sse/utils/errorSanitization.ts`, `errorPathRedaction.ts`,
`credentialPatterns.ts`, `shared/utils/upstreamError.ts`,
`executors/bedrock.ts`, `services/usage/anthropicApiKey.ts` and
`services/notionStreamParser.ts`.

## Items

1. **Error sanitization** (`toJsonErrorPayload`): every upstream error body
   reaching a client passes a sanitizer — allow-listed fields
   (`type`,`code`,`param`,`reason`,`status`,`message`), credential redaction
   (known token shapes, labeled `key=value` assignments, URL userinfo/sensitive
   query params, PEM blocks, base64 data URLs, Bearer/Basic), stack-trace tail
   strip, absolute-path redaction, bounded security-escape decoding, 4KB cap.
2. **Responses refusal/phase**: `response.refusal.delta` maps to OpenAI
   `delta.refusal`; `phase` passes through on output_text deltas.
3. **Cache-creation tokens**: claude usage counts `cache_creation_input_tokens`
   and `cache_read_input_tokens` inside prompt total (never double-added).
4. **Anthropic rate-limit quota**: probe `POST /v1/messages` (haiku, max_tokens
   1) and read `anthropic-ratelimit-{requests,input-tokens,output-tokens,tokens}
   -{limit,remaining,reset}` headers → `GET /api/provider-connections/{id}/
   usage-quota`. Ratelimit headers also propagate upstream → client.
5. **Bedrock same-role merge**: consecutive same-role messages merge; a plain
   user turn never absorbs a following tool-result turn; empty filler removed.
6. **Notion text sanitize**: strip BOM + `<lang>` tags from notion-sourced text.

## Invariants

- No credential-shaped substring may reach a client error payload.
- Usage token math identical for inputs without cache fields.
- Streaming output stays valid SSE; new fields additive only.

## Tests

- Sanitizer: token shapes, labeled assignments, URL creds, PEM, stack tail,
  path redaction, allow-list payload, truncation.
- Bedrock merge ordering cases.
- Refusal/phase translation.
- ExtractUsage cache fields.
- Rate-limit header parsing + propagation.

# SPEC-003 — Format translators complete + remaining gateway endpoints

## Goal

Port the upstream translator layer faithfully so any client format reaches any provider
format. This is the compatibility heart of the product.

## Upstream sources

- `open-sse/translator/**` (9router: `formats/{openai,claude,gemini,responsesApi,maxTokens}.js`, `schema/`, `concerns/`; OmniRoute has the evolved TS version at `open-sse/translator/` — prefer it when they differ)
- `open-sse/handlers/{chatCore.js,embeddingsCore.js,responsesHandler.js,ttsCore.js,sttCore.js,imageGenerationCore.js,search/,embeddingProviders/,ttsProviders/,videoProviders/,imageProviders/}`
- `src/app/api/v1/**` + `src/app/api/v1beta/**` for endpoint surface

## Scope

### Translators (`LLMRouter.Core/Translation/`)

1. **claude.js** full port: `normalizeClaudePassthrough`, `prepareClaudeRequest`
   (system cache_control anchoring with 4-marker budget, `fixToolUseOrdering`,
   `ensureTrailingUserTurn`, empty-message filtering, thinking block handling with
   signature passthrough, `hoistToolResultImages`, max_tokens ceiling +
   thinking-budget reconciliation, `output_config`/`thinking.off` model quirks,
   `tool_choice` normalization).
2. **openai.js**: `filterToOpenAIFormat` (developer→system, thinking strip, block-type
   whitelist, tool_use→tool_calls, empty-message drop, `tools:[]` drop, Claude/Gemini
   tool normalization incl. `functionDeclarations`).
3. **gemini.js**: contents/parts ↔ messages, `systemInstruction`, `generateContent`
   response → openai chat completion, `convertOpenAIContentToParts`,
   `extractTextContent`, function call/response mapping, schema cleanup
   (`cleanJSONSchemaForAntigravity` core rules).
4. **responsesApi.js**: Responses API (`input`/`instructions`/`output` array) ↔
   chat.completions, incl. `reasoning`/`tool_calls`/`output_text` items.
5. **maxTokens.js**: `adjustMaxTokens` rules.

Both directions for each pair actually used by transports: openai, claude, gemini,
openai-responses, antigravity (gemini-wrapped).

### Gateway endpoints (all under /v1 + /api/v1)

- `POST /chat/completions` (exists) — route per provider `format`/`targetFormat`:
  claude upstream gets claude-translated body; gemini upstream gets gemini body;
  `openai-responses` models/upstreams go through the Responses translator.
- `POST /messages` + `POST /messages/count_tokens` (Anthropic inbound, any upstream).
- `POST /responses` + `POST /responses/compact` (Responses inbound).
- `GET /models`, `GET /models/{id}`, `GET /models/info`.
- `POST /embeddings`, `POST /images/generations`, `POST /audio/speech`,
  `POST /audio/transcriptions`, `GET /audio/voices`, `POST /search`, `POST /web/fetch`
  — passthrough to providers exposing the capability (serviceKinds) with the same
  fallback cascade; media providers themselves land in SPEC-010.
- `/v1beta/models/{model}:generateContent` + `:streamGenerateContent` + `GET /v1beta/models`
  — Gemini inbound → translate → cascade → translate back (SSE `streamGenerateContent`).

### Streaming

- Full SSE transform chain: openai chunks ↔ claude events (`message_start`,
  `content_block_*`, `message_delta`, `message_stop`, `ping`) ↔ gemini stream chunks.
  Preserve `usage`/`stop_reason`/`finish_reason` mapping and tool-call streaming
  (tool_use blocks ↔ tool_calls deltas with index tracking).

## Acceptance criteria

- Round-trip matrix green in tests: {openai, claude, gemini, responses} inbound ×
  {openai, claude, gemini, responses} upstream for a basic text chat + a tool call +
  a streaming request.
- Claude Code-style `/v1/messages` request with tools + system blocks reaches an
  openai provider correctly and its response re-emits valid Anthropic SSE.

## Tests

- Fixture-driven translator tests: capture real request/response samples for each
  direction (write fixtures under `tests/LLMRouter.Tests/Fixtures/`).
- SSE transform tests assert event sequence + finish_reason mapping.

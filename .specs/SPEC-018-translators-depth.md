# SPEC-018 — Translator depth (claude/openai/gemini/responses)

## Goal
Bring translators to upstream depth and prove the full round-trip matrix.

## Upstream sources
- `open-sse/translator/{claude,openai,gemini,responsesApi,maxTokens}.js`

## Scope
- claude.js missing pieces: `cache_control` system anchoring (4-marker budget),
  `fixToolUseOrdering`, `ensureTrailingUserTurn`, thinking block signature
  passthrough, `hoistToolResultImages`, max_tokens ceiling + thinking-budget
  reconciliation, `tool_choice` normalization.
- openai.js: developer→system, thinking strip, block whitelist, tool_use→tool_calls,
  empty-message/tools:[] drop, functionDeclarations normalization.
- gemini.js: schema cleanup rules, function call/response mapping.
- responsesApi.js: input/instructions/output ↔ chat.completions incl. reasoning
  and tool_calls items.
- Fixture-driven round-trip matrix tests: {openai,claude,gemini,responses} inbound
  × same upstream — text chat + tool call + streaming; fixtures in
  `tests/LLMRouter.Tests/Fixtures/`.

## Tests
- Matrix green; Claude-Code-style /v1/messages w/ tools+system → openai upstream →
  valid Anthropic SSE back.

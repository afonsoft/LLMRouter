# SPEC-106 — Semantic cache (opt-in) behind PromptCache

Origin: `comparativo-mercado` §8.6 — every enterprise gateway ships semantic
caching (Portkey, Kong vectordb, LiteLLM redis-semantic, Bifrost). Our
`PromptCache` is exact-match SHA256 only. Add an opt-in similarity layer.

## Work

- `settings.promptCache.semantic = {enabled, threshold: 0.0-1.0, embeddingModel,
  maxEntries}` — default disabled (documented: semantic cache misfires on
  agentic/tool traffic; LiteLLM says the same).
- Embedding source: reuse an existing provider connection — `embeddingModel`
  resolves through the normal gateway (`/v1/embeddings` self-call on the
  resolved connection); fallback = no semantic layer (fail-open to exact).
- Storage: `semanticCache` table (id, embedding BLOB float32, promptHash,
  model, responseJson, createdAt, hitCount). In-memory cosine scan up to
  `maxEntries` (5k default — fine at this scale; no vectordb dependency).
- Pipeline order: exact hit → semantic hit (≥threshold) → upstream. Never cache:
  streaming requests, requests with tools/tool_calls, `temperature > 0.2`,
  responses with `finish_reason != stop`.
- `GET /api/cache/semantic/stats` (entries, hitRate, avgSimilarity) + `DELETE
  /api/cache/semantic` flush; wire into existing cache page section.
- Guard: per-scope opt-out — connection `data.cacheOptOut` or apiKey restriction
  skips both layers (sensitive tenants never serve cached responses).

## Tests (≥5)

- near-identical prompt (embedding stub returns fixed vectors) hits semantic;
- below-threshold → upstream call recorded;
- tools/streaming/high-temp bypass both layers;
- threshold `1.0` degrades to exact-match behavior;
- `cacheOptOut` connection never serves or stores cache entries.

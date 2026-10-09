# SPEC-062 — Qdrant / memory embeddings

Upstream: `settings/qdrant/*`, `memory/{embedding-providers,rerank-providers,
engine-status,retrieve-preview,summarize,reindex,health}`. Memory is
substring/FTS + file/notion backends — no vector retrieval.

## Scope
- `Core/Memory/VectorMemoryBackend.cs` — IMemoryBackend writing embeddings
  to Qdrant (REST client) or an embedded fallback (sqlite-vec /
  brute-force cosine over stored vectors) selectable via
  `settings.memory.vector.mode`.
- `embeddingProviders` — config rows {provider,model,dims} using existing
  provider connections' /v1/embeddings; `rerankProviders` likewise.
- Endpoints: `GET/PUT /api/settings/qdrant` (url,key,collection),
  `GET /api/memory/embedding-providers` + `/rerank-providers`,
  `GET /api/memory/engine-status` + `/health`, `POST /api/memory/reindex`,
  `POST /api/memory/retrieve-preview` (semantic query → ranked items),
  `POST /api/memory/summarize` (LLM compress an item).
- UI: extend Memory page — backend picker gains `vector`, engine status
  card, retrieve-preview search box, reindex button.
- Tests: embedded-fallback backend stores+retrieves nearest item;
  settings round-trip; retrieve-preview ranking order.

## Non-goals
- Managed Qdrant provisioning (user supplies URL).

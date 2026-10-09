# SPEC-045 — Prompt cache

Upstream: `cache/{entries,reasoning,stats}` + `cache`, `cache/media` pages
(promptCache). Today every request hits upstream.

## Scope
- `cacheEntries` table: {id, hash, provider, model, request(json),
  response(json), tokensSaved, hits, createdAt, expiresAt}.
- `Core/Cache/PromptCache.cs` — SHA256 of normalized request body
  (messages+model+params, excluding stream/metadata); middleware in
  GatewayPipeline: hit → replay stored response (mark `x-cache: hit`);
  miss → store on success. TTL + max-size eviction in settings.
- Endpoints: `GET /api/cache/entries` (paged+filter), `GET /api/cache/stats`
  (hit rate, tokens saved, size), `DELETE /api/cache/entries/{id}`,
  `POST /api/cache/purge`, `GET /api/cache/reasoning` (entries that held
  reasoning blocks).
- UI: `/dashboard/cache` page — stats cards + entries table + purge;
  `/dashboard/cache/media` for media-kind cached responses.
- Tests: two identical requests → second is cache hit with same body and
  no upstream call; expired entry misses; purge clears.

# Semantic prompt cache (SPEC-106)

Opt-in similarity layer behind the exact-match `promptCache` (SPEC-045).
Lookup order per non-stream request: **exact hash → semantic cosine ≥ threshold → upstream**.

## Configuration

```jsonc
// settings.promptCache
{
  "enabled": true,
  "ttlMinutes": 60,
  "semantic": {
    "enabled": true,          // default false — opt-in
    "threshold": 0.85,        // cosine gate, clamped 0..1; 1.0 ≈ exact-only
    "embeddingModel": "text-embedding-3-small", // optional hint
    "maxEntries": 5000        // in-scan cap; oldest rows evicted past this
  }
}
```

## How it works

- **Embedding**: the first configured `settings.memory.embeddingProviders`
  connection (`POST {baseUrl}/v1/embeddings`), else the deterministic local
  char n-gram embedder (dim 128). Failures fall open to exact-only behavior.
- **Prompt text**: user/assistant message contents only — `system`,
  `developer` and `tool` roles are skipped so injected scaffolding
  (e.g. `Active skills: …`) doesn't make every prompt look identical.
- **Storage**: `semanticCache` table — `{id, embedding (float32 blob),
  promptHash, model, responseJson, createdAt, hitCount}`. In-memory cosine
  scan over the newest `maxEntries` rows.
- **Never cached/served**: streaming requests, bodies with `tools` /
  `tool_calls`, `temperature > 0.2`, responses with `finish_reason != "stop"`.
- **Opt-outs** (skip BOTH layers): `data.cacheOptOut: true` on the resolved
  connection, or `accessRestricted` on the API key.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/cache/semantic/stats` | `{entries, hits, misses, storedHits, hitRate, avgSimilarity}` |
| DELETE | `/api/cache/semantic` | Purge all semantic rows, reset counters → `{purged}` |

Semantic hits return `x-cache: semantic` and `x-cache-similarity: 0.xxx`
headers; usage is logged with `meta.cached=true, meta.layer="semantic"`.

## Tuning notes

- Start at `threshold: 0.9` for chat traffic; lower to ~0.85 for
  paraphrase-heavy workloads.
- A provider embedding model is strongly preferred over the local n-gram
  fallback — local embeddings treat same-language text as moderately similar
  (~0.4–0.6 for unrelated English), so thresholds below ~0.7 need a real
  embedding model to stay meaningful.

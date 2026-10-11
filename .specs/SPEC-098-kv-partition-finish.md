# SPEC-098 — kv partition finish: webhooks list + deliveries → tables

Finish the SPEC-087 kv partitioning work. Done already: `audit` → `auditEvents`
table, `memory` → per-item kv rows, `v1batches` counters partitioned. Remaining
single-row JSON blobs under write load:

- `kv("webhooks","list")` — the whole webhook list in one row; every upsert is
  read-modify-write of a growing array. Writers: `MgmtBatchEndpoints` (CRUD +
  enable/test/delete), `ExtrasEndpoints` (register/delete + KvIndex rebuild);
  readers: `WebhooksDispatchAsync` (Core/Extras), inspector.
- `kv("webhookDeliveries", webhookId)` — deliveries array appended per webhook;
  hot append path (one row per delivery is the upstream contract).

## Work

- New tables `webhooks` (Id PK, Url, Secret?, Events json, Headers json?,
  Enabled, CreatedAt, UpdatedAt) and `webhookDeliveries` (Id PK, WebhookId FK,
  At, Status, LatencyMs, Request?, Response?) via `EnsureCreated`-era pattern
  (no EF migrations in this repo — add entities + DbSets).
- Boot migration: if blob exists and table empty → split into rows, then mark
  `kv._migrated:{scope}` (same convention as audit/memory).
- Update every reader/writer listed above; keep ALL external contracts:
  - `GET/POST/PUT/DELETE /api/webhooks{,/{id}}` — same JSON shapes;
  - `GET /api/webhooks/{id}/deliveries` — `{webhookId, deliveries:[...]}`;
  - `POST /api/webhooks/{id}/test` + `/api/webhooks/validate-url`;
  - `Core.Extras.WebhooksDispatchAsync` semantics unchanged (event filter,
    headers merge, delivery record write);
  - `KvIndex` rebuild endpoint keeps working on scope `webhooks`.
- Delivery retention: keep last 100 per webhook (upstream behavior) on insert.

## Tests (≥4)

- blob → table migration runs once, no dup, `_migrated` flag set;
- webhook CRUD round-trip preserves response shape (fields a UI consumer uses:
  id, url, secret, events, enabled);
- dispatch writes one delivery ROW per send and `/deliveries` returns it;
- retention: after 105 inserts only newest 100 rows remain;
- `KvIndex` rebuild on scope `webhooks` still returns the list count.

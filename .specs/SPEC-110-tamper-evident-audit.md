# SPEC-110 — Tamper-evident audit hash-chain

Origin: `comparativo-mercado` §8.12 — nobody in the CLI-proxy niche has
integrity-proofed audit; cheap compliance differentiator ahead of the
multi-user work (org audit needs it more than solo use).

## Work

- Extend `auditEvents` with `prevHash` + `hash` (SHA256 of
  `prevHash|actor|action|target|meta|ip|at`). Write path: single in-process
  tail lock (or compute in `UsageSinkWorker` if SPEC-108 landed — chain order
  = insert order).
- `GET /api/audit/verify?from&to` — walks the range recomputing hashes;
  returns `{ok, checked, firstBrokenAt?}`; also a `GET /api/audit/verify/latest`
  quick check (last N rows).
- Compliance export (`/api/compliance/audit-log`) includes hash fields so an
  exported log is independently verifiable (document the recompute recipe).
- Purge interaction (SPEC-103): deleting middle rows breaks the chain —
  retention purge keeps a **chain anchor** (first hash of the first retained
  row + tombstone row `action=retention.purge, meta={cutoff, removed}`), so
  verification of the retained suffix still works. Document the caveat:
  pre-purge history isn't verifiable after purge (export before TTL).
- Migration backfill (runs under SPEC-109 migrations if it landed, else lazy
  backfill like SPEC-098 pattern): chain existing rows oldest→newest.

## Tests (≥4)

- write 3 events → chain verifies end-to-end;
- tamper a middle row (flip `target`) → verify reports `firstBrokenAt` = that row;
- purge-mid-history keeps verifiable suffix via anchor tombstone;
- export CSV/JSON carries hash columns.

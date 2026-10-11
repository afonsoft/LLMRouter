# SPEC-108 — Async usage sink (channel + batch writer)

Origin: `comparativo-mercado` §8.8 — `UsageWriter` writes `usageHistory` +
`requestDetails` per-request synchronously on EF Core/SQLite; under burst this
is the throughput ceiling (see perf-results p95≈458ms). LiteLLM batches
spend-log writes; move ours off the hot path.

## Work

- Replace direct `db.Add` in `UsageWriter` with a bounded
  `Channel<UsageRecord>` (capacity ~10k) consumed by a hosted service
  (`UsageSinkWorker`) that flushes batches (size 200 or 500ms, whichever first)
  in one transaction.
- `requestDetails` heavy `Data` payload goes through the same channel;
  `usageDaily` rollup updated from the same batch (single writer keeps it
  consistent).
- Backpressure contract: channel full → count `llmr_usage_dropped_total`
  (SPEC-101 metric) and **do not block the request path** (config
  `settings.usageSink.blockOnFull` default false; true = wait, for strict
  accounting installs).
- Graceful drain on shutdown (`IHostedLifecycleService.StoppingAsync` flush
  remainder with 5s budget).
- Keep synchronous mode behind `settings.usageSink.sync=true` escape hatch
  (debug + tests that assert immediate visibility).

## Tests (≥5)

- 200 requests → worker writes all rows (flush on batch boundary), request
  path never touches `DbContext.SaveChanges` inline;
- batch of mixed success/error rows persists status correctly;
- channel-full path drops + increments dropped counter without throwing;
- shutdown drains pending rows;
- `sync=true` preserves previous immediate-write behavior.

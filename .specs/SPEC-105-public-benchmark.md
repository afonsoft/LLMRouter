# SPEC-105 — Public benchmark: methodology + docs/perf.md

Origin: `comparativo-mercado` §8.5 — `perf-results.txt` is the only perf data in
the niche and it's buried at repo root with no methodology. Bifrost/TensorZero
win mindshare by publishing reproducible numbers; ours are competitive
(~279 rps single-node, p50 ≈ 9 ms) and should be citable.

## Work

- Formalize `scripts/perf/` (already: `run.sh`, `load.py`, `stub.mjs`) with
  knobs: `DURATION`, `CONCURRENCY`, `RATE`, `SCENARIO`
  (`single|combo|ratelimited|cached`). Keep CI invocation unchanged
  (`bash scripts/perf/run.sh`, ≥30 rps gate).
- Emit machine-readable `RESULT_JSON` (already present) **plus** a Markdown
  table row appended to `docs/perf.md#results` (env label, date, commit, rps,
  p50/p95/p99, errors).
- `docs/perf.md` — methodology: topology (stub upstream, no network), payload
  shape, what is measured (gateway overhead vs end-to-end), hardware note,
  how to reproduce (`bash scripts/perf/run.sh`), and honest caveats
  (single-node, SQLite, stub ≠ real LLM latency).
- Add a "compare" table putting our numbers next to published Bifrost/LiteLLM
  t3.medium-class figures, with source links and a caveat about methodology
  differences (mock latency, payload size).
- `perf-results.txt` → keep as last-run artifact; CI keeps uploading it.

## Tests (≥3)

- `load.py` accepts `RATE`/`CONCURRENCY`/`SCENARIO` envs (argparse defaults);
- `RESULT_JSON` line still emitted and parseable (CI gate intact);
- `docs/perf.md` results section gets a row appended by `run.sh`.

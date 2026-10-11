# Performance benchmark

Public, reproducible numbers for the LLMRouter gateway — see
`scripts/perf/` (SPEC-105). This page explains what is measured, how to
re-run it, and how our numbers compare to published competitor figures.

## Methodology

**Topology.** `scripts/perf/run.sh` boots:

1. `scripts/perf/stub.mjs` — a Node stub upstream that answers
   `POST /v1/chat/completions` with a canned 200 in ~1 ms. **No external
   network calls.**
2. A Release-published `LLMRouter.Server` on `PORT` (default 18000) backed by
   a throwaway SQLite `perf.db` (`LLMR_SYNC_WRITES=0` — async usage sink on).
3. 3 provider connections pointing at the stub, 3 fallback combos
   (`combo-a/b/c`) + one `auto/*` virtual combo, 6 API keys.

`scripts/perf/load.py` then drives `6 keys × 4 combos = 24` concurrent
sessions for `DURATION` (default 20 s) against `POST /v1/chat/completions`
with body `{"model":"<combo>","messages":[{"role":"user","content":"ping"}]}`.

**What is measured.** Per-request client-observed latency and achieved
throughput — i.e. *gateway overhead + stub*, not model latency. This is the
number that matters when comparing gateways: the upstream cost is identical
for every proxy in front of the same model.

**Knobs** (`load.py` flags / env):

| Flag | Env | Default | Meaning |
|---|---|---|---|
| `--rate` | `RATE` | 0 (unthrottled) | global target rps (token bucket) |
| `--concurrency` | `CONCURRENCY` | 0 (24) | cap on worker sessions |
| `--scenario` | `SCENARIO` | `combo` | `single`, `combo`, `ratelimited`, `cached` |

**Hardware.** CI runs on `ubuntu-latest` GitHub-hosted runners (2 vCPU-class);
local runs vary — each results row records the environment label.

## Reproduce

```bash
bash scripts/perf/run.sh                 # ~1 min: publish + seed + 20s load
DURATION=60 CONCURRENCY=48 bash scripts/perf/run.sh
SCENARIO=cached bash scripts/perf/run.sh
MIN_RPS=50 bash scripts/perf/run.sh      # custom gate (default ≥30 rps)
```

Raw per-second output and the `RESULT_JSON` line land in `perf-results.txt`
(kept as the last-run artifact; CI uploads it).

## Honest caveats

- **Stub ≠ real LLM.** A real provider adds 200 ms–30 s; these numbers isolate
  gateway overhead, which is the only fair axis for proxy-vs-proxy comparison.
- **Single node, SQLite.** Numbers are one process on one box; multi-node
  deployments (see `docs/analysis/evolucao-multiuser-openrouter.md`) change
  the envelope.
- **Comparisons are indicative.** Competitors publish on different hardware,
  mock latencies and payload sizes — treat the delta column as directional.

## Results

<!-- results:start -->
| date | env | commit | scenario | rps | p50 ms | p95 ms | p99 ms | errors |
|---|---|---|---|---|---|---|---|---|
<!-- results:end -->

## Compare (published figures, different methodologies)

| Gateway | Claim | Source |
|---|---|---|
| LLMRouter | ~279 rps, p50 ≈ 9 ms (stub, 24 sessions, single node) | this page / `perf-results.txt` |
| Bifrost (Go) | ~5–9 µs added overhead internally, thousands rps | [maximhq/bifrost README](https://github.com/maximhq/bifrost) |
| LiteLLM (Python) | ~100–500 rps typical single-instance, batch-dependent | [LiteLLM load test docs](https://docs.litellm.ai/docs/benchmarks) |
| TensorZero (Rust) | <1 ms gateway overhead @ 10k rps | [tensorzero.com benchmarks](https://www.tensorzero.com/) |

Published numbers are not normalized (hardware, mock latency, payload,
concurrency all differ). Our ~279 rps with p50 ≈ 9 ms on a 2-vCPU runner is
competitive in the personal-proxy niche; high-rps claims from Rust/Go gateways
assume larger instances and different payload shapes.

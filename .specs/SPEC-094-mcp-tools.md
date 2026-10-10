# SPEC-094 — MCP tools batch (close the ~37-tool gap)

Gap §3.4 of `docs/analysis/paridade-2026-10-10.md`: upstream ships ~69
`omniroute_*` tools; we ship 32 (+ `chat__*`). Add the cheap ones that map onto
existing endpoints/services — same naming style as current tools.

## Routing/introspection

- `route_request` — dry-run: `{model, prompt?}` → resolved provider/combo +
  reason codes (uses the `/api/explain/routing` logic).
- `explain_route` — same dry-run with factor-level detail (scoring inspector).
- `simulate_route` — like route_request but returns full candidate ordering.
- `best_combo_for_task` — `{taskType?}` → best auto/* combo for the task hint.
- `pick_fastest_model` — lowest recent-latency model from usage stats.
- `switch_combo` — `{combo}` → sets the active/default combo (settings).
- `test_combo` — `{comboId}` → run a test request against the combo, return
  per-model results.

## Config setters (the `set_*` family)

- `set_routing_strategy` — `{strategy}` → update adaptiveRouting strategy.
- `set_resilience_profile` — `{profile}` → breaker/cooldown profile preset.
- `set_compression_engine` — `{engine}` → default compression engine.
- `set_budget_guard` — `{dailyUsd?, monthlyUsd?, warnAt?}` → budget config.
- All write through the same settings/kv paths as the REST endpoints.

## Pool/cache/ops

- `pool_health` / `pool_status` / `pool_sessions` / `pool_reset` / `pool_warm` —
  provider pool visibility (breaker states, active sessions, warm-up request).
- `cache_stats` / `cache_flush` — HotCache/HybridCache counters + tag flush.
- `db_health_check` — sqlite integrity + table counts + DB size.
- `tool_search` — `{query}` → fuzzy match over the tool catalog.

## Usage/metrics getters

- `get_provider_metrics` — per-provider calls/latency/error-rate snapshot.
- `get_combo_metrics` — per-combo score + health roll-up.
- `get_session_snapshot` — current session/settings snapshot (safe fields).
- `cost_report` — spend by provider/model/day (`?from/to`).
- `check_quota` — quota pools/windows usage (extends existing quota tool).
- `list_models_catalog` — registry+synced catalog (paged).
- `sync_pricing` — trigger pricing sync (SPEC-092).
- `sync_token` — cli token sync status/trigger.

## Misc cheap

- `memory_clear` — clear memory store (kv scope `memory`) with `confirm:true`.
- `validation_echo` — echo args back (connectivity probe).
- `login_time` — session login timestamp.
- `ccr_store` / `ccr_retrieve` / `ccr_inspect` / `ccr_list` / `ccr_stats` /
  `ccr_delete` — context-compression store ops over `compression` tables.
- `skills_execute` / `skills_executions` — run a skill + list executions.
- `oneproxy_fetch` / `oneproxy_rotate` / `oneproxy_stats` — egress proxy ops.
- `rtk_discover` / `rtk_learn` — context/rtk endpoints when SPEC-095 lands;
  stub returning `{enabled:false}` is acceptable until then.
- `music` / `video` / `web_search` / `web_fetch` / `x_search` — thin wrappers
  over `/v1/music/generations`, `/v1/videos/generations`, `/v1/search`,
  `/v1/web/fetch` (loopback call, keep `V1Authed` semantics).
- `github_skills_search` / `github_skills_scan` / `github_skills_install` —
  github-sourced skills (reuse skills marketplace plumbing).
- `browser_pool_status` — headless browser pool status (stub if pool absent).

## Tests (≥8)

- each tool callable via `tools/call` (names registered + happy path);
- route_request/simulate_route return resolved provider + reasons;
- set_* writes reach the settings blob (read-back assertion);
- pool_* reflect a seeded breaker state;
- cache_stats exposes counters; cache_flush drops entries;
- db_health_check returns table counts;
- ccr_* round-trip a stored blob;
- media wrappers issue the loopback `/v1/*` call with auth.

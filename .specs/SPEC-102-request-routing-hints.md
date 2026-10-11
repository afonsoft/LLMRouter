# SPEC-102 — Request-level routing hints (OpenRouter-style `provider` object)

Origin: `comparativo-mercado` §8.2 — our routing is 100% server-side (combos);
clients can't ask "cheapest" / "no-fallback" / "only these providers" per call.
OpenRouter accepts a `provider` object in the request body; adopt a compatible
subset in `GatewayPipeline`/`ComboPlanner`.

## Work

- Accept optional `"provider": {...}` on `/v1/chat/completions`, `/v1/messages`,
  `/v1/responses` (all inbounds — normalize once in the pipeline pre-resolution):
  - `order: string[]` — provider slugs tried in order (maps to our provider ids);
    implied fallback sequence, overrides combo step order.
  - `allow_fallbacks: bool` (default true) — false = single attempt, no cascade.
  - `require_parameters: bool` — only targets whose model supports every param
    in the request (vision/tools/json_schema); reuse capability checks used by
    capability auto-switch.
  - `max_price: {prompt?, completion?}` — drop candidates above the $/Mtok cap
    (registry `pricing` + provider overrides).
  - `sort: "price"|"latency"|"throughput"` — order candidates by pricing asc /
    measured p50 upstream latency / tokens-per-sec from `usageHistory`.
  - `data_collection: "allow"|"deny"` — restrict to connections whose
    `data.noTraining`/`registry zdr flag` matches; add `zdr`/`trainsOnData`
    optional flags to `providers.json` entries + per-connection override
    (`data.trainsOnData`), default unknown → allowed.
- Precedence: request hints > combo strategy > defaults; unknown slug in
  `order` → 400 `invalid_provider`; hints on combo models only (bare
  `provider/model` bypasses like upstream single-target calls).
- Return chosen route in existing `explain/routing` shape + response header
  `x-llmr-route` (provider/model/connection) for observability.
- Doc note in `docs/` + playground simulate-route echoes resolved order.

## Tests (≥5)

- `order` forces provider sequence; fallback only within listed slugs;
- `allow_fallbacks:false` returns upstream error without cascade;
- `require_parameters` filters a vision request to vision-capable candidates;
- `max_price` excludes over-cap candidate (registry pricing fixture);
- `sort:price` picks the cheapest of 3 stub connections;
- `data_collection:deny` + connection flagged `trainsOnData` → skipped.

# SPEC-020 — Combo strategies expansion

## Goal
Port the remaining upstream combo strategies.

## Upstream sources
- `open-sse/services/combo.ts`, `docs/routing/AUTO-COMBO.md`

## Scope
- New strategies alongside priority/round-robin/sticky: weighted, random,
  strict-random, least-used, cost-optimized, p2c (power-of-two-choices),
  quota-weighted, headroom (remaining capacity), reset-aware, context-optimized
  (context-length fit), cache-optimized, lkgp (last-known-good-provider).
- `auto` = composite score over the factors we track (cost, latency, cooldown,
  usage) — simplified 16-factor port.
- fusion: parallel fan-out + judge model synthesis (needs second model param).
- pipeline: ordered transform chain mapping to conductor.
- Combo Studio UI: strategy selector with per-strategy options.

## Tests
- Each strategy's candidate ordering/selection unit-tested with stub usage data.

# SPEC-155 — Test-mode API p/ e2e (Antirion gap J)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.11.
- Gated por env `LLMROUTER_TEST_MODE=1` (fora do modo → 404).
- `POST /api/test/reset` (recria DB em LLMROUTER_DB_PATH), `POST /api/test/seed` (fixture
  mínima: provider fake, key, combo), `POST /api/test/clock` {offsetSec} (clock skew p/
  testes de quota window), `POST /api/test/fail` {provider,code,pct}.
- Atualizar e2e/smoke.mjs para usar seed em vez de mexer no DB direto.
- Testes: endpoints bloqueados sem flag; seed idempotente; clock afeta window.

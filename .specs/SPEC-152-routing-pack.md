# SPEC-152 — Routing pack: hedging + prefix-affinity + predicates + shadow (Antirion gap G)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.6.
- Hedging: `combo.hedge = {delayMs}` — dispara tentativa paralela p/ 2º candidato se a 1ª
  não responder no delay; vence quem chegar primeiro (cancela a outra); só non-stream.
- Prefix-affinity: kv `affinity:{hash(prompt-prefix)}` → deploymentId; TTL 15min;
  prioriza o candidato com o prefixo quente (vLLM/KV-cache) antes da strategy normal.
- Response predicates: `combo.predicates = ["finish_reason","nonempty_content","max_latency_ms:N"]`
  — 2xx que falha no predicate conta como erro e cai pro próximo candidato.
- Shadow mirror: `combo.mirror = {model}` — copia fire-and-forget da request p/ mirror,
  grava divergência (status/tokens) em `requestDetails.data.shadow`.
- Testes: hedge win/lose, affinity hit/miss, predicate reject→fallback, shadow não-bloqueia.

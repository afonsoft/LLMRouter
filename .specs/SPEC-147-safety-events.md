# SPEC-147 — Safety events + guardrails pack 1 (Antirion gap B)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.7.
- Tabela `safetyEvents` {id,at,guardrail,kind,action,severity,keyId,model,detail JSON,reviewed}.
- Guardrails novos (além de PromptGuard/ResponseGuard): `keywords`, `regex` (config por rule),
  `json_schema` (valida structured output), `tool_permission` (allowlist de tools por key/role);
  actions novas `flag` (marca e passa) e `divert` (re-rota p/ modelo fallback configurado).
- Wire no pipeline: input antes do route, output após tradução (non-stream) — stream: flag-only.
- API `/api/safety*`: list events (filtros kind/action/model/key), mark reviewed,
  stats by category/guardrail/key; `POST /api/guardrails/test` (dry-run de texto).
- UI `Pages/Safety.razor`: events table + review + stats cards + rule editor (kv `guardrailRules`).
- Testes: cada guardrail novo, actions flag/divert, event write, stats endpoint.

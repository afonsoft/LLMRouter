# SPEC-153 — Change control leve: config revisions + approvals (Antirion gap H)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.3.
- `configRevisions` {id,at,actor,area,patch JSON,prev JSON} — snapshot automático antes de
  writes em providers/connections/keys/settings sensíveis (middleware ou helper `Rev.Before`).
- `GET /api/config/revisions`, `/api/config/diff?id=N`, `POST /api/config/rollback` {id}
  — rollback reaplica `prev` e cria revision nova.
- `settings.changeControl {requireApproval:[areas]}` — write em área marcada retorna 409
  `change_request_required` e grava `changeRequests` {id,area,patch,state}; approve aplica.
- UI Settings > Revisions: lista + diff viewer (JSON side-by-side) + rollback button.
- Testes: revision written on write, rollback restores, 409 flow approve/deny.

# SPEC-129 — A2A persisted history + gamification XP/leaderboard/federation

> Spec retroativo — implementado no PR #164, merge 2026-10-12.

Origem: fila do `docs/analysis/feature-coverage-audit-2026-10-11.md` —
profundidade de a2a tasks e gamification que a auditoria flagou vs upstream.

## Work

### a2a (`/api/a2a/*`)

- `GET /tasks/history` — histórico persistido de tasks (distinto da lista
  in-flight): filtros `from`/`to`/`skill`/`state`/`limit` (clamp ≤500)/
  `offset`, retorna `{id,state,skill,createdAt,updatedAt,completedAt}` com
  nomes de estado upstream (`submitted|working|completed|failed|cancelled`,
  mapeados de `queued|running|done`). Estado inválido → 400.
- Coluna `A2aTask.Skill` + `skill` aceito em `POST /tasks`
  (migração `SPEC129_GamiDepth`).

### gamification (`/api/gamification/*`)

- Novas tabelas `userLevels` + `leaderboard`; `GamiCore` porta o upstream
  `xp.ts` verbatim (`XpForLevel = ⌊100·l^1.5⌋`, `CalculateLevel` via curva
  inversa + reconciliação de threshold).
- `EventAsync` agora alimenta `user_levels`, `leaderboard` global e streaks de
  dias consecutivos (kv `streaks`) — dados reais, não stubs.
- `GET /level?apiKeyId=` → `{level:{apiKeyId,totalXp,currentLevel,updatedAt},
  streak:{current,longest}}` (agregado = soma xp / max streak sem key).
- `GET /badges/earned?apiKeyId=` → `{badges}` por key ou distinct across keys.
- Federation: `GET /federation/leaderboard?scope&limit` +
  `POST /federation/score`, autenticado por `Bearer` →
  `PBKDF2(token,"omniroute-federation-salt",120000,SHA256)` batendo o
  `keyHash` de um server peer conectado (via `POST /servers {url,apiKey}`).
  401 ausente / 403 inválido / 400 limit fora de 1–200 — erros verbatim
  upstream.

## Tests

`Spec129A2aGamiDepthTests` (7): histórico a2a com filtros/estados/400, skill
persistida, curva de XP verbatim, level+streak, badges por key, federation
auth (401/403/400) e score postado.

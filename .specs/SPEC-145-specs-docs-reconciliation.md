# SPEC-145 — Reconciliação de specs/docs (specs retroativos + tracking desatualizado)

Origem: auditoria de `.specs/` em 2026-10-18 — todos os specs foram mergeados até
o PR #184 (SPEC-144), mas 6 features landed sem arquivo de spec e os docs de
tracking (`.specs/README.md`, `docs/analysis/feature-matrix.md`) ficaram
desatualizados. **Docs-only** — sem mudança de código/testes.

## Work

### Specs retroativos (reconstruir a partir dos PRs mergeados + evidência de código)

Criar os arquivos abaixo no formato padrão (`# SPEC-NNN — título`, origem,
`## Work`, `## Tests`), cada um com nota de cabeçalho
`Spec retroativo — implementado no PR #NNN, merge <data>`:

- `.specs/SPEC-126-cli-tools-depth.md` — PR #160 (cli-tools depth: registry,
  settings RW, apply, backups)
- `.specs/SPEC-127-agent-bridge-traffic-inspector.md` — PRs #161 + #162
  (agent bridge + traffic inspector; duas branches sob um número — documentar ambos)
- `.specs/SPEC-128-provider-passthrough.md` — PR #163 (provider passthrough
  fidelity: route-level validation + retirement gate)
- `.specs/SPEC-129-a2a-gami-depth.md` — PR #164 (a2a persisted history +
  gamification depth)
- `.specs/SPEC-130-provider-auth.md` — PR #165 (provider auth
  apply-local/export + refresh)
- `.specs/SPEC-140-element-parity-2.md` — PR #178 (element parity round 2 —
  input/select/icon-btn tokens vs OmniRoute; e2e: removeu a assertion de
  inset-shadow por paridade upstream, PR #182)

### SPEC-031 duplicado

- Manter `SPEC-031-plugins-search-tools.md` (superset) como canônico;
- fundir qualquer linha única de `SPEC-031-plugins-search.md` e deletar o
  arquivo duplicado.

### `.specs/README.md`

- Tabela do roadmap: SPEC-001..016 → `done` (todos landed).
- Cobrir os specs landed hoje ausentes das tabelas: 017–100 (parity waves),
  111, 112, 125, 131–139, 141–144 + os retroativos 126–130/140.
- Registrar nota: SPEC-080 nunca foi usado (número pulado, sem arquivo nem PR).
- Nova regra: o arquivo `.specs/SPEC-NNN-*.md` deve entrar **no mesmo PR** da
  implementação — previne a recorrência de 126–130/140.

### `docs/analysis/feature-matrix.md`

- Linhas 001–013 `🟡` → `✅`; 014–016 `⬜` → `✅`
  (MITM = 014+028+100; extras = 015+055/056; dist = 016+022).
- Tabela 1xx: 102–108, 110 `⬜` → `✅`; adicionar linhas 111/112 `✅`.
- Acrescentar índice compacto "SPEC-017+ landed" (lista/tabela resumida) para a
  matriz refletir o estado real sem virar dump de 130+ linhas.

## Verification (docs-only — sem testes unitários novos)

- `ls .specs/SPEC-{126,127,128,129,130,140}-*.md` — todos existem;
- `ls .specs/SPEC-031-*.md` — exatamente 1 arquivo;
- todo `SPEC-*.md` presente aparece nas tabelas do `.specs/README.md`
  (check scriptado: comparar `ls .specs` vs números citados no README);
- `feature-matrix.md` sem `⬜`/`🟡` para trabalho já mergeado em `main`.

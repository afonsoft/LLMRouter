# SPEC-058 — Skills extras

Upstream: `skills/{executions,collect/{chaos,detect,install},marketplace
(+install),skillssh(+install)}`. We have scan/toggle/install local
(SPEC-009/026/031).

## Scope
- `skillExecutions` table + run endpoint — execute a skill's declared
  command in a sandbox dir, capture stdout/stderr/exit; history per skill.
- `POST /api/skills/collect/detect` — detect skillable dirs (agentskills
  frontmatter) anywhere under configured roots; `.../collect` copies them
  into the skills store; `.../chaos` returns skills whose declared deps
  are missing on PATH.
- `marketplace`: settings-driven list of remote indexes (GitHub repo trees
  or skill registries); `GET /api/skills/marketplace` lists remote skills;
  `POST /api/skills/marketplace/install` downloads SKILL.md tree into store.
- `skillssh` — install from a `skills.sh`-style registry entry.
- UI: extend `/dashboard/skills` — executions tab, marketplace tab,
  detect/collect actions.
- Tests: detect finds fixture skill dir; collect imports it; marketplace
  install writes SKILL.md.

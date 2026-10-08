# SPEC-009 — CLI Tools, Translator UI, Skills pages

## Goal

`/dashboard/cli-tools` (install snippets + detected tools), `/dashboard/translator`
(quick format converter + translator for coding CLIs), `/dashboard/omni-skills` +
`/dashboard/agent-skills` (skills marketplace-ish pages).

## Upstream sources

- 9router `src/app/(dashboard)/dashboard/{cli-tools,translator,skills}/**`
- OmniRoute `src/app/(dashboard)/dashboard/{cli-code,cli-agents,translator,omni-skills,agent-skills}/**`
- `src/app/api/{cli-tools,translator,skills,agent-skills,github-skills}/**`,
  `config/cli-tools-manifest.json`, `src/lib/{cliTools/,translator*,agentSkills/,skills/}`
- `skills/` dir in 9router (bundled skill definitions)

## Scope

- CLI tools page: manifest-driven cards (name, detect command, install command,
  env vars to point at the router — `OPENAI_BASE_URL`, `ANTHROPIC_BASE_URL`, key);
  copy buttons; detected/not-detected status (`which`-style probing server-side).
- Translator: per-tool config generator (Claude Code, Codex, Gemini CLI, Cursor,
  OpenCode, Windsurf, Kiro, Zed, etc. from `cliTools/`) — produces the exact
  env/config block; inline format converter playground (paste openai body → see
  claude/gemini output using SPEC-003 translators).
- Skills pages: list bundled + user skills (`agentSkills` lib: SKILL.md frontmatter
  parse), enable/disable, install from GitHub (`github-skills` API).

## Tests

- Manifest parse; config generation for ≥3 CLIs; translator endpoint conversions.

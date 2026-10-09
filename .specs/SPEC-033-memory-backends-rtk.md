# SPEC-033 — Memory backends (kv/obsidian/notion) + filtros RTK

## Origem
Últimos módulos upstream não portados (pendencias item 18 + RTK): `memory/backend.ts`, `obsidianBackend.ts`, `notion/api.ts`, e o catálogo de filtros de compressão (compressRoles/skipRules/preservePatterns/enabledFilters).

## Escopo
- **`Core/Extras/RtkFilters.cs`**: catálogo de filtros nomeados aplicados ao body no token-saver quando `settings.tokenSaver` declara `filters`/`skipRules`/`preservePatterns`/`compressRoles`:
  - collapseWhitespace, dedupLines, dropEmptyLines (paridade com legado), stripComments (line + block), stripAnsi, truncateLines (maxLineLength), redactSecrets (sk-/AKIA/ghp_/xox/Bearer/JWT), minifyJson
  - `compressRoles` limita quais roles de `messages[]` são comprimidos; `skipRules`/`preservePatterns` regex por linha (preserve vence tudo)
  - `GET/POST /api/token-saver/filters` (catálogo + save)
- **`Core/Extras/MemoryStore.cs`**: backends pluggáveis via `settings.memory.backend`:
  - `kv` (default, comportamento SPEC-015), `obsidian` (1 arquivo `.md` c/ frontmatter por item em `settings.obsidian.vaultPath`), `notion` (1 página por item sob `settings.notion.parentId`, REST v1, token em `settings.notion.token` — mascarado no GET)
  - `/api/memory` GET/POST roteados pelo backend; `GET/POST /api/memory/backend` para config
- **UI**: TokenSaver.razor — checkboxes dos filtros + maxLineLength + compressRoles/skipRules/preservePatterns; Extras.razor memory — picker de backend + campos obsidian/notion

## Aceite
- `RtkMemoryTests` (7 testes: filtros, roles, obsidian+kv roundtrip) verdes; CI verde.

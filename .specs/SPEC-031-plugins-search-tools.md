# SPEC-031 — Plugins + search-tools funcionais

## Origem
Gap do `pendencias.md`: plugins e search-tools eram placeholder (sem execução).

## Escopo
- **Plugin hooks no gateway** (`LLMRouter.Core/Extras/PluginHooks.cs`):
  - Registry em `kv scope="plugins" key="registered"`: `[{id,name,enabled,hooks:[{on,action,value}]}]`
  - `onRequest`: `setModel` (re-escreve body.model antes da resolução), `addHeaders` (headers injetados em toda chamada upstream — pass-through + traduzido)
  - `onResponse`: `setModel`, `annotate` (adiciona `x_plugins` com nomes) na resposta traduzida não-stream
- **API**: `GET/POST/DELETE /api/plugins`, `GET /api/search-tools/registered`, `POST/DELETE /api/search-tools` (upsert por id)
- **MCP**: `searchTools.list` retorna conexões + registered; `searchTools.run` executa `webFetch` (GET url) e `search` (GET `{url}/search?q=`)
- **UI** (`Extras.razor`): seções "Registered plugins" (toggle/delete + add com hooks JSON) e "Registered tools" (add/toggle/delete)

## Aceite
- Testes `PluginHooksTests` (setModel+addHeaders, disabled skip, annotate) — 3 verdes
- CI verde

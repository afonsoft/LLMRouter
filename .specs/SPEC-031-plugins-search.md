# SPEC-031 — Plugins e search-tools funcionais

Pendência #17.

## Escopo
- Plugins: registry real em kv `plugins`/`registered` — cada plugin = {id,name,
  enabled,hooks[]}; hook `onRequest`/`onResponse` executados no pipeline do
  gateway (transformação de body mínima: addHeaders/setModel).
- Search-tools: `GET/POST /api/search-tools` CRUD (webSearch/webFetch configs);
  tool MCP `searchTools.run` executa webFetch de verdade (GET → texto).
- Telas: páginas plugins/search-tools consomem os endpoints reais.

## Aceite
- Plugin com hook onRequest alterando body é aplicado numa request de teste;
  searchTools.run retorna conteúdo de URL mockada; página lista estado real.

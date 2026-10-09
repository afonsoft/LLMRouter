# SPEC-032 — localCorpus real + i18n completo

## Origem
Gaps restantes do `pendencias.md`: localCorpus stub (item 18) e i18n parcial (item 3).

## Escopo
- **`localCorpus.search` (MCP)**: stub → busca real ranqueada por `MemorySearch.Score` sobre corpus composto: itens de memory (`kv memory/items`), docs embutidos e `skills/**/SKILL.md` (ContentRootPath, até 50 arquivos, 2KB cada). Retorna `{results, count}` com `source`, `id`, `content`, `score`.
- **i18n 100%**: pt-BR.json e es.json ganham as 13 chaves faltantes (login, mcp, models, onboard, placeholder, proxyPools, pxpipe, quota, services, status, tokenSaver, tokens, logsTimeline) — agora 163/163 nos 3 locales.
- Nota: `ForwardProxy` Proxy-Authorization já implementado na SPEC-023 (item do pendencias estava desatualizado).

## Aceite
- Build limpo server+client; suíte de testes verde; CI verde.

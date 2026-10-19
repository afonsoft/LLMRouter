# SPEC-151 — UX transversal: URL filters + compare + starred + CSV (Antirion gap F)
Origem: docs/analysis/antirion-gap-2026-10-19.md §2.1.
- `TimeRange` service do Client: presets + custom + `cmp` (janela anterior) na query string;
  Overview/Usage/Analytics/Logs passam a ler/gravar `?from=&to=&cmp=`.
- `useUrlFilters` equivalente: filtros de listas (Logs, Usage, Combos, Providers, Keys)
  persistem na URL — link copiado reabre a mesma visão.
- Starred/favorites: `localStorage` por tipo (model|combo|provider|key); filtro "Starred" nas listas.
- `CopyLinkButton` em páginas de detalhe (Combo, Provider, Model, Key).
- `<ExportCsv>` padronizado nas tabelas principais (Logs, Usage, Costs, Keys).
- Atalhos por página: `/` foca busca, `e` exporta, `f` abre filtros.
- Testes: parser de URL filters, compare-window math, csv output.

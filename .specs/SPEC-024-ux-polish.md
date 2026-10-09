# SPEC-024 — UX: i18n completo, conversations viewer, docs GFM

Pendências #3, #13, #15, #25.

## Escopo
- i18n: auditar todos os `Loc.T(...)` sem chave nas locales; completar en/pt-BR/es das páginas extras/inspector/tokens/mcp/conductor/onboarding/error/pxpipe.
- `/dashboard/conversations`: página com lista + detalhe (mensagens renderizadas, busca por texto).
- `MdToHtml`: tabelas GFM, listas aninhadas, headings com TOC anchor.
- Alinhar porta dev nos docs/README (5159 dev / 20128 Docker).

## Aceite
- `Loc.T` sem fallback pendente (script grep de chaves); conversa abre e renderiza mensagens; tabela GFM renderiza no docs viewer; teste do renderer.

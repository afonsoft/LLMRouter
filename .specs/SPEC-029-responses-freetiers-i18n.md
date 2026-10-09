# SPEC-029 — /v1/responses API, free-tier ranking, i18n gaps

Pendências #9, #12, #3.

## Escopo
- `POST /v1/responses`: Responses API shape (input string|array, output[] com
  message + output_text, usage {input_tokens,output_tokens,total_tokens}).
  Traduzir input→messages, rodar pipeline, traduzir resposta de volta; stream
  SSE com eventos response.created/in_progress/output_text.delta/completed.
- Free-tiers: `GET /api/free-tiers` rankear providers gratuitos por saúde real
  (breaker state + latência do último check + erro recente) em vez de substring.
- i18n: keys faltantes nas páginas novas (extras, inspector, tokens, mcp,
  conductor, onboarding, error) em en/pt-BR/es, formato nested das locales.

## Aceite
- Teste: /v1/responses non-stream retorna output_text; free-tiers ordena por
  score; Loc.T das páginas novas resolve em pt-BR.

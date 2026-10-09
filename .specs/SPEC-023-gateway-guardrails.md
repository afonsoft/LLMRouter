# SPEC-023 — Gateway: guardrails, Responses API, chaos por rota, proxy auth

Pendências #8, #9, #21 + bug ForwardProxy auth.

## Escopo
- `Guardrails` (Core): prompt-injection guard no gateway (regex/heurísticas: instruction override, data exfiltration, jailbreak keywords — portar lista de `open-sse` upstream), validação de body nas rotas `/v1/*` (model+messages obrigatórios, tipos), API key policy (escopo `data.scopes`, quota por key).
- `POST /v1/responses` — Responses API ↔ Chat Completions transformer (portar `open-sse/transformer/responsesTransformer.ts` — streaming incluído).
- Chaos por rota/modelo: `chaos` kv ganha regras `{match: "provider/model", errorPct, latencyMs}` avaliadas no pipeline, não só global.
- `ForwardProxy` respeita `Proxy-Authorization` (mesma API key do gateway).

## Aceite
- Testes: injection bloqueada (400/403), body inválido 400, /v1/responses round-trip, chaos por rota dispara só no match, proxy exige auth quando keys existem.

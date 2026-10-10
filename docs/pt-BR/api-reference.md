# Referência de API

Auth: `Authorization: Bearer sk-*` / `x-api-key` / `?key=` (tabela `apiKeys`).
Cookie de sessão do dashboard funciona em `/api/*`.

## Gateway (`/v1`, também `/api/v1`)

| Endpoint | Notas |
|---|---|
| `POST /v1/chat/completions` | formato OpenAI; SSE com `stream:true` |
| `POST /v1/messages` | formato Claude (+ SSE); headers `anthropic-ratelimit-*` propagados |
| `POST /v1/messages/count_tokens` | |
| `POST /v1/responses`, `/v1/responses/compact` | Responses API; refusal/phase preservados |
| `GET /v1/models`, `/v1/models/info`, `/v1/models/{*path}` | |
| `POST /v1beta/models/{m}:generateContent` / `:streamGenerateContent` / `:countTokens`, `GET /v1beta/models*` | formato Gemini |
| `POST/GET/DELETE /v1/files*` | upload/list/download; espelho `/api/files*` |
| `POST /v1/batches` | input_file_id JSONL ou requests inline |
| `/v1/vscode/{token}/api/*` | superfície Ollama+OpenAI pra extensões VS Code |
| `POST /v1/relay/chat/completions` | relay por token de cliente com quota diária |

Formatos não-gateway passam por `/v1/{*path}` para a conexão correspondente.

## Management (`/api/*`) — grupos principais

| Grupo | Cobre |
|---|---|
| `/api/combos*` | CRUD, `test`, `reorder`, `duplicate`, `builder/options`, `live`, `metrics` |
| `/api/providers*` | catálogo, CRUD de conexões, `test`, `test-batch`, `bulk`, `validate`, `usage-quota`, health/availability |
| `/api/models*` | registry + catálogos sincronizados, capability overrides, synced-available-models |
| `/api/keys*`, `/api/quota*` | grupos de keys, regenerate/reveal, limites de uso, planos, preview, pools |
| `/api/rate-limits*` | regras sliding-window |
| `/api/fallback-chains`, `/api/model-cooldowns`, `/api/routing/decisions` | ops de roteamento |
| `/api/usage*` | history, analytics summary, combo-health, utilization, model-latency, requests-by-provider-date, key-quota |
| `/api/settings*` | get/put, ops (purge, system-prompt, tiers, thinking-budget, auto-disable), routing settings |
| `/api/compression*`, `/api/context/*` | engines, combos/assignments, analytics, caveman |
| `/api/jobs*` | list/enable/run-now/runs do scheduler |
| `/api/db-backups*` | export `.db`, export-all JSON, import |
| `/api/log-export*` | destinations (file/webhook/s3), run/test/status |
| `/api/files*`, `/api/batches*` | files + batch rows |
| `/api/cli-credentials*`, `/api/cli/*` | scan/import de credenciais CLI, device login |
| `/api/evals*` | suites/cases/runs de avaliação |
| `/api/inspector*`, `/api/docs*`, `/api/openapi*` | request inspector, docs, OpenAPI explorer |
| `/api/a2a*`, `/api/conductor*` | tasks de agente + fleet |
| `/api/credentials/expiration`, `/api/credentials/expiring` | upsert de expiração + lista a expirar |
| `/api/policies*`, `/api/tags*`, `/api/sessions*` | evaluate de policies, tags CRUD, chat sessions |
| `/api/translator*` | detect/formats/history |
| `/api/oauth*`, `/api/token-health*`, `/api/free-tiers*` | fluxos OAuth, saúde de tokens, ranking de providers free |
| `/api/memory*`, `/api/rtk*`, `/api/skills*` | backends de memória + picker, filtros token-saver, skills |
| `/api/cache*`, `/api/prompt-cache*` | stats/purge de cache, replay cache |
| `/api/playground*` | improve-prompt, presets, simulate-route |
| `/api/translations` (+docs) | bundles i18n |

`model` do gateway aceita `provider/model`, nome de combo ou `auto/{x}`.
Erros sempre chegam ao cliente sanitizados (`ErrorSanitizer`).

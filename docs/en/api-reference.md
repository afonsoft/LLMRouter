# API Reference

Auth: `Authorization: Bearer sk-*` / `x-api-key` / `?key=` (`apiKeys` table).
Dashboard session cookie works for `/api/*`.

## Gateway (`/v1`, also `/api/v1`)

| Endpoint | Notes |
|---|---|
| `POST /v1/chat/completions` | OpenAI shape; SSE when `stream:true` |
| `POST /v1/messages` | Claude shape (+ SSE); `anthropic-ratelimit-*` headers propagated |
| `POST /v1/messages/count_tokens` | |
| `POST /v1/responses`, `/v1/responses/compact` | Responses API; refusal/phase preserved |
| `GET /v1/models`, `/v1/models/info`, `/v1/models/{*path}` | |
| `POST /v1beta/models/{m}:generateContent` / `:streamGenerateContent` / `:countTokens`, `GET /v1beta/models*` | Gemini shape |
| `POST/GET/DELETE /v1/files*` | file upload/list/download; `/api/files*` mirror |
| `POST /v1/batches` | JSONL input_file_id or inline requests |
| `/v1/vscode/{token}/api/*` | Ollama+OpenAI surface for VS Code extensions |
| `POST /v1/relay/chat/completions` | per-client-token relay with daily quota |

Non-gateway formats proxy through `/v1/{*path}` to the matched connection.

## Management (`/api/*`) — main groups

| Group | Covers |
|---|---|
| `/api/combos*` | CRUD, `test`, `reorder`, `duplicate`, `builder/options`, `live`, `metrics` |
| `/api/providers*` | catalog, connections CRUD, `test`, `test-batch`, `bulk`, `validate`, `usage-quota`, health/availability |
| `/api/models*` | registry + synced catalogs, capability overrides, synced-available-models |
| `/api/keys*`, `/api/quota*` | key groups, regenerate/reveal, usage limits, plans, preview, pools |
| `/api/rate-limits*` | sliding-window rules |
| `/api/fallback-chains`, `/api/model-cooldowns`, `/api/routing/decisions` | routing ops |
| `/api/usage*` | history, analytics summary, combo-health, utilization, model-latency, requests-by-provider-date, key-quota |
| `/api/settings*` | settings get/put, ops (purge, system-prompt, tiers, thinking-budget, auto-disable), routing settings |
| `/api/compression*`, `/api/context/*` | engines, combos/assignments, analytics, caveman |
| `/api/jobs*` | scheduler list/enable/run-now/runs |
| `/api/db-backups*` | export `.db`, export-all JSON, import |
| `/api/log-export*` | destinations (file/webhook/s3), run/test/status |
| `/api/files*`, `/api/batches*` | files + batch rows |
| `/api/cli-credentials*`, `/api/cli/*` | CLI credential scan/import, device login |
| `/api/evals*` | eval suites/cases/runs |
| `/api/inspector*`, `/api/docs*`, `/api/openapi*` | request inspector, docs pages, OpenAPI explorer |
| `/api/a2a*`, `/api/conductor*` | agent tasks + fleet |
| `/api/credentials/expiration`, `/api/credentials/expiring` | expiry upsert + expiring list |
| `/api/policies*`, `/api/tags*`, `/api/sessions*` | policy evaluate, tag CRUD, chat sessions |
| `/api/translator*` | detect/formats/history |
| `/api/oauth*`, `/api/token-health*`, `/api/free-tiers*` | OAuth flows, token health, free provider rankings |
| `/api/memory*`, `/api/rtk*`, `/api/skills*` | memory backends + picker, token-saver filters, skills |
| `/api/cache*`, `/api/prompt-cache*` | cache stats/purge, replay cache |
| `/api/playground*` | improve-prompt, presets, simulate-route |
| `/api/translations` (+docs) | i18n bundles |

Gateway `model` accepts `provider/model`, a combo name, or `auto/{x}`.
Errors are always sanitized (`ErrorSanitizer`) before reaching the client.

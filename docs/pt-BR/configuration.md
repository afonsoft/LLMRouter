# Configuração

## Variáveis de ambiente

| Variável | Padrão | Função |
|---|---|---|
| `ASPNETCORE_URLS` / `LLMROUTER_PORT` | `http://+:20128` | Bind HTTP |
| `LLMROUTER_DB_PATH` | `~/.local/share/LLMRouter/llmrouter.db` | Caminho do SQLite (`/data/llmrouter.db` no docker) |
| `LLMROUTER_ADMIN_PASSWORD` | — | Senha inicial do admin no primeiro boot |
| `LLMR_SYNC_WRITES` | `0` | `1` = escritas de telemetria inline (testes) |
| `LLMR_NADIR_API_KEY` / `OMNIROUTE_NADIR_API_KEY` | — | Chave da API de decisão Nadir (auto-router) |
| `LLMR_NADIR_BASE_URL` / `OMNIROUTE_NADIR_BASE_URL` | `https://api.getnadir.com` | Base URL do Nadir |
| `LLM_PROVIDER` / `LLM_BASE_URL` / `LLM_API_KEY` / `LLM_MODEL` | — | LLM externo usado por testes/agentes |
| `{PROVIDER}_OAUTH_CLIENT_ID/SECRET` | — | Fluxos OAuth device (página Tokens) |

## Settings (JSON `data` da row `settings`)

| Chave | Formato | Efeito |
|---|---|---|
| `autoRouter` | `{explorationRate, sla:{targetP95Ms,maxErrorRate,maxCostPer1MTokens,hardConstraints}, lkgp:{enabled}, nadir:{apiKey,baseUrl,timeoutMs}}` | tuning do auto-router |
| `comboAutoPruneStaleSteps` | `true/false` | auto-prune de steps obsoletos no sync |
| `comboDefaults` | `{kind, stickyLimit, ...}` | defaults aplicados a novos combos |
| `memory.backend` | `kv`/`obsidian`/`notion` | backend do memory store (+ `obsidian.vaultPath`, `notion.token`, `notion.parentId`) |
| `rateLimits` | array de regras | limites sliding-window por key/provider |
| `retention.usageDays` | `30` | prune do usageHistory (job) |
| `freeTier.providers` | array | pool de providers free |
| `guardrails.*` | toggles | checagens de prompt injection |
| `rtk.*` | config de filtros | filtros de economia de tokens no body |
| `compression.*` | mode/overrides/pipeline | compressão de contexto |
| `ipFilter`, `payloadRules`, `reasoningRoutingRules`, `taskRouting`, `oneproxy` | objetos | features de settings-routing |

## Providers e conexões

- `providers.json` — 288 providers (id, formato, modelos, capacidades, esquema
  de auth `bearer|cookie|none`, baseUrl) — registry é singleton.
- `providerConnections` — credencial/instância de um provider: o JSON `Data`
  guarda `apiKey`, override opcional de `baseUrl`, `customHeaders` (headers
  extras por conexão — nomes hop-by-hop + auth são filtrados), limites
  `quotaDaily`/`quotaMonthly`, metadados de expiração.
- `syncedModels` — catálogos de modelos obtidos ao vivo por provider (sync via
  `POST /api/synced-available-models`); `modelCapabilityOverrides` ajusta
  flags de capacidade por modelo.

## API keys

Rows `apiKeys`: `key`, `isActive`, limites (rpm/tpm/dailyTokens), regras,
plano opcional (`quotaPlans` + `quotaSchedules`), grupos (`keyGroups`).
Auth do gateway aceita `Authorization: Bearer`, `x-api-key` ou `?key=`.

## Jobs (settings `jobs.*`)

Built-ins: `proxy-pool-health` (15 min), `usage-prune` (24 h),
`db-backup` (24 h → `backups/`, mantém 14), `log-export` (5 min, agendamento
por destination). Gerenciados em `/api/jobs*` ou `/dashboard/jobs`.

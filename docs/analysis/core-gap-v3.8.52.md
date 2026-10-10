# Core gap vs OmniRoute 3.8.52 — gateway / routing / combos / providers

Comparação focada **só no core do gateway** (docker deployment). Fora de escopo por decisão: proxy/mitm/tunnels, CLI device login / vscode tokens / oauth de CLIs, Electron, gamification, radar, media, bots.

Legenda: ✅ temos · 🔶 parcial · ❌ falta

## Combos

| Upstream 3.8.52 | Status | Nota |
|---|---|---|
| `combos` CRUD + `combos/{id}` | ✅ | |
| `combos/auto` — combos virtuais `auto/*` (auto/best-coding, auto/glm, categorias/tiers) resolvidos de um candidate pool sem row persistida | ❌ | **maior delta de combo** — roteamento automático por capacidade/família |
| `combos/duplicate` — materializar `auto/*` em combo estático editável | ❌ | depende do auto |
| `combos/test` — probe do candidate pool do combo | ❌ | health-check por combo |
| `combos/reorder` — ordenação persistida | ❌ | trivial |
| `combos/builder/options` — presets do builder | ❌ | UI helper |
| `combos/metrics` | 🔶 | temos `/api/usage/combo-health` |
| `model-combo-mappings` | ✅ | |
| `settings/combo-defaults` — default combo/handoffModel por caso de uso | ❌ | |
| `settings/model-aliases` | ✅ | `/api/model-aliases` |

## Providers / conexões

| Upstream 3.8.52 | Status | Nota |
|---|---|---|
| CRUD providerConnections + test single | ✅ | |
| `providers/bulk` — criar várias key-connections de um provider | ❌ | |
| `providers/test-batch` — testar grupo de conexões com concorrência | ❌ | |
| `providers/validate` — validação de credencial | ❌ | |
| `providers/quota-windows` — janelas nomeadas de quota por provider + thresholds | ❌ | usado pelo cutoff modal de Provider Limits |
| `providers/expiration` — expiração de credenciais | ❌ | |
| `providers/deprecated` — flag/lista de providers deprecados | ❌ | baixa prioridade |
| `providers/health-autopilot(+actions)` — desativa/reativa conexões por saúde | 🔶 | temos auto-disable + `/api/settings/reenable-connections` + breakers |
| `monitoring/health` — saúde cached + deep check | 🔶 | `/api/health/connections` + `/api/monitoring/providers` |
| `token-health` | ✅ | |
| `free-provider-rankings`, `free-models`, `free-tier/summary` | 🔶 | `/api/free-tiers` existe; rankings/summary não |
| `synced-available-models` — catálogo de modelos realmente disponíveis upstream | ❌ | nossa lista vem do registry estático; upstream checa liveCatalogAvailability |
| providers CLI/oauth (codex-auth, claude-auth, cursor, zed, agy, command-code, client, web-session-contract) | — | **drop** por escopo |
| `openrouter-catalog/stats`, `volcengine-plan` | — | provider-específico, baixa prioridade |

## Routing / gateway core

| Upstream 3.8.52 | Status | Nota |
|---|---|---|
| combo strategies (fallback/priority/least-used/round-robin) + cooldowns por conexão | ✅ | ComboStrategies + resilience/breakers/cooldowns/lockouts |
| `resilience/model-cooldowns` — cooldown **por modelo** | ❌ | hoje só por conexão |
| `routing/decisions/{requestId}` — decisão de rota explicável por request | 🔶 | temos inspector/flows; não é o mesmo formato |
| `fallback/chains` — CRUD de cadeias de fallback nomeadas | ❌ | nosso fallback é via ordem do combo |
| `model-capability-overrides` — override de capacidades (vision/tools/json) por modelo | ❌ | |
| `modality-bridge` — ponte de modalidade (image/audio/video via modelos de texto) | 🔶 | temos vision-adapter combos só |
| `guardrails` | 🔶 | `Core/Guardrails/PromptGuard.cs` existe; endpoints/CRUD upstream não portados |
| `policies` — políticas de roteamento | ❌ | |
| `tags` — tags em modelos/providers | ❌ | |
| `sessions` — sessões de chat | ❌ | temos session-pools (outra coisa) |
| `translator/detect|translate|transform-stream|send|history` | 🔶 | temos `/api/translator` único; detect/history/stream não |
| `v1beta/models` | ✅ | |
| rate-limits, session-pools, files, batches, cache, evals | ✅ | |
| proxy-fallback, upstream-proxy, network, tunnels, mitm | — | **drop** por escopo |

## Proposta de ordem (core, simples→complexo)

1. `combos/reorder` + `providers/bulk` + `providers/validate` — pequenos, alto uso em docker
2. `providers/test-batch` — teste em lote das conexões (essencial p/ operar pool em docker)
3. `resilience/model-cooldowns` — cooldown por modelo (melhora roteamento real)
4. `combos/test` — probe do combo
5. `model-capability-overrides` + `synced-available-models` — capacidade/disponibilidade de modelos
6. `combos/auto` (+`combos/duplicate`) — auto-combos virtuais (o maior delta, mas maior)
7. `fallback/chains` + `routing/decisions` — fallback nomeado + explicabilidade
8. `settings/combo-defaults`, `providers/quota-windows`, `providers/expiration`, `tags`, `sessions`, `policies`, `free-provider-rankings` — complementos

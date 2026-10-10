# Routing gap — OmniRoute 3.8.52+ & 9router vs LLMRouter

Scopo: somente core de roteamento/LLM (combos, estratégias, configs, resiliência).
Proxy/MITM, tunnels, desktop, CLI-auth, media/modality-bridge, radar,
gamification = fora de escopo (decisão do dono).

## A. Modelo de combo — faltando no LLMRouter

| Upstream | O que é | LLMRouter |
|---|---|---|
| `combos/steps.ts` combo-ref | Step de combo pode referenciar OUTRO combo (`kind:"combo-ref"`) — combos aninhados, expandidos na resolução | só `provider/model` flat (combo-ref não resolve) |
| `combos/steps.ts` provider-wildcard | Step `provider/*` — “qualquer modelo deste provider” | não existe |
| `combos/steps.ts` routing metadata | steps carregam metadados de rota além do peso `~N` | só peso |
| `combos/compositeTiers.ts` | combos compostos em tiers com validação dedicada | não existe |
| `combos/invariants.ts` | `ComboInvariantError` + family patterns (gpt/claude/gemini/glm/kimi…) — validação de compatibilidade de família nos steps | sem validação de família |
| `combos/modelNameCollision.ts` | combo com nome == model id bare = mecanismo suportado de fallback por modelo (combo-antes-do-rewrite) | verificar precedência idêntica no ModelResolver |
| `combos/autoPromote.ts` | `comboAutoPromoteEnabled`: modelo que respondeu com sucesso é promovido pro topo do combo (reordenação persistida) | não existe |
| `combos/comboContext.ts` | context_window efetivo do combo a partir dos membros | não calculado |
| `combos/comboSort.ts` | sort methods manual/provider/score/name (UI) | parcial |
| `combos/builderDraft.ts` | draft state do builder (UI wizard: basics→steps→strategy→intelligent→review) | builder atual é simples |
| `combos/deadConfigKeys.ts` | higiene de config keys mortas | não existe |
| `combos/intelligentRouting.ts` | **MAIOR GAP**: `auto`/`lkgp` usam 16 pesos (quota, health, costInv, latencyInv, taskFit, stability, tierPriority, tierAffinity, specificityMatch, contextAffinity, cacheAffinity, sessionAvailability, resetWindowAffinity, connectionDensity, quality, reliability) + modePack + budgetCap + explorationRate + candidatePool | nosso auto-router (SPEC-078) é o conjunto simples rules/score — falta o port completo |
| `routing/adaptiveRouting.ts` | AllocationDecision allow/warn/deny + healthScore + capabilityScore + RoutingExplanation com reasons/factors | temos routingDecisions básicas, sem allocation gating nem explanation rica |

## B. Endpoints — faltando

| Upstream | O que é | LLMRouter |
|---|---|---|
| `model-combo-mappings` (glob) | pattern glob → combo com `priority` + `enabled`; gateway resolve `resolveComboForModel` por padrão (ex.: `gpt-*` → combo) | temos CRUD mas exact-match kv, sem glob/priority/enabled |
| `resilience/reset` | reset bulk de todos breakers + lockouts | falta (temos delete individual) |
| `quota/keys/{id}/models` | `qtSd/` virtual models — key restrita vê só os modelos dos pools de quota permitidos, inclusive em `/v1/models` | temos key restrictions por provider/model, não por pool de quota |
| `free-models` | catálogo FREE_MODEL_BUDGETS (monthlyTokens/creditTokens/freeType/poolKey) | temos free-tiers + rankings; budget catalog falta |
| `discovery/scan + results + verify` | probing de endpoints de provider na rede (local-only, anti-SSRF) — discovery de Ollama/llama.cpp em LAN | não existe |
| `intelligence/sync` | sync de Arena ELO → fator `quality` do intelligentRouting | não existe (alimenta o peso quality) |
| `provider-metrics` | métricas agregadas por provider | temos provider-stats; verificar paridade |
| `pricing/defaults + sync + models` | pricing com fontes + sync | temos GET/PUT básico; falta sync/defaults |
| `headroom/start|stop|status` | lifecycle do serviço headroom | só engine de compressão |
| `fallback/chains` | já temos | ok |
| `rate-limit` | deprecated → skip | — |
| `omniroute/route/preview` | preview de decisão de rota | temos simulate-route (paridade provável) |

## C. 9router — deltas de roteamento

| 9router | O que é | LLMRouter |
|---|---|---|
| `disabledModelsDb` | enable/disable por modelo (some de `/v1/models` e do roteamento) | temos capability overrides, mas não disable |
| `keyAccess` restricted | key restrita com allow-list de combos + provider-as-model kinds; msg `keyAccessDeniedMessage` | AccessAllow cobre provider/model; checar combos na allow-list |
| `comboPresets` | auto-seed de presets Cursor/Claude com aliases client-native (`cc/claude-sonnet-5`, `cu/*`) — routing zero-config | não existe (onboarding) |
| `antigravityQuota` | quota accounting provider-specific | menor |

## Plano proposto (simples → complexo)

- **SPEC-081** small knobs: resilience/reset, free-models budgets, disabled-models, comboSort/comboContext, deadConfigKeys.
- **SPEC-082** combo steps 2.0: nested combo-ref + provider-wildcard + step metadata + invariants (family validation) + autoPromote + modelNameCollision semantics.
- **SPEC-083** mappings/scoping: model-combo-mappings glob+priority+enabled, quota `qtSd/` key-model scoping, keyAccess combo allow-list (9router).
- **SPEC-084** routing brain: port completo do `intelligentRouting` (16 pesos + modePack + budgetCap + exploration) + `adaptiveRouting` (allow/warn/deny + explanations) + `intelligence/sync` (ELO → quality).
- **SPEC-085** discovery + onboarding: provider discovery scan + comboPresets auto-seed.

# Estratégias de Roteamento

Um combo roteia pelo `Combo.Kind`. Estratégias de ordenação retornam a lista
de candidatos reordenada; estratégias de execução rodam no endpoint.

## Ordenação (`ComboStrategies.OrderAsync`)

| Kind | Comportamento |
|---|---|
| `fallback` (default) | ordem declarada |
| `round-robin` | cabeça rotativa, `stickyLimit` requests cada |
| `random` / `strict-random` | shuffle / pick único |
| `lkgp` | último bom do combo primeiro |
| `cache-optimized` | afinidade por hash do prompt ao último vencedor |
| `weighted` | parse de `provider/model~N` |
| `least-used` | menor contagem no usageHistory |
| `cost-optimized` | mais barato por custo observado |
| `p2c` | power-of-two-choices |
| `auto` | score composto (latência/erro/quota) |
| `quota-weighted` | weighted-random por quota restante |
| `headroom` | maior quota restante primeiro |
| `reset-aware` | prefere reset de quota mais próximo |
| `context-optimized` | encaixa tokens do request no context length |

## Auto-router (SPEC-078, upstream `routerStrategy.ts`)

Candidatos recebem telemetria viva: estado do breaker, fração de quota,
custo/1M tokens, latência p95/média + stddev, taxa de erro (usageHistory).
Candidatos com breaker OPEN cedem aos saudáveis; all-OPEN devolve o pool
inalterado.

| Kind | Scoring |
|---|---|
| `rules` | quota .25 / health .2 / cost .2 / latency .15 / reliability .1 / stability .1 |
| `score` | `rules` + re-roll da cabeça por `explorationRate` |
| `cost` / `eco` | menor custo/1M primeiro |
| `latency` / `fast` | e2e .45 / erro .25 / estabilidade .15 / breaker .15 |
| `sla-aware` / `sla` | latência .35 / erro .35 / health .15 / custo .10 / estabilidade .05; `hardConstraints` ordena por violation score (defaults p95 2000 ms, err 5 %) |
| `lkgp` | último provider com sucesso (usageHistory) primeiro, depois rules — o kind `lkgp` de combo continua separado |
| `nadir` | `POST {base}/v1/bucket` `{prompt≤16k, menu≤100, source:omniroute}` + `X-API-Key`; `selected_model` encabeça a lista, rules ordena o resto; fail-open em qualquer erro + cooldown 30 s por baseURL. Config/env: `autoRouter.nadir.*`, `LLMR_NADIR_*`, `OMNIROUTE_NADIR_*` |

Config em `settings.data.autoRouter`.

## Execução

| Kind | Comportamento |
|---|---|
| `fusion` | fan-out paralelo pra todos candidatos + juiz |
| `pipeline` | steps de transformação sequenciais |
| `vision-adapter` | roteia requests de visão por membro vision-capable |

## Pré-passada (antes da ordenação)

- `modelCooldowns` — provider+modelo em cooldown é pulado.
- `fallbackChains` — chain nomeada substitui a ordem bruta do pool.
- Filtros `ProviderBreaker`/`ModelLockout`/`QuotaWindows`/`CooldownTracker`.
- `capabilityOverrides` + caps do registry pré-filtram (visão/tools/json/PDF).
- Toda escolha é registrada em `routingDecisions` (explicável via
  `GET /api/routing/decisions`).

## Combos virtuais `auto/*`

`auto/best|coding|fast|free` e `auto/{substring}` resolvem pool ao vivo:
conexões ativas × registry + catálogos sincronizados, filtrado por padrão de
nome. Usável direto como `model` no gateway sem combo persistido.

# Comparativo — OmniRoute 3.8.52 vs LLMRouter

Decisão de escopo (do dono): deploy docker-first → proxy/MITM, tunnels,
desktop Electron e auth por assinatura de CLI estão **fora de escopo** por
design. Todo o core de gateway/roteamento é portado fielmente (mesma
semântica do código `open-sse` upstream), não reimplementado de forma frouxa.

## Portado 1:1

| Área | Upstream | LLMRouter |
|---|---|---|
| Registry de providers | `providers.json` (~288 entradas) | mesmo arquivo, mesmos ids/formatos |
| Formatos do gateway | OpenAI/Claude/Gemini/Responses + SSE | `/v1/*` + `/v1beta/*` |
| Sanitização de erros | `toJsonErrorPayload` | `ErrorSanitizer` |
| Campos SSE refusal/phase | sim | sim (SPEC-075) |
| Tokens de cache-creation | contados no total de prompt | igual |
| Headers anthropic rate-limit | propagados | igual + probe `usage-quota` |
| Merge same-role do bedrock | sim | igual |
| Sanitize de texto notion | sim | igual |
| Headers custom por conexão | `connectionCustomHeaders.ts` + `upstreamHeaders.ts` | `CustomHeaders` — mesmos conjuntos proibidos (SPEC-077) |
| Disponibilidade tipada | `providerAvailability.ts` | `ProviderAvailability` — terminais confiáveis ≤24 h (SPEC-076) |
| Refs obsoletas de combo | `staleModelRefs` + prune | `StaleComboRefs` — flag + prune opt-in (SPEC-076) |
| Scoring por janela de quota | estratégia quota-aware | `RemainingQuotaScoreAsync` — frações janela + diária/mensal |
| Estratégias do auto-router | `routerStrategy.ts` + `nadirStrategy.ts` | `AutoRouter` — rules/score/cost/latency/sla/lkgp/nadir (SPEC-078) |
| Engines de compressão | 8 engines + framework | portadas; llmlingua/omniglyph stubs fail-open (SPEC-034) |
| Combos | CRUD, test, reorder, duplicate, builder, defaults | todos presentes |
| Model cooldowns / fallback chains / routing decisions | sim | sim (SPEC-070) |
| Ops de providers | test/test-batch/bulk/validate/synced-models | sim (SPEC-068/071) |
| Quota windows / expiração de credencial / tags / policies / sessions / translator | sim | sim (SPEC-073) |
| Combos virtuais auto/* | sim | sim (SPEC-072) |
| Backends de memória | kv/obsidian/notion | iguais (SPEC-033) |
| Filtros RTK | catálogo de 8 filtros | igual (SPEC-033) |
| Files/Batches | sim | sim (SPEC-040) |
| Keys/quota avançado | grupos, regenerate, reveal, limites, planos, preview | sim (SPEC-041) |
| Analytics de uso | ~7 endpoints + páginas | iguais (SPEC-036) |
| Import de credenciais CLI | claude/codex/cursor/kiro/trae/zed/agy | igual + scanners do LLMRouter (SPEC-035) |
| Scheduler de jobs | sim | sim (SPEC-038) |
| Backup/export/import de DB | sim | sim (SPEC-037) |
| Destinations de log export | sim | sim (SPEC-042) |
| Rate limiting | sim | sliding-window (SPEC-039) |
| Extras do playground | improve-prompt, presets, simulate-route | sim (SPEC-043) |
| Evals | suites/cases/runs + juízes | sim (SPEC-052) |
| A2A + Conductor | lifecycle de tasks + fleet | executor in-process (SPEC-053) |
| Session pools | sim | pool DB-backed (SPEC-049) |
| Prompt cache | sim | replay de requests idênticas (SPEC-045) |
| Settings ops | purge, system-prompt, tiers, thinking-budget | sim (SPEC-046) |
| Settings routing | ip-filter, regras payload/reasoning/task, free-proxies | sim (SPEC-047) |
| Relay / vscode tokens | sim | sim (SPEC-048/051) |
| Device login de CLI | sim | sim (SPEC-050) |
| OpenAPI explorer | sim | sim (SPEC-044) |
| i18n | en/pt-BR/es | mesmos bundles + fallback de locale |

## Fora de escopo (intencional)

Desktop Electron, proxy MITM/tunnels (cloudflared/ngrok/tailscale), oauth de
assinatura de CLIs nos providers (login codex/claude/cursor/zed/agy),
serviços sidecar (bifrost/cliproxy/dario/mux/openwa/llmlingua-server),
gamification/radar/media/integrações de bots, handler WebDAV (nosso backend
obsidian lê o vault direto).

## Só no LLMRouter

- `InternalChat` (dispatch in-process pra executores em background),
  `SqliteIdent` tipado pra SQL de backup, camada de throughput
  `HotCache`/`UsageWriter`, imagem docker + publicação de binário único,
  hook de teste `LLMR_SYNC_WRITES`.

## Docs de análise

- `docs/analysis/upstream-gap-scan.md` — gap scan completo original
- `docs/analysis/core-gap-v3.8.52.md` — gaps do core/gateway
- `docs/analysis/omniroute-3.8.52-delta.md` — delta dos commits da v3.8.52
- `docs/analysis/feature-matrix.md` — status por feature

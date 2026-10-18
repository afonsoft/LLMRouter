# Feature matrix — OmniRoute/9router → LLMRouter

Status: ✅ done · 🟡 partial · ⬜ planned (spec)

> 2026-10-18 (SPEC-145 audit): todo o programa de specs está mergeado em `main`
> (até PR #184 / SPEC-144). Todos os itens abaixo refletem estado real.

| Feature | Upstream page/API | Spec | Status |
|---|---|---|---|
| Dashboard shell (sidebar/header/theme/i18n) | `(dashboard)/layout`, `shared/components` | 001 | ✅ |
| Auth (local password + setup) | `/login`, `/api/auth/*` | 001 | ✅ |
| Provider registry (288) | `open-sse/config/providers` | 001 | ✅ |
| Gateway `/v1/chat/completions` (+combos+fallback) | `api/v1/chat` | 001 | ✅ |
| Gateway `/v1/messages` (claude) | `api/v1/messages` | 001 | ✅ |
| Gateway `/v1/models` | `api/v1/models` | 001 | ✅ |
| Usage logging | `usageHistory` etc | 001 | ✅ |
| Pages: home/endpoint/providers/models/combos/usage/logs/playground/settings | — | 001 | ✅ |
| Provider connections full CRUD+test+nodes | `/dashboard/providers` | 002 | ✅ |
| Translators complete + all inbound formats | `open-sse/translator` | 003 | ✅ |
| Embeddings/images/audio/search/responses endpoints | `api/v1/*` | 003 | ✅ |
| Combos advanced + studio + aliases | `/dashboard/combos*` | 004 | ✅ |
| Usage charts/costs/pricing/provider-stats | `/dashboard/{usage,analytics,costs}` | 005 | ✅ |
| Logs/console/timeline/health/resilience/export | `/dashboard/{logs,health}` | 006 | ✅ |
| All settings sections | `/dashboard/settings/*` | 007 | ✅ |
| Quota/token-saver/proxy-pools | 9router pages | 008 | ✅ |
| CLI tools/translator/skills pages | `/dashboard/{cli-tools,translator,skills}` | 009 | ✅ |
| Media providers (tts/stt/image/video/etc) | `/dashboard/media-providers` | 010 | ✅ |
| OAuth flows + token health | `src/lib/oauth`, modals | 011 | ✅ |
| MCP/A2A/conductor/orchestration | `/dashboard/{mcp,a2a,conductor}` | 012 | ✅ |
| Landing/docs/error pages/PWA/onboarding | `src/app/*` | 013 | ✅ |
| MITM relay + traffic inspector + pxpipe | `src/mitm`, pages | 014+028+100, 127 | ✅ |
| Gamification/radar/discovery/free-tiers/batch/etc | various | 015+055/056, 129 | ✅ |
| CLI + Docker + release CI | `bin/`, Dockerfile | 016+022 | ✅ |

## SPEC-1xx (melhorias de mercado)

| Feature | Eixo | Spec | Status |
|---|---|---|---|
| EF Core migrations | config | 109 | ✅ |
| Usage sink assíncrono | performance | 108 | ✅ |
| OTEL `/metrics` + OTLP (Prom/Grafana) | performance | 101 | ✅ |
| Routing hints por request | parametrização | 102 | ✅ |
| Audit retention TTL + purge | auditoria | 103 | ✅ |
| Config-as-code export/import | parametrização | 104 | ✅ |
| Benchmark público | performance | 105 | ✅ |
| Cache semântico | performance | 106 | ✅ |
| Response guardrails + PII | auditoria | 107 | ✅ |
| Audit hash-chain | auditoria | 110 | ✅ |
| Virtual keys + ledger + rate-limit por usuário | multiuser | 111 | ✅ |
| Catálogo público + signup | multiuser | 112 | ✅ |

## SPEC-017+ landed (índice)

O roadmap de paridade upstream e as waves seguintes estão completos —
o detalhe por spec está no H1 de cada `.specs/SPEC-*.md` e no índice de
`.specs/README.md`:

- **017–100** — parity waves (translators, MCP, routing brain, combos, MITM,
  media, ops, resilience, deltas v3.8.52, kv partition, llmlingua…): ✅
- **113–125** — UI-parity pages (activity, keys, providers, logs, analytics,
  costs, combos/models, health, profile, docs, settings, home, oauth import): ✅
- **126–130** — feature-coverage queue (cli-tools depth, agent-bridge +
  traffic inspector, provider passthrough, a2a/gami, provider auth): ✅
- **131–144** — element/UI fidelity + OpenRouter-style account (pagamentos,
  account keys/activity, combo pricing, generation detail, plan tiers): ✅

Nota: SPEC-080 nunca foi usado; SPEC-126–130 e SPEC-140 têm specs retroativos
(escritos na SPEC-145).

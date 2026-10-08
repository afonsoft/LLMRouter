# Feature matrix — OmniRoute/9router → LLMRouter

Status: ✅ done · 🟡 partial · ⬜ planned (spec)

| Feature | Upstream page/API | Spec | Status |
|---|---|---|---|
| Dashboard shell (sidebar/header/theme/i18n) | `(dashboard)/layout`, `shared/components` | 001 | 🟡 |
| Auth (local password + setup) | `/login`, `/api/auth/*` | 001 | 🟡 |
| Provider registry (288) | `open-sse/config/providers` | 001 | 🟡 |
| Gateway `/v1/chat/completions` (+combos+fallback) | `api/v1/chat` | 001 | 🟡 |
| Gateway `/v1/messages` (claude) | `api/v1/messages` | 001 | 🟡 |
| Gateway `/v1/models` | `api/v1/models` | 001 | 🟡 |
| Usage logging | `usageHistory` etc | 001 | 🟡 |
| Pages: home/endpoint/providers/models/combos/usage/logs/playground/settings | — | 001 | 🟡 |
| Provider connections full CRUD+test+nodes | `/dashboard/providers` | 002 | 🟡 |
| Translators complete + all inbound formats | `open-sse/translator` | 003 | 🟡 |
| Embeddings/images/audio/search/responses endpoints | `api/v1/*` | 003 | 🟡 |
| Combos advanced + studio + aliases | `/dashboard/combos*` | 004 | ⬜ |
| Usage charts/costs/pricing/provider-stats | `/dashboard/{usage,analytics,costs}` | 005 | ⬜ |
| Logs/console/timeline/health/resilience/export | `/dashboard/{logs,health}` | 006 | ⬜ |
| All settings sections | `/dashboard/settings/*` | 007 | ⬜ |
| Quota/token-saver/proxy-pools | 9router pages | 008 | ⬜ |
| CLI tools/translator/skills pages | `/dashboard/{cli-tools,translator,skills}` | 009 | ⬜ |
| Media providers (tts/stt/image/video/etc) | `/dashboard/media-providers` | 010 | ⬜ |
| OAuth flows + token health | `src/lib/oauth`, modals | 011 | ⬜ |
| MCP/A2A/conductor/orchestration | `/dashboard/{mcp,a2a,conductor}` | 012 | ⬜ |
| Landing/docs/error pages/PWA/onboarding | `src/app/*` | 013 | ⬜ |
| MITM relay + traffic inspector + pxpipe | `src/mitm`, pages | 014 | ⬜ |
| Gamification/radar/discovery/free-tiers/batch/etc | various | 015 | ⬜ |
| CLI + Docker + release CI | `bin/`, Dockerfile | 016 | ⬜ |

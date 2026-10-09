# SPEC-030 — PWA offline + E2E smoke no CI

Pendências #16, #23, #25.

## Escopo
- Service worker: cache-first para assets estáticos + network-first com
  fallback cache para GET /api/* (dashboard abre offline).
- Playwright smoke no CI: job separado que sobe o server e testa 3 fluxos
  (login → dashboard carrega, providers lista, playground renderiza).
- Alinhar porta: documentar 20128 como padrão; launchSettings usa ASPNETCORE_URLS.

## Aceite
- SW registrado e cobrindo /api GET; job e2e verde no CI; README consistente.

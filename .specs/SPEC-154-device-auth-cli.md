# SPEC-154 — Device authorization flow p/ CLI (Antirion gap I)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.11.
- `POST /api/cli/device` → {deviceCode,userCode,verifyUrl,expiresIn,interval};
  `POST /api/cli/device/token` {deviceCode} → pending|approved {token,scope:cli}.
- `GET /cli/device?code=USERCODE` (Client page) — usuário logado aprova/nega; grava em
  `cliDeviceCodes` table.
- CLI flow: `llmr login` (doc-only helper script em scripts/) abre verifyUrl e faz poll.
- Testes: issue→poll pending→approve→token, deny, expiry, wrong code.

# SPEC-144 — Plan tiers + platform fee + free tier
- `settings.payments.feePct` — % somada ao checkout (fee de plataforma).
- Free tier: usuário sem saldo → N req/dia (config) via BudgetPeriod daily.
- `/models` pública: bloco de tiers (Free/Standard) estilo pricing page.
- Admin users page: plano por usuário (free|standard) com rate-limit default.

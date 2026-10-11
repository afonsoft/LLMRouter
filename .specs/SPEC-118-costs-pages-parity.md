# SPEC-118 — Costs pages parity (`/dashboard/costs*`)

## Work

**Overview** (`/dashboard/costs`):
- Period selector: 7/30/90/180/365/All days.
- Cards: Spend today / Spend {period} / Projected window.
- Stat row: requests in window, active providers, active models, avg
  cost/request.
- "Cost Explorer": group-by selector (Provider/Model/API key/Account/
  Service) → aggregated table with cost bars; keep existing tables below.

**Pricing** (`/dashboard/costs/pricing`):
- Stat cards: providers with pricing / registry models / priced / user
  overrides counts.
- "Pricing sync" toggle + "Clear Synced Pricing" button (wires to existing
  pricing-sync endpoint if present, else add).
- Provider rows expandable → per-model Input$/1M, Output$/1M, override
  editor (writes `settings.catalogUserPricing`); coverage % bar per
  provider.

**Budget** (`/dashboard/costs/budget`): verify vs upstream; add missing
  fields per audit.

**Quota share** (`/dashboard/costs/quota-share`):
- Warning banner, "How Quota Share works" explainer, Available endpoints
  card (BASE URL + sample POST), Preview-by-key stats, illustrated empty
  state, "+ New group".

## Tests (≥5)

- Overview selector changes aggregation window (server or client).
- Group-by switches Explorer dimensions.
- Pricing override writes catalogUserPricing and shows as override.
- Coverage % computed from priced/total models.
- Quota-share empty state + endpoints card render.

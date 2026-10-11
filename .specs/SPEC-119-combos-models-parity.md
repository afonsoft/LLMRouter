# SPEC-119 — Combos + Model catalog parity (`/dashboard/combos`, `/dashboard/models`)

## Work

**Combos**:
- "Auto-routing catalog" section listing built-in auto combos
  (auto-fastest, auto-cheapest, …) resolved from connected providers.
- "Getting Started with Combos" 3-step onboarding card (dismissible).
- Alert banners row (missing combos, failure alerts, Health Agent).
- Tabs: All / Intelligent / Deterministic with counts + search box.
- Combo rows keep Test/Live/Edit/Delete; style row like upstream
  (name + model chips + mode badge + actions).

**Model catalog** (`/dashboard/models`):
- Search by model, provider, or capability; dropdowns All providers /
  All types.
- Columns: Provider (icon+name) / Model / Type / Capabilities chips /
  Context / Output / Flags; keep combo entries flagged as type=combo.

## Tests (≥4)

- Auto-routing section lists auto-* combos from connected providers.
- Tab filter narrows table (Deterministic hides intelligent).
- Catalog search matches capability text ("vision").
- Dismissed onboarding stays dismissed (localStorage).

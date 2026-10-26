# SPEC-161 — Dashboard migration fidelity fixtures

Origin: visual comparison of OmniRoute v3.8.52 and the LLMRouter Blazor dashboard.

## Goal

Turn the migration screenshots into repeatable visual/interaction contracts instead of one-off
manual checks.

## Scope

- Fixture states for Home, Providers, Analytics, Logs, and Settings.
- Browser checks for navigation, filters, empty states, loading/error states, dark/light/system themes,
  responsive widths, and EN/PT-BR labels.
- Screenshot baselines stored in `docs/screenshots/migration/`.
- A comparison report generated from the same fixture state.

## Acceptance criteria

- The five target routes render without zombie/blank pages in a fresh database.
- Critical controls have accessible names and keyboard focus.
- Screenshot drift is reviewed by route, not hidden in a full-page diff.

## Tests

- Playwright smoke and sidebar sweep.
- Focused accessibility assertions.
- `git diff --check` and screenshot dimension validation.

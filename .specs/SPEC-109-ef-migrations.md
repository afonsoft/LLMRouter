# SPEC-109 — EF Core migrations (replace `EnsureCreated`)

Origin: `comparativo-mercado` §8.9 — schema evolves via `EnsureCreated` + lazy
ad-hoc column adds (kv-partition migrations in SPEC-098 already paid that
price). Upstream has 202 migrations; we need the same upgrade discipline before
the multi-user work adds user/org tables.

## Work

- Add `dotnet ef` design-time setup (`Microsoft.EntityFrameworkCore.Design`)
  to `LLMRouter.Core`; generate `InitialCreate` migration matching today's
  full schema (baseline — existing DBs get `__EFMigrationsHistory` seeded so
  the migration is a no-op on upgrade).
- Replace `Database.EnsureCreated()` in `Program.cs` with
  `Database.Migrate()` behind `settings.database.autoMigrate` (default true;
  env `LLMR_SKIP_MIGRATE=1` to opt out for exotic recovery).
- Runtime migration for existing DBs: create `__EFMigrationsHistory` if absent
  and mark `InitialCreate` applied when the mirrored upstream tables already
  exist (detect `providerConnections` table) — else run it.
- CI step: `dotnet ef migrations has-pending-model-changes` (or diff check)
  to stop schema drift shipping without a migration.
- Remove the lazy `_migrated`-flag kv migration helpers superseded by real
  migrations (keep the flag read for downgrade safety).
- Doc note: `LLMROUTER_DB_PATH` unchanged; fresh dev DBs migrate on boot.

## Tests (≥4)

- fresh DB boots → schema applied via migrations (not EnsureCreated);
- pre-existing SPEC-098-era DB → boot succeeds, history seeded, no data loss;
- second boot is a no-op (idempotent);
- `LLMR_SKIP_MIGRATE=1` leaves an empty DB empty (expected failure surfaced).

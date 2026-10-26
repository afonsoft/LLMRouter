# SPEC-162 — Compatibility and upgrade validation

Origin: long-lived OmniRoute → LLMRouter migration maintenance.

## Goal

Validate that upstream-compatible data and configuration can be imported, migrated, backed up, and
rolled back safely across release waves.

## Scope

- Curated OmniRoute SQLite fixtures for provider connections, combos, keys, usage, and settings.
- Import/export compatibility tests with encrypted and redacted fields.
- EF migration smoke tests on fresh and upgrade databases.
- Rollback rehearsal and release upgrade checklist.

## Acceptance criteria

- A fixture from the pinned upstream baseline imports without schema loss.
- Secrets remain encrypted or redacted according to the existing storage policy.
- Upgrade and rollback are repeatable on a copy of the database.

## Tests

- Fixture round-trip tests.
- Migration-from-previous-version tests.
- Backup/restore and rollback integration tests.

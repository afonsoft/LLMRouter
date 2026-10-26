# SPEC-158 — Upstream release contract and drift revalidation

Origin: OmniRoute migration revalidation against `cheaper-inference/OmniRoute` v3.8.52.

## Goal

Make the .NET port reproducible across upstream releases and prevent a version label from silently
drifting away from the source tree that was actually compared.

## Scope

- Add a checked-in release manifest with upstream repository, tag, commit, date, and comparison scope.
- Add a deterministic check that the manifest tag resolves to the recorded commit.
- Record provider/model catalog counts and API/UI route counts as measured evidence.
- Add a release revalidation checklist for the next published v3.8.53 tag.
- Update the README and `.specs/README.md` to link the manifest and explain the pinned baseline.

## Acceptance criteria

- A clean checkout can verify the pinned upstream commit without network-dependent code changes.
- The check fails when the tag, commit, or manifest schema is inconsistent.
- Revalidation output distinguishes `ported`, `partial`, `intentionally out of scope`, and `missing`.
- No feature is marked as v3.8.53 until that upstream release exists.

## Tests

- Manifest parser and commit mismatch tests.
- CI check for malformed/duplicate entries.
- Documentation link and `git diff --check` validation.

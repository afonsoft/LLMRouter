# SPEC-157 — Release hardening: SBOM + provenance (Antirion gap N)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.11.
- Release workflow: gerar SBOM (anchore/syft JSON + SPDX) p/ binários e imagem;
  attestar com `actions/attest-sbom` + `actions/attest-build-provenance` (SLSA).
- Scan da imagem (trivy) como step não-bloqueante com upload SARIF.
- README: badge/linha "releases ship SBOM + provenance".
- Validação: rodar workflow numa tag de teste ou dry-run do job novo.

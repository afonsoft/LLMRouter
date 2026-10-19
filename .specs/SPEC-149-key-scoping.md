# SPEC-149 — API key scoping + anomaly feed (Antirion gap D)
Origem: docs/analysis/antirion-gap-2026-10-19.md §3.5.
- apiKeys ganha: `allowedEndpoints` JSON (ex: chat|embeddings|responses|*), `allowedIpRanges`
  JSON (CIDRs), `rotate` endpoint (nova sk-, revoga antiga com grace 5min).
- Enforcement em VirtualKeyGate/AuthenticatedKey: endpoint não permitido → 403 `key_scope`;
  IP fora dos ranges → 403 `key_ip` (respeta X-Forwarded-For quando `settings.trustProxy`).
- `keyAnomalies` {id,keyId,kind:new_ip|usage_spike|scope_denied,detail,at} — escritas pelo
  gate (thr: >3x média horária ou IP nunca visto); `GET /api/keys/anomalies`.
- UI Keys/ApiManager: campos de scope + badge de anomalias + rotate button.
- Testes: endpoint deny, CIDR allow/deny, rotate grace, anomaly write on new_ip.

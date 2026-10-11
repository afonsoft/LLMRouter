# SPEC-100 — MITM TLS: per-host intercept policy + WS tunnel + capture modes

`ConnectProxy` (SPEC-028) already does CONNECT + TLS interception + CA cert
minting + TrafficCapture on `LLMROUTER_PROXY_PORT` (default 8889) when
`settings.mitm.tlsIntercept` — the "MITM não implementado" note in the parity
doc was stale. Remaining real deltas vs upstream mitm.py:

## Work

- **Per-host intercept policy**: `settings.mitm.intercept` =
  `{mode: allow|deny|all, hosts: string[]}` (glob `*.example.com` / exact).
  When `tlsIntercept` is on, `InterceptAsync` only runs for matching hosts —
  non-matching CONNECTs get the existing blind tunnel. Default `all` keeps
  current behavior.
- **WebSocket upgrade**: inside the decrypted HTTP/1.1 loop, an `Upgrade:
  websocket` request currently dies (we buffer-and-close). Detect upgrade
  requests and fall back to a raw blind relay for the remainder of the
  connection (capture the handshake request/response in TrafficCapture,
  then pump bytes both ways).
- **Capture mode**: `settings.mitm.captureMode` = `full|headers|none`
  (default `full`). `headers` records flow rows without request/response
  bodies; `none` tunnels without capture. Applies to intercept path only.
- Wire `mitm/status` (InspectorEndpoints) to report {port, tlsIntercept,
  interceptMode, captureMode, activeConnections}.
- Doc note: HAR export/replay stays out of scope (agent-bridge desktop).

## Tests (≥4)

- allow/deny glob matching unit tests (host pattern matcher);
- deny-listed host under tlsIntercept gets blind tunnel (status/behavior flag
  observable — assert no flow row captured);
- captureMode `headers` stores a flow row with empty bodies;
- `mitm/status` reflects the configured mode fields.

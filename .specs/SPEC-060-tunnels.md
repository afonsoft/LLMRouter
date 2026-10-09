# SPEC-060 — Tunnels

Upstream: `tunnels/{cloudflared,ngrok,tailscale}/*` (install/enable/login/
start-daemon). Exposes the gateway publicly without port-forwarding.

## Scope
- `Core/Tunnels/TunnelManager.cs` — process supervisor abstraction
  {start,stop,status,logs,publicUrl} per backend.
- cloudflared: download binary (cached under {DbDir}/bin), quick-tunnel
  (`cloudflared tunnel --url`) or named tunnel with token; parse public
  URL from output.
- ngrok: `ngrok http <port> --authtoken`, API readout via local 4040 API.
- tailscale: `tailscale serve` / `tailscale funnel` status+enable
  (requires tailscaled present).
- Endpoints: `GET /api/tunnels` (all statuses),
  `POST /api/tunnels/{name}/start|stop`, `GET /api/tunnels/{name}/logs`,
  `PUT /api/tunnels/{name}/config` (tokens stored in settings).
- UI: `/dashboard/tunnels` page — three cards, start/stop, public URL
  copy, log tail.
- Tests: manager spawns stub binary and parses URL; status reflects exit.

## Non-goals
- Named-tunnel DNS management APIs.

# SPEC-028 — MITM: CONNECT + interceptação TLS

Pendência #4 (resto).

## Escopo
- `CONNECT host:port` no ForwardProxy: tunnel TCP cego + handshake TLS com cert por host assinado pela CA raiz (`/api/mitm/ca` já gera).
- Traffic inspector captura requests TLS descriptografados (host, path, headers, body) no mesmo feed do HTTP plano.
- Opt-in: `settings.mitm.tlsIntercept = true` (default só tunnel, sem decrypt).

## Aceite
- `curl -x ... https://` atravessa; com TLS intercept on + CA confiada, request HTTPS aparece no inspector; sem CA, tunnel cego funciona.

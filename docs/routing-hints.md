# Request-level routing hints (`provider` object)

SPEC-102 — OpenRouter-compatible subset. Clients can steer routing per request
on **combo models** (bare `provider/model` calls keep upstream single-target
behavior). Precedence: request hints > combo strategy > defaults.

```json
{
  "model": "my-combo",
  "messages": [...],
  "provider": {
    "order": ["anthropic", "openai"],
    "allow_fallbacks": false,
    "require_parameters": true,
    "max_price": { "prompt": 1.5, "completion": 8.0 },
    "sort": "price",
    "data_collection": "deny"
  }
}
```

| Field | Effect |
|---|---|
| `order` | Provider slugs tried in that order; implied fallback sequence (overrides combo step order). Unknown slug → `400 invalid_provider`. |
| `allow_fallbacks` | `false` = single attempt, no cascade. |
| `require_parameters` | Keeps only candidates supporting every capability the request needs (vision/tools/json_schema) — same detection as capability auto-switch. |
| `max_price` | Drops candidates above the $/Mtok cap (`prompt`/`completion`) using registry `pricing` + synced/user overrides. Unknown price is kept. |
| `sort` | `"price"` (registry asc) · `"latency"` (measured p50 from usageHistory) · `"throughput"` (measured tokens/sec). |
| `data_collection` | `"deny"` restricts to candidates not flagged for training — `trainsOnData` in `providers.json` or per-connection `data.trainsOnData`. Unknown → allowed. |

The resolved order is returned by `/api/playground/simulate-route` and the
chosen route rides the `x-llmr-route` response header
(`provider/model/connectionId`) on every served request.

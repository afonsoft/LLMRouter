# SPEC-121 — Profile/CLI pages parity + content fixes

## Work

**Profile** (`/dashboard/profile`) — currently renders "OAuth tokens"
content (wrong page). Restore/build the profile screen:
- Identity card (Explorer level, XP, streak) fed by gamification data —
  if gamification isn't implemented, show account card (name, key counts,
  member-since) + streak placeholder behind feature flag.
- Badges grid (unlocked/locked states) — same flag.
- Move OAuth env-vars table to its proper home (check where upstream
  puts it — providers detail or settings) or delete if duplicated.

**CLI Agents** (`/dashboard/cli-agents`):
- Detection status per agent (Found / not found) via existing detection
  endpoint; category tabs: Code tools / Broad autonomous agent / CLI used
  as backend; search box; card per agent with Install/How-to actions.

**CLI Code** (`/dashboard/cli-code`) — currently a duplicate of
cli-agents:
- "CLI profile auto-sync" section (checkboxes per CLI, auto-sync toggle,
  last-sync time).
- "CLI Code's" + "CLI companion" info sections.
- Keep env-var export blocks but move under each detected CLI card.

## Tests (≥4)

- /profile renders profile content (not OAuth tokens).
- cli-code no longer renders the cli-agents grid.
- Detection badges render found/not-found from endpoint data.
- Auto-sync toggle persists (settings write).

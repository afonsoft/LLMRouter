# SPEC-056 — Gamification completo

Upstream: `gamification/{badges(+earned),level,invite(+redeem),federation/
{leaderboard,score},notifications,rotate,servers,stream,transfer,anomalies}`
+ `gamification/admin` page. We have minimal `/gamification` + `/leaderboard`.

## Scope
- Tables: `badges`/`earnedBadges`, `invites`, `gamiNotifications`,
  `gamiServers` (federation peers), `gamiTransfers`, `gamiAnomalies`.
- Point events wired from usage (requests, combos created, streaks) →
  `level` computed; badges auto-awarded by rules table.
- Endpoints: `GET /api/gamification/{badges,earned,level,notifications}`,
  `POST /api/gamification/invite` + `/{code}/redeem`,
  `GET /api/gamification/federation/{leaderboard,score}`,
  `POST /api/gamification/rotate` (rotate federation identity),
  `POST /api/gamification/servers` (federate another instance),
  `GET /api/gamification/stream` (SSE), `POST /api/gamification/transfer`,
  `GET /api/gamification/anomalies`.
- UI: `/dashboard/gamification` rewritten — level bar, badge shelf, invite
  flow, federation leaderboard; `gamification/admin` page.
- Tests: usage event increments score; badge rule awards; invite redeem.

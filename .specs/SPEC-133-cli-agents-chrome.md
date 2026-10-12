# SPEC-133 — `/dashboard/cli-agents` upstream page chrome

Upstream `CliAgentsPageClient.tsx` wraps the tool grid in page chrome our
page was missing. Port verbatim:

- Header: title + subtitle + **Refresh detection** button (`GET /api/cli-tools/detect`,
  reloads `/api/cli-tools` + `/api/cli-tools/all-statuses`).
- Filter row: search input + detection `<select>` (`All` / `Installed` /
  `Not installed`) + visible count (`{count} tools`).
- Empty state: `search_off` icon + message when filters match nothing.
- Cards: terminal icon + name + binary badge + detected/not-found badge
  + status chips (`installed`, `configured`, `runnable`) from `all-statuses`.
  Status keys are short ids (`claude`) while `/api/cli-tools` ids are
  long (`claude-code`) — matched with Contains in both directions.
- Grouped grid (code/agent/ide) + env block + copy kept (our extra features).

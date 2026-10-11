#!/usr/bin/env bash
# Perf load harness: node stub upstream + seeded LLMRouter + concurrent load.
# Env: PORT (gateway, 18000), STUB_PORT (19001), DURATION (20s), MIN_RPS (30).
set -euo pipefail
cd "$(dirname "$0")/../.."
PORT="${PORT:-18000}"; STUB_PORT="${STUB_PORT:-19001}"
BASE="http://127.0.0.1:$PORT"; D="${DURATION:-20}"
DB="$(mktemp -d)/perf.db"; JAR="$(mktemp)"; KEYSF="$(mktemp)"
export DB__PATH="$DB" ASPNETCORE_URLS="$BASE" LLMR_SYNC_WRITES=0

cleanup() { kill "${SPID:-}" "${RPID:-}" 2>/dev/null || true; }
trap cleanup EXIT

node scripts/perf/stub.mjs & SPID=$!
dotnet publish src/LLMRouter.Server -c Release -o "$PWD/.perf-pub" -v q
dotnet .perf-pub/LLMRouter.Server.dll & RPID=$!
for i in $(seq 1 60); do curl -sf "$BASE/api/version" >/dev/null 2>&1 && break || sleep 1; done

api() { curl -s -b "$JAR" -c "$JAR" -H 'content-type: application/json' "$@"; }

# first-run: any password bootstraps admin
api -X POST "$BASE/api/auth/login" -d '{"password":"ci"}' >/dev/null

# provider connections on the stub (3 distinct names => several upstreams)
for n in a b c; do
  api -X POST "$BASE/api/provider-connections" -d "{\"provider\":\"openai\",\"name\":\"stub-$n\",\"data\":{\"baseUrl\":\"http://127.0.0.1:$STUB_PORT\",\"apiKey\":\"x\"}}" >/dev/null
done

# combos: 3 fallback chains + one live auto/* virtual used directly by name
api -X POST "$BASE/api/combos" -d '{"name":"combo-a","models":["openai/gpt-4o-mini","openai/gpt-4o"]}' >/dev/null
api -X POST "$BASE/api/combos" -d '{"name":"combo-b","models":["openai/gpt-4o","openai/gpt-4o-mini"]}' >/dev/null
api -X POST "$BASE/api/combos" -d '{"name":"combo-c","models":["openai/gpt-4o-mini"]}' >/dev/null

# 6 api keys => 6x4=24 concurrent sessions
for i in 1 2 3 4 5 6; do
  api -X POST "$BASE/api/keys" -d "{\"name\":\"k$i\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["key"]["key"])' >> "$KEYSF"
done
wc -l "$KEYSF"

COMBOS="combo-a,combo-b,combo-c,auto/gpt" GATEWAY_URL="$BASE" \
  python3 scripts/perf/load.py "$KEYSF" "$D" | tee perf-results.txt

# Append a citable results row to docs/perf.md (SPEC-105) — env label via
# PERF_LABEL, commit sha, and the RESULT_JSON metrics of this run.
if [ "${PERF_APPEND:-1}" = "1" ] && [ -f docs/perf.md ]; then
  python3 - <<'EOF' || echo "warn: could not append docs/perf.md row" >&2
import json, os, re, subprocess
line = [l for l in open("perf-results.txt") if l.startswith("RESULT_JSON=")]
if not line: raise SystemExit(1)
r = json.loads(line[-1].split("=", 1)[1])
sha = subprocess.run(["git", "rev-parse", "--short", "HEAD"],
                     capture_output=True, text=True).stdout.strip()
label = os.environ.get("PERF_LABEL",
    "ci" if os.environ.get("CI") else "local")
import datetime
row = (f"| {datetime.date.today():%Y-%m-%d} | {label} | {sha} "
       f"| {r.get('scenario','combo')} | {r['rps']:.0f} "
       f"| {r['p50'] if r['p50'] is not None else '-'} "
       f"| {r['p95'] if r['p95'] is not None else '-'} "
       f"| {r.get('p99') if r.get('p99') is not None else '-'} "
       f"| {r['errors']} |\n")
p = "docs/perf.md"; doc = open(p).read()
doc = doc.replace("<!-- results:end -->", row + "<!-- results:end -->")
open(p, "w").write(doc)
EOF
fi

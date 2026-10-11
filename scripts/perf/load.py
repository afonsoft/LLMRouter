#!/usr/bin/env python3
"""LLMRouter perf driver: worker pool of api-key x combo sessions hitting
POST /v1/chat/completions.

Usage: load.py <keys-file> [duration-s] [--rate RPS] [--concurrency N]
               [--scenario single|combo|ratelimited|cached]

Env fallbacks: GATEWAY_URL, COMBOS, DURATION, RATE, CONCURRENCY, SCENARIO,
MIN_RPS (exit gate).
"""
import argparse, json, os, sys, threading, time, urllib.request

p = argparse.ArgumentParser(description="LLMRouter perf load driver")
p.add_argument("keys_file", help="one api key per line")
p.add_argument("duration", nargs="?", type=int,
               default=int(os.environ.get("DURATION", "20")))
p.add_argument("--rate", type=float, default=float(os.environ.get("RATE", "0")),
               help="target requests/s across all workers (0 = unthrottled)")
p.add_argument("--concurrency", type=int,
               default=int(os.environ.get("CONCURRENCY", "0")),
               help="max worker sessions (0 = one per key x combo)")
p.add_argument("--scenario", default=os.environ.get("SCENARIO", "combo"),
               choices=["single", "combo", "ratelimited", "cached"],
               help="single=1 key/1 combo; combo=key x combo matrix (CI default); "
                    "ratelimited=matrix + invalid key probing error path; "
                    "cached=identical body repeated (exercises gateway cache)")
args = p.parse_args()

URL = os.environ.get("GATEWAY_URL", "http://127.0.0.1:18000") + "/v1/chat/completions"
COMBOS = os.environ.get("COMBOS", "combo-a,combo-b,combo-c,auto/gpt").split(",")
KEYS = [k.strip() for k in open(args.keys_file) if k.strip()]

pairs = [(k, c) for k in KEYS for c in COMBOS]
body_base = {"messages": [{"role": "user", "content": "ping"}]}
if args.scenario == "single":
    pairs, COMBOS = pairs[:1], COMBOS[:1]
elif args.scenario == "ratelimited":
    pairs = pairs + [("llmr-invalid-probe", c) for c in COMBOS]
elif args.scenario == "cached":
    body_base["messages"] = [{"role": "user", "content": "ping cached"}]
if args.concurrency > 0:
    pairs = pairs[: args.concurrency]

stats = {"lat": [], "ok": 0, "err": 0, "errs": {}}
lock = threading.Lock(); stop = threading.Event()
t0 = time.time(); issued = 0

def next_slot():
    """Global token bucket: spread request starts to args.rate/s."""
    if args.rate <= 0: return
    global issued
    while not stop.is_set():
        with lock: i = issued; issued += 1
        wait = t0 + i / args.rate - time.time()
        if wait <= 0: return
        time.sleep(min(wait, 0.5))

def worker(key, combo):
    body = dict(body_base, model=combo)
    while not stop.is_set():
        next_slot()
        t = time.perf_counter()
        try:
            req = urllib.request.Request(
                URL, data=json.dumps(body).encode(),
                headers={"Authorization": f"Bearer {key}",
                         "Content-Type": "application/json"}, method="POST")
            with urllib.request.urlopen(req, timeout=30) as r:
                code = r.status; r.read()
            lat = (time.perf_counter() - t) * 1000
            with lock:
                if code == 200: stats["ok"] += 1; stats["lat"].append(lat)
                else: stats["err"] += 1; stats["errs"][code] = stats["errs"].get(code, 0) + 1
        except Exception as e:
            code = getattr(e, "code", type(e).__name__)
            with lock:
                stats["err"] += 1; stats["errs"][code] = stats["errs"].get(code, 0) + 1

threads = [threading.Thread(target=worker, args=pr, daemon=True) for pr in pairs]
for t in threads: t.start()

last = 0
for i in range(args.duration):
    time.sleep(1)
    with lock: ok = stats["ok"]
    print(f"t+{i+1}s rps={ok-last} total={ok}")
    last = ok
stop.set()
for t in threads: t.join(timeout=2)

lat = sorted(stats["lat"]); n = len(lat); dur = time.time() - t0
rps = stats["ok"] / dur
print("\n=== RESULT ===")
print(f"scenario={args.scenario} duration={dur:.0f}s total={stats['ok']+stats['err']} ok={stats['ok']} err={stats['err']}")
print(f"throughput={rps:.1f} rps")
if n:
    print(f"latency ms: p50={lat[n//2]:.0f} p95={lat[int(n*.95)]:.0f} p99={lat[int(n*.99)]:.0f} max={lat[-1]:.0f}")
print(f"errors: {stats['errs']}")
print(f"RESULT_JSON={json.dumps({'rps': rps, 'p50': lat[n//2] if n else None, 'p95': lat[int(n*.95)] if n else None, 'p99': lat[int(n*.99)] if n else None, 'errors': stats['err'], 'scenario': args.scenario})}")
sys.exit(0 if rps >= float(os.environ.get("MIN_RPS", "30")) else 1)

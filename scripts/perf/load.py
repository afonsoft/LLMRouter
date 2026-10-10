#!/usr/bin/env python3
"""LLMRouter perf driver: N api keys x M combos as concurrent sessions hitting
POST /v1/chat/completions. Usage: load.py <keys-file> <duration-s>
keys-file: one api key per line. Combos/URL via env (defaults for CI)."""
import json, os, sys, threading, time, urllib.request

URL = os.environ.get("GATEWAY_URL", "http://127.0.0.1:18000") + "/v1/chat/completions"
COMBOS = os.environ.get("COMBOS", "combo-a,combo-b,combo-c,auto/gpt").split(",")
DURATION = int(sys.argv[2]) if len(sys.argv) > 2 else int(os.environ.get("DURATION", "20"))
KEYS = [k.strip() for k in open(sys.argv[1]) if k.strip()]

stats = {"lat": [], "ok": 0, "err": 0, "errs": {}}
lock = threading.Lock(); stop = threading.Event()

def worker(key, combo):
    while not stop.is_set():
        t0 = time.perf_counter()
        try:
            req = urllib.request.Request(
                URL, data=json.dumps({"model": combo,
                    "messages": [{"role": "user", "content": "ping"}]}).encode(),
                headers={"Authorization": f"Bearer {key}",
                         "Content-Type": "application/json"}, method="POST")
            with urllib.request.urlopen(req, timeout=30) as r:
                code = r.status; r.read()
            lat = (time.perf_counter() - t0) * 1000
            with lock:
                if code == 200: stats["ok"] += 1; stats["lat"].append(lat)
                else: stats["err"] += 1; stats["errs"][code] = stats["errs"].get(code, 0) + 1
        except Exception as e:
            code = getattr(e, "code", type(e).__name__)
            with lock:
                stats["err"] += 1; stats["errs"][code] = stats["errs"].get(code, 0) + 1

threads = [threading.Thread(target=worker, args=(k, c), daemon=True)
           for k in KEYS for c in COMBOS]
for t in threads: t.start()

t0 = time.time(); last = 0
for i in range(DURATION):
    time.sleep(1)
    with lock: ok = stats["ok"]
    print(f"t+{i+1}s rps={ok-last} total={ok}")
    last = ok
stop.set()
for t in threads: t.join(timeout=2)

lat = sorted(stats["lat"]); n = len(lat); dur = time.time() - t0
rps = stats["ok"] / dur
print("\n=== RESULT ===")
print(f"duration={dur:.0f}s total={stats['ok']+stats['err']} ok={stats['ok']} err={stats['err']}")
print(f"throughput={rps:.1f} rps")
if n:
    print(f"latency ms: p50={lat[n//2]:.0f} p95={lat[int(n*.95)]:.0f} p99={lat[int(n*.99)]:.0f} max={lat[-1]:.0f}")
print(f"errors: {stats['errs']}")
print(f"RESULT_JSON={json.dumps({'rps': rps, 'p50': lat[n//2] if n else None, 'p95': lat[int(n*.95)] if n else None, 'errors': stats['err']})}")
sys.exit(0 if rps >= float(os.environ.get("MIN_RPS", "30")) else 1)

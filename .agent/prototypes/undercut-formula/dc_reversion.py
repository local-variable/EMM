"""Does the Aether figure tell Cactuar anything its own history doesn't?

Scope is deliberately narrow: Cactuar as the World, Aether as its data centre.
No other world or DC is touched.

The gut-check ruling only earns its place if the DC carries information the
World's own series lacks. So this does not merely ask "does a big gap predict
reversion" - it asks whether the gap predicts anything AFTER the World's own
deviation from its longer-run level is accounted for. That is a partial rank
correlation, and it is the honest form of the question: a World trading above
its own 180-day level would tend to fall back anyway.

Timeline, with t = 30 days ago:
    world_30   median unit price, Cactuar, (t-30, t)
    dc_30      median unit price, Aether,  (t-30, t)
    world_180  median unit price, Cactuar, (now-180, t)   - the slow level
    forward    median unit price, Cactuar, (t, now)       - what happened next

A deep Aether history request returns HTTP 500, so this fetches a stratified
subset at a 90-day window, throttled to the same ceiling as everything else.
"""

import json
import math
import os
import statistics
import sys
import threading
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor

import stream
from wtype import TYPES, classify

WORLD = "Cactuar"
DC = "Aether"
SUBSET_PER_TYPE = 120
DAY = 86400

UA = "EorzeanMarketMaster-prototype/0.3 (github.com/local-variable/EMM; wayfinder ticket 11)"
HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "data_all")
DCDIR = os.path.join(CACHE, "dc_hist")

CONNECTIONS = 2
RATE_PER_SEC = 4.0
_lock = threading.Lock()
_next = [0.0]


def _throttle():
    with _lock:
        now = time.monotonic()
        slot = max(now, _next[0])
        _next[0] = slot + 1.0 / RATE_PER_SEC
    d = slot - time.monotonic()
    if d > 0:
        time.sleep(d)


def fetch(url, tries=3):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    for attempt in range(tries):
        _throttle()
        try:
            with urllib.request.urlopen(req, timeout=180) as r:
                return r.read()
        except Exception as e:
            if attempt == tries - 1:
                print(f"    give up: {e}", file=sys.stderr)
                return None
            time.sleep(4)


def pick_subset():
    """Items with enough Cactuar trade to measure, stratified by ware type."""
    per_type = {t: [] for t in TYPES}
    for w in stream.wares(min_sales=1):
        if w.hq:
            continue                       # NQ only: one series per item
        recent = len(w.within(60))
        older = len(w.within(30))
        if recent >= 20 and older >= 8:
            per_type[classify(w.meta)].append((recent, w.item))
    chosen = []
    for t in TYPES:
        per_type[t].sort(reverse=True)
        chosen += [i for _, i in per_type[t][:SUBSET_PER_TYPE]]
    return sorted(set(chosen))


def fetch_dc(items):
    os.makedirs(DCDIR, exist_ok=True)
    jobs = []
    for n in range(0, len(items), 10):
        chunk = items[n : n + 10]
        path = os.path.join(DCDIR, f"dc_{n:04d}.json")
        if os.path.exists(path) and os.path.getsize(path) > 2:
            continue
        s = ",".join(map(str, chunk))
        jobs.append((f"https://universalis.app/api/v2/history/{DC}/{s}"
                     f"?entriesToReturn=2000&entriesWithin={90 * DAY}", path))
    if not jobs:
        print(f"  DC history already cached ({len(items)} items)")
        return
    print(f"  fetching Aether history: {len(jobs)} requests", flush=True)
    done = [0]

    def work(job):
        url, path = job
        raw = fetch(url)
        if raw:
            tmp = path + ".part"
            with open(tmp, "wb") as f:
                f.write(raw)
            os.replace(tmp, path)
        with _lock:
            done[0] += 1
            if done[0] % 20 == 0:
                print(f"    {done[0]}/{len(jobs)}", flush=True)

    with ThreadPoolExecutor(max_workers=CONNECTIONS) as pool:
        list(pool.map(work, jobs))


def load_dc():
    out = {}
    if not os.path.isdir(DCDIR):
        return out
    for name in sorted(os.listdir(DCDIR)):
        if not name.endswith(".json"):
            continue
        with open(os.path.join(DCDIR, name), "r", encoding="utf-8") as f:
            payload = json.load(f)
        for k, v in payload.get("items", {}).items():
            out[int(k)] = [(e["timestamp"], e["pricePerUnit"])
                           for e in v.get("entries", []) if not e["hq"]]
    return out


def rank(v):
    order = sorted(range(len(v)), key=lambda i: v[i])
    r = [0.0] * len(v)
    i = 0
    while i < len(order):
        j = i
        while j + 1 < len(order) and v[order[j + 1]] == v[order[i]]:
            j += 1
        avg = (i + j) / 2.0 + 1.0
        for k in range(i, j + 1):
            r[order[k]] = avg
        i = j + 1
    return r


def spearman(xs, ys):
    if len(xs) < 10:
        return None
    rx, ry = rank(xs), rank(ys)
    mx, my = statistics.fmean(rx), statistics.fmean(ry)
    num = sum((a - mx) * (b - my) for a, b in zip(rx, ry))
    dx = math.sqrt(sum((a - mx) ** 2 for a in rx))
    dy = math.sqrt(sum((b - my) ** 2 for b in ry))
    return num / (dx * dy) if dx and dy else None


def partial(r_yx2, r_yx1, r_x1x2):
    d = math.sqrt(max(1e-12, (1 - r_yx1 ** 2) * (1 - r_x1x2 ** 2)))
    return (r_yx2 - r_yx1 * r_x1x2) / d


def main():
    print("DC reversion test - Cactuar within Aether", flush=True)
    subset = pick_subset()
    print(f"  subset: {len(subset)} items", flush=True)
    fetch_dc(subset)
    dc = load_dc()
    print(f"  Aether series loaded for {len(dc):,} items", flush=True)

    now = stream.NOW_TS
    t = now - 30 * DAY
    rows = []
    split = []
    for w in stream.wares(min_sales=1):
        if w.hq or w.item not in dc:
            continue
        world_30 = [s.price for s in w.sales if t - 30 * DAY <= s.ts < t]
        forward = [s.price for s in w.sales if s.ts >= t]
        world_180 = [s.price for s in w.sales if s.ts < t]
        d30 = [p for ts, p in dc[w.item] if t - 30 * DAY <= ts < t]
        if min(len(world_30), len(forward), len(d30)) < 8 or len(world_180) < 20:
            continue
        w30 = statistics.median(world_30)
        f30 = statistics.median(forward)
        w180 = statistics.median(world_180)
        d = statistics.median(d30)
        if min(w30, f30, w180, d) <= 0:
            continue
        rows.append((classify(w.meta),
                     math.log(w30 / w180),    # x1 own-series deviation
                     math.log(w30 / d),       # x2 DC gap
                     math.log(f30 / w30)))    # y  forward change

        # Split-half version. In the panel above, w30 appears in the
        # denominator of both predictors and the numerator of the outcome, so
        # ordinary measurement noise in w30 pushes the correlation negative on
        # its own - the classic division bias. Here the predictors are built
        # from the OLDER half of the window and the outcome is measured against
        # the NEWER half, so the two no longer share an estimate.
        a = [s.price for s in w.sales if t - 30 * DAY <= s.ts < t - 15 * DAY]
        b = [s.price for s in w.sales if t - 15 * DAY <= s.ts < t]
        da = [p for ts, p in dc[w.item] if t - 30 * DAY <= ts < t - 15 * DAY]
        if min(len(a), len(b), len(da)) < 5:
            continue
        wa, wb, dca = (statistics.median(a), statistics.median(b),
                       statistics.median(da))
        if min(wa, wb, dca) <= 0:
            continue
        split.append((classify(w.meta),
                      math.log(wa / w180),
                      math.log(wa / dca),
                      math.log(f30 / wb)))

    def panel(data, title, note):
        print("\n" + "=" * 96)
        print(title)
        print("=" * 96)
        print(f"  {note}")
        if len(data) < 30:
            print(f"  only {len(data)} usable Wares - not enough to say anything")
            return
        hdr = (f"  {'ware type':13s} {'wares':>7s} {'r(own dev)':>11s} {'r(DC gap)':>11s} "
               f"{'partial DC':>11s}   reading")
        print(hdr)
        print("  " + "-" * (len(hdr) + 10))
        for t_ in TYPES + ["ALL"]:
            sel = data if t_ == "ALL" else [r for r in data if r[0] == t_]
            if len(sel) < 25:
                continue
            x1 = [r[1] for r in sel]
            x2 = [r[2] for r in sel]
            y = [r[3] for r in sel]
            r1, r2 = spearman(x1, y), spearman(x2, y)
            r12 = spearman(x1, x2)
            if None in (r1, r2, r12):
                continue
            pr = partial(r2, r1, r12)
            reading = ("DC adds signal" if pr < -0.10
                       else "DC adds nothing" if pr > -0.05 else "marginal")
            print(f"  {t_:13s} {len(sel):>7,} {r1:>+11.3f} {r2:>+11.3f} "
                  f"{pr:>+11.3f}   {reading}")

    panel(rows, "TABLE 8a - does the Aether gap predict Cactuar's next 30 days?",
          "SHARED-BASELINE version - carries a division bias, see 8b.")
    panel(split, "TABLE 8b - the same question, split-half baseline (the one to trust)",
          "Predictors from days 30-15 back; outcome measured against days 15-0.")

    print("\n  Negative = a World trading above the reference falls back afterwards.")
    print("  'partial DC' is the DC gap's correlation with what happened next AFTER the")
    print("  World's own deviation from its 180-day level is accounted for. That is the")
    print("  column that matters: the first two overlap heavily with each other.")
    print("\n  Why two panels: in 8a the same 30-day median sits in the denominator of both")
    print("  predictors and the numerator of the outcome, so noise in that one estimate")
    print("  drags the correlation negative by itself. 8b builds the predictors from the")
    print("  older half of the window and measures the outcome against the newer half, so")
    print("  no estimate is shared. Where 8a and 8b disagree, 8b is the honest number and")
    print("  the difference between them is the size of the artefact.")
    print("\n  Caveats that neither panel fixes: one 30-day forward window on one World, so")
    print("  this is a single observation of a relationship rather than an estimate of its")
    print("  stability; and the Aether median is itself dominated by whichever worlds")
    print("  upload most.")


if __name__ == "__main__":
    main()

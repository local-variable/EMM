"""Stage 2: pull Universalis data for the whole 1000-item sample.

Resumable - every batch is its own cache file, so a 504 costs one batch and
not the run. Deliberately throttled far below the published ceiling (25 req/s,
8 simultaneous connections, both per IP): this issues one request at a time
with a pause between, which is roughly 1 req/s at worst.
"""

import json
import os
import sys
import time
import urllib.request

WORLD = "Cactuar"
DC = "Aether"
HISTORY_DAYS = 180
HISTORY_ENTRIES = 3000

UA = "EorzeanMarketMaster-prototype/0.2 (github.com/local-variable/EMM; wayfinder ticket 11)"
HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "data")
BULK = os.path.join(CACHE, "bulk")
BASE = "https://universalis.app/api/v2"


def items():
    with open(os.path.join(CACHE, "items.json"), "r", encoding="utf-8") as f:
        return sorted(int(k) for k in json.load(f))


def get(url, name, tries=4):
    os.makedirs(BULK, exist_ok=True)
    path = os.path.join(BULK, name + ".json")
    if os.path.exists(path):
        return False
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    for attempt in range(tries):
        try:
            with urllib.request.urlopen(req, timeout=180) as r:
                raw = r.read()
            break
        except Exception as e:
            print(f"    {name}: {e} (attempt {attempt + 1})")
            if attempt == tries - 1:
                print(f"    {name}: GIVING UP, will be missing from the analysis")
                return False
            time.sleep(8)
    with open(path, "wb") as f:
        f.write(raw)
    time.sleep(0.6)
    return True


def batched(seq, n):
    for i in range(0, len(seq), n):
        yield seq[i : i + n]


def main():
    ids = items()
    print(f"{len(ids):,} items")

    print("aggregated (world, dc) - batches of 100")
    for n, chunk in enumerate(batched(ids, 100)):
        s = ",".join(map(str, chunk))
        get(f"{BASE}/aggregated/{WORLD}/{s}", f"agg_world_{n:03d}")
        get(f"{BASE}/aggregated/{DC}/{s}", f"agg_dc_{n:03d}")
        print(f"  {n + 1}/10")

    print("listings (world) - batches of 100, 40 listings each")
    for n, chunk in enumerate(batched(ids, 100)):
        s = ",".join(map(str, chunk))
        get(f"{BASE}/{WORLD}/{s}?listings=40&entries=0", f"lst_world_{n:03d}")
        print(f"  {n + 1}/10")

    print(f"history (world) - batches of 20, {HISTORY_DAYS}d window")
    total = (len(ids) + 19) // 20
    for n, chunk in enumerate(batched(ids, 20)):
        s = ",".join(map(str, chunk))
        url = (f"{BASE}/history/{WORLD}/{s}"
               f"?entriesToReturn={HISTORY_ENTRIES}"
               f"&entriesWithin={HISTORY_DAYS * 86400}")
        fresh = get(url, f"hist_world_{n:03d}")
        if fresh or n % 10 == 0:
            print(f"  {n + 1}/{total}")

    files = os.listdir(BULK)
    size = sum(os.path.getsize(os.path.join(BULK, f)) for f in files)
    print(f"\n{len(files)} cache files, {size / 1e6:.1f} MB")


if __name__ == "__main__":
    sys.exit(main())

"""Stages 1b/2b: the ENTIRE marketable catalogue, not a sample.

Writes to `data_all/` so the seeded 1,000-item run in `data/` stays intact and
reproducible - the two are meant to be comparable, and a sample that quietly
became a census would destroy that.

Resumable: every batch is its own file. Interrupt and re-run freely.
"""

import json
import os
import sys
import threading
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor

# Citizenship, matching the ceilings this project set for itself in the safety
# posture ticket: at most 2 simultaneous connections against a published cap of
# 8, and a sustained rate well under the published 25 req/s. Both limits are
# per IP and that IP may be a household, so the prototype holds to the same
# rule EMM will.
CONNECTIONS = 2
RATE_PER_SEC = 4.0

_rate_lock = threading.Lock()
_next_slot = [0.0]


def _throttle():
    with _rate_lock:
        now = time.monotonic()
        slot = max(now, _next_slot[0])
        _next_slot[0] = slot + 1.0 / RATE_PER_SEC
    delay = slot - time.monotonic()
    if delay > 0:
        time.sleep(delay)

# EMM_WORLD picks which world to pull. Aether is the only data centre in
# scope. The Cactuar and Aether caches are COMPLETE and must not be refetched -
# a second world writes to its own directory and leaves them alone.
WORLD = os.environ.get("EMM_WORLD", "Cactuar")
DC = "Aether"
FETCH_DC = os.environ.get("EMM_FETCH_DC", "1") == "1"
HISTORY_DAYS = 180
HISTORY_ENTRIES = 3000

UA = "EorzeanMarketMaster-prototype/0.3 (github.com/local-variable/EMM; wayfinder ticket 11)"
HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, os.environ.get("EMM_DATA", "data_all"))
BULK = os.path.join(CACHE, "bulk")
BASE = "https://universalis.app/api/v2"

FIELDS = ",".join([
    "Name", "ItemUICategory.Name", "ItemSearchCategory.Name",
    "ItemSearchCategory.Category", "StackSize", "LevelItem.value", "Rarity",
    "CanBeHq", "IsUnique", "IsUntradable", "PriceLow", "PriceMid", "IsCollectable",
])


def fetch(url, tries=4, timeout=180):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    for attempt in range(tries):
        _throttle()
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except Exception as e:
            if attempt == tries - 1:
                print(f"    GIVING UP: {e}")
                return None
            time.sleep(4)


def cached(url, name, tries=4):
    os.makedirs(BULK, exist_ok=True)
    path = os.path.join(BULK, name + ".json")
    if os.path.exists(path) and os.path.getsize(path) > 2:
        return False
    raw = fetch(url, tries)
    if raw is None:
        return False
    tmp = path + ".part"
    with open(tmp, "wb") as f:
        f.write(raw)
    os.replace(tmp, path)   # never leave a half-written file for the resume
    return True


def run_batch(jobs, label):
    """jobs = [(url, name)], fetched over a bounded connection pool."""
    done = [0]
    lock = threading.Lock()

    def work(job):
        cached(*job)
        with lock:
            done[0] += 1
            if done[0] % 50 == 0 or done[0] == len(jobs):
                print(f"  {label} {done[0]}/{len(jobs)}", flush=True)

    with ThreadPoolExecutor(max_workers=CONNECTIONS) as pool:
        list(pool.map(work, jobs))


def batched(seq, n):
    for i in range(0, len(seq), n):
        yield seq[i : i + n]


def stage_metadata(ids):
    path = os.path.join(CACHE, "items.json")
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            have = json.load(f)
        if len(have) >= len(ids) * 0.98:
            print(f"metadata already cached: {len(have):,}")
            return
    else:
        have = {}
    todo = [i for i in ids if str(i) not in have]
    print(f"metadata: {len(todo):,} to fetch")
    for n, chunk in enumerate(batched(todo, 100)):
        rows = ",".join(str(x) for x in chunk)
        raw = fetch(f"https://v2.xivapi.com/api/sheet/Item?rows={rows}&fields={FIELDS}")
        if raw is None:
            continue
        for row in json.loads(raw.decode("utf-8"))["rows"]:
            f_ = row["fields"]
            isc = f_.get("ItemSearchCategory") or {}
            iuc = f_.get("ItemUICategory") or {}
            have[str(row["row_id"])] = {
                "name": f_.get("Name", ""),
                "searchCategory": (isc.get("fields") or {}).get("Name", ""),
                "searchCategoryId": isc.get("row_id", 0),
                "searchGroup": (isc.get("fields") or {}).get("Category", 0),
                "uiCategory": (iuc.get("fields") or {}).get("Name", ""),
                "stackSize": f_.get("StackSize", 1),
                "ilvl": (f_.get("LevelItem") or {}).get("value", 0),
                "rarity": f_.get("Rarity", 1),
                "canBeHq": bool(f_.get("CanBeHq")),
                "isUnique": bool(f_.get("IsUnique")),
                "isCollectable": bool(f_.get("IsCollectable")),
                "vendorBuy": f_.get("PriceMid", 0),
                "vendorSell": f_.get("PriceLow", 0),
            }
        if n % 20 == 0:
            print(f"  metadata {len(have):,}/{len(ids):,}")
            with open(path, "w", encoding="utf-8") as f:
                json.dump(have, f)
        time.sleep(0.25)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(have, f)
    print(f"  metadata done: {len(have):,}")


def main():
    os.makedirs(CACHE, exist_ok=True)
    raw = fetch(f"{BASE}/marketable")
    ids = sorted(json.loads(raw.decode("utf-8")))
    print(f"marketable catalogue: {len(ids):,} items")

    stage_metadata(ids)

    agg, lst, hist = [], [], []
    for n, chunk in enumerate(batched(ids, 100)):
        s = ",".join(map(str, chunk))
        agg.append((f"{BASE}/aggregated/{WORLD}/{s}", f"agg_world_{n:04d}"))
        if FETCH_DC:
            agg.append((f"{BASE}/aggregated/{DC}/{s}", f"agg_dc_{n:04d}"))
        lst.append((f"{BASE}/{WORLD}/{s}?listings=40&entries=0", f"lst_world_{n:04d}"))
    for n, chunk in enumerate(batched(ids, 20)):
        s = ",".join(map(str, chunk))
        hist.append((f"{BASE}/history/{WORLD}/{s}"
                     f"?entriesToReturn={HISTORY_ENTRIES}"
                     f"&entriesWithin={HISTORY_DAYS * 86400}", f"hist_world_{n:04d}"))

    print(f"aggregated: {len(agg)} requests", flush=True)
    run_batch(agg, "agg")
    print(f"listings: {len(lst)} requests", flush=True)
    run_batch(lst, "lst")
    print(f"history {HISTORY_DAYS}d: {len(hist)} requests", flush=True)
    run_batch(hist, "hist")

    files = os.listdir(BULK)
    size = sum(os.path.getsize(os.path.join(BULK, f)) for f in files)
    print(f"\n{len(files):,} cache files, {size / 1e6:,.0f} MB")


if __name__ == "__main__":
    sys.exit(main())

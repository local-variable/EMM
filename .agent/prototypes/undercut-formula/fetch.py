"""Fetch real Universalis data for the undercut-formula prototype (wayfinder ticket #11).

Read-only. Caches to disk so the analysis can be re-run without re-fetching.
"""

import json
import os
import time
import urllib.request

WORLD = "Cactuar"
DC = "Aether"
REGION = "North-America"

UA = "EorzeanMarketMaster-prototype/0.1 (github.com/local-variable/EMM; wayfinder ticket 11)"
HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "data")

# Real wares from the maintainer's own retainers (Allagan Tools inventory dump),
# chosen to span the shapes the ticket names.
# The totems in that dump (Bliss, Hades, Suzaku, Naught) and Cracked
# Prismaticluster return no market data at all: they are untradeable, so the
# price column in the inventory dump is not a listing.
ITEMS = {
    41769: "Quicktongue Materia XI",        # ~64 sales/day  cheap commodity
    41763: "Gatherer's Guile Materia XI",   # ~45 sales/day  high-value commodity
    44036: "Mythloam Aethersand",           # ~7 sales/day   material
    41145: "Labyrinthos Grape Lamppost",    # ~1.2 sales/day housing
    47166: "Gold Thumb's Mallet",           # ~1.1 sales/day crafted tool
    41139: "Flower-wreathed Gazebo",        # ~0.8 sales/day slow, high value
    44421: "Everseeker's Fishing Rod",      # 4 sales / 90d  thin market
}


def get(url, name):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name + ".json")
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                payload = json.loads(r.read().decode("utf-8"))
            break
        except urllib.error.HTTPError as e:
            print(f"  {name}: HTTP {e.code} (attempt {attempt + 1})")
            if attempt == 3:
                raise
            time.sleep(5)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f)
    time.sleep(0.5)  # well under the published 25 req/s
    return payload


def main():
    ids = ",".join(str(i) for i in ITEMS)
    base = "https://universalis.app/api/v2"

    get(f"{base}/aggregated/{WORLD}/{ids}", "aggregated_world")
    get(f"{base}/aggregated/{DC}/{ids}", "aggregated_dc")
    get(f"{base}/{WORLD}/{ids}?listings=100&entries=0", "listings_world")
    get(f"{base}/{DC}/{ids}?listings=100&entries=0", "listings_dc")
    # Deep world history: the full batch works but is slow and 504s on a first
    # try often enough to need a retry. Deep DC history 500s outright — a
    # data-centre-wide multi-year series is more than the API will assemble —
    # so the DC series is capped at 90 days, which is all the DC-scope
    # reference statistics need.
    get(
        f"{base}/history/{WORLD}/{ids}"
        f"?entriesToReturn=99999&entriesWithin={3 * 365 * 24 * 3600}",
        "history_world",
    )
    get(
        f"{base}/history/{DC}/{ids}"
        f"?entriesToReturn=99999&entriesWithin={90 * 24 * 3600}",
        "history_dc_90d",
    )
    print("cached to", CACHE)
    for n in sorted(os.listdir(CACHE)):
        p = os.path.join(CACHE, n)
        print(f"  {n:24s} {os.path.getsize(p):>9,} bytes")


if __name__ == "__main__":
    main()

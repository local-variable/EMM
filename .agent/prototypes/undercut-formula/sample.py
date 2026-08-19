"""Stage 1: draw a reproducible random sample of marketable items and attach
the game's own metadata to each.

The sample is seeded so every later stage, and any future agent re-running
this, works on exactly the same 1000 items.
"""

import json
import os
import random
import time
import urllib.request

SEED = 20260817
SAMPLE_SIZE = 1000
UA = "EorzeanMarketMaster-prototype/0.2 (github.com/local-variable/EMM; wayfinder ticket 11)"

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "data")
ITEMS_FILE = os.path.join(CACHE, "items.json")

FIELDS = ",".join([
    "Name",
    "ItemUICategory.Name",
    "ItemSearchCategory.Name",
    "ItemSearchCategory.Category",
    "StackSize",
    "LevelItem.value",
    "Rarity",
    "CanBeHq",
    "IsUnique",
    "IsUntradable",
    "PriceLow",
    "PriceMid",
    "IsCollectable",
])


def fetch(url, tries=4):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    for attempt in range(tries):
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                return json.loads(r.read().decode("utf-8"))
        except Exception as e:
            if attempt == tries - 1:
                raise
            print(f"    retry {attempt + 1}: {e}")
            time.sleep(5)


def main():
    os.makedirs(CACHE, exist_ok=True)
    marketable = fetch("https://universalis.app/api/v2/marketable")
    print(f"marketable items on Universalis: {len(marketable):,}")

    rng = random.Random(SEED)
    sample = sorted(rng.sample(marketable, SAMPLE_SIZE))
    print(f"sampled {len(sample):,} with seed {SEED}")

    out = {}
    for i in range(0, len(sample), 100):
        chunk = sample[i : i + 100]
        rows = ",".join(str(x) for x in chunk)
        url = f"https://v2.xivapi.com/api/sheet/Item?rows={rows}&fields={FIELDS}"
        data = fetch(url)
        for row in data["rows"]:
            f = row["fields"]
            isc = f.get("ItemSearchCategory") or {}
            iuc = f.get("ItemUICategory") or {}
            out[row["row_id"]] = {
                "name": f.get("Name", ""),
                "searchCategory": (isc.get("fields") or {}).get("Name", ""),
                "searchCategoryId": isc.get("row_id", 0),
                # ItemSearchCategory.Category: 1 weapons, 2 armour, 3 items,
                # 4 housing. The game's own coarsest market grouping.
                "searchGroup": (isc.get("fields") or {}).get("Category", 0),
                "uiCategory": (iuc.get("fields") or {}).get("Name", ""),
                "stackSize": f.get("StackSize", 1),
                "ilvl": (f.get("LevelItem") or {}).get("value", 0),
                "rarity": f.get("Rarity", 1),
                "canBeHq": bool(f.get("CanBeHq")),
                "isUnique": bool(f.get("IsUnique")),
                "isCollectable": bool(f.get("IsCollectable")),
                "vendorBuy": f.get("PriceMid", 0),
                "vendorSell": f.get("PriceLow", 0),
            }
        print(f"  metadata {len(out):,}/{len(sample):,}")
        time.sleep(0.3)

    with open(ITEMS_FILE, "w", encoding="utf-8") as f:
        json.dump(out, f)
    print(f"wrote {ITEMS_FILE} ({os.path.getsize(ITEMS_FILE):,} bytes)")

    missing = set(sample) - set(out)
    if missing:
        print(f"WARNING: {len(missing)} sampled ids returned no game row")


if __name__ == "__main__":
    main()

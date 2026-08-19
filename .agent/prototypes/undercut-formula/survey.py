"""Survey the fetched data: how much of it is there, per item, per scope."""

import datetime as dt
import json
import os

from fetch import ITEMS, WORLD, DC

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "data")
NOW = dt.datetime.now(dt.timezone.utc)


def load(name):
    with open(os.path.join(CACHE, name + ".json"), "r", encoding="utf-8") as f:
        return json.load(f)


def by_item(payload):
    if "items" in payload:
        return {int(k): v for k, v in payload["items"].items()}
    return {payload["itemID"]: payload}


def ts(ms_or_s):
    v = ms_or_s
    if v > 1e12:
        v /= 1000.0
    return dt.datetime.fromtimestamp(v, dt.timezone.utc)


def main():
    hw = by_item(load("history_world"))
    hd = by_item(load("history_dc_90d"))
    lw = by_item(load("listings_world"))
    ld = by_item(load("listings_dc"))
    aw = {int(x["itemId"]): x for x in load("aggregated_world")["results"]}

    print(f"scope: {WORLD} (world) / {DC} (dc)   as of {NOW:%Y-%m-%dT%H:%MZ}\n")
    hdr = (
        f"{'item':34s} {'sales(w)':>9s} {'span':>10s} {'sales/d':>8s} "
        f"{'sales90(w)':>11s} {'sales90(dc)':>12s} {'lstg(w)':>8s} {'lstg(dc)':>9s}"
    )
    print(hdr)
    print("-" * len(hdr))
    for item, name in ITEMS.items():
        e = hw.get(item, {}).get("entries", [])
        ed = hd.get(item, {}).get("entries", [])
        lstw = lw.get(item, {}).get("listings", [])
        lstd = ld.get(item, {}).get("listings", [])
        if e:
            oldest = ts(min(x["timestamp"] for x in e))
            span_days = (NOW - oldest).days
            per_day = len(e) / max(span_days, 1)
        else:
            span_days, per_day = 0, 0.0
        cut = (NOW - dt.timedelta(days=90)).timestamp()
        n90 = sum(1 for x in e if x["timestamp"] > cut)
        n90d = sum(1 for x in ed if x["timestamp"] > cut)
        print(
            f"{name:34s} {len(e):>9,} {span_days:>8,}d {per_day:>8.2f} "
            f"{n90:>11,} {n90d:>12,} {len(lstw):>8,} {len(lstd):>9,}"
        )

    print("\naggregated (world) fields present:")
    sample = aw[list(ITEMS)[0]]
    print(json.dumps(sample, indent=2)[:1200])


if __name__ == "__main__":
    main()

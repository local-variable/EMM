"""Streaming loader for the full catalogue.

The 1,000-item cache fits in memory as parsed JSON. The full 16,843-item one
does not - roughly a quarter of a gigabyte of history, which becomes several
gigabytes once every sale row is a Python dict. So this yields Wares one at a
time, holding only a single history batch plus the (much smaller) listing and
aggregate indices.

Same Ware surface as `dataset.py`, so the analysis code reads the same either
way; only the driver changes from a list to a generator, which is why each
stage here makes an explicit pass rather than iterating a collection twice.
"""

import datetime as dt
import glob
import json
import os

NOW = dt.datetime.now(dt.timezone.utc)
NOW_TS = NOW.timestamp()
DAY = 86400

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, os.environ.get("EMM_DATA", "data_all"))
BULK = os.path.join(CACHE, "bulk")


def _dirs(cache=None):
    """(cache, bulk) for an explicit cache dir, or the module default.

    Explicit so two worlds can be loaded in one process for comparison; the
    default keeps every single-world caller unchanged.
    """
    c = os.path.join(HERE, cache) if cache else CACHE
    return c, os.path.join(c, "bulk")


def items_meta(cache=None):
    c, _ = _dirs(cache)
    with open(os.path.join(c, "items.json"), "r", encoding="utf-8") as f:
        return {int(k): v for k, v in json.load(f).items()}


def _listing_index(bulk=None):
    """(item, hq) -> list of (pricePerUnit, quantity), cheapest first.

    Compacted to tuples on the way in; the full listing rows carry fifteen
    fields each and only two are used downstream.
    """
    idx = {}
    bulk = bulk or BULK
    for path in sorted(glob.glob(os.path.join(bulk, "lst_world_*.json"))):
        with open(path, "r", encoding="utf-8") as f:
            payload = json.load(f)
        for k, v in payload.get("items", {}).items():
            item = int(k)
            for hq in (False, True):
                rows = [(l["pricePerUnit"], l["quantity"])
                        for l in v.get("listings", []) if bool(l["hq"]) == hq]
                if rows:
                    rows.sort()
                    idx[(item, hq)] = rows
    return idx


def _agg_index(bulk=None):
    """item -> {'nq': {...}, 'hq': {...}} for world and dc aggregates."""
    idx = {}
    bulk = bulk or BULK
    for prefix, key in (("agg_world_", "w"), ("agg_dc_", "d")):
        for path in sorted(glob.glob(os.path.join(bulk, prefix + "*.json"))):
            with open(path, "r", encoding="utf-8") as f:
                payload = json.load(f)
            for row in payload.get("results", []):
                slot = idx.setdefault(int(row["itemId"]), {})
                slot[key] = {"nq": row.get("nq", {}), "hq": row.get("hq", {})}
    return idx


class Sale:
    __slots__ = ("ts", "price", "qty", "buyer")

    def __init__(self, ts, price, qty, buyer):
        self.ts = ts
        self.price = price
        self.qty = qty
        self.buyer = buyer


class Ware:
    __slots__ = ("item", "hq", "meta", "sales", "listings", "agg_world", "agg_dc")

    def __init__(self, item, hq, meta, sales, listings, agg_world, agg_dc):
        self.item = item
        self.hq = hq
        self.meta = meta
        self.sales = sales          # oldest first
        self.listings = listings    # [(price, qty)] cheapest first
        self.agg_world = agg_world
        self.agg_dc = agg_dc

    @property
    def name(self):
        return f"{self.meta['name']} {'HQ' if self.hq else 'NQ'}"

    def within(self, days_back, end=None):
        end = end if end is not None else NOW_TS
        lo = end - days_back * DAY
        return [s for s in self.sales if lo <= s.ts < end]

    def min_listing(self):
        return self.listings[0][0] if self.listings else None

    def units_on_board(self):
        return sum(q for _, q in self.listings)


def wares(min_sales=1, cache=None):
    """Yield every Ware in the cache, one history batch at a time."""
    _, bulk = _dirs(cache)
    meta = items_meta(cache)
    listings = _listing_index(bulk)
    aggs = _agg_index(bulk)
    for path in sorted(glob.glob(os.path.join(bulk, "hist_world_*.json"))):
        with open(path, "r", encoding="utf-8") as f:
            payload = json.load(f)
        for k, v in payload.get("items", {}).items():
            item = int(k)
            m = meta.get(item)
            if not m:
                continue
            entries = v.get("entries", [])
            for hq in ((False, True) if m["canBeHq"] else (False,)):
                rows = [Sale(e["timestamp"], e["pricePerUnit"], e["quantity"],
                             hash(e.get("buyerName") or "") & 0x7FFFFFFF)
                        for e in entries if bool(e["hq"]) == hq]
                if len(rows) < min_sales:
                    continue
                rows.sort(key=lambda s: s.ts)
                a = aggs.get(item, {})
                q = "hq" if hq else "nq"
                yield Ware(item, hq, m, rows,
                           listings.get((item, hq), []),
                           (a.get("w") or {}).get(q, {}),
                           (a.get("d") or {}).get(q, {}))
        del payload


def all_wares_including_empty(cache=None):
    """Every (item, quality) pair, including those with no sales at all -
    needed for coverage, which is precisely a statement about the empties."""
    meta = items_meta(cache)
    seen = set()
    for w in wares(min_sales=1, cache=cache):
        seen.add((w.item, w.hq))
        yield w
    for item, m in meta.items():
        for hq in ((False, True) if m["canBeHq"] else (False,)):
            if (item, hq) not in seen:
                yield Ware(item, hq, m, [], [], {}, {})


def all_wares_including_empty_from(cache):
    """Explicit-cache alias, for loading two worlds in one process."""
    return all_wares_including_empty(cache)

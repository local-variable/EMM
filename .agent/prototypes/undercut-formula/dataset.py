"""Loader: fold the bulk cache into one tidy record per Ware.

A Ware is (item, quality), never an item - the glossary is binding here and the
first prototype measured a 3.3x gap between the two qualities of one item.
"""

import datetime as dt
import glob
import json
import os
import statistics

HERE = os.path.dirname(os.path.abspath(__file__))
# EMM_DATA picks the cache: "data" is the seeded 1,000-item sample, "data_all"
# the whole marketable catalogue. Kept separate so the sample stays
# reproducible rather than quietly becoming a census.
CACHE = os.path.join(HERE, os.environ.get("EMM_DATA", "data"))
BULK = os.path.join(CACHE, "bulk")
NOW = dt.datetime.now(dt.timezone.utc)
NOW_TS = NOW.timestamp()
DAY = 86400


def _load_all(prefix):
    out = {}
    for path in sorted(glob.glob(os.path.join(BULK, prefix + "*.json"))):
        with open(path, "r", encoding="utf-8") as f:
            payload = json.load(f)
        if "items" in payload:
            for k, v in payload["items"].items():
                out[int(k)] = v
        elif "results" in payload:
            for row in payload["results"]:
                out[int(row["itemId"])] = row
        elif "itemID" in payload:
            out[int(payload["itemID"])] = payload
    return out


def items_meta():
    with open(os.path.join(CACHE, "items.json"), "r", encoding="utf-8") as f:
        return {int(k): v for k, v in json.load(f).items()}


class Ware:
    __slots__ = ("item", "hq", "meta", "sales", "listings", "agg_world", "agg_dc")

    def __init__(self, item, hq, meta, sales, listings, agg_world, agg_dc):
        self.item = item
        self.hq = hq
        self.meta = meta
        self.sales = sales          # newest first
        self.listings = listings    # cheapest first
        self.agg_world = agg_world
        self.agg_dc = agg_dc

    @property
    def name(self):
        return f"{self.meta['name']} {'HQ' if self.hq else 'NQ'}"

    def sales_within(self, days_back, end=None):
        end = end if end is not None else NOW_TS
        lo = end - days_back * DAY
        return [s for s in self.sales if lo <= s["timestamp"] < end]

    # --- sale-pattern descriptors ------------------------------------
    def span_days(self):
        if not self.sales:
            return 0.0
        return (NOW_TS - min(s["timestamp"] for s in self.sales)) / DAY

    def velocity(self, days_back=90):
        return len(self.sales_within(days_back)) / days_back

    def units_per_sale(self, days_back=90):
        s = self.sales_within(days_back)
        if not s:
            return None
        return sum(x["quantity"] for x in s) / len(s)

    def dispersion(self, days_back=90):
        """IQR / median of unit price - how wide the traded band is."""
        s = self.sales_within(days_back)
        if len(s) < 4:
            return None
        p = sorted(x["pricePerUnit"] for x in s)
        med = statistics.median(p)
        if not med:
            return None
        q1 = p[int(0.25 * (len(p) - 1))]
        q3 = p[int(0.75 * (len(p) - 1))]
        return (q3 - q1) / med

    def drift(self):
        """30-day median over 180-day median - is the level moving?"""
        a = self.sales_within(30)
        b = self.sales_within(180)
        if len(a) < 3 or len(b) < 8:
            return None
        mb = statistics.median(x["pricePerUnit"] for x in b)
        if not mb:
            return None
        return statistics.median(x["pricePerUnit"] for x in a) / mb

    def burstiness(self, days_back=90):
        """CV of inter-arrival times. 1.0 = Poisson, >1 = clustered."""
        s = sorted(x["timestamp"] for x in self.sales_within(days_back))
        if len(s) < 6:
            return None
        gaps = [(b - a) / DAY for a, b in zip(s, s[1:]) if b > a]
        if len(gaps) < 5:
            return None
        m = statistics.fmean(gaps)
        return statistics.stdev(gaps) / m if m else None

    def days_of_supply(self):
        units = sum(l["quantity"] for l in self.listings)
        s = self.sales_within(30)
        sold = sum(x["quantity"] for x in s) / 30.0
        return units / sold if sold else None

    def min_listing(self):
        return self.listings[0]["pricePerUnit"] if self.listings else None


def load(min_sales=0):
    meta = items_meta()
    hist = _load_all("hist_world_")
    lst = _load_all("lst_world_")
    aggw = _load_all("agg_world_")
    aggd = _load_all("agg_dc_")

    wares = []
    for item, m in meta.items():
        entries = hist.get(item, {}).get("entries", [])
        listings = lst.get(item, {}).get("listings", [])
        qualities = (False, True) if m["canBeHq"] else (False,)
        for hq in qualities:
            s = sorted(
                (e for e in entries if bool(e["hq"]) == hq),
                key=lambda e: e["timestamp"],
                reverse=True,
            )
            if len(s) < min_sales:
                continue
            li = sorted(
                (l for l in listings if bool(l["hq"]) == hq),
                key=lambda l: l["pricePerUnit"],
            )
            aw = aggw.get(item, {}).get("hq" if hq else "nq", {})
            ad = aggd.get(item, {}).get("hq" if hq else "nq", {})
            wares.append(Ware(item, hq, m, s, li, aw, ad))
    return wares


def coverage():
    """How much of the bulk fetch actually landed."""
    meta = items_meta()
    return {
        "sampled": len(meta),
        "history": len(_load_all("hist_world_")),
        "listings": len(_load_all("lst_world_")),
        "aggregated": len(_load_all("agg_world_")),
    }

"""Shared loading and statistics for the undercut-formula prototype."""

import datetime as dt
import json
import os
import statistics

from fetch import ITEMS, WORLD, DC

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "data")
NOW = dt.datetime.now(dt.timezone.utc)
NOW_TS = NOW.timestamp()

# The maintainer's own retainers, read from AutoRetainer's offline data. Kept
# here only so "Competing Listing" can exclude them; never printed.
OWN_RETAINERS = {"Itotia", "Iosefkaa", "Au'fia", "Abreone"}


def load(name):
    with open(os.path.join(CACHE, name + ".json"), "r", encoding="utf-8") as f:
        return json.load(f)


def by_item(payload):
    return {int(k): v for k, v in payload["items"].items()}


def sales(scope="world", item=None, hq=None, since=None, until=None):
    """Individual sale rows, newest first, optionally filtered."""
    src = "history_world" if scope == "world" else "history_dc_90d"
    rows = by_item(load(src)).get(item, {}).get("entries", [])
    out = []
    for r in rows:
        if hq is not None and bool(r["hq"]) != hq:
            continue
        if since is not None and r["timestamp"] < since:
            continue
        if until is not None and r["timestamp"] >= until:
            continue
        out.append(r)
    out.sort(key=lambda r: r["timestamp"], reverse=True)
    return out


def listings(scope="world", item=None, hq=None, exclude_own=True):
    src = "listings_world" if scope == "world" else "listings_dc"
    rows = by_item(load(src)).get(item, {}).get("listings", [])
    out = []
    for r in rows:
        if hq is not None and bool(r["hq"]) != hq:
            continue
        if exclude_own and r.get("retainerName") in OWN_RETAINERS:
            continue
        out.append(r)
    out.sort(key=lambda r: r["pricePerUnit"])
    return out


# --- candidate reference statistics -------------------------------------
# Each takes a list of sale rows and returns a unit price, or None when the
# sample cannot support it.


def mean(rows):
    return statistics.fmean(r["pricePerUnit"] for r in rows) if rows else None


def median(rows):
    return statistics.median(r["pricePerUnit"] for r in rows) if rows else None


def trimmed_mean(rows, frac=0.1):
    if len(rows) < 5:
        return median(rows)
    p = sorted(r["pricePerUnit"] for r in rows)
    k = int(len(p) * frac)
    p = p[k : len(p) - k] or p
    return statistics.fmean(p)


def vwap(rows):
    """Volume-weighted: a 99-stack sale says more than a 1-stack sale."""
    if not rows:
        return None
    num = sum(r["pricePerUnit"] * r["quantity"] for r in rows)
    den = sum(r["quantity"] for r in rows)
    return num / den if den else None


def quantile(rows, q):
    if not rows:
        return None
    p = sorted(r["pricePerUnit"] for r in rows)
    if len(p) == 1:
        return float(p[0])
    idx = q * (len(p) - 1)
    lo, hi = int(idx), min(int(idx) + 1, len(p) - 1)
    return p[lo] + (p[hi] - p[lo]) * (idx - lo)


def units(rows):
    return sum(r["quantity"] for r in rows)


def days(n):
    return n * 86400

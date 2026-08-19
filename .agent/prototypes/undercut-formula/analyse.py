"""Table 1-3: what the candidate reference statistics actually say, and how
badly behaved each one is, on real Cactuar data.
"""

import statistics

from common import (
    ITEMS,
    NOW,
    NOW_TS,
    days,
    listings,
    mean,
    median,
    quantile,
    sales,
    trimmed_mean,
    units,
    vwap,
)


def wares():
    """(item, hq) pairs that have any sales in the last year."""
    out = []
    for item, name in ITEMS.items():
        for hq in (False, True):
            n = len(sales(item=item, hq=hq, since=NOW_TS - days(365)))
            if n:
                out.append((item, hq, f"{name} {'HQ' if hq else 'NQ'}", n))
    return out


def g(v):
    if v is None:
        return "     -"
    return f"{v:>10,.0f}"


def table1():
    print("\n" + "=" * 118)
    print("TABLE 1 - candidate reference statistics, computed now (Cactuar)")
    print("=" * 118)
    hdr = (
        f"{'ware':36s} {'n/30d':>6s} {'minLst':>10s} {'mean30':>10s} "
        f"{'med30':>10s} {'trim30':>10s} {'vwap30':>10s} {'q25/30':>10s} {'med90':>10s}"
    )
    print(hdr)
    print("-" * len(hdr))
    for item, hq, label, _ in wares():
        s30 = sales(item=item, hq=hq, since=NOW_TS - days(30))
        s90 = sales(item=item, hq=hq, since=NOW_TS - days(90))
        lst = listings(item=item, hq=hq)
        ml = lst[0]["pricePerUnit"] if lst else None
        print(
            f"{label:36s} {len(s30):>6,} {g(ml)} {g(mean(s30))} {g(median(s30))} "
            f"{g(trimmed_mean(s30))} {g(vwap(s30))} {g(quantile(s30, 0.25))} "
            f"{g(median(s90))}"
        )
    print(
        "\n  minLst = lowest competing listing on Cactuar, the maintainer's own"
        "\n  retainers excluded. '-' = the statistic has no sample to stand on."
    )


def table2():
    """How much does the statistic itself move day to day? A reference that
    jitters is a reference that makes EMM chase its own tail."""
    print("\n" + "=" * 118)
    print("TABLE 2 - instability: spread of the statistic itself, recomputed daily over 60 days")
    print("         (max-min as a % of its own median - lower is steadier)")
    print("=" * 118)
    hdr = (
        f"{'ware':36s} {'minLst':>9s} {'mean7':>9s} {'mean30':>9s} "
        f"{'med7':>9s} {'med30':>9s} {'trim30':>9s} {'vwap30':>9s} {'q25/30':>9s}"
    )
    print(hdr)
    print("-" * len(hdr))
    for item, hq, label, _ in wares():
        series = {k: [] for k in
                  ("minLst", "mean7", "mean30", "med7", "med30", "trim30", "vwap30", "q25/30")}
        for d in range(60, 0, -1):
            t = NOW_TS - days(d)
            s7 = sales(item=item, hq=hq, since=t - days(7), until=t)
            s30 = sales(item=item, hq=hq, since=t - days(30), until=t)
            # No historical order book, so the cheapest sale in the last day is
            # the honest stand-in for "what the cheapest listing was".
            s1 = sales(item=item, hq=hq, since=t - days(1), until=t)
            series["minLst"].append(min((r["pricePerUnit"] for r in s1), default=None))
            series["mean7"].append(mean(s7))
            series["mean30"].append(mean(s30))
            series["med7"].append(median(s7))
            series["med30"].append(median(s30))
            series["trim30"].append(trimmed_mean(s30))
            series["vwap30"].append(vwap(s30))
            series["q25/30"].append(quantile(s30, 0.25))
        cells = []
        for k in ("minLst", "mean7", "mean30", "med7", "med30", "trim30", "vwap30", "q25/30"):
            vals = [v for v in series[k] if v]
            if len(vals) < 5:
                cells.append("        -")
            else:
                m = statistics.median(vals)
                spread = (max(vals) - min(vals)) / m * 100 if m else 0
                cells.append(f"{spread:>8,.0f}%")
        print(f"{label:36s} " + " ".join(cells))
    print(
        "\n  '-' = fewer than 5 of the 60 days could compute the statistic at all."
    )


def table3():
    print("\n" + "=" * 118)
    print("TABLE 3 - what the outliers actually look like (last 30 days of sales)")
    print("=" * 118)
    hdr = (
        f"{'ware':36s} {'n':>6s} {'units':>8s} {'min':>10s} {'q25':>10s} "
        f"{'median':>10s} {'q75':>10s} {'max':>12s} {'max/med':>9s}"
    )
    print(hdr)
    print("-" * len(hdr))
    for item, hq, label, _ in wares():
        s = sales(item=item, hq=hq, since=NOW_TS - days(30))
        if not s:
            continue
        p = sorted(r["pricePerUnit"] for r in s)
        med = statistics.median(p)
        ratio = p[-1] / med if med else 0
        print(
            f"{label:36s} {len(s):>6,} {units(s):>8,} {p[0]:>10,} "
            f"{quantile(s, 0.25):>10,.0f} {med:>10,.0f} {quantile(s, 0.75):>10,.0f} "
            f"{p[-1]:>12,} {ratio:>8,.1f}x"
        )


if __name__ == "__main__":
    print(f"undercut-formula prototype - Cactuar (Aether) - {NOW:%Y-%m-%dT%H:%MZ}")
    table1()
    table3()
    table2()

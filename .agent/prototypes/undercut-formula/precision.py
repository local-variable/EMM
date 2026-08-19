"""Stage 5: replace "n >= 5" with a precision criterion.

The rule EMM needs is not "are there enough sales" but "is the reference
pinned down finely enough for the decision resting on it". This module
produces, for any Ware and window, the reference statistic together with an
interval around it - which is also exactly what the graph layer needs to draw
a band, so `estimate()` is written to be the single source for both.

Why a BLOCK bootstrap and not the ordinary one: stage 3 measured inter-arrival
CV well above 1 on every ware type (Materia 2.38, Material 1.63), i.e. sales
arrive in clumps, not as a Poisson process. An i.i.d. bootstrap assumes each
row is independent evidence and will therefore report an interval that is too
narrow. Resampling contiguous blocks keeps the clumping intact. The size of
that correction is measured below rather than assumed.
"""

import math
import random
import statistics

import dataset
from wtype import TYPES, classify

REPS = 400
RNG = random.Random(20260817)


def _median(xs):
    return statistics.median(xs)


def estimate(rows, stat=_median, conf=0.80, reps=REPS, block=True):
    """Reference statistic with a bootstrap interval.

    rows must be in time order. Returns (point, lo, hi, n) or None.
    This is the function the graph band should call - same estimator, same
    interval, so the chart cannot disagree with the pricing engine.
    """
    n = len(rows)
    if n < 3:
        return None
    prices = [r["pricePerUnit"] for r in rows]
    point = stat(prices)
    if block:
        # Moving-block bootstrap. b ~ n^(1/3) is the standard rule of thumb
        # for a stationary series and is used here for want of a per-Ware
        # estimate of the dependence length.
        b = max(2, int(round(n ** (1 / 3))))
        nblocks = max(1, math.ceil(n / b))
        starts = max(1, n - b + 1)
        draws = []
        for _ in range(reps):
            s = []
            for _ in range(nblocks):
                i = RNG.randrange(starts)
                s.extend(prices[i : i + b])
            draws.append(stat(s[:n]))
    else:
        draws = [stat([prices[RNG.randrange(n)] for _ in range(n)])
                 for _ in range(reps)]
    draws.sort()
    lo_i = int((1 - conf) / 2 * (reps - 1))
    hi_i = int((1 - (1 - conf) / 2) * (reps - 1))
    return point, draws[lo_i], draws[hi_i], n


def rel_halfwidth(est):
    """Interval half-width as a fraction of the point estimate."""
    if not est:
        return None
    point, lo, hi, _ = est
    if not point:
        return None
    return (hi - lo) / 2.0 / point


N_BUCKETS = [(3, 4), (5, 9), (10, 19), (20, 49), (50, 99), (100, 299), (300, 10 ** 9)]


def nlabel(n):
    for lo, hi in N_BUCKETS:
        if lo <= n <= hi:
            return f"{lo}-{hi}" if hi < 10 ** 8 else f"{lo}+"
    return "<3"


def table_block_vs_iid(wares):
    print("\n" + "=" * 112)
    print("TABLE H - how much does the clumping cost? block bootstrap against i.i.d. bootstrap")
    print("         Ratio of interval widths on the same Wares and the same 30-day windows.")
    print("=" * 112)
    hdr = f"  {'ware type':14s} {'wares':>7s} {'median ratio':>13s} {'p90':>8s}   reading"
    print(hdr)
    print("  " + "-" * (len(hdr) + 10))
    for t in TYPES:
        ratios = []
        for w in wares:
            if classify(w.meta) != t:
                continue
            rows = sorted(w.sales_within(30), key=lambda r: r["timestamp"])
            if len(rows) < 8:
                continue
            a = rel_halfwidth(estimate(rows, block=True))
            b = rel_halfwidth(estimate(rows, block=False))
            if a and b:
                ratios.append(a / b)
        if len(ratios) < 5:
            continue
        ratios.sort()
        med = statistics.median(ratios)
        reading = ("block interval is wider" if med > 1.05
                   else "no material difference" if med > 0.95
                   else "block interval is narrower")
        print(f"  {t:14s} {len(ratios):>7,} {med:>13.2f} "
              f"{ratios[int(0.9 * (len(ratios) - 1))]:>8.2f}   {reading}")
    print("\n  A ratio above 1 means the ordinary bootstrap was overstating precision.")


def table_precision_by_n(wares):
    print("\n" + "=" * 112)
    print("TABLE I - what precision does a given sample actually buy?")
    print("         Median relative half-width of the 80% interval on the 30-day median")
    print("         unit price, by sample size and ware type. Read it as +/- that fraction.")
    print("=" * 112)
    labels = [f"{lo}-{hi}" if hi < 10 ** 8 else f"{lo}+" for lo, hi in N_BUCKETS]
    hdr = f"  {'ware type':14s} " + " ".join(f"{l:>10s}" for l in labels)
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t in TYPES:
        acc = {l: [] for l in labels}
        for w in wares:
            if classify(w.meta) != t:
                continue
            rows = sorted(w.sales_within(30), key=lambda r: r["timestamp"])
            if len(rows) < 3:
                continue
            rh = rel_halfwidth(estimate(rows))
            if rh is not None:
                acc[nlabel(len(rows))].append(rh)
        if sum(len(v) for v in acc.values()) < 10:
            continue
        cells = []
        for l in labels:
            v = acc[l]
            cells.append(f"{statistics.median(v):>9.1%}" if len(v) >= 5 else f"{'-':>10s}")
        print(f"  {t:14s} " + " ".join(cells))
    print("\n  '-' = fewer than 5 Wares of that type landed in that sample bucket.")


def table_precision_curve(wares):
    """Table I compares different Wares at different n, so sample size is
    confounded with which Ware landed in the bucket. This holds the Ware fixed
    and subsamples its own window down to each n, which is the only way to see
    what n by itself buys.
    """
    print("\n" + "=" * 112)
    print("TABLE I2 - precision against sample size, WITHIN Ware")
    print("         Wares with >= 60 sales in 90 days, their own window subsampled down to")
    print("         each n (20 draws each). Median relative half-width of the 80% interval.")
    print("=" * 112)
    ns = [3, 5, 10, 20, 40, 60]
    hdr = f"  {'ware type':14s} {'wares':>7s} " + " ".join(f"{'n=' + str(n):>9s}" for n in ns)
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t in TYPES:
        acc = {n: [] for n in ns}
        count = 0
        for w in wares:
            if classify(w.meta) != t:
                continue
            rows = sorted(w.sales_within(90), key=lambda r: r["timestamp"])
            if len(rows) < 60:
                continue
            count += 1
            for n in ns:
                widths = []
                for _ in range(20):
                    i = RNG.randrange(0, len(rows) - n + 1)
                    rh = rel_halfwidth(estimate(rows[i : i + n], reps=200))
                    if rh is not None:
                        widths.append(rh)
                if widths:
                    acc[n].append(statistics.median(widths))
        if count < 5:
            continue
        cells = []
        for n in ns:
            v = acc[n]
            cells.append(f"{statistics.median(v):>8.1%}" if len(v) >= 5 else f"{'-':>9s}")
        print(f"  {t:14s} {count:>7,} " + " ".join(cells))
    print("\n  Contiguous subsamples, not random rows, so the clumping is preserved.")


def table_rule(wares):
    print("\n" + "=" * 112)
    print("TABLE J - what a precision rule would actually admit")
    print("         Share of Wares of each type that clear a given precision bar on the")
    print("         30-day median, with n >= 5 in 30 days as a hard floor beneath all of them.")
    print("=" * 112)
    bars = [0.05, 0.10, 0.15, 0.25, 0.50]
    hdr = (f"  {'ware type':14s} {'wares':>7s} {'n>=5':>7s} "
           + " ".join(f"{'+/-' + str(int(b * 100)) + '%':>9s}" for b in bars))
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t in TYPES:
        ws = [w for w in wares if classify(w.meta) == t]
        if len(ws) < 5:
            continue
        n5 = 0
        passes = {b: 0 for b in bars}
        for w in ws:
            rows = sorted(w.sales_within(30), key=lambda r: r["timestamp"])
            if len(rows) < 5:
                continue
            n5 += 1
            rh = rel_halfwidth(estimate(rows))
            if rh is None:
                continue
            for b in bars:
                if rh <= b:
                    passes[b] += 1
        if not n5:
            continue
        print(f"  {t:14s} {len(ws):>7,} {n5:>7,} "
              + " ".join(f"{passes[b] / len(ws):>9.0%}" for b in bars))
    print("\n  Percentages are of ALL Wares of that type, not of those clearing the n floor,")
    print("  so they state the real coverage cost of the rule: what EMM would refuse to price.")


def main():
    wares = dataset.load()
    print(f"undercut-formula prototype, stage 5 - precision - {dataset.NOW:%Y-%m-%dT%H:%MZ}")
    print(f"{REPS} bootstrap replicates, 80% interval, median unit price over 30 days")
    table_block_vs_iid(wares)
    table_precision_by_n(wares)
    table_precision_curve(wares)
    table_rule(wares)


if __name__ == "__main__":
    main()

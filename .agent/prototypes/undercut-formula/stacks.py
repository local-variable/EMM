"""Stage 4: stack size. Does a bigger stack fetch a worse unit price, and do
some stack sizes sit on the board while others clear?

This is the free variable in Slot Yield's `Stack` term, and the maintainer's
observation is the hypothesis under test: full stacks may sell worse than
10/20/30/50.
"""

import math
import statistics

import dataset
import wtype
from wtype import TYPES, classify

BUCKETS = [
    (1, 1, "1"),
    (2, 9, "2-9"),
    (10, 19, "10-19"),
    (20, 49, "20-49"),
    (50, 98, "50-98"),
    (99, 99, "99"),
    (100, 199, "100-199"),
    (200, 499, "200-499"),
    (500, 998, "500-998"),
    (999, 10 ** 9, "999+"),
]


def bucket(q):
    for lo, hi, label in BUCKETS:
        if lo <= q <= hi:
            return label
    return "999+"


def _rank(xs):
    order = sorted(range(len(xs)), key=lambda i: xs[i])
    r = [0.0] * len(xs)
    i = 0
    while i < len(order):
        j = i
        while j + 1 < len(order) and xs[order[j + 1]] == xs[order[i]]:
            j += 1
        avg = (i + j) / 2.0 + 1.0
        for k in range(i, j + 1):
            r[order[k]] = avg
        i = j + 1
    return r


def spearman(xs, ys):
    if len(xs) < 8:
        return None
    rx, ry = _rank(xs), _rank(ys)
    mx, my = statistics.fmean(rx), statistics.fmean(ry)
    num = sum((a - mx) * (b - my) for a, b in zip(rx, ry))
    dx = math.sqrt(sum((a - mx) ** 2 for a in rx))
    dy = math.sqrt(sum((b - my) ** 2 for b in ry))
    return num / (dx * dy) if dx and dy else None


def table_price_vs_stack(wares):
    print("\n" + "=" * 112)
    print("TABLE D - does a bigger stack fetch a worse UNIT price?")
    print("         Spearman rank correlation of stack size against unit price, one per Ware,")
    print("         computed only on Wares that actually trade in varied stack sizes.")
    print("=" * 112)
    hdr = (f"  {'ware type':14s} {'wares':>7s} {'median rho':>11s} {'p10':>8s} {'p90':>8s} "
           f"{'% negative':>11s} {'% rho<-0.3':>11s}")
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t in TYPES:
        rhos = []
        for w in wares:
            if classify(w.meta) != t:
                continue
            s = w.sales_within(180)
            if len(s) < 20:
                continue
            qs = [x["quantity"] for x in s]
            if len(set(qs)) < 5:
                continue  # no variation to correlate against
            r = spearman(qs, [x["pricePerUnit"] for x in s])
            if r is not None:
                rhos.append(r)
        if len(rhos) < 5:
            continue
        rhos.sort()
        neg = sum(1 for r in rhos if r < 0) / len(rhos)
        strong = sum(1 for r in rhos if r < -0.3) / len(rhos)
        print(f"  {t:14s} {len(rhos):>7,} {statistics.median(rhos):>11.2f} "
              f"{rhos[int(0.1 * (len(rhos) - 1))]:>8.2f} "
              f"{rhos[int(0.9 * (len(rhos) - 1))]:>8.2f} {neg:>10.0%} {strong:>11.0%}")
    print("\n  Negative rho = a bulk discount: larger stacks change hands at a lower unit price.")


def _shares(counts):
    total = sum(counts.values())
    return {k: v / total for k, v in counts.items()} if total else {}


def table_bucket_mix(wares):
    print("\n" + "=" * 112)
    print("TABLE E - which stack sizes CLEAR, and which SIT")
    print("         Per Ware: share of the last 180d of sale events in each bucket, against")
    print("         that bucket's share of the units sitting on the board right now.")
    print("         Median across Wares of the type. Ratio > 1 = clears faster than it is listed.")
    print("=" * 112)
    labels = [b[2] for b in BUCKETS]
    for t in TYPES:
        rows = {lab: {"sale": [], "book": []} for lab in labels}
        n = 0
        for w in wares:
            if classify(w.meta) != t or w.meta["stackSize"] <= 1:
                continue
            s = w.sales_within(180)
            if len(s) < 20 or not w.listings:
                continue
            n += 1
            sale_c, book_c = {}, {}
            for x in s:
                sale_c[bucket(x["quantity"])] = sale_c.get(bucket(x["quantity"]), 0) + 1
            for l in w.listings:
                book_c[bucket(l["quantity"])] = book_c.get(bucket(l["quantity"]), 0) + 1
            ss, bs = _shares(sale_c), _shares(book_c)
            for lab in labels:
                rows[lab]["sale"].append(ss.get(lab, 0.0))
                rows[lab]["book"].append(bs.get(lab, 0.0))
        if n < 5:
            continue
        print(f"\n  {t}  ({n} Wares with varied stacks and a live book)")
        hdr = f"    {'stack':>9s} {'% of sales':>11s} {'% of book':>11s} {'clear ratio':>12s}"
        print(hdr)
        print("    " + "-" * (len(hdr) - 4))
        for lab in labels:
            sale = statistics.fmean(rows[lab]["sale"])
            book = statistics.fmean(rows[lab]["book"])
            if sale < 0.005 and book < 0.005:
                continue
            ratio = sale / book if book else float("inf")
            flag = ""
            if book >= 0.05 and ratio < 0.6:
                flag = "  <- sits"
            elif sale >= 0.05 and ratio > 1.6:
                flag = "  <- clears"
            rstr = "inf" if ratio == float("inf") else f"{ratio:>12.2f}"
            print(f"    {lab:>9s} {sale:>10.1%} {book:>10.1%} {rstr:>12s}{flag}")


def table_recommended_stack(wares):
    print("\n" + "=" * 112)
    print("TABLE F - expected gil per slot-day by stack bucket")
    print("         value = median unit price in the bucket x bucket units, divided by an")
    print("         estimated days-to-clear (units on the board in that bucket / units sold")
    print("         per day in that bucket). Aggregated as the median across Wares of the type,")
    print("         each Ware's buckets first normalised to its own best bucket = 1.00.")
    print("=" * 112)
    labels = [b[2] for b in BUCKETS]
    for t in TYPES:
        acc = {lab: [] for lab in labels}
        n = 0
        for w in wares:
            if classify(w.meta) != t or w.meta["stackSize"] <= 1:
                continue
            s = w.sales_within(180)
            if len(s) < 30 or not w.listings:
                continue
            per = {}
            for lab in labels:
                sb = [x for x in s if bucket(x["quantity"]) == lab]
                lb = [l for l in w.listings if bucket(l["quantity"]) == lab]
                if len(sb) < 3:
                    continue
                units_day = sum(x["quantity"] for x in sb) / 180.0
                on_board = sum(l["quantity"] for l in lb)
                if not units_day:
                    continue
                days = max(on_board / units_day, 0.25)
                price = statistics.median(x["pricePerUnit"] for x in sb)
                qty = statistics.median(x["quantity"] for x in sb)
                per[lab] = price * qty / days
            if len(per) < 2:
                continue
            n += 1
            best = max(per.values())
            for lab, v in per.items():
                acc[lab].append(v / best if best else 0)
        if n < 5:
            continue
        print(f"\n  {t}  ({n} Wares)")
        hdr = f"    {'stack':>9s} {'wares':>7s} {'relative value':>15s}"
        print(hdr)
        print("    " + "-" * (len(hdr) - 4))
        best_lab, best_val = None, -1
        for lab in labels:
            if len(acc[lab]) < 5:
                continue
            m = statistics.median(acc[lab])
            if m > best_val:
                best_lab, best_val = lab, m
        for lab in labels:
            if len(acc[lab]) < 5:
                continue
            m = statistics.median(acc[lab])
            bar = "#" * int(round(m * 30))
            mark = "  <- best" if lab == best_lab else ""
            print(f"    {lab:>9s} {len(acc[lab]):>7,} {m:>15.2f}  {bar}{mark}")


def table_paired(wares):
    """Tables E and F disagree, and both compare different Wares in different
    buckets. This compares buckets WITHIN a Ware, so selection cannot explain
    the result, and reports a sign test rather than a difference of medians.
    """
    print("\n" + "=" * 112)
    print("TABLE G - paired within-Ware comparison: is a full stack worse than a mid stack?")
    print("         Only Wares that traded in BOTH buckets. Sign test on the per-Ware direction.")
    print("=" * 112)
    pairs = [("50-98", "99"), ("20-49", "99"), ("10-19", "99"), ("2-9", "99")]
    hdr = (f"  {'ware type':13s} {'buckets':>14s} {'wares':>6s} "
           f"{'mid wins':>9s} {'full wins':>10s} {'p (2-sided)':>12s}   verdict")
    print(hdr)
    print("  " + "-" * (len(hdr) + 8))
    for t in TYPES:
        for lo_lab, hi_lab in pairs:
            wins_lo = wins_hi = 0
            for w in wares:
                if classify(w.meta) != t or w.meta["stackSize"] <= 1:
                    continue
                s = w.sales_within(180)
                if len(s) < 30 or not w.listings:
                    continue
                vals = {}
                for lab in (lo_lab, hi_lab):
                    sb = [x for x in s if bucket(x["quantity"]) == lab]
                    lb = [l for l in w.listings if bucket(l["quantity"]) == lab]
                    if len(sb) < 3:
                        continue
                    units_day = sum(x["quantity"] for x in sb) / 180.0
                    if not units_day:
                        continue
                    days = max(sum(l["quantity"] for l in lb) / units_day, 0.25)
                    vals[lab] = (statistics.median(x["pricePerUnit"] for x in sb)
                                 * statistics.median(x["quantity"] for x in sb) / days)
                if len(vals) == 2:
                    if vals[lo_lab] > vals[hi_lab]:
                        wins_lo += 1
                    elif vals[hi_lab] > vals[lo_lab]:
                        wins_hi += 1
            n = wins_lo + wins_hi
            if n < 8:
                continue
            p = binom_two_sided(wins_lo, n)
            if p < 0.05:
                verdict = f"{lo_lab} beats {hi_lab}" if wins_lo > wins_hi else f"{hi_lab} beats {lo_lab}"
            else:
                verdict = "no difference"
            print(f"  {t:13s} {lo_lab + ' vs ' + hi_lab:>14s} {n:>6,} "
                  f"{wins_lo:>9,} {wins_hi:>10,} {p:>12.3f}   {verdict}")
    print("\n  Sign test only - it asks which direction, never how big, and it is blind to")
    print("  the size of the gap. Both buckets are scored on the same crude days-to-clear")
    print("  estimate, so a bias in that estimate cancels within a pair but not across types.")


def binom_two_sided(k, n, p=0.5):
    """Exact two-sided binomial test, small n, no scipy."""
    from math import comb
    def pmf(i):
        return comb(n, i) * p ** i * (1 - p) ** (n - i)
    obs = pmf(k)
    return min(1.0, sum(pmf(i) for i in range(n + 1) if pmf(i) <= obs * 1.0000001))


def main():
    wares = dataset.load()
    print(f"undercut-formula prototype, stage 4 - stack size - {dataset.NOW:%Y-%m-%dT%H:%MZ}")
    table_price_vs_stack(wares)
    table_bucket_mix(wares)
    table_recommended_stack(wares)
    table_paired(wares)


if __name__ == "__main__":
    main()

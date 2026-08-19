"""Stage 6: the policy back-test, per ware type, with a per-type horizon.

Three things change from the first prototype:

  1. Horizon and step are derived from the TYPE's own trade rate rather than
     fixed at 21d/3d. A 21-day horizon is most of a Gear Ware's lifetime and
     an eternity for Materia.

  2. The fill rule is repaired for stack-traded Wares. It previously assumed
     the observed sale price was the cheapest listing, which is false where
     unit price moves with stack size. Each Ware is now pinned to its own
     modal stack bucket, and both the trailing statistics and the fill test
     are restricted to sales in that bucket - so a 99-stack is compared
     against other 99-stacks and the stack confound cancels.

  3. Results aggregate ACROSS Wares of a type - share of Wares on which each
     policy ranked first, and the median normalised score - rather than
     reporting single-Ware tables that invite reading noise as signal.

The uncorrectable biases from the first prototype still stand: history is a
union of overlapping 20-sale windows rather than a census, EMM's own listing
would have moved the book, and queue position among equal prices is ignored.
"""

import statistics

import dataset
from stacks import bucket
from wtype import TYPES, classify

TAX = 0.05
MIN_SAMPLE = 5
FLOOR_LEVELS = [0.70, 0.80, 0.90]

BASE_POLICIES = ["undercut -1g", "undercut -5%", "match",
                 "q25 (30d)", "median (30d)", "trimmed mean", "median +10%"]
POLICIES = BASE_POLICIES + [f"floor {int(f * 100)}%" for f in FLOOR_LEVELS]


def trimmed(ps):
    if len(ps) < 5:
        return statistics.median(ps)
    p = sorted(ps)
    k = int(len(p) * 0.1)
    return statistics.fmean(p[k : len(p) - k] or p)


def quantile(ps, q):
    p = sorted(ps)
    if len(p) == 1:
        return float(p[0])
    i = q * (len(p) - 1)
    lo, hi = int(i), min(int(i) + 1, len(p) - 1)
    return p[lo] + (p[hi] - p[lo]) * (i - lo)


def prices_for(hist, recent):
    ps = [r["pricePerUnit"] for r in hist]
    if len(ps) < MIN_SAMPLE or not recent:
        return {}
    cheapest = min(r["pricePerUnit"] for r in recent)
    med = statistics.median(ps)
    out = {
        "undercut -1g": max(cheapest - 1, 1),
        "undercut -5%": max(round(cheapest * 0.95), 1),
        "match": cheapest,
        "q25 (30d)": quantile(ps, 0.25),
        "median (30d)": med,
        "trimmed mean": trimmed(ps),
        "median +10%": med * 1.10,
    }
    aggressive = max(cheapest - 1, 1)
    for f in FLOOR_LEVELS:
        out[f"floor {int(f * 100)}%"] = aggressive if aggressive >= med * f else None
    return out


def type_params(wares, t):
    """Horizon and step from the type's own trade rate."""
    vs = [w.velocity(90) for w in wares
          if classify(w.meta) == t and w.velocity(90) > 0]
    if not vs:
        return None
    gap = 1.0 / statistics.median(vs)           # median days between sales
    horizon = int(min(max(round(gap * 6), 3), 60))
    step = max(1, horizon // 7)
    return horizon, step, gap


def run_ware(w, horizon, step, window):
    rows = sorted(w.sales, key=lambda r: r["timestamp"])
    if len(rows) < 20:
        return None
    # Pin stack-traded Wares to their own modal bucket so like meets like.
    if w.meta["stackSize"] > 1:
        counts = {}
        for r in rows:
            b = bucket(r["quantity"])
            counts[b] = counts.get(b, 0) + 1
        modal = max(counts, key=counts.get)
        rows = [r for r in rows if bucket(r["quantity"]) == modal]
        if len(rows) < 20:
            return None
    start = rows[0]["timestamp"] + window * 86400
    end = dataset.NOW_TS - horizon * 86400
    if end <= start:
        return None

    res = {p: {"gil": 0.0, "slot_days": 0.0, "acted": 0, "held": 0, "unsold": 0}
           for p in POLICIES}
    t = start
    points = 0
    while t < end:
        hist = [r for r in rows if t - window * 86400 <= r["timestamp"] < t]
        recent = [r for r in rows if t - horizon * 86400 <= r["timestamp"] < t]
        future = [r for r in rows if t <= r["timestamp"] < t + horizon * 86400]
        ps = prices_for(hist, recent)
        t += step * 86400
        if not ps:
            continue
        points += 1
        for name, price in ps.items():
            s = res[name]
            if price is None:
                s["held"] += 1
                continue
            s["acted"] += 1
            hit = next((r for r in future if r["pricePerUnit"] >= price), None)
            if hit is None:
                s["unsold"] += 1
                s["slot_days"] += horizon
            else:
                s["gil"] += price * (1 - TAX)
                s["slot_days"] += max((hit["timestamp"] - t) / 86400.0, 0.25)
    if points < 8:
        return None
    scores = {}
    for name, s in res.items():
        if not s["acted"]:
            continue
        scores[name] = {
            "per_day": s["gil"] / s["slot_days"] if s["slot_days"] else 0,
            "per_dec": s["gil"] / s["acted"],
            "hold_rate": s["held"] / (s["held"] + s["acted"]),
        }
    return scores if len(scores) >= 4 else None


def report(wares):
    print(f"undercut-formula prototype, stage 6 - typed back-test - {dataset.NOW:%Y-%m-%dT%H:%MZ}")
    for t in TYPES:
        params = type_params(wares, t)
        if not params:
            continue
        horizon, step, gap = params
        window = max(30, horizon)
        results = []
        for w in wares:
            if classify(w.meta) != t:
                continue
            r = run_ware(w, horizon, step, window)
            if r:
                results.append(r)
        if len(results) < 8:
            continue
        print("\n" + "=" * 112)
        print(f"{t}  -  {len(results)} Wares  |  median gap between sales {gap:.1f}d  "
              f"|  horizon {horizon}d, step {step}d, window {window}d")
        print("=" * 112)
        hdr = (f"  {'policy':16s} {'wins/slot-day':>14s} {'wins/decision':>14s} "
               f"{'rel slot-day':>13s} {'rel per-unit':>13s} {'hold rate':>10s}")
        print(hdr)
        print("  " + "-" * (len(hdr) - 2))
        rows = []
        for p in POLICIES:
            per_day, per_dec, holds = [], [], []
            w1 = w2 = 0
            for r in results:
                if p not in r:
                    continue
                bd = max(x["per_day"] for x in r.values())
                bu = max(x["per_dec"] for x in r.values())
                if bd and r[p]["per_day"] >= bd - 1e-9:
                    w1 += 1
                if bu and r[p]["per_dec"] >= bu - 1e-9:
                    w2 += 1
                if bd:
                    per_day.append(r[p]["per_day"] / bd)
                if bu:
                    per_dec.append(r[p]["per_dec"] / bu)
                holds.append(r[p]["hold_rate"])
            if not per_day:
                continue
            rows.append((statistics.median(per_dec) if per_dec else 0, p,
                         w1 / len(results), w2 / len(results),
                         statistics.median(per_day),
                         statistics.median(per_dec) if per_dec else 0,
                         statistics.fmean(holds) if holds else 0))
        for _, p, w1, w2, rd, ru, hr in sorted(rows, reverse=True):
            print(f"  {p:16s} {w1:>13.0%} {w2:>14.0%} {rd:>13.2f} {ru:>13.2f} {hr:>10.0%}")
        print("\n  wins = share of Wares where the policy scored top on that metric.")
        print("  rel  = the policy's score as a fraction of the best policy on that Ware, median.")


def main():
    wares = dataset.load()
    report(wares)


if __name__ == "__main__":
    main()

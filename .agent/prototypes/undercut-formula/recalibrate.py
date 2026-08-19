"""Daily recalibration: the per-type parameters are FITTED, never constants.

Standing rule (maintainer, 2026-08-17): once the plugin is live, the formulas
adjust on a daily basis after the day's data is pulled. They will never remain
constant, and the machinery for that has to exist in the logic rather than be
retrofitted.

Two things here:

  calibrate(as_of)  the refit itself, as a callable step - this is the shape
                    the daily job would take, computing every per-type
                    parameter from data strictly before `as_of`.

  the drift report  runs it at weekly points across the history and measures
                    how far each parameter actually moves. That is the
                    evidence for why a constant would go stale, rather than
                    an assertion that it would.
"""

import math
import statistics
import sys

import stream
from wtype import TYPES, classify

DAY = 86400
WINDOW = 30          # days of history each refit looks at
WEEKS = 12           # how many weekly refits to compare
MIN_PER_TYPE = 8


def fit(samples):
    """Turn one window's raw samples into the per-type parameter set.

    This is the whole of the daily job's arithmetic: everything downstream -
    horizon, step, stack target - is derived here and nothing is hardcoded.
    """
    out = {}
    for t, s in samples.items():
        if len(s["velocity"]) < MIN_PER_TYPE:
            continue
        gap = 1.0 / statistics.median(s["velocity"])
        horizon = int(min(max(round(gap * 6), 3), 60))
        out[t] = {
            "gap": gap,
            "horizon": horizon,
            "step": max(1, horizon // 7),
            "window": max(30, horizon),
            "target_txn": statistics.median(s["txn"]) if len(s["txn"]) >= 50 else None,
            "price_level": statistics.median(s["price"]) if s["price"] else None,
            "wares": len(s["velocity"]),
        }
    return out


def calibrate(as_of, wares_iter):
    """One refit, from data strictly before `as_of`."""
    samples = {t: {"velocity": [], "txn": [], "price": []} for t in TYPES}
    for w in wares_iter:
        rows = w.within(WINDOW, end=as_of)
        if not rows:
            continue
        t = classify(w.meta)
        s = samples[t]
        s["velocity"].append(len(rows) / WINDOW)
        s["price"].append(statistics.median(x.price for x in rows))
        if w.meta["stackSize"] > 1 and len(rows) >= 5:
            s["txn"].extend(x.price * x.qty for x in rows)
    return fit(samples)


def main():
    as_ofs = [stream.NOW_TS - w * 7 * DAY for w in range(WEEKS, 0, -1)]
    samples = {a: {t: {"velocity": [], "txn": [], "price": []} for t in TYPES}
               for a in as_ofs}

    seen = 0
    for w in stream.wares(min_sales=1):
        seen += 1
        if seen % 5000 == 0:
            print(f"  {seen:,} wares", flush=True, file=sys.stderr)
        t = classify(w.meta)
        stackable = w.meta["stackSize"] > 1
        for a in as_ofs:
            rows = w.within(WINDOW, end=a)
            if not rows:
                continue
            s = samples[a][t]
            s["velocity"].append(len(rows) / WINDOW)
            s["price"].append(statistics.median(x.price for x in rows))
            if stackable and len(rows) >= 5:
                s["txn"].extend(x.price * x.qty for x in rows)

    fits = {a: fit(samples[a]) for a in as_ofs}

    print("\n" + "=" * 104)
    print(f"TABLE 9 - do the fitted parameters hold still? {WEEKS} weekly refits, "
          f"{WINDOW}-day window each")
    print("=" * 104)
    print("  Each refit uses only data before its own date, exactly as the daily job would.")
    for key, label, fmt in (("horizon", "horizon (days)", "{:.0f}"),
                            ("target_txn", "stack target (gil)", "{:,.0f}"),
                            ("price_level", "median unit price", "{:,.0f}")):
        print(f"\n  {label}")
        hdr = (f"    {'ware type':13s} {'first':>12s} {'last':>12s} {'min':>12s} "
               f"{'max':>12s} {'range/median':>13s} {'wk-to-wk':>10s}")
        print(hdr)
        print("    " + "-" * (len(hdr) - 4))
        for t in TYPES:
            series = [fits[a][t][key] for a in as_ofs
                      if t in fits[a] and fits[a][t].get(key) is not None]
            if len(series) < WEEKS - 2:
                continue
            med = statistics.median(series)
            if not med:
                continue
            spread = (max(series) - min(series)) / med
            steps = [abs(b - a) / a for a, b in zip(series, series[1:]) if a]
            wk = statistics.median(steps) if steps else 0
            print(f"    {t:13s} {fmt.format(series[0]):>12s} "
                  f"{fmt.format(series[-1]):>12s} {fmt.format(min(series)):>12s} "
                  f"{fmt.format(max(series)):>12s} {spread:>12.0%} {wk:>9.0%}")

    print("\n  range/median = how far the parameter travelled across the 12 refits.")
    print("  wk-to-wk     = the typical move between one refit and the next.")
    print("\n  These are WEEKLY refits on a 30-day window, so they are the slow-moving")
    print("  version of the daily job. A daily refit on the same window moves less per")
    print("  step and reacts sooner; both are strictly better than a constant.")


if __name__ == "__main__":
    main()

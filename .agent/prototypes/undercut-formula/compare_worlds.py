"""Cactuar against Adamantoise: which parameters are structure, which are calibration?

Both worlds sit on Aether, so this is the cheapest available test of the thing
that matters for shipping: a parameter that agrees across two worlds can be a
shipped default, and one that disagrees has to be fitted on the player's own
world before it means anything.

Two worlds is enough to detect disagreement and nowhere near enough to
establish agreement - two worlds agreeing could still both differ from a third.
Read a match as "no evidence against", not as "transfers".
"""

import math
import statistics
import sys

import stream
from wtype import TYPES, classify

WORLDS = [("Cactuar", "data_all"), ("Adamantoise", "data_adam")]
WINDOW = 90


def collect(cache):
    """Per-type samples and per-Ware price levels for one world."""
    per_type = {t: {"velocity": [], "txn": [], "price": [], "wares": 0,
                    "any": 0, "eff5": 0} for t in TYPES}
    levels = {}
    seen = 0
    for w in stream.all_wares_including_empty_from(cache):
        t = classify(w.meta)
        s = per_type[t]
        s["wares"] += 1
        seen += 1
        if seen % 8000 == 0:
            print(f"    {seen:,}", flush=True, file=sys.stderr)
        rows = w.within(WINDOW)
        if not rows:
            continue
        s["any"] += 1
        import models
        if models.effective_n(w.within(30)) >= 5:
            s["eff5"] += 1
        s["velocity"].append(len(rows) / WINDOW)
        med = statistics.median(x.price for x in rows)
        s["price"].append(med)
        levels[(w.item, w.hq)] = (med, len(rows))
        if w.meta["stackSize"] > 1 and len(rows) >= 5:
            s["txn"].extend(x.price * x.qty for x in rows)
    return per_type, levels


def fit(per_type):
    out = {}
    for t, s in per_type.items():
        if len(s["velocity"]) < 8:
            continue
        gap = 1.0 / statistics.median(s["velocity"])
        out[t] = {
            "gap": gap,
            "horizon": int(min(max(round(gap * 6), 3), 60)),
            "txn": statistics.median(s["txn"]) if len(s["txn"]) >= 50 else None,
            "price": statistics.median(s["price"]) if s["price"] else None,
            "coverage": s["eff5"] / s["wares"] if s["wares"] else 0,
            "wares": s["wares"],
        }
    return out


def main():
    fits, levels = {}, {}
    for name, cache in WORLDS:
        print(f"loading {name} ...", flush=True)
        pt, lv = collect(cache)
        fits[name] = fit(pt)
        levels[name] = lv
        print(f"  {name}: {sum(v['wares'] for v in pt.values()):,} Wares", flush=True)

    a, b = WORLDS[0][0], WORLDS[1][0]

    print("\n" + "=" * 104)
    print(f"TABLE 10 - fitted parameters, {a} against {b} (both on Aether)")
    print("=" * 104)
    for key, label, fmt in (("horizon", "horizon (days)", "{:.0f}"),
                            ("txn", "stack target (gil)", "{:,.0f}"),
                            ("price", "median unit price", "{:,.0f}"),
                            ("coverage", "share clearing n_eff>=5", "{:.1%}")):
        print(f"\n  {label}")
        hdr = f"    {'ware type':13s} {a:>14s} {b:>14s} {'ratio':>9s}   reading"
        print(hdr)
        print("    " + "-" * (len(hdr) + 8))
        for t in TYPES:
            if t not in fits[a] or t not in fits[b]:
                continue
            va, vb = fits[a][t].get(key), fits[b][t].get(key)
            if not va or not vb:
                continue
            ratio = vb / va
            reading = ("agrees" if 0.8 <= ratio <= 1.25
                       else "differs" if 0.5 <= ratio <= 2.0 else "differs sharply")
            print(f"    {t:13s} {fmt.format(va):>14s} {fmt.format(vb):>14s} "
                  f"{ratio:>9.2f}   {reading}")

    print("\n" + "=" * 104)
    print(f"TABLE 11 - the same Ware priced on both worlds")
    print("=" * 104)
    hdr = (f"  {'ware type':13s} {'wares':>8s} {'p10':>8s} {'median':>8s} {'p90':>8s} "
           f"{'|gap|>25%':>10s}")
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    common = set(levels[a]) & set(levels[b])
    meta = stream.items_meta("data_all")
    by_type = {}
    for key in common:
        pa, na = levels[a][key]
        pb, nb = levels[b][key]
        if min(na, nb) < 8 or min(pa, pb) <= 0:
            continue
        m = meta.get(key[0])
        if not m:
            continue
        by_type.setdefault(classify(m), []).append(pb / pa)
    for t in TYPES:
        rs = by_type.get(t, [])
        if len(rs) < 10:
            continue
        rs.sort()
        big = sum(1 for r in rs if abs(r - 1) > 0.25) / len(rs)
        print(f"  {t:13s} {len(rs):>8,} {rs[int(0.1 * (len(rs) - 1))]:>8.2f} "
              f"{statistics.median(rs):>8.2f} {rs[int(0.9 * (len(rs) - 1))]:>8.2f} "
              f"{big:>10.0%}")
    allr = sorted(r for v in by_type.values() for r in v)
    if allr:
        print("  " + "-" * (len(hdr) - 2))
        big = sum(1 for r in allr if abs(r - 1) > 0.25) / len(allr)
        print(f"  {'ALL':13s} {len(allr):>8,} {allr[int(0.1 * (len(allr) - 1))]:>8.2f} "
              f"{statistics.median(allr):>8.2f} "
              f"{allr[int(0.9 * (len(allr) - 1))]:>8.2f} {big:>10.0%}")
    print(f"\n  ratio = {b} price / {a} price, {WINDOW}-day median, Wares with >= 8 sales")
    print("  on both worlds. 1.00 = the two worlds price the Ware the same.")


if __name__ == "__main__":
    main()

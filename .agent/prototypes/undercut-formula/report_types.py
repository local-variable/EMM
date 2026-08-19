"""Stage 3: do the proposed ware types actually have different sale patterns?"""

import statistics

import dataset
import wtype
from wtype import TYPES, classify


def fmt(v, nd=2):
    if v is None:
        return "     -"
    if abs(v) >= 1000:
        return f"{v:>10,.0f}"
    return f"{v:>10,.{nd}f}"


def table_coverage(wares):
    print("\n" + "=" * 112)
    print("TABLE A - sample composition and market-data coverage (Cactuar, 180-day window)")
    print("=" * 112)
    cov = dataset.coverage()
    print(f"  sampled items {cov['sampled']:,} | with history {cov['history']:,} "
          f"| with listings {cov['listings']:,} | with aggregate {cov['aggregated']:,}")
    hdr = (f"  {'ware type':14s} {'wares':>7s} {'any sale':>9s} {'>=5/30d':>9s} "
           f"{'>=30/30d':>9s} {'listed now':>11s} {'no data':>9s}")
    print()
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t in TYPES:
        ws = [w for w in wares if classify(w.meta) == t]
        if not ws:
            continue
        any_sale = sum(1 for w in ws if w.sales)
        n5 = sum(1 for w in ws if len(w.sales_within(30)) >= 5)
        n30 = sum(1 for w in ws if len(w.sales_within(30)) >= 30)
        listed = sum(1 for w in ws if w.listings)
        print(f"  {t:14s} {len(ws):>7,} {any_sale:>9,} {n5:>9,} {n30:>9,} "
              f"{listed:>11,} {len(ws) - any_sale:>9,}")
    ws = wares
    print("  " + "-" * (len(hdr) - 2))
    print(f"  {'ALL':14s} {len(ws):>7,} {sum(1 for w in ws if w.sales):>9,} "
          f"{sum(1 for w in ws if len(w.sales_within(30)) >= 5):>9,} "
          f"{sum(1 for w in ws if len(w.sales_within(30)) >= 30):>9,} "
          f"{sum(1 for w in ws if w.listings):>11,} "
          f"{sum(1 for w in ws if not w.sales):>9,}")
    print("\n  A Ware is (item, quality). Items that can be HQ contribute two Wares.")


METRICS = [
    ("sales/day", lambda w: w.velocity(90), 3),
    ("units/sale", lambda w: w.units_per_sale(90), 2),
    ("IQR/median", lambda w: w.dispersion(90), 2),
    ("30d/180d drift", lambda w: w.drift(), 2),
    ("burstiness CV", lambda w: w.burstiness(90), 2),
    ("days of supply", lambda w: w.days_of_supply(), 1),
    ("unit price", lambda w: statistics.median(
        [s["pricePerUnit"] for s in w.sales_within(90)]) if w.sales_within(90) else None, 0),
]


def table_patterns(wares):
    print("\n" + "=" * 112)
    print("TABLE B - sale-pattern descriptors by ware type (median, with p10-p90 beneath)")
    print("=" * 112)
    hdr = f"  {'ware type':14s} " + " ".join(f"{n:>16s}" for n, _, _ in METRICS)
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t in TYPES:
        ws = [w for w in wares if classify(w.meta) == t and w.sales]
        if len(ws) < 5:
            continue
        meds, spreads = [], []
        for _, fn, nd in METRICS:
            s = wtype.summarise([fn(w) for w in ws])
            if not s:
                meds.append(f"{'-':>16s}")
                spreads.append(f"{'':>16s}")
                continue
            meds.append(f"{s['median']:>16,.{nd}f}")
            spreads.append(f"{s['p10']:,.{nd}f}-{s['p90']:,.{nd}f}".rjust(16))
        print(f"  {t:14s} " + " ".join(meds))
        print(f"  {'':14s} " + " ".join(spreads))
    print("\n  burstiness CV: coefficient of variation of inter-arrival times."
          "\n  1.0 is a Poisson (memoryless) arrival process; above 1.0 means"
          "\n  sales clump, so a short window is a worse estimator than its n suggests.")


def table_separation(wares):
    print("\n" + "=" * 112)
    print("TABLE C - is the ware-type split real? Kruskal-Wallis effect size per metric")
    print("=" * 112)
    print("  eps^2 = share of the rank variation in that metric accounted for by ware type.")
    print("  Rank-based and on log values, because these distributions are heavy-tailed.")
    print()
    hdr = f"  {'metric':18s} {'eps^2':>8s} {'H':>10s} {'wares':>8s} {'groups':>7s}   reading"
    print(hdr)
    print("  " + "-" * (len(hdr) + 12))
    for name, fn, _ in METRICS:
        res, counts = wtype.separation(wares, fn)
        if not res:
            continue
        eps, h, n, k = res
        if eps >= 0.14:
            reading = "large"
        elif eps >= 0.06:
            reading = "medium"
        elif eps >= 0.01:
            reading = "small"
        else:
            reading = "negligible"
        print(f"  {name:18s} {eps:>8.3f} {h:>10,.0f} {n:>8,} {k:>7}   {reading}")
    print("\n  Conventional epsilon-squared bands: 0.01 small, 0.06 medium, 0.14 large.")


def main():
    wares = dataset.load()
    print(f"undercut-formula prototype, stage 3 - {dataset.NOW:%Y-%m-%dT%H:%MZ}")
    print(f"{len(wares):,} Wares from a seeded 1,000-item sample of the 16,843 marketable items")
    table_coverage(wares)
    table_patterns(wares)
    table_separation(wares)


if __name__ == "__main__":
    main()

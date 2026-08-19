"""Stage 7: the local (per-World) pricing index, with the DC figure as a gut check.

The ruling being tested: a Player trades on ONE World, so the World figure is
the pricing reference and the DC figure is a secondary sanity signal - never
the price, because a DC figure describes markets you would have to travel to.
"""

import statistics

import dataset
from wtype import TYPES, classify


def pair(w, field):
    """(world, dc) for an aggregated field, or None."""
    a = w.agg_world.get(field) or {}
    world = (a.get("world") or {}).get("price")
    dc = (a.get("dc") or {}).get("price")
    if not world or not dc:
        return None
    return world, dc


def table_gap(wares):
    print("\n" + "=" * 112)
    print("TABLE K - how far does the local World sit from its data centre?")
    print("         Ratio of the Cactuar figure to the Aether figure, per Ware.")
    print("=" * 112)
    for field, title in (("averageSalePrice", "average sale price"),
                         ("minListing", "cheapest listing")):
        print(f"\n  {title}")
        hdr = (f"    {'ware type':14s} {'wares':>7s} {'p10':>8s} {'median':>8s} {'p90':>8s} "
               f"{'|gap|>25%':>10s} {'|gap|>50%':>10s}")
        print(hdr)
        print("    " + "-" * (len(hdr) - 4))
        for t in TYPES:
            rs = []
            for w in wares:
                if classify(w.meta) != t:
                    continue
                p = pair(w, field)
                if p:
                    rs.append(p[0] / p[1])
            if len(rs) < 5:
                continue
            rs.sort()
            big = sum(1 for r in rs if abs(r - 1) > 0.25) / len(rs)
            huge = sum(1 for r in rs if abs(r - 1) > 0.50) / len(rs)
            print(f"    {t:14s} {len(rs):>7,} {rs[int(0.1 * (len(rs) - 1))]:>8.2f} "
                  f"{statistics.median(rs):>8.2f} {rs[int(0.9 * (len(rs) - 1))]:>8.2f} "
                  f"{big:>10.0%} {huge:>10.0%}")
    print("\n  1.00 = the local World agrees with its data centre.")


def table_reachability(wares):
    """The DC minListing is frequently on another World, so it is not a price
    a local seller can be undercut by. Quantify how often."""
    print("\n" + "=" * 112)
    print("TABLE L - is the DC's cheapest listing even on this World?")
    print("=" * 112)
    hdr = (f"  {'ware type':14s} {'wares':>7s} {'dc min is local':>16s} "
           f"{'median local premium':>21s}")
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t in TYPES:
        local = 0
        total = 0
        prem = []
        for w in wares:
            if classify(w.meta) != t:
                continue
            a = w.agg_world.get("minListing") or {}
            world = (a.get("world") or {}).get("price")
            dcp = (a.get("dc") or {}).get("price")
            if not world or not dcp:
                continue
            total += 1
            if world <= dcp:
                local += 1
            prem.append(world / dcp)
        if total < 5:
            continue
        print(f"  {t:14s} {total:>7,} {local / total:>15.0%} "
              f"{statistics.median(prem):>20.2f}x")
    print("\n  Where the local cheapest listing is above the DC's, the gap is not an")
    print("  opportunity for a local seller - it is a price on a board the buyer would")
    print("  have to travel to, and cross-DC travel is out of scope by decision.")


def table_index(wares):
    """A local pricing index: each Ware's own level, expressed against the DC,
    aggregated per type. This is what a 'this World runs rich/cheap' signal
    would be built on."""
    print("\n" + "=" * 112)
    print("TABLE M - a local pricing index for Cactuar, by ware type")
    print("         Median of (World average sale price / DC average sale price) across Wares,")
    print("         with the share of Wares above and below parity.")
    print("=" * 112)
    hdr = (f"  {'ware type':14s} {'wares':>7s} {'index':>8s} {'% rich':>8s} {'% cheap':>9s} "
           f"{'velocity share':>15s}")
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t in TYPES:
        rs, vs = [], []
        for w in wares:
            if classify(w.meta) != t:
                continue
            p = pair(w, "averageSalePrice")
            if p:
                rs.append(p[0] / p[1])
            v = w.agg_world.get("dailySaleVelocity") or {}
            vw = (v.get("world") or {}).get("quantity")
            vd = (v.get("dc") or {}).get("quantity")
            if vw and vd:
                vs.append(vw / vd)
        if len(rs) < 5:
            continue
        rich = sum(1 for r in rs if r > 1.05) / len(rs)
        cheap = sum(1 for r in rs if r < 0.95) / len(rs)
        vshare = statistics.median(vs) if vs else 0
        print(f"  {t:14s} {len(rs):>7,} {statistics.median(rs):>8.2f} "
              f"{rich:>7.0%} {cheap:>8.0%} {vshare:>14.1%}")
    print("\n  velocity share = this World's daily units sold as a fraction of the whole DC's,")
    print("  i.e. how much of the data centre's trade in that type happens here.")
    print("\n  NOT MEASURED: whether a large World-vs-DC gap predicts the World reverting")
    print("  towards the DC. That needs a DC history series, and a deep DC history request")
    print("  returns HTTP 500 - so it needs a targeted, throttled backfill of its own.")


def main():
    wares = dataset.load()
    print(f"undercut-formula prototype, stage 7 - local index - {dataset.NOW:%Y-%m-%dT%H:%MZ}")
    table_gap(wares)
    table_reachability(wares)
    table_index(wares)


if __name__ == "__main__":
    main()

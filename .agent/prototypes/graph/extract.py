"""Build `data.js` for the graph prototype from the local corpus.

Backs wayfinder ticket #12 "Design the price history graph and its confidence
bands". Reads ONLY the cached Cactuar corpus under
`../undercut-formula/data_all/` - nothing is fetched. Do not point this at the
network; the corpus is complete and pinned by standing instruction.

Everything the prototype draws that comes from a MODEL is computed here with
the same code the pricing engine prototype uses (`models.estimate`,
`models.effective_n`, `models.block_bootstrap`), so the line on the chart is
the number the pricer would quote. That is the point: one estimator, two
views.

Privacy: buyer names are hashed, retainer names are dropped, and the
maintainer's own retainers are reduced to a boolean `own` flag on live
listings. Nothing exported names a person, a character or a retainer.
"""

import datetime as dt
import json
import os
import statistics
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
UF = os.path.join(HERE, "..", "undercut-formula")
sys.path.insert(0, UF)

import models  # noqa: E402
import stream  # noqa: E402
import wtype  # noqa: E402
from common import OWN_RETAINERS  # noqa: E402  (names never exported)

DAY = 86400

# Item ids: the maintainer's own seven Wares from the v1 prototype (familiar
# shapes to react to) plus four picked from the aggregate scan to cover the
# regimes the ticket names.
ITEMS = [
    41769,  # Quicktongue Materia XI      liquid materia, cheap, 34x outlier on record
    41763,  # Gatherer's Guile Materia XI liquid materia, high value
    44036,  # Mythloam Aethersand         material, ~7 sales/day
    36060,  # Tsai tou Vounou             consumable, HQ and NQ both trade
    47166,  # Gold Thumb's Mallet         crafted tool, HQ/NQ, 3.3x premium
    47189,  # Crested Hood of Gathering   crafted gear, HQ dominant, NQ thin
    41145,  # Labyrinthos Grape Lamppost  furnishing, ~1.2 sales/day
    41139,  # Flower-wreathed Gazebo      furnishing, slow, high value - hold pays
    46785,  # Wind-up Feo Ul              minion, ~1/day, multi-million
    44421,  # Everseeker's Fishing Rod    thin: a handful of sales in 90 days
    1621,   # Blunt Goblin Longsword      no sales at all, two listings, upload 26 days stale
]

# Table 7 of the #11 prototype - per-type window / horizon / floor. Cold-start
# seeds; the daily refit overwrites them once EMM is live.
TYPE_PARAMS = {
    "Materia":     dict(window=30, horizon=3,  step=1, floor=0.95),
    "Material":    dict(window=30, horizon=9,  step=1, floor=0.95),
    "Consumable":  dict(window=39, horizon=39, step=5, floor=0.70),
    "Gear":        dict(window=60, horizon=60, step=8, floor=0.80),
    "Furnishing":  dict(window=30, horizon=27, step=3, floor=0.95),
    "Collectible": dict(window=30, horizon=19, step=2, floor=0.95),
    "Miscellany":  dict(window=30, horizon=20, step=2, floor=0.95),
}


def load_batches(ids):
    """Locate each item's history/listing/aggregate batch without streaming
    the whole 296 MB corpus: batches were written in sorted-id order, 20 per
    history file and 100 per listing/aggregate file."""
    meta = stream.items_meta()
    order = sorted(meta)
    pos = {i: n for n, i in enumerate(order)}
    out = {}
    for item in ids:
        p = pos[item]
        out[item] = dict(
            meta=meta[item],
            hist=os.path.join(stream.BULK, f"hist_world_{p // 20:04d}.json"),
            lst=os.path.join(stream.BULK, f"lst_world_{p // 100:04d}.json"),
            aggw=os.path.join(stream.BULK, f"agg_world_{p // 100:04d}.json"),
            aggd=os.path.join(stream.BULK, f"agg_dc_{p // 100:04d}.json"),
        )
    return out


def read(path):
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def build_ware(item, hq, meta, hist_item, lst_item, aggw, aggd):
    entries = hist_item.get("entries", []) if hist_item else []
    rows = [stream.Sale(e["timestamp"], e["pricePerUnit"], e["quantity"],
                        hash(e.get("buyerName") or "") & 0x7FFFFFFF)
            for e in entries if bool(e["hq"]) == hq]
    rows.sort(key=lambda s: s.ts)
    listings = [(l["pricePerUnit"], l["quantity"])
                for l in (lst_item or {}).get("listings", []) if bool(l["hq"]) == hq]
    listings.sort()
    q = "hq" if hq else "nq"
    return stream.Ware(item, hq, meta, rows, listings,
                       (aggw or {}).get(q, {}), (aggd or {}).get(q, {}))


def estimate_series(ware, start_ts, end_ts):
    """The estimator, evaluated once per UTC day. Historical points may only
    use rungs 1-3 (own-World sales): the live book, the data centre and the
    vendor price have no history in the corpus, and pretending today's book
    existed 90 days ago would be exactly the anachronism the rendering rule
    forbids. Where no sales rung applies the series has a gap - the line
    breaks, it does not bridge."""
    bare = stream.Ware(ware.item, ware.hq, ware.meta, ware.sales, [], {}, {})
    out = []
    t = (int(start_ts) // DAY) * DAY
    while t <= end_ts:
        e = models.estimate(bare, t, reps=100)
        if e.basis in ("30-day history", "90-day history", "180-day history"):
            out.append([t, round(e.point, 2), round(e.lo, 2), round(e.hi, 2),
                        round(e.n_eff, 2), e.severity, e.basis])
        else:
            out.append([t, None, None, None, 0, e.severity, e.basis])
        t += DAY
    return out


def replay_strategy(ware, params, start_ts, end_ts):
    """A Strategy replayed over real History, to give the own-activity layer
    something real-shaped to draw. SIMULATED - these are not the Player's
    acts. The Strategy is Table 7's pick for the type: undercut the cheapest
    recent Sale by one gil, but never below `floor` x the trailing median;
    below that, Hold with a reason and a review time. A listing sells at the
    first later Sale at or above its Unit Price (the fill rule the #11
    prototype used for unit-traded Wares - it flatters commodities, which is
    why the replay is offered only on single-unit Wares here)."""
    win, hor, step, floor = (params[k] for k in ("window", "horizon", "step", "floor"))
    events = []
    t = (int(start_ts) // DAY) * DAY + win * DAY
    active = None  # (price, listed_at)
    while t <= end_ts:
        hist = ware.within(win, end=t)
        recent = ware.within(7, end=t)
        # settle an active listing first
        if active:
            p, listed_at = active
            sold_at = next((s.ts for s in ware.sales if s.ts > listed_at and s.price >= p), None)
            if sold_at is not None and sold_at <= t:
                events.append(dict(t=sold_at, kind="sold", price=p))
                active = None
        n_eff = models.effective_n(hist)
        if n_eff < models.MIN_EFF or not recent:
            if not active:
                events.append(dict(t=t, kind="hold", price=None,
                                   reason=f"Sample too thin ({n_eff:.1f} effective observations in {win} days)",
                                   review=t + step * DAY))
            t += step * DAY
            continue
        med = statistics.median(s.price for s in hist)
        cheapest = min(s.price for s in recent)
        target = max(cheapest - 1, 1)
        fl = med * floor
        if active:
            p, listed_at = active
            if target < p and target >= fl:
                events.append(dict(t=t, kind="reprice", price=target, prev=p,
                                   reason=f"cheapest recent Sale {cheapest:,.0f} - Reprice from {p:,.0f} to {target:,.0f}, above Floor {fl:,.0f}"))
                active = (target, t)
            elif target < fl and t - listed_at >= hor * DAY:
                events.append(dict(t=t, kind="hold", price=p,
                                   reason=f"cheapest recent Sale {cheapest:,.0f} is below Floor {fl:,.0f} - holding at {p:,.0f}",
                                   review=t + step * DAY))
        else:
            if target >= fl:
                events.append(dict(t=t, kind="list", price=target,
                                   reason=f"List at {target:,.0f}: one gil under the cheapest recent Sale, above Floor {fl:,.0f} (95% of the {win}-day median)" if floor == 0.95 else
                                          f"List at {target:,.0f}: one gil under the cheapest recent Sale, above Floor {fl:,.0f} ({int(floor*100)}% of the {win}-day median)"))
                active = (target, t)
            else:
                events.append(dict(t=t, kind="hold", price=None,
                                   reason=f"cheapest recent Sale {cheapest:,.0f} is below Floor {fl:,.0f} - let the undercutter's stock clear",
                                   review=t + step * DAY))
        t += step * DAY
    return events


def main():
    batches = load_batches(ITEMS)
    wares_out = []
    now_ts = 0
    for item in ITEMS:
        b = batches[item]
        meta = b["meta"]
        hist_item = read(b["hist"]).get("items", {}).get(str(item))
        lst_item = read(b["lst"]).get("items", {}).get(str(item))
        aggw = next((r for r in read(b["aggw"]).get("results", []) if int(r["itemId"]) == item), None)
        aggd = next((r for r in read(b["aggd"]).get("results", []) if int(r["itemId"]) == item), None)
        upload = (lst_item or {}).get("lastUploadTime") or (hist_item or {}).get("lastUploadTime") or 0
        now_ts = max(now_ts, upload // 1000)
        for hq in ((False, True) if meta["canBeHq"] else (False,)):
            w = build_ware(item, hq, meta, hist_item, lst_item, aggw, aggd)
            wares_out.append((w, lst_item, aggw, aggd, upload))

    # "as of" = the moment the corpus was captured, so Freshness reads
    # correctly rather than ageing every time the page opens.
    NOW = now_ts
    print("as of", dt.datetime.fromtimestamp(NOW, dt.timezone.utc).isoformat())

    export = []
    for w, lst_item, aggw, aggd, upload in wares_out:
        t = wtype.classify(w.meta)
        params = TYPE_PARAMS[t]
        q = "hq" if w.hq else "nq"
        first = w.sales[0].ts if w.sales else NOW - 30 * DAY
        est = estimate_series(w, first, NOW) if w.sales else []
        # the estimate the stats card shows: full ladder, at NOW
        e_now = models.estimate(w, NOW, reps=200)
        listings = []
        for l in (lst_item or {}).get("listings", []):
            if bool(l["hq"]) != w.hq:
                continue
            listings.append(dict(p=l["pricePerUnit"], q=l["quantity"],
                                 rt=l["lastReviewTime"],
                                 own=bool(l.get("retainerName") in OWN_RETAINERS)))
        listings.sort(key=lambda x: x["p"])
        single_unit = w.meta["stackSize"] == 1 or (
            w.sales and statistics.median(s.qty for s in w.sales) == 1)
        replay = replay_strategy(w, params, first, NOW) if (w.sales and single_unit and len(w.sales) >= 20) else []
        wa = (aggw or {}).get(q, {})
        da = (aggd or {}).get(q, {})
        export.append(dict(
            id=w.item, hq=w.hq, name=w.meta["name"], type=t,
            category=w.meta["searchCategory"], uiCategory=w.meta["uiCategory"],
            stackSize=w.meta["stackSize"], ilvl=w.meta["ilvl"],
            vendorBuy=w.meta["vendorBuy"], vendorSell=w.meta["vendorSell"],
            canBeHq=w.meta["canBeHq"],
            params=params,
            lastUpload=upload // 1000,
            sales=[[s.ts, s.price, s.qty, s.buyer] for s in w.sales],
            listings=listings,
            agg=dict(
                world=dict(minListing=(wa.get("minListing") or {}).get("world", {}).get("price"),
                           avgSale=(wa.get("averageSalePrice") or {}).get("world", {}).get("price"),
                           velocity=(wa.get("dailySaleVelocity") or {}).get("world", {}).get("quantity"),
                           recent=(wa.get("recentPurchase") or {}).get("world", {})),
                dc=dict(minListing=(da.get("minListing") or {}).get("dc", {}).get("price"),
                        avgSale=(da.get("averageSalePrice") or {}).get("dc", {}).get("price"),
                        velocity=(da.get("dailySaleVelocity") or {}).get("dc", {}).get("quantity")),
            ),
            estimate=est,
            estimateNow=dict(point=e_now.point, lo=e_now.lo, hi=e_now.hi, nEff=e_now.n_eff,
                             basis=e_now.basis, reason=e_now.reason,
                             confident=e_now.confident, severity=e_now.severity),
            replay=replay,
            truncated=False,  # set below, per item
        ))
        print(f"  {w.name:38s} type={t:11s} sales={len(w.sales):5d} listings={len(listings):3d} "
              f"est_now={e_now.point:>12,.0f} [{e_now.basis}/{e_now.severity}] replay={len(replay)}")

    # sample truncation: the fetch capped each item at 3,000 entries, so a
    # busy item's History begins later than 180 days ago. Flag per item.
    per_item_counts = {}
    for w, *_ in wares_out:
        per_item_counts[w.item] = per_item_counts.get(w.item, 0) + len(w.sales)
    for row in export:
        row["truncated"] = per_item_counts[row["id"]] >= 3000

    payload = dict(
        asOf=NOW,
        world="Cactuar",
        dc="Aether",
        note="Cached Universalis data captured 2026-08-17. Buyer names hashed; retainer names dropped. "
             "Replay events are SIMULATED - a Strategy replayed over real History, not the Player's acts.",
        wares=export,
    )
    out = os.path.join(HERE, "data.js")
    with open(out, "w", encoding="utf-8") as f:
        f.write("window.EMM_DATA = ")
        json.dump(payload, f, separators=(",", ":"))
        f.write(";\n")
    print("wrote", out, f"{os.path.getsize(out)/1024:,.0f} KB")


if __name__ == "__main__":
    main()

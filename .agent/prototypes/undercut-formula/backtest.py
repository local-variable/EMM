"""Table 4: back-test candidate listing policies over real Cactuar history.

The question is not "which policy prices highest" but "which policy earns most
per market slot per day" - Slot Yield's shape, measured rather than argued.

MODEL, stated in full, because the result is worth exactly what these
assumptions are worth.

  Fill rule. There is no historical order book, so a policy may only use
  trailing sale rows. A listing at unit price P placed at time t is taken to
  sell at the first later sale row priced >= P, on the reasoning that a buyer
  who paid >= P would have taken an equal-or-cheaper listing first. This rests
  on "observed sale price == the cheapest listing at that moment", which holds
  only where wares trade one unit at a time. It does NOT hold for commodities:
  buyers there take whole stacks, so unit price moves with stack size and the
  cheapest unit price is often not the cheapest purchase. Wares are therefore
  split below into where the model holds and where it does not.

  Common decision points. Every policy is scored on the SAME decision points.
  An earlier draft priced the aggressive policies off the cheapest sale in the
  trailing 24h, which silently restricted them to points that followed a sale
  - i.e. to active market days - and flattered them badly on thin wares. The
  minListing proxy is now the cheapest sale in the trailing 7 days, and any
  point where a policy cannot act is dropped for every policy alike.

  Remaining known biases, not corrected:
  * Universalis history is a union of overlapping 20-sale windows, not a
    census, so unrecorded sales inflate every days-to-sell figure - worst on
    the busiest wares.
  * EMM's own listing would have changed the book; here it does not.
  * Ties and queue position among equal prices are ignored.
  * A Hold occupies no slot, so a policy that holds often is scored on the
    points it chose to act on. The hold rate is reported alongside for that
    reason - the two numbers must be read together.

  Net proceeds are taken as 95% of the asking price. Tax incidence is still
  open (ticket #19); it scales every policy alike and so moves no ranking.
"""

import statistics

from common import ITEMS, NOW_TS, days, median, quantile, sales, trimmed_mean, units

HORIZON = days(21)      # a listing left up longer than this counts as unsold
STEP = days(3)          # how often a decision is taken
LOOKBACK = days(270)    # how far back the back-test runs
MIN_SAMPLE = 5          # below this every policy is required to decline
TAX = 0.05

POLICY_NAMES = [
    "undercut -1g",
    "undercut -5%",
    "match",
    "q25 (30d)",
    "median (30d)",
    "trimmed mean",
    "median +10%",
    "floored undercut",
]


def policies(hist, recent):
    """Price per policy, or None to Hold. {} when nobody may act."""
    if len(hist) < MIN_SAMPLE or not recent:
        return {}
    cheapest = min(r["pricePerUnit"] for r in recent)
    med = median(hist)
    out = {
        "undercut -1g": max(cheapest - 1, 1),
        "undercut -5%": max(round(cheapest * 0.95), 1),
        "match": cheapest,
        "q25 (30d)": quantile(hist, 0.25),
        "median (30d)": med,
        "trimmed mean": trimmed_mean(hist),
        "median +10%": med * 1.10,
    }
    # Undercut, but never below 85% of the trailing median: the Floor as a gate.
    aggressive = max(cheapest - 1, 1)
    out["floored undercut"] = aggressive if aggressive >= med * 0.85 else None
    return out


def run(item, hq):
    rows = sales(item=item, hq=hq)
    if not rows:
        return None
    rows.sort(key=lambda r: r["timestamp"])
    start = max(NOW_TS - LOOKBACK, rows[0]["timestamp"] + days(30))
    results = {n: {"sold": [], "unsold": 0, "held": 0} for n in POLICY_NAMES}
    points = declines = 0

    t = start
    while t < NOW_TS - HORIZON:
        hist = [r for r in rows if t - days(30) <= r["timestamp"] < t]
        recent = [r for r in rows if t - days(7) <= r["timestamp"] < t]
        future = [r for r in rows if t <= r["timestamp"] < t + HORIZON]
        ps = policies(hist, recent)
        points += 1
        if not ps:
            declines += 1
            t += STEP
            continue
        for name, price in ps.items():
            slot = results[name]
            if price is None:
                slot["held"] += 1
                continue
            hit = next((r for r in future if r["pricePerUnit"] >= price), None)
            if hit is None:
                slot["unsold"] += 1
            else:
                slot["sold"].append((price, (hit["timestamp"] - t) / 86400.0))
        t += STEP
    return results, points, declines


def unit_traded(item, hq):
    """True where the ware trades one unit at a time - the fill rule's premise."""
    s = sales(item=item, hq=hq, since=NOW_TS - days(270))
    if not s:
        return None
    return units(s) / len(s) < 1.05


def report(item, name, hq):
    r = run(item, hq)
    if not r:
        return
    results, points, declines = r
    if not any(v["sold"] or v["unsold"] for v in results.values()):
        return
    label = f"{name} {'HQ' if hq else 'NQ'}"
    print(f"\n{label}   -   {points} decision points, "
          f"{declines} declined for a thin sample ({declines / points:.0%})")
    hdr = (f"  {'policy':20s} {'asking':>10s} {'sold':>7s} {'unsold':>7s} "
           f"{'held':>6s} {'days to sell':>13s} {'gil/slot-day':>14s} {'gil/decision':>14s}")
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    scored = []
    for pname, slot in results.items():
        sold = slot["sold"]
        if not sold:
            scored.append((-1, pname, slot, None, None, 0))
            continue
        ask = statistics.fmean(p for p, _ in sold)
        dts = statistics.median(d for _, d in sold)
        gil = sum(p * (1 - TAX) for p, _ in sold)
        slot_days = sum(max(d, 0.25) for _, d in sold)
        slot_days += slot["unsold"] * (HORIZON / 86400.0)
        # gil/slot-day rewards turnover and assumes the slot can be refilled
        # from stock at once. gil/decision is the same run scored as a
        # one-off: what a single unit of stock realised on average, whether or
        # not it moved. The two disagree, and which one is right is a
        # product decision, not a statistical one.
        acted = len(sold) + slot["unsold"]
        scored.append((gil / slot_days if slot_days else 0, pname, slot, ask, dts,
                       gil / acted if acted else 0))
    for per_day, pname, slot, ask, dts, per_point in sorted(scored, reverse=True):
        if ask is None:
            print(f"  {pname:20s} {'-':>10s} {0:>7} {slot['unsold']:>7} "
                  f"{slot['held']:>6} {'-':>13s} {'-':>14s} {'-':>14s}")
            continue
        print(f"  {pname:20s} {ask:>10,.0f} {len(slot['sold']):>7} "
              f"{slot['unsold']:>7} {slot['held']:>6} {dts:>12.1f}d "
              f"{per_day:>14,.0f} {per_point:>14,.0f}")


def main():
    unit, stacked = [], []
    for item, name in ITEMS.items():
        for hq in (False, True):
            u = unit_traded(item, hq)
            if u is None:
                continue
            (unit if u else stacked).append((item, name, hq))

    print("\n" + "=" * 118)
    print("TABLE 4a - policy back-test where the fill rule HOLDS (wares that trade one unit at a time)")
    print("           270 days of real Cactuar sales, a decision every 3 days, 21-day horizon")
    print("=" * 118)
    for item, name, hq in unit:
        report(item, name, hq)

    print("\n\n" + "=" * 118)
    print("TABLE 4b - the same back-test where the fill rule DOES NOT HOLD (stack-traded commodities)")
    print("           Shown for contrast only. Unit price moves with stack size here, so")
    print("           'cheapest unit price wins the buyer' is false and these rankings are artefacts.")
    print("=" * 118)
    for item, name, hq in stacked:
        report(item, name, hq)


if __name__ == "__main__":
    main()

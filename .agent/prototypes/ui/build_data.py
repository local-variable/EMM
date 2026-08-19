"""Build data.js for the EMM UI prototype (wayfinder ticket #13).

Reads ONLY the #12 graph prototype's already-extracted `data.js` — no network, no
re-run of the corpus extraction. Market numbers (Wares, Sales, Listings, Estimates,
severities) are therefore real Cactuar data captured 2026-08-17.

Everything about the *Player* is invented: Character names, Retainer names, which
Retainer holds what, Cost Basis, and the sell space marks. The real ones are not used
so a screenshot of this prototype carries nothing personal.

    python .agent/prototypes/ui/build_data.py
"""

import json
import math
import os
import random

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "graph", "data.js")
OUT = os.path.join(HERE, "data.js")

DAY = 86400
RNG = random.Random(20260817)


def load_graph_data():
    with open(SRC, encoding="utf-8") as fh:
        text = fh.read()
    start = text.index("{")
    end = text.rstrip().rstrip(";").rindex("}") + 1
    return json.loads(text[start:end])


# --- the invented Player -------------------------------------------------------
# Retainer names are inventions. "Bloodstained" is reused because it appears in the
# approved [EMM] log-line copy from ticket #9.

CHARACTERS = [
    {
        "name": "Sable Ashgrove",
        "world": "Cactuar",
        "retainers": [
            {"name": "Bloodstained", "city": "Limsa Lominsa", "tax": 5},
            {"name": "Meadowlark", "city": "Ul'dah", "tax": 5},
            {"name": "Copperkettle", "city": "Gridania", "tax": 3},
            {"name": "Nightjar", "city": "Ishgard", "tax": 5},
        ],
    },
    {
        "name": "Wren Halloway",
        "world": "Cactuar",
        "retainers": [
            {"name": "Saltmarsh", "city": "Kugane", "tax": 3},
            {"name": "Thimble", "city": "Ul'dah", "tax": 5},
        ],
    },
]


def ware_key(w):
    return "%d-%s" % (w["id"], "hq" if w["hq"] else "nq")


def compact_ware(w, as_of):
    """Trim one Ware down to what the UI prototype draws."""
    est_now = w.get("estimateNow") or {}
    agg = w.get("agg") or {}
    world = agg.get("world") or {}
    dc = agg.get("dc") or {}

    # Estimate series: [dayTs, point, lo, hi] over the last 90 days, gaps kept as null.
    series = []
    for row in w.get("estimate", []):
        ts, point, lo, hi = row[0], row[1], row[2], row[3]
        if ts < as_of - 91 * DAY:
            continue
        series.append([ts, point, lo, hi])

    # Sale dots over the same window, subsampled so the mini chart stays cheap.
    dots = [s for s in w.get("sales", []) if s[0] >= as_of - 91 * DAY]
    if len(dots) > 420:
        stride = len(dots) / 420.0
        dots = [dots[int(i * stride)] for i in range(420)]
    dots = [[s[0], s[1], s[2]] for s in dots]

    listings = [[l["p"], l["q"], l["rt"], 1 if l.get("own") else 0] for l in w.get("listings", [])]

    units_for_sale = sum(l[1] for l in listings)

    # Sales in the last 30 days, for the Sample the UI shows beside every figure.
    recent = [s for s in w.get("sales", []) if s[0] >= as_of - 30 * DAY]
    n30 = len(recent)

    # Sold per day is computed from the Sales themselves, not from the aggregator's
    # own velocity field — ticket #3 found that figure tends toward the same value for
    # every Item and warned against using it. Units, not rows, because a Listing is
    # bought whole.
    velocity = sum(s[2] for s in recent) / 30.0
    dos = (units_for_sale / velocity) if velocity > 0 else None

    return {
        "key": ware_key(w),
        "id": w["id"],
        "hq": w["hq"],
        "canBeHq": w.get("canBeHq", False),
        "name": w["name"],
        "type": w["type"],
        "category": w.get("uiCategory") or w.get("category"),
        "stackSize": w.get("stackSize", 1),
        "vendorSell": w.get("vendorSell"),
        "lastUpload": w.get("lastUpload"),
        "window": (w.get("params") or {}).get("window"),
        "horizon": (w.get("params") or {}).get("horizon"),
        "est": {
            "point": est_now.get("point"),
            "lo": est_now.get("lo"),
            "hi": est_now.get("hi"),
            "nEff": round(est_now.get("nEff") or 0, 1),
            "basis": est_now.get("basis"),
            "reason": est_now.get("reason"),
            "severity": est_now.get("severity") or "severe",
            "confident": bool(est_now.get("confident")),
        },
        "minListing": world.get("minListing"),
        "avgSale": world.get("avgSale"),
        "velocity": velocity,
        "dcAvgSale": dc.get("avgSale"),
        "dcMinListing": dc.get("minListing"),
        "unitsForSale": units_for_sale,
        "dos": dos,
        "n30": n30,
        "series": series,
        "dots": dots,
        "listings": listings,
    }


# --- the invented Holdings and sell space --------------------------------------
# Which Retainer carries which Ware, how many units, and what the Player paid.
# `basis` of None models the crafted / gathered / venture gap that ticket #10 ruled
# EMM must derive a Floor for rather than refuse to act on.

HOLDINGS = [
    # (ware key, retainer, units held, listed stack, cost basis fraction of Estimate)
    ("41769-nq", "Bloodstained", 640, 99, 0.55),
    ("41763-nq", "Bloodstained", 410, 99, 0.62),
    ("44036-nq", "Bloodstained", 88, 30, None),
    ("36060-hq", "Bloodstained", 42, 12, 0.71),
    ("36060-nq", "Meadowlark", 60, 20, 0.66),
    ("47166-hq", "Meadowlark", 3, 1, 0.58),
    ("47189-hq", "Meadowlark", 5, 1, 0.64),
    ("41145-nq", "Copperkettle", 4, 1, None),
    ("41139-nq", "Copperkettle", 2, 1, None),
    ("46785-nq", "Copperkettle", 1, 1, 0.80),
    ("44421-hq", "Nightjar", 2, 1, 0.52),
    ("1621-nq", "Nightjar", 1, 1, 0.05),
    ("47166-nq", "Saltmarsh", 2, 1, 0.60),
    ("44036-nq", "Saltmarsh", 120, 99, None),
    ("41763-nq", "Thimble", 250, 99, 0.60),
    ("36060-hq", "Thimble", 18, 6, 0.69),
]

# Sell space: postings EMM may occupy (of the game's 20 market slots) and inventory
# slots it may draw stock from, per Retainer. Ticket #9 ruled the space covers both.
SELL_SPACE = {
    "Bloodstained": {"postings": 14, "invSlots": 30},
    "Meadowlark": {"postings": 10, "invSlots": 24},
    "Copperkettle": {"postings": 6, "invSlots": 12},
    "Nightjar": {"postings": 4, "invSlots": 10},
    "Saltmarsh": {"postings": 8, "invSlots": 20},
    "Thimble": {"postings": 12, "invSlots": 26},
}

# Strategy names describe what the Strategy is trying to do. All are proposals.
STRATEGY_BY_TYPE = {
    "Materia": "Undercut to Sell Fast",
    "Material": "Undercut to Sell Fast",
    "Collectible": "Follow the Estimate",
    "Consumable": "Follow the Estimate",
    "Gear": "Hold for a Better Price",
    "Furnishing": "Hold for a Better Price",
    "Miscellany": "Follow the Estimate",
}


def net_proceeds(unit_price, tax_pct):
    return unit_price * (1.0 - tax_pct / 100.0)


def build_proposals(wares_by_key, as_of):
    """Derive the work queue from the real Estimates plus the invented Holdings."""
    retainer_tax = {}
    for ch in CHARACTERS:
        for r in ch["retainers"]:
            retainer_tax[r["name"]] = r["tax"]

    proposals = []
    for key, retainer, units, stack, basis_frac in HOLDINGS:
        w = wares_by_key.get(key)
        if not w:
            continue
        est = w["est"]
        point = est["point"]
        if not point:
            continue

        tax = retainer_tax[retainer]
        stack = min(stack, w["stackSize"], units)
        basis = round(point * basis_frac) if basis_frac is not None else None

        min_listing = w["minListing"]
        floor = round(point * 0.95) if w["type"] in ("Materia", "Material", "Collectible") else round(point * 0.80)
        if basis is not None:
            floor = max(floor, round(basis * 1.15))

        # What EMM would ask, and therefore which act this is.
        listed_at = None
        if min_listing:
            # The Player's own current Listing has drifted off the book since it was put
            # up — a few per cent, not a different order of magnitude.
            drift = 1.03 + ((w["id"] + len(retainer)) % 17) / 100.0
            listed_at = round(min_listing * drift)
        target = None
        act = "List"
        if min_listing and min_listing > floor:
            target = max(floor, min_listing - max(1, round(min_listing * 0.01)))
        else:
            target = max(floor, point)

        if listed_at is not None and RNG.random() < 0.75:
            act = "Reprice"
        elif RNG.random() < 0.25:
            act = "Relist"

        hold = False
        reason = ""
        if min_listing and min_listing < floor:
            hold = True
            act = "Hold"
            reason = "Lowest Competing Listing %s is under the Floor %s." % (
                fmt_gil(min_listing),
                fmt_gil(floor),
            )
        elif est["severity"] in ("warning", "severe"):
            hold = True
            act = "Hold"
            reason = "Estimate graded %s: %s" % (est["severity"], est["basis"])

        units_below = 0
        for p, q, _rt, own in w["listings"]:
            if own:
                continue
            if target and p < target:
                units_below += q

        np_unit = net_proceeds(target or point, tax)
        per_unit_profit = np_unit - (basis if basis is not None else point * 0.5)
        sold_per_day = w["velocity"] or 0.0
        slot_yield = 0.0
        if stack > 0:
            slot_yield = stack * per_unit_profit * sold_per_day / max(1.0, units_below + stack)

        proposals.append(
            {
                "ware": key,
                "retainer": retainer,
                "act": act,
                "hold": hold,
                "units": units,
                "stack": stack,
                "from": listed_at,
                "to": None if hold else target,
                "floor": floor,
                "basis": basis,
                "tax": tax,
                "netProceeds": round(np_unit),
                "slotYield": round(slot_yield),
                "dos": w["dos"],
                "severity": est["severity"],
                "sample": w["n30"],
                "strategy": STRATEGY_BY_TYPE.get(w["type"], "Patient Ticket"),
                "reason": reason,
                "unitsBelow": units_below,
            }
        )

    proposals.sort(key=lambda p: (p["hold"], -p["slotYield"]))
    return proposals


def fmt_gil(n):
    if n is None:
        return "--"
    return "{:,}".format(int(round(n)))


def main():
    src = load_graph_data()
    as_of = src["asOf"]

    wares = [compact_ware(w, as_of) for w in src["wares"]]
    wares_by_key = {w["key"]: w for w in wares}

    proposals = build_proposals(wares_by_key, as_of)

    # Sell space occupancy, derived from the proposals actually placed.
    space = {}
    for name, cfg in SELL_SPACE.items():
        used = sum(1 for p in proposals if p["retainer"] == name and not p["hold"])
        held = sum(1 for p in proposals if p["retainer"] == name and p["hold"])
        space[name] = {
            "postings": cfg["postings"],
            "invSlots": cfg["invSlots"],
            "occupied": min(cfg["postings"], used + held),
            "pending": used,
            "held": held,
        }

    payload = {
        "asOf": as_of,
        "world": src["world"],
        "dc": src["dc"],
        "note": (
            "Market data is real Cactuar data cached 2026-08-17 (Wares, Sales, Listings, "
            "Estimates and severities come from the ticket #12 extraction). The Player is "
            "invented: Character names, Retainer names, Holdings, Cost Basis and the sell "
            "space marks are all fabricated so no screenshot carries anything personal."
        ),
        "characters": CHARACTERS,
        "space": space,
        "wares": wares,
        "proposals": proposals,
    }

    with open(OUT, "w", encoding="utf-8") as fh:
        fh.write("window.EMM_UI = ")
        json.dump(payload, fh, separators=(",", ":"))
        fh.write(";\n")

    print("wrote %s (%.0f KB)" % (OUT, os.path.getsize(OUT) / 1024.0))
    print("%d Wares, %d Proposals (%d Hold), %d Retainers" % (
        len(wares),
        len(proposals),
        sum(1 for p in proposals if p["hold"]),
        len(space),
    ))


if __name__ == "__main__":
    main()

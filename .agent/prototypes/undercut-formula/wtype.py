"""Ware types: a structural classifier, plus the test of whether it is real.

The classifier uses ONLY game data (search category, stack size, quality
flag). That is deliberate and load-bearing: EMM must be able to classify a
Ware it has never seen trade - on install day, or the first time a new patch
item appears - so a taxonomy derived from observed behaviour would be useless
exactly when it is needed most.

Whether the structural split actually tracks behaviour is then an empirical
question, answered by `separation()` below rather than asserted.
"""

import math
import statistics

# ItemSearchCategory.Category, the game's own coarsest market grouping.
WEAPONS, ARMOUR, ITEMS, HOUSING = 1, 2, 3, 4

CONSUMABLE_CATS = {"Meals", "Medicine", "Ingredients", "Seafood"}
COLLECTIBLE_CATS = {
    "Minions", "Orchestrion Components", "Paintings", "Registrable Miscellany",
    "Materia",  # replaced below; listed so the set reads completely
}
COLLECTIBLE_CATS.discard("Materia")


def classify(meta):
    """Overarching ware type from game data alone."""
    cat = meta["searchCategory"]
    group = meta["searchGroup"]
    stackable = meta["stackSize"] > 1

    if cat == "Materia":
        return "Materia"
    if group in (WEAPONS, ARMOUR):
        return "Gear"
    if group == HOUSING:
        return "Furnishing"
    if cat in COLLECTIBLE_CATS:
        return "Collectible"
    if cat in CONSUMABLE_CATS:
        return "Consumable"
    if stackable:
        return "Material"
    return "Miscellany"


TYPES = ["Materia", "Material", "Consumable", "Gear", "Furnishing",
         "Collectible", "Miscellany"]


# --- is the split real? -------------------------------------------------

def _ranks(values):
    order = sorted(range(len(values)), key=lambda i: values[i])
    ranks = [0.0] * len(values)
    i = 0
    ties = []
    while i < len(order):
        j = i
        while j + 1 < len(order) and values[order[j + 1]] == values[order[i]]:
            j += 1
        avg = (i + j) / 2.0 + 1.0
        for k in range(i, j + 1):
            ranks[order[k]] = avg
        ties.append(j - i + 1)
        i = j + 1
    return ranks, ties


def kruskal_epsilon_sq(groups):
    """Rank-based effect size for a k-group split: what share of the
    variation in this metric the grouping accounts for.

    Kruskal-Wallis H with tie correction, converted to epsilon-squared
    (H - k + 1) / (N - k). Rank-based because these distributions are
    heavy-tailed and nowhere near normal. Returns (eps_sq, H, N, k).
    """
    groups = [g for g in groups if len(g) >= 2]
    k = len(groups)
    if k < 2:
        return None
    flat = [v for g in groups for v in g]
    n = len(flat)
    ranks, ties = _ranks(flat)
    idx = 0
    total = 0.0
    for g in groups:
        r = sum(ranks[idx : idx + len(g)])
        total += r * r / len(g)
        idx += len(g)
    h = 12.0 / (n * (n + 1)) * total - 3 * (n + 1)
    tie_corr = 1 - sum(t ** 3 - t for t in ties) / (n ** 3 - n)
    if tie_corr > 0:
        h /= tie_corr
    eps = (h - k + 1) / (n - k) if n > k else 0.0
    return max(eps, 0.0), h, n, k


def separation(wares, metric, types=None):
    """Effect size of the ware-type split on one sale-pattern metric."""
    types = types or TYPES
    groups = []
    for t in types:
        vals = []
        for w in wares:
            if classify(w.meta) != t:
                continue
            v = metric(w)
            if v is not None and not math.isnan(v) and v > 0:
                vals.append(math.log(v))
        groups.append(vals)
    return kruskal_epsilon_sq(groups), {t: len(g) for t, g in zip(types, groups)}


def summarise(values):
    vals = [v for v in values if v is not None]
    if not vals:
        return None
    vals.sort()
    return {
        "n": len(vals),
        "p10": vals[int(0.10 * (len(vals) - 1))],
        "median": statistics.median(vals),
        "p90": vals[int(0.90 * (len(vals) - 1))],
    }

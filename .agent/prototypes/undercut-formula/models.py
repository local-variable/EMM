"""The models, separated from the reporting so each can be argued with alone.

Three are new in v3, each answering a specific ruling:

  effective_n()      - "all three weight into this", so row count, distinct
                       sale days and distinct buyers combine into one bounded
                       measure. Validated against out-of-sample error rather
                       than asserted.

  recommend_stack()  - "no one wants a 99 stack of something at 20k a unit
                       (except materia)". Stack is chosen against a per-type
                       target TRANSACTION value, so it falls automatically as
                       unit price rises.

  score_policies()   - held slot-days are credited at the type's opportunity
                       rate, so a policy that holds no longer scores only on
                       the points it chose to act on.
"""

import math
import random
import statistics

RNG = random.Random(20260817)


# --- effective sample size ---------------------------------------------

def sample_components(sales):
    """(rows, distinct sale days, distinct buyers) for a window."""
    if not sales:
        return 0, 0, 0
    days = {int(s.ts // 86400) for s in sales}
    buyers = {s.buyer for s in sales if s.buyer}
    return len(sales), len(days), len(buyers) or len(sales)


def effective_n(sales, weights=(1.0, 1.0, 1.0)):
    """Weighted geometric mean of the three counts.

    Geometric rather than arithmetic so that any one component collapsing
    drags the result down hard - three sales to one buyer in one minute
    should not read as three independent observations. Bounded above by the
    row count, because distinct days and distinct buyers can never exceed it,
    so n_eff <= n always and the measure cannot invent evidence.
    """
    n, d, b = sample_components(sales)
    if n == 0:
        return 0.0
    wn, wd, wb = weights
    total = wn + wd + wb
    return math.exp((wn * math.log(n) + wd * math.log(max(d, 1))
                     + wb * math.log(max(b, 1))) / total)


# --- precision ----------------------------------------------------------

def block_bootstrap(prices, stat=statistics.median, conf=0.80, reps=200):
    """Moving-block bootstrap interval. Blocks, not rows, because sales clump."""
    n = len(prices)
    if n < 3:
        return None
    point = stat(prices)
    b = max(2, int(round(n ** (1 / 3))))
    nblocks = max(1, math.ceil(n / b))
    starts = max(1, n - b + 1)
    draws = []
    for _ in range(reps):
        s = []
        for _ in range(nblocks):
            i = RNG.randrange(starts)
            s.extend(prices[i : i + b])
        draws.append(stat(s[:n]))
    draws.sort()
    lo = draws[int((1 - conf) / 2 * (reps - 1))]
    hi = draws[int((1 - (1 - conf) / 2) * (reps - 1))]
    return point, lo, hi


def rel_halfwidth(est):
    if not est or not est[0]:
        return None
    point, lo, hi = est
    return (hi - lo) / 2.0 / point


MIN_EFF = 5.0


class Estimate:
    """The shared estimator's result, for the pricing engine AND the graph band.

    A caller cannot take the number without also being handed how well it is
    pinned down, how much independent evidence stands behind it, and whether
    it is provisional.

    Standing rule (maintainer, 2026-08-17): EMM ALWAYS produces an estimate.
    Where the normal machinery cannot produce a valid one, the estimate is
    marked provisional and carries a reason for the warning the UI shows - it
    is never withheld. `confident` gates act/decline; `interval` feeds the
    graph band; `basis` and `reason` feed the warning.
    """

    __slots__ = ("point", "lo", "hi", "n_eff", "basis", "reason", "confident",
                 "severity")

    def __init__(self, point, lo, hi, n_eff, basis, reason, confident,
                 severity="ok"):
        self.point = point
        self.lo = lo
        self.hi = hi
        self.n_eff = n_eff
        self.basis = basis          # which rung of the ladder produced it
        self.reason = reason        # plain-language, for the warning box
        self.confident = confident
        # Severity, not a single flat warning. Measured error differs by an
        # order of magnitude across the provisional rungs (41% median on a
        # widened own-history window against 83% on a vendor price, and a p90
        # of 2020% on live listings), so one yellow box for all of them would
        # be misleading. "a provisional estimate has to be grounded in
        # reality" - the grading is what keeps that honest.
        #   ok       from own sales, enough independent evidence
        #   caution  own sales, but the window had to be widened
        #   warning  no own sales - asks or another market stand in
        #   severe   not a market price at all
        self.severity = severity

    @property
    def provisional(self):
        return not self.confident

    @property
    def rel_halfwidth(self):
        if not self.point or self.lo is None:
            return None
        return (self.hi - self.lo) / 2.0 / self.point

    def __repr__(self):
        tag = "confident" if self.confident else f"PROVISIONAL/{self.basis}"
        return f"<Estimate {self.point:,.0f} n_eff={self.n_eff:.1f} {tag}>"


def estimate(ware, now_ts, conf=0.80, reps=200, min_eff=MIN_EFF):
    """Always returns an Estimate. Never None.

    A ladder, tried in order, each rung weaker than the last and each saying so:

      1. own sales, 30 days      - the normal machinery
      2. own sales, 90 days      - widen before weakening
      3. own sales, 180 days
      4. the live book           - asks, not sales: what sellers hope for
      5. the data centre         - a market the buyer would have to travel to
      6. the vendor price        - a floor of last resort, not a market price

    Only rung 1-3 with n_eff at or above the floor counts as confident. Gating
    is on n_eff alone, per the ruling; the interval is reported either way but
    does not decide.
    """
    best_weak = None
    for days, label in ((30, "30-day history"), (90, "90-day history"),
                        (180, "180-day history")):
        rows = ware.within(days, end=now_ts)
        if len(rows) < 3:
            continue
        eff = effective_n(rows)
        est = block_bootstrap([s.price for s in rows], conf=conf, reps=reps)
        if not est:
            continue
        if eff >= min_eff:
            # Clears the floor. Confident only on the primary window; a wider
            # one means the recent market was too thin to speak for itself.
            reason = ("" if days == 30 else
                      f"priced from {days} days of sales - the last 30 were too thin")
            return Estimate(est[0], est[1], est[2], eff, label, reason,
                            days == 30, "ok" if days == 30 else "caution")
        # Below the floor. Keep the widest attempt as the fallback and go on
        # widening, since more evidence is strictly better here.
        best_weak = Estimate(est[0], est[1], est[2], eff, label,
                             f"only {eff:.1f} effective observations in {days} days "
                             f"of sales", False, "caution")

    if best_weak is not None:
        return best_weak

    if ware.listings:
        prices = [p for p, _ in ware.listings]
        point = statistics.median(prices)
        return Estimate(point, min(prices), max(prices), 0.0, "live listings",
                        "no recorded sales - priced from what sellers are asking, "
                        "which is not what buyers have paid", False, "warning")

    # The data centre is a COLD-START seed, not an ongoing signal: it stands in
    # only where the home world is silent, and is replaced the moment the home
    # world has sales of its own. Its predictive value beyond the world's own
    # history did not survive its control (see the reversion test).
    dc = ((ware.agg_dc or {}).get("averageSalePrice") or {}).get("dc") or {}
    if dc.get("price"):
        return Estimate(dc["price"], None, None, 0.0, "data centre",
                        "no sales or listings on this world - priced from the data "
                        "centre until this world has trade of its own", False, "warning")

    vendor = ware.meta.get("vendorBuy") or 0
    if vendor and vendor < 99999:
        return Estimate(float(vendor), None, None, 0.0, "vendor price",
                        "no market data at all - this is the vendor price, which is "
                        "not a market price and should not be treated as one", False,
                        "severe")

    return Estimate(0.0, None, None, 0.0, "none",
                    "no market data and no vendor price - nothing real to ground an "
                    "estimate on", False, "severe")


# --- stack size ---------------------------------------------------------

def transaction_values(sales):
    return [s.price * s.qty for s in sales]


def recommend_stack(unit_price, target_value, stack_size, floor=1):
    """Largest stack whose TOTAL price stays within what buyers of this type
    actually transact. Falls automatically as unit price rises, which is the
    behaviour asked for: nobody buys 99 of a 20,000 gil item."""
    if not unit_price or unit_price <= 0:
        return floor
    n = int(target_value // unit_price)
    return max(floor, min(n, stack_size))


# --- policies -----------------------------------------------------------

FLOOR_LEVELS = [0.70, 0.80, 0.90, 0.95]
BASE_POLICIES = ["undercut -1g", "undercut -5%", "match",
                 "q25", "median", "trimmed mean", "median +10%"]
POLICIES = BASE_POLICIES + [f"floor {int(f * 100)}%" for f in FLOOR_LEVELS]
HOLDING = {f"floor {int(f * 100)}%" for f in FLOOR_LEVELS}


def trimmed_mean(ps, frac=0.1):
    if len(ps) < 5:
        return statistics.median(ps)
    p = sorted(ps)
    k = int(len(p) * frac)
    return statistics.fmean(p[k : len(p) - k] or p)


def quantile(ps, q):
    p = sorted(ps)
    if len(p) == 1:
        return float(p[0])
    i = q * (len(p) - 1)
    lo, hi = int(i), min(int(i) + 1, len(p) - 1)
    return p[lo] + (p[hi] - p[lo]) * (i - lo)


def policy_prices(hist, recent, min_eff):
    """Price per policy, None to Hold, {} where nobody may act."""
    if effective_n(hist) < min_eff or not recent:
        return {}
    ps = [s.price for s in hist]
    cheapest = min(s.price for s in recent)
    med = statistics.median(ps)
    out = {
        "undercut -1g": max(cheapest - 1, 1),
        "undercut -5%": max(round(cheapest * 0.95), 1),
        "match": cheapest,
        "q25": quantile(ps, 0.25),
        "median": med,
        "trimmed mean": trimmed_mean(ps),
        "median +10%": med * 1.10,
    }
    aggressive = max(cheapest - 1, 1)
    for f in FLOOR_LEVELS:
        out[f"floor {int(f * 100)}%"] = aggressive if aggressive >= med * f else None
    return out


def ware_opportunity_rate(raw):
    """Per-Ware opportunity rate: what THIS Ware's slot earns under the best
    policy that never holds.

    Per-Ware rather than per-type, by ruling. It is also the more coherent
    comparison for the decision actually being made - "hold this ware or list
    it now" is answered by what this ware would earn if listed, not by what the
    median ware of its type would. That makes the back-test and the Slot Yield
    allocator the same machine rather than two that happen to agree.
    """
    best = 0.0
    for name in BASE_POLICIES:
        s = raw.get(name)
        if s and s["occupied_days"]:
            best = max(best, s["gil"] / s["occupied_days"])
    return best


def score_policies(raw, opp_rate):
    """Turn accumulated per-policy counters into comparable scores.

    `opp_rate` is gil per slot-day the slot could earn if this policy declined
    to use it. Crediting held time at that rate is what removes the selection
    effect: a Hold is now only worth taking if waiting beats handing the slot
    to something else.
    """
    out = {}
    for name, s in raw.items():
        if not s["acted"]:
            continue
        occupied = s["occupied_days"]
        held = s["held_days"]
        total_days = occupied + held
        if total_days <= 0:
            continue
        out[name] = {
            "per_day_acted": s["gil"] / occupied if occupied else 0.0,
            "per_day_total": (s["gil"] + held * opp_rate) / total_days,
            "per_dec": s["gil"] / s["acted"],
            "hold_rate": s["held"] / (s["held"] + s["acted"]),
            "unsold_rate": s["unsold"] / s["acted"],
        }
    return out

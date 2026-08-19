"""v3: the whole marketable catalogue, in two streaming passes.

Pass 1  descriptive statistics, coverage, effective-n and its validation,
        stack behaviour, and the per-type parameters everything else needs.
Pass 2  the policy back-test at those parameters, scored with held slot-days
        credited at the type's opportunity rate.

Written as passes rather than over a list because the full catalogue does not
fit in memory as parsed JSON - see stream.py.
"""

import math
import os
import statistics
import sys

import models
import stream
from stacks import BUCKETS, bucket
from wtype import TYPES, classify, kruskal_epsilon_sq

DAY = 86400
TAX = 0.05
MIN_EFF = 5.0          # effective-n floor, the maintainer's baseline of 5
PRECISION_BAR = 0.15   # provisional; the cost of each bar is reported


def pct(x):
    return f"{x:>7.1%}"


# =======================================================================
# PASS 1
# =======================================================================

class Acc:
    def __init__(self):
        self.cov = {t: {"wares": 0, "any": 0, "eff5": 0, "n5": 0, "listed": 0}
                    for t in TYPES}
        self.metrics = {t: {} for t in TYPES}
        self.precision = {t: [] for t in TYPES}
        self.pass_bar = {t: {b: 0 for b in (0.05, 0.10, 0.15, 0.25, 0.50)}
                         for t in TYPES}
        self.validation = []          # (n_rows, n_eff, out-of-sample error)
        self.stack_sales = {t: {} for t in TYPES}
        self.stack_book = {t: {} for t in TYPES}
        self.txn_values = {t: [] for t in TYPES}
        self.qty_vs_price = {t: [] for t in TYPES}
        self.velocity = {t: [] for t in TYPES}
        self.stackable = {t: [0, 0] for t in TYPES}   # [stackable, total]
        self.ladder = {}          # basis -> [(confident, forward error)]
        self.ladder_types = {}    # (type, basis) -> count

    def add_metric(self, t, name, v):
        if v is not None and v > 0 and not math.isnan(v):
            self.metrics[t].setdefault(name, []).append(math.log(v))


METRIC_FNS = ("sales/day", "units/sale", "IQR/median", "drift", "burstiness",
              "days of supply", "unit price")


def ware_metrics(w):
    s90 = w.within(90)
    out = dict.fromkeys(METRIC_FNS)
    if not s90:
        return out
    out["sales/day"] = len(s90) / 90.0
    out["units/sale"] = sum(x.qty for x in s90) / len(s90)
    prices = sorted(x.price for x in s90)
    med = statistics.median(prices)
    out["unit price"] = med
    if len(s90) >= 4 and med:
        q1 = prices[int(0.25 * (len(prices) - 1))]
        q3 = prices[int(0.75 * (len(prices) - 1))]
        out["IQR/median"] = (q3 - q1) / med
    s30, s180 = w.within(30), w.within(180)
    if len(s30) >= 3 and len(s180) >= 8:
        m180 = statistics.median(x.price for x in s180)
        if m180:
            out["drift"] = statistics.median(x.price for x in s30) / m180
    ts = [x.ts for x in s90]
    if len(ts) >= 6:
        gaps = [(b - a) / DAY for a, b in zip(ts, ts[1:]) if b > a]
        if len(gaps) >= 5:
            m = statistics.fmean(gaps)
            if m:
                out["burstiness"] = statistics.stdev(gaps) / m
    units = w.units_on_board()
    sold = sum(x.qty for x in s30) / 30.0
    if sold:
        out["days of supply"] = units / sold
    return out


def pass1():
    acc = Acc()
    seen = 0
    for w in stream.all_wares_including_empty():
        t = classify(w.meta)
        c = acc.cov[t]
        c["wares"] += 1
        acc.stackable[t][1] += 1
        if w.meta["stackSize"] > 1:
            acc.stackable[t][0] += 1
        seen += 1
        if seen % 5000 == 0:
            print(f"  pass1 {seen:,} wares", flush=True, file=sys.stderr)
        if w.listings:
            c["listed"] += 1
        if not w.sales:
            continue
        c["any"] += 1

        s30 = w.within(30)
        if len(s30) >= 5:
            c["n5"] += 1
        eff = models.effective_n(s30)
        if eff >= MIN_EFF:
            c["eff5"] += 1

        for k, v in ware_metrics(w).items():
            acc.add_metric(t, k, v)
        vel = len(w.within(90)) / 90.0
        if vel > 0:
            acc.velocity[t].append(vel)

        # precision on the 30-day window
        if len(s30) >= 3:
            rh = models.rel_halfwidth(models.block_bootstrap([x.price for x in s30]))
            if rh is not None:
                acc.precision[t].append((eff, len(s30), rh))
                for b in acc.pass_bar[t]:
                    if rh <= b and eff >= MIN_EFF:
                        acc.pass_bar[t][b] += 1

        # effective-n validation: estimate on the older half, check against
        # the newer half. The question is which sample measure predicts the
        # error, not which produces the tightest interval.
        s60 = w.within(60)
        if len(s60) >= 8:
            mid = stream.NOW_TS - 30 * DAY
            old = [x for x in s60 if x.ts < mid]
            new = [x for x in s60 if x.ts >= mid]
            if len(old) >= 3 and len(new) >= 3:
                a = statistics.median(x.price for x in old)
                b = statistics.median(x.price for x in new)
                if a and b:
                    acc.validation.append((len(old), models.effective_n(old),
                                           abs(a - b) / b))

        # The estimate ladder, and what a provisional estimate actually costs.
        # Estimate as of 30 days ago from data strictly before that, then check
        # against what the last 30 days did. Every Ware gets an estimate - the
        # point of the rule is that none are refused - so every Ware is scored.
        cut = stream.NOW_TS - 30 * DAY
        actual_rows = w.within(30)
        if len(actual_rows) >= 3:
            actual = statistics.median(x.price for x in actual_rows)
            est = models.estimate(w, cut, reps=100)
            if actual > 0 and est.point > 0:
                err = abs(est.point - actual) / actual
                acc.ladder.setdefault(est.basis, []).append((est.confident, err))
                key = (t, est.basis)
                acc.ladder_types[key] = acc.ladder_types.get(key, 0) + 1

        # stack behaviour
        s180 = w.within(180)
        if w.meta["stackSize"] > 1 and len(s180) >= 20:
            for x in s180:
                lab = bucket(x.qty)
                acc.stack_sales[t][lab] = acc.stack_sales[t].get(lab, 0) + 1
            for price, qty in w.listings:
                lab = bucket(qty)
                acc.stack_book[t][lab] = acc.stack_book[t].get(lab, 0) + 1
            acc.txn_values[t].extend(x.price * x.qty for x in s180)
            mp = statistics.median(x.price for x in s180)
            mq = statistics.median(x.qty for x in s180)
            if mp > 0 and mq > 0:
                acc.qty_vs_price[t].append((math.log(mp), math.log(mq)))
    return acc


# =======================================================================
# reporting for pass 1
# =======================================================================

def spearman_pairs(pairs):
    if len(pairs) < 8:
        return None
    xs = [p[0] for p in pairs]
    ys = [p[1] for p in pairs]

    def rank(v):
        order = sorted(range(len(v)), key=lambda i: v[i])
        r = [0.0] * len(v)
        i = 0
        while i < len(order):
            j = i
            while j + 1 < len(order) and v[order[j + 1]] == v[order[i]]:
                j += 1
            avg = (i + j) / 2.0 + 1.0
            for k in range(i, j + 1):
                r[order[k]] = avg
            i = j + 1
        return r

    rx, ry = rank(xs), rank(ys)
    mx, my = statistics.fmean(rx), statistics.fmean(ry)
    num = sum((a - mx) * (b - my) for a, b in zip(rx, ry))
    dx = math.sqrt(sum((a - mx) ** 2 for a in rx))
    dy = math.sqrt(sum((b - my) ** 2 for b in ry))
    return num / (dx * dy) if dx and dy else None


def report_coverage(acc):
    print("\n" + "=" * 104)
    print("TABLE 1 - the whole marketable catalogue: coverage by ware type (Cactuar, 180d)")
    print("=" * 104)
    hdr = (f"  {'ware type':13s} {'wares':>8s} {'any sale':>9s} {'n>=5/30d':>10s} "
           f"{'n_eff>=5':>10s} {'listed':>8s} {'no data':>9s}")
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    tot = {k: 0 for k in ("wares", "any", "n5", "eff5", "listed")}
    for t in TYPES:
        c = acc.cov[t]
        if not c["wares"]:
            continue
        for k in tot:
            tot[k] += c[k]
        print(f"  {t:13s} {c['wares']:>8,} {c['any']:>9,} {c['n5']:>10,} "
              f"{c['eff5']:>10,} {c['listed']:>8,} {c['wares'] - c['any']:>9,}")
    print("  " + "-" * (len(hdr) - 2))
    print(f"  {'ALL':13s} {tot['wares']:>8,} {tot['any']:>9,} {tot['n5']:>10,} "
          f"{tot['eff5']:>10,} {tot['listed']:>8,} {tot['wares'] - tot['any']:>9,}")
    print(f"\n  share of all Wares clearing n>=5:      {tot['n5'] / tot['wares']:.1%}")
    print(f"  share of all Wares clearing n_eff>=5:  {tot['eff5'] / tot['wares']:.1%}")
    print("  n_eff is the geometric mean of rows, distinct sale days and distinct")
    print("  buyers, so it is never above the row count and is usually well below it.")


def report_separation(acc):
    print("\n" + "=" * 104)
    print("TABLE 2 - is the ware-type split real on the full catalogue?")
    print("=" * 104)
    hdr = f"  {'metric':16s} {'eps^2':>8s} {'H':>10s} {'wares':>9s}   reading"
    print(hdr)
    print("  " + "-" * (len(hdr) + 10))
    for name in METRIC_FNS:
        groups = [acc.metrics[t].get(name, []) for t in TYPES]
        res = kruskal_epsilon_sq(groups)
        if not res:
            continue
        eps, h, n, k = res
        reading = ("large" if eps >= 0.14 else "medium" if eps >= 0.06
                   else "small" if eps >= 0.01 else "negligible")
        print(f"  {name:16s} {eps:>8.3f} {h:>10,.0f} {n:>9,}   {reading}")


def report_effn(acc):
    print("\n" + "=" * 104)
    print("TABLE 3 - does effective-n predict error better than a raw row count?")
    print("         Median unit price estimated on days 60-30 back, checked against days 30-0.")
    print("         A more negative correlation means the measure better anticipates error.")
    print("=" * 104)
    v = acc.validation
    if len(v) < 50:
        print("  not enough paired windows")
        return
    r_rows = spearman_pairs([(math.log(a), b) for a, _, b in v])
    r_eff = spearman_pairs([(math.log(max(e, 1)), b) for _, e, b in v])
    print(f"  Wares with both windows: {len(v):,}")
    print(f"    Spearman(log raw n,   |error|) = {r_rows:+.4f}")
    print(f"    Spearman(log n_eff,   |error|) = {r_eff:+.4f}")
    better = "n_eff" if r_eff < r_rows else "raw n"
    print(f"    -> {better} is the better predictor of out-of-sample error")
    for lo, hi, lab in [(0, 5, "n_eff < 5"), (5, 10, "5-10"), (10, 25, "10-25"),
                        (25, 10 ** 9, "25+")]:
        sel = [b for _, e, b in v if lo <= e < hi]
        if len(sel) >= 20:
            print(f"    {lab:>10s}: median |error| {statistics.median(sel):>6.1%} "
                  f"({len(sel):,} Wares)")


def report_precision(acc):
    print("\n" + "=" * 104)
    print("TABLE 4 - cost of a precision bar, on top of the n_eff >= 5 floor")
    print("=" * 104)
    bars = (0.05, 0.10, 0.15, 0.25, 0.50)
    hdr = (f"  {'ware type':13s} {'wares':>8s} "
           + " ".join(f"{'+/-' + str(int(b * 100)) + '%':>9s}" for b in bars))
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t in TYPES:
        c = acc.cov[t]["wares"]
        if c < 20:
            continue
        print(f"  {t:13s} {c:>8,} "
              + " ".join(f"{acc.pass_bar[t][b] / c:>9.1%}" for b in bars))
    print("\n  Percentages are of ALL Wares of the type - the real coverage cost.")


LADDER_ORDER = ["30-day history", "90-day history", "180-day history",
                "live listings", "data centre", "vendor price", "none"]


def report_ladder(acc):
    print("\n" + "=" * 104)
    print("TABLE 4b - EMM always produces an estimate. What does the weaker one cost?")
    print("           Estimate made as of 30 days ago from data strictly before it,")
    print("           scored against what the following 30 days actually did.")
    print("=" * 104)
    hdr = (f"  {'basis':18s} {'wares':>8s} {'share':>8s} {'confident':>10s} "
           f"{'median error':>13s} {'p90 error':>11s}")
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    total = sum(len(v) for v in acc.ladder.values())
    if not total:
        print("  no scored Wares")
        return
    for basis in LADDER_ORDER:
        rows = acc.ladder.get(basis)
        if not rows:
            continue
        errs = sorted(e for _, e in rows)
        conf = sum(1 for c, _ in rows if c) / len(rows)
        print(f"  {basis:18s} {len(rows):>8,} {len(rows) / total:>7.1%} "
              f"{conf:>10.0%} {statistics.median(errs):>12.1%} "
              f"{errs[int(0.9 * (len(errs) - 1))]:>10.1%}")
    conf_rows = [e for v in acc.ladder.values() for c, e in v if c]
    prov_rows = [e for v in acc.ladder.values() for c, e in v if not c]
    if conf_rows and prov_rows:
        print("  " + "-" * (len(hdr) - 2))
        print(f"  {'CONFIDENT':18s} {len(conf_rows):>8,} "
              f"{len(conf_rows) / total:>7.1%} {'100%':>10s} "
              f"{statistics.median(conf_rows):>12.1%}")
        print(f"  {'PROVISIONAL':18s} {len(prov_rows):>8,} "
              f"{len(prov_rows) / total:>7.1%} {'0%':>10s} "
              f"{statistics.median(prov_rows):>12.1%}")
    print("\n  A provisional estimate is not refused - it is produced and marked. These")
    print("  are the numbers the warning has to be honest about.")


def report_stacks(acc):
    print("\n" + "=" * 104)
    print("TABLE 5 - stack size against unit price: is there a buyer budget?")
    print("=" * 104)
    hdr = (f"  {'ware type':13s} {'wares':>7s} {'rho(price,qty)':>15s} "
           f"{'median txn value':>17s} {'p90 txn value':>15s}")
    print(hdr)
    print("  " + "-" * (len(hdr) - 2))
    for t in TYPES:
        pairs = acc.qty_vs_price[t]
        vals = acc.txn_values[t]
        if len(pairs) < 8 or not vals:
            continue
        r = spearman_pairs(pairs)
        if r is None:
            continue
        vals.sort()
        print(f"  {t:13s} {len(pairs):>7,} {r:>+15.2f} "
              f"{statistics.median(vals):>17,.0f} "
              f"{vals[int(0.9 * (len(vals) - 1))]:>15,.0f}")
    print("\n  rho is across Wares: median unit price against median stack size sold.")
    print("  Strongly negative = buyers hold a transaction budget, so stack shrinks")
    print("  as unit price rises. Near zero = stack is chosen for other reasons.")

    print("\n  which stack sizes clear, and which sit (pooled over the type):")
    for t in TYPES:
        sales, book = acc.stack_sales[t], acc.stack_book[t]
        if sum(sales.values()) < 200:
            continue
        ts, tb = sum(sales.values()), sum(book.values()) or 1
        print(f"\n    {t}")
        print(f"      {'stack':>9s} {'% sales':>9s} {'% book':>8s} {'clear ratio':>12s}")
        for _, _, lab in BUCKETS:
            s, b = sales.get(lab, 0) / ts, book.get(lab, 0) / tb
            if s < 0.005 and b < 0.005:
                continue
            ratio = s / b if b else float("inf")
            flag = "  <- sits" if (b >= 0.05 and ratio < 0.6) else ""
            rs = "inf" if ratio == float("inf") else f"{ratio:.2f}"
            print(f"      {lab:>9s} {s:>8.1%} {b:>7.1%} {rs:>12s}{flag}")


def type_params(acc):
    params = {}
    for t in TYPES:
        vels = acc.velocity[t]
        if len(vels) < 8:
            continue
        gap = 1.0 / statistics.median(vels)
        horizon = int(min(max(round(gap * 6), 3), 60))
        vals = acc.txn_values[t]
        target = statistics.median(vals) if len(vals) >= 50 else None
        params[t] = {
            "gap": gap,
            "horizon": horizon,
            "step": max(1, horizon // 7),
            "window": max(30, horizon),
            "target_txn": target,
        }
    return params


# =======================================================================
# PASS 2 - back-test
# =======================================================================

def backtest_ware(w, p):
    horizon, step, window = p["horizon"], p["step"], p["window"]
    rows = w.sales
    if len(rows) < 20:
        return None
    if w.meta["stackSize"] > 1:
        counts = {}
        for r in rows:
            b = bucket(r.qty)
            counts[b] = counts.get(b, 0) + 1
        modal = max(counts, key=counts.get)
        rows = [r for r in rows if bucket(r.qty) == modal]
        if len(rows) < 20:
            return None
    start = rows[0].ts + window * DAY
    end = stream.NOW_TS - horizon * DAY
    if end <= start:
        return None
    raw = {n: {"gil": 0.0, "occupied_days": 0.0, "held_days": 0.0,
               "acted": 0, "held": 0, "unsold": 0} for n in models.POLICIES}
    t, points = start, 0
    while t < end:
        hist = [r for r in rows if t - window * DAY <= r.ts < t]
        recent = [r for r in rows if t - horizon * DAY <= r.ts < t]
        future = [r for r in rows if t <= r.ts < t + horizon * DAY]
        prices = models.policy_prices(hist, recent, MIN_EFF)
        t += step * DAY
        if not prices:
            continue
        points += 1
        for name, price in prices.items():
            s = raw[name]
            if price is None:
                s["held"] += 1
                s["held_days"] += step
                continue
            s["acted"] += 1
            hit = next((r for r in future if r.price >= price), None)
            if hit is None:
                s["unsold"] += 1
                s["occupied_days"] += horizon
            else:
                s["gil"] += price * (1 - TAX)
                s["occupied_days"] += max((hit.ts - (t - step * DAY)) / DAY, 0.25)
    return raw if points >= 8 else None


def pass2(params):
    per_type = {t: [] for t in params}
    seen = 0
    for w in stream.wares(min_sales=20):
        t = classify(w.meta)
        if t not in params:
            continue
        seen += 1
        if seen % 2000 == 0:
            print(f"  pass2 {seen:,} wares", flush=True, file=sys.stderr)
        r = backtest_ware(w, params[t])
        if r:
            per_type[t].append(r)
    return per_type


def report_backtest(per_type, params):
    print("\n" + "=" * 104)
    print("TABLE 6 - policy back-test per ware type, held slot-days CREDITED at the")
    print("          type's opportunity rate, so a Hold is only worth taking if waiting")
    print("          beats handing the slot to something else.")
    print("=" * 104)
    presets = {}
    for t, results in per_type.items():
        if len(results) < 20:
            continue
        p = params[t]
        # Opportunity rate is now PER WARE (ruling, 2026-08-17): what this same
        # Ware's slot earns under the best policy that never holds. The median
        # across Wares is printed only to describe the type, never used.
        rates = [r for r in (models.ware_opportunity_rate(raw) for raw in results) if r]

        opp_port = statistics.median(rates) if rates else 0.0
        print(f"\n  {t}  -  {len(results):,} Wares  |  gap {p['gap']:.1f}d  "
              f"|  horizon {p['horizon']}d, step {p['step']}d, window {p['window']}d")
        print(f"  portfolio opportunity rate: {opp_port:,.1f} gil/slot-day")

        # Two scorings, because the choice of opportunity rate turns out to
        # decide the answer. PORTFOLIO credits a held slot at what a typical
        # other Ware of the type would earn in it - the slot's real alternative
        # use. OWN credits it at what THIS Ware would earn if listed, which is
        # the thing being declined, so a Hold is refunded exactly what it gave
        # up and the metric goes flat by construction. The second column is
        # printed to show that, not because it should be believed.
        scored_port = [models.score_policies(raw, opp_port) for raw in results]
        scored_own = [models.score_policies(raw, models.ware_opportunity_rate(raw))
                      for raw in results]

        hdr = (f"    {'policy':16s} {'rel(portfolio)':>15s} {'rel(own)':>10s} "
               f"{'rel per-unit':>13s} {'wins':>7s} {'hold':>7s} {'unsold':>8s}")
        print(hdr)
        print("    " + "-" * (len(hdr) - 4))

        def relmed(scored, name, key):
            vals = []
            for sc in scored:
                if name not in sc:
                    continue
                best = max(x[key] for x in sc.values())
                if best:
                    vals.append(sc[name][key] / best)
            return statistics.median(vals) if vals else 0.0

        rows = []
        for name in models.POLICIES:
            present = [sc for sc in scored_port if name in sc]
            if len(present) < 10:
                continue
            wins = sum(1 for sc in scored_port
                       if name in sc
                       and sc[name]["per_day_total"] >= max(
                           x["per_day_total"] for x in sc.values()) - 1e-9)
            rows.append((
                relmed(scored_port, name, "per_day_total"), name,
                relmed(scored_own, name, "per_day_total"),
                relmed(scored_port, name, "per_dec"),
                wins / len(scored_port),
                statistics.fmean([sc[name]["hold_rate"] for sc in present]),
                statistics.fmean([sc[name]["unsold_rate"] for sc in present]),
            ))
        rows.sort(reverse=True)
        for rel, name, relown, relu, win, hold, uns in rows:
            print(f"    {name:16s} {rel:>15.2f} {relown:>10.2f} {relu:>13.2f} "
                  f"{win:>7.0%} {hold:>7.0%} {uns:>8.0%}")
        if rows:
            spread_own = max(r[2] for r in rows) - min(r[2] for r in rows)
            spread_port = max(r[0] for r in rows) - min(r[0] for r in rows)
            print(f"    spread across policies: portfolio {spread_port:.02f}, "
                  f"own {spread_own:.02f}"
                  + ("   <- own-rate scoring cannot discriminate"
                     if spread_own < spread_port * 0.6 else ""))
        if rows:
            presets[t] = {"policy": rows[0][1], "params": p,
                          "opp": statistics.median(rates) if rates else 0.0,
                          "runner_up": rows[1][1] if len(rows) > 1 else None,
                          "margin": (rows[0][0] - rows[1][0]) if len(rows) > 1 else None}
            if len(rows) > 1 and rows[0][0] - rows[1][0] < 0.05:
                print(f"    NOTE: {rows[0][1]} and {rows[1][1]} are within "
                      f"{rows[0][0] - rows[1][0]:.02f} on the total metric - too close "
                      f"to call from this data alone.")
    return presets


def report_presets(presets, acc):
    print("\n" + "=" * 104)
    print("TABLE 7 - MODEL-SUGGESTED starting defaults per ware type")
    print("          Suggestions for approval, not settled defaults.")
    print("=" * 104)
    hdr = (f"  {'ware type':13s} {'policy':16s} {'window':>7s} {'horizon':>8s} "
           f"{'n_eff':>6s} {'prec':>6s} {'stack rule':>30s}   runner-up")
    print(hdr)
    print("  " + "-" * (len(hdr) + 6))
    for t in TYPES:
        if t not in presets:
            continue
        p = presets[t]["params"]
        tv = p.get("target_txn")
        # Whether a stack rule applies is decided by OBSERVED behaviour, not by
        # the nominal stack size. Collectibles are nominally stackable and sell
        # 97% as single units; a stack rule there would be an artefact of the
        # game data rather than of how the ware actually trades.
        sales = acc.stack_sales[t]
        total = sum(sales.values())
        singles = sales.get("1", 0) / total if total else 1.0
        st, tot = acc.stackable[t]
        share_stackable = st / tot if tot else 0.0
        # Both tests must pass. The nominal one alone would put a stack rule on
        # Collectibles, which are stackable and sell 97% as single units. The
        # behavioural one alone would put a stack rule on Gear, whose observed
        # stack sales come from 86 oddities out of 14,429 Wares.
        if tv and total and singles < 0.60 and share_stackable >= 0.5:
            stack = f"qty ~ {tv:,.0f} gil / unit price"
        elif share_stackable < 0.5:
            stack = f"single unit ({share_stackable:.0%} of Wares stackable)"
        else:
            stack = f"single unit ({singles:.0%} of sales are qty 1)"
        ru = presets[t].get("runner_up")
        margin = presets[t].get("margin")
        note = f"{ru} (+{margin:.02f})" if ru else ""
        if margin is not None and margin < 0.05:
            note += "  <- too close to call"
        print(f"  {t:13s} {presets[t]['policy']:16s} {p['window']:>6d}d "
              f"{p['horizon']:>7d}d {MIN_EFF:>6.0f} {PRECISION_BAR:>5.0%} "
              f"{stack:>30s}   {note}")
    print("\n  stack rule: target transaction value divided by unit price, clamped to the")
    print("  item's stack size - so a 20,000 gil/unit ware is offered in small stacks and a")
    print("  50 gil/unit ware in large ones, without either being hardcoded.")


def main():
    print("undercut-formula prototype v3 - FULL CATALOGUE", flush=True)
    print(f"cache: {os.path.basename(stream.CACHE)}  |  {stream.NOW:%Y-%m-%dT%H:%MZ}", flush=True)
    acc = pass1()
    report_coverage(acc)
    report_separation(acc)
    report_effn(acc)
    report_precision(acc)
    report_ladder(acc)
    report_stacks(acc)
    params = type_params(acc)
    per_type = pass2(params)
    presets = report_backtest(per_type, params)
    report_presets(presets, acc)


if __name__ == "__main__":
    main()

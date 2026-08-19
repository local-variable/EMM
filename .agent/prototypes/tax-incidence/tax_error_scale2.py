"""#19 -- how large is a plausible tax error relative to a realistic flip edge? (joined)

The ticket asserts "a 5% error is roughly half the edge on a typical flip". The live rate
table makes the plausible error 2 points (assume 5%, actually 3%), not 5. This measures
both against the Cactuar corpus.

METHOD (stated before the result, because this is reviewed as statistics):
  - Unit of analysis: a Ware (item x quality), NQ and HQ reported separately. Mixing
    qualities would compare two different goods.
  - Buy proxy  = lowest current listing of that quality (the floor a flipper pays).
  - Sell proxy = MEDIAN unit price of that quality's sales in the last 30 days. Median,
    not mean, because sale rows are heavy-tailed and #11 found means unusable here.
  - Gross edge E = (sell - buy) / buy. This is a SCALE CHECK, not a backtest: no
    time-to-clear, no slot cost, no undercutting response.
  - A tax error of d points costs d% of revenue; revenue ~ cost x (1+E), so the error as
    a share of the edge is d*(1+E)/E.
  - Inclusion: >= 5 current listings AND >= 5 sales of that quality in the 30-day window.

KNOWN BIASES, not corrected:
  - The buy proxy is minListing, the least stable statistic on the board (#11: moves
    205-340% of its own median over 60 days). Using it OVERSTATES achievable edge.
  - The sell proxy is survivorship-biased toward Wares that actually sell, and the two
    proxies are measured at different times (listings 2026-08-17, sales the 30 days before).
  - One World, one snapshot. No confidence interval is quoted on E because the sampling
    unit is a Ware and the Wares are not independent (shared crafting inputs, shared
    demand shocks).
  - Net effect: E is biased UPWARD, so error-as-share-of-edge below is a LOWER bound.
"""
import json, glob, os, time
from collections import defaultdict

# The cached corpus is not published; see ../undercut-formula/README.md. Point EMM_CORPUS at a
# copy, or leave it unset to use the sibling directory in a full working copy.
ROOT = os.environ.get(
    "EMM_CORPUS",
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "undercut-formula"),
)
WINDOW_DAYS = 30
NOW = 1786982400  # ~2026-08-17, the fetch instant seen in lastReviewTime
CUT = NOW - WINDOW_DAYS * 86400

def pct(xs, p):
    xs = sorted(xs)
    if not xs:
        return float("nan")
    k = (len(xs) - 1) * p
    lo = int(k); hi = min(lo + 1, len(xs) - 1)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)

# ---- pass 1: sales, per (itemID, hq) ----
sales = defaultdict(list)
t0 = time.time()
for path in sorted(glob.glob(os.path.join(ROOT, "data_all", "bulk", "hist_world_*.json"))):
    with open(path, encoding="utf-8") as fh:
        blob = json.load(fh)
    for key, item in (blob.get("items") or {}).items():
        iid = item.get("itemID") or key
        for e in (item.get("entries") or []):
            tsx = e.get("timestamp") or 0
            ppu = e.get("pricePerUnit")
            if tsx >= CUT and ppu:
                sales[(int(iid), bool(e.get("hq")))].append(ppu)
print(f"pass 1: {len(sales):,} (item,quality) keys with >=1 sale in {WINDOW_DAYS}d"
      f"  [{time.time()-t0:.0f}s]")

# ---- pass 2: listings, join ----
edges = {False: [], True: []}
for path in sorted(glob.glob(os.path.join(ROOT, "data_all", "bulk", "lst_world_*.json"))):
    with open(path, encoding="utf-8") as fh:
        blob = json.load(fh)
    for key, item in (blob.get("items") or {}).items():
        iid = int(item.get("itemID") or key)
        for hq in (False, True):
            px = [l["pricePerUnit"] for l in (item.get("listings") or [])
                  if bool(l.get("hq")) == hq and l.get("pricePerUnit")]
            sp = sales.get((iid, hq)) or []
            if len(px) < 5 or len(sp) < 5:
                continue
            buy = min(px)
            sell = pct(sp, 0.5)
            if buy > 0:
                edges[hq].append((sell - buy) / buy)

for hq in (False, True):
    E = edges[hq]
    label = "HQ" if hq else "NQ"
    if not E:
        print(f"\n== {label}: no qualifying Wares =="); continue
    n = len(E)
    print(f"\n== {label} — {n:,} qualifying Wares (Cactuar, >=5 listings & >=5 sales/30d) ==")
    print("  gross edge E = (median 30d sale - lowest listing) / lowest listing")
    for p in (0.10, 0.25, 0.50, 0.75, 0.90):
        print(f"    p{int(p*100):<3} {pct(E,p)*100:9.2f}%")
    neg = sum(1 for e in E if e <= 0)
    print(f"    E <= 0 (floor already at or above the median sale): {neg:,} ({100.0*neg/n:.1f}%)")
    for d in (0.02, 0.05):
        share = [d * (1 + e) / e for e in E if e > 0]
        killed = sum(1 for s in share if s >= 1.0)
        print(f"  a {int(d*100)}-point tax error as a share of that edge:")
        print(f"    p25 {pct(share,0.25)*100:7.1f}%   median {pct(share,0.5)*100:7.1f}%"
              f"   p75 {pct(share,0.75)*100:8.1f}%")
        print(f"    Wares where it exceeds the ENTIRE edge: {killed:,}/{len(share):,}"
              f" ({100.0*killed/max(len(share),1):.1f}%)")

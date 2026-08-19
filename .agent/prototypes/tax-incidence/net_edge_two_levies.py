"""#19 -- flip economics once BOTH levies are counted.

Supersedes the single-levy framing of the first scale check. Source reading established
two separate charges:
    buyer fee   ~5% ADDED to what a purchase costs   (flat, per every source found)
    seller tax  0-5% DEDUCTED from what a sale pays  (per city, time-bounded)

A flip pays both. Gross edge is therefore the wrong number; net edge is:

    cost     = buy  * (1 + f)          f = buyer fee   (0.05)
    proceeds = sell * (1 - r)          r = seller tax  (0.00 / 0.03 / 0.05)
    netEdge  = (proceeds - cost) / cost

METHOD is unchanged from the first pass and its biases carry over verbatim:
  - Ware = item x quality, NQ and HQ separate.
  - buy  = lowest current listing of that quality  (minListing: the least stable
           statistic on the board, #11 -- this OVERSTATES the edge)
  - sell = median unit price of that quality's sales in the trailing 30 days
  - inclusion: >=5 current listings AND >=5 sales in the window; Cactuar only.
  - survivorship bias toward Wares that sell; proxies measured at different times;
    one World, one snapshot; Wares are not independent, so no interval is quoted.
  => every figure below is a LOWER bound on how bad the picture is.
"""
import json, glob, os
from collections import defaultdict

# The cached corpus is not published; see ../undercut-formula/README.md. Point EMM_CORPUS at a
# copy, or leave it unset to use the sibling directory in a full working copy.
ROOT = os.environ.get(
    "EMM_CORPUS",
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "undercut-formula"),
)
WINDOW_DAYS = 30
NOW = 1786982400
CUT = NOW - WINDOW_DAYS * 86400
BUYER_FEE = 0.05

def pct(xs, p):
    xs = sorted(xs)
    if not xs:
        return float("nan")
    k = (len(xs) - 1) * p
    lo = int(k); hi = min(lo + 1, len(xs) - 1)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)

sales = defaultdict(list)
for path in sorted(glob.glob(os.path.join(ROOT, "data_all", "bulk", "hist_world_*.json"))):
    with open(path, encoding="utf-8") as fh:
        blob = json.load(fh)
    for key, item in (blob.get("items") or {}).items():
        iid = int(item.get("itemID") or key)
        for e in (item.get("entries") or []):
            if (e.get("timestamp") or 0) >= CUT and e.get("pricePerUnit"):
                sales[(iid, bool(e.get("hq")))].append(e["pricePerUnit"])

pairs = {False: [], True: []}
for path in sorted(glob.glob(os.path.join(ROOT, "data_all", "bulk", "lst_world_*.json"))):
    with open(path, encoding="utf-8") as fh:
        blob = json.load(fh)
    for key, item in (blob.get("items") or {}).items():
        iid = int(item.get("itemID") or key)
        for hq in (False, True):
            px = [l["pricePerUnit"] for l in (item.get("listings") or [])
                  if bool(l.get("hq")) == hq and l.get("pricePerUnit")]
            sp = sales.get((iid, hq)) or []
            if len(px) >= 5 and len(sp) >= 5 and min(px) > 0:
                pairs[hq].append((min(px), pct(sp, 0.5)))

for hq in (False, True):
    label = "HQ" if hq else "NQ"
    P = pairs[hq]
    if not P:
        print(f"== {label}: none ==");
        continue
    n = len(P)
    print(f"\n== {label} — {n:,} qualifying Wares (Cactuar) ==")
    gross = [(s - b) / b for b, s in P]
    print(f"  gross edge (no levies)      median {pct(gross,0.5)*100:7.2f}%"
          f"   share with edge <= 0: {100.0*sum(1 for e in gross if e<=0)/n:5.1f}%")
    for r in (0.00, 0.03, 0.05):
        net = [((s * (1 - r)) - (b * (1 + BUYER_FEE))) / (b * (1 + BUYER_FEE)) for b, s in P]
        dead = sum(1 for e in net if e <= 0)
        print(f"  net edge, seller tax {int(r*100)}%      median {pct(net,0.5)*100:7.2f}%"
              f"   share with edge <= 0: {100.0*dead/n:5.1f}%"
              f"   p75 {pct(net,0.75)*100:7.2f}%  p90 {pct(net,0.90)*100:7.2f}%")
    # how many Wares are profitable gross but UNPROFITABLE once both levies bite
    r = 0.03
    net = [((s * (1 - r)) - (b * (1 + BUYER_FEE))) / (b * (1 + BUYER_FEE)) for b, s in P]
    flipped = sum(1 for g, nn in zip(gross, net) if g > 0 and nn <= 0)
    pos = sum(1 for g in gross if g > 0)
    print(f"  Wares that look profitable GROSS but are not once both levies bite"
          f" (seller tax 3%): {flipped:,} / {pos:,} ({100.0*flipped/max(pos,1):.1f}%)")

"""#19 follow-ups: does anything in the corpus DISCRIMINATE game-truth from aggregator-constant?

  E. Do the tax==0 rows all satisfy total < 20 (i.e. the floor rule, not a missing value)?
  F. lastReviewTime spread -- if `tax` were stored at upload time and the rate rotated,
     old listings would carry a different rate. Any deviation at all?
  G. Do HISTORY rows (recorded sales) carry tax, and is their `total` ppu*qty?
  H. The v2 sample cache (`data/`) was fetched at a different time from `data_all/`.
     Same rate there?
"""
import json, os, glob, math, datetime
from collections import Counter, defaultdict

# The cached corpus is not published; see ../undercut-formula/README.md. Point EMM_CORPUS at a
# copy, or leave it unset to use the sibling directory in a full working copy.
ROOT = os.environ.get(
    "EMM_CORPUS",
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "undercut-formula"),
)

def listing_rows(datadir, pattern="lst_world_*.json"):
    for path in sorted(glob.glob(os.path.join(ROOT, datadir, "bulk", pattern))):
        with open(path, encoding="utf-8") as fh:
            blob = json.load(fh)
        for item in (blob.get("items") or {}).values():
            for ls in (item.get("listings") or []):
                yield ls

# ---------- E + F : Cactuar ----------
zero_bad = 0; zero_n = 0
lrt_min = None; lrt_max = None
dev_by_week = Counter(); n_by_week = Counter()
worst = []
for ls in listing_rows("data_all"):
    tot, tax = ls.get("total"), ls.get("tax")
    if tot is None or tax is None or not tot:
        continue
    if tax == 0:
        zero_n += 1
        if tot >= 20:
            zero_bad += 1
    lrt = ls.get("lastReviewTime")
    if lrt:
        lrt_min = lrt if lrt_min is None else min(lrt_min, lrt)
        lrt_max = lrt if lrt_max is None else max(lrt_max, lrt)
        wk = datetime.datetime.utcfromtimestamp(lrt).strftime("%Y-W%W")
        n_by_week[wk] += 1
        if tax != math.floor(tot * 0.05):
            dev_by_week[wk] += 1
            if len(worst) < 10:
                worst.append(ls)

print("E. tax==0 rows:", f"{zero_n:,}", "| of those with total >= 20 (rule violation):", zero_bad)
print("F. lastReviewTime range:",
      datetime.datetime.utcfromtimestamp(lrt_min).isoformat(), "->",
      datetime.datetime.utcfromtimestamp(lrt_max).isoformat(),
      f"({(lrt_max-lrt_min)/86400:.1f} days)")
print("   deviations from floor(total*0.05), by review week:")
for wk in sorted(n_by_week):
    print(f"     {wk}  n={n_by_week[wk]:>7,}  deviations={dev_by_week.get(wk,0)}")

# ---------- G : history rows ----------
hist_keys = None; h_n = 0; h_mismatch = 0; h_has_tax = 0
for path in sorted(glob.glob(os.path.join(ROOT, "data_all", "bulk", "hist_world_*.json")))[:20]:
    with open(path, encoding="utf-8") as fh:
        blob = json.load(fh)
    for item in (blob.get("items") or {}).values():
        for e in (item.get("entries") or []):
            if hist_keys is None:
                hist_keys = sorted(e.keys())
            h_n += 1
            if "tax" in e:
                h_has_tax += 1
            ppu, qty, tot = e.get("pricePerUnit"), e.get("quantity"), e.get("total")
            if None not in (ppu, qty, tot) and tot != ppu * qty:
                h_mismatch += 1
print("\nG. history entry keys:", hist_keys)
print(f"   sampled {h_n:,} sale rows | carrying a 'tax' field: {h_has_tax:,}"
      f" | total != ppu*qty: {h_mismatch:,}")

# ---------- H : the earlier v2 sample cache ----------
for tag, datadir in (("data (v2 sample, Cactuar)", "data"),):
    c = Counter(); n = 0
    for ls in listing_rows(datadir):
        tot, tax = ls.get("total"), ls.get("tax")
        if not tot or tax is None:
            continue
        n += 1
        c["floor .05" if tax == math.floor(tot * 0.05) else "OTHER"] += 1
    print(f"\nH. {tag}: n={n:,} ->", dict(c))
    p = os.path.join(ROOT, datadir, "bulk", "lst_world_000.json")
    if os.path.exists(p):
        print("   cache file mtime:",
              datetime.datetime.fromtimestamp(os.path.getmtime(p)).isoformat())
p2 = os.path.join(ROOT, "data_all", "bulk", "lst_world_0000.json")
print("   data_all cache file mtime:",
      datetime.datetime.fromtimestamp(os.path.getmtime(p2)).isoformat())

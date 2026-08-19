"""#19 tax incidence -- empirical pass over the cached Universalis v2 listing rows.

Reads the local corpus only. No network. Two worlds:
  data_all/  = Cactuar   (Aether)
  data_adam/ = Adamantoise (Aether)

Questions this pass answers, from the listing rows alone:
  A. Is `total` exactly pricePerUnit * quantity?  (is tax separate/additive?)
  B. What is tax/total, per retainerCity, per world?
  C. Which exact arithmetic reproduces `tax` -- floor/round/ceil of total*r?
  D. Do cities differ from one another at the same instant?  Do worlds differ?
"""
import json, os, glob, math
from collections import defaultdict, Counter

# The cached corpus is not published; see ../undercut-formula/README.md. Point EMM_CORPUS at a
# copy, or leave it unset to use the sibling directory in a full working copy.
ROOT = os.environ.get(
    "EMM_CORPUS",
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "undercut-formula"),
)
WORLDS = [("Cactuar", "data_all"), ("Adamantoise", "data_adam")]

# Universalis retainerCity -> name. Verified against the set of ids actually observed
# below; ids not in this table are printed raw so nothing is silently mislabelled.
CITY = {1: "Limsa Lominsa", 2: "Gridania", 3: "Ul'dah", 4: "Ishgard",
        7: "Kugane", 10: "Crystarium", 12: "Old Sharlayan", 14: "Tuliyollal"}

def rows(datadir):
    for path in sorted(glob.glob(os.path.join(ROOT, datadir, "bulk", "lst_world_*.json"))):
        with open(path, encoding="utf-8") as fh:
            blob = json.load(fh)
        for item in (blob.get("items") or {}).values():
            for ls in (item.get("listings") or []):
                yield ls

for world, datadir in WORLDS:
    n = 0
    mismatch_total = 0            # A: total != ppu*qty
    zero_total = 0
    by_city_ratio = defaultdict(Counter)   # B: city -> Counter of exact ratio
    by_city_n = Counter()
    rule_hits = defaultdict(Counter)       # C: city -> rule -> hits
    tax_zero = Counter()

    for ls in rows(datadir):
        ppu = ls.get("pricePerUnit"); qty = ls.get("quantity")
        tot = ls.get("total");        tax = ls.get("tax")
        city = ls.get("retainerCity")
        if tot is None or tax is None:
            continue
        n += 1
        if ppu is not None and qty is not None and tot != ppu * qty:
            mismatch_total += 1
        if not tot:
            zero_total += 1
            continue
        by_city_n[city] += 1
        if tax == 0:
            tax_zero[city] += 1
        by_city_ratio[city][round(tax / tot, 6)] += 1
        for r in (0.03, 0.05):
            if tax == math.floor(tot * r): rule_hits[city][f"floor(total*{r})"] += 1
            if tax == round(tot * r):      rule_hits[city][f"round(total*{r})"] += 1
            if tax == math.ceil(tot * r):  rule_hits[city][f"ceil(total*{r})"]  += 1

    print("=" * 78)
    print(f"{world}  ({datadir})   listings with total+tax: {n:,}")
    print(f"  A. total != pricePerUnit*quantity : {mismatch_total:,}  "
          f"({100.0*mismatch_total/max(n,1):.4f}%)")
    print(f"     total == 0                      : {zero_total:,}")
    print("  B/C. per retainerCity:")
    for city in sorted(by_city_n, key=lambda c: -by_city_n[c]):
        cn = by_city_n[city]
        name = CITY.get(city, f"<unmapped id {city}>")
        ratios = by_city_ratio[city].most_common(4)
        ratio_s = "  ".join(f"{v:.5f}x{c:,}" for v, c in ratios)
        print(f"   [{city:>2}] {name:<15} n={cn:>7,}  tax==0:{tax_zero[city]:>6,}"
              f"  ratios: {ratio_s}")
        best = [(k, v) for k, v in rule_hits[city].items() if v]
        best.sort(key=lambda kv: -kv[1])
        for k, v in best[:4]:
            print(f"        {k:<22} {v:>7,} / {cn:,}  = {100.0*v/cn:6.2f}%")

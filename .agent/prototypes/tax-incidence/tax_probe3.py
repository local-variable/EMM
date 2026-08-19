"""#19 probe 3 -- corrects probe 2's F, and adds a cross-world check.

F': group by the ITEM's `lastUploadTime` (when Universalis last received this item's
    board), not by the listing's `lastReviewTime` (when the retainer last touched it).
    If `tax` is a value stamped at upload time, upload date is the variable that would
    expose a rate change. If it is computed at serve time, every bucket reads the same.
G': history rows -- confirm which fields are present/absent (no `total`, no `tax`).
I : the DC-wide listing cache covers OTHER worlds on Aether -- same rate off-world?
"""
import json, os, glob, math, datetime
from collections import Counter, defaultdict

# The cached corpus is not published; see ../undercut-formula/README.md. Point EMM_CORPUS at a
# copy, or leave it unset to use the sibling directory in a full working copy.
ROOT = os.environ.get(
    "EMM_CORPUS",
    os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "undercut-formula"),
)
UTC = datetime.timezone.utc
ts = lambda t: datetime.datetime.fromtimestamp(t, UTC)

# ---------- F' : by item lastUploadTime ----------
n_by_month = Counter(); dev_by_month = Counter()
up_min = up_max = None
for path in sorted(glob.glob(os.path.join(ROOT, "data_all", "bulk", "lst_world_*.json"))):
    with open(path, encoding="utf-8") as fh:
        blob = json.load(fh)
    for item in (blob.get("items") or {}).values():
        up = item.get("lastUploadTime")          # milliseconds
        if not up:
            continue
        up_s = up / 1000.0
        up_min = up_s if up_min is None else min(up_min, up_s)
        up_max = up_s if up_max is None else max(up_max, up_s)
        month = ts(up_s).strftime("%Y-%m")
        for ls in (item.get("listings") or []):
            tot, tax = ls.get("total"), ls.get("tax")
            if not tot or tax is None:
                continue
            n_by_month[month] += 1
            if tax != math.floor(tot * 0.05):
                dev_by_month[month] += 1

print("F'. item lastUploadTime range:", ts(up_min).isoformat(), "->", ts(up_max).isoformat(),
      f"({(up_max-up_min)/86400:.1f} days)")
print("    deviations from floor(total*0.05), by UPLOAD month:")
for m in sorted(n_by_month):
    print(f"      {m}  n={n_by_month[m]:>7,}  deviations={dev_by_month.get(m,0)}")

# ---------- G' : history field inventory ----------
keys = Counter(); h_n = 0
for path in sorted(glob.glob(os.path.join(ROOT, "data_all", "bulk", "hist_world_*.json")))[:10]:
    with open(path, encoding="utf-8") as fh:
        blob = json.load(fh)
    for item in (blob.get("items") or {}).values():
        for e in (item.get("entries") or []):
            h_n += 1
            for k in e:
                keys[k] += 1
print(f"\nG'. {h_n:,} history (sale) rows sampled. Field presence:")
for k, v in keys.most_common():
    print(f"      {k:<16} {v:>8,}  ({100.0*v/h_n:5.1f}%)")
for k in ("total", "tax"):
    print(f"      {k:<16} {'ABSENT from every row' if keys[k]==0 else keys[k]}")

# ---------- I : DC-wide cache, other worlds ----------
p = os.path.join(ROOT, "data", "listings_dc.json")
if os.path.exists(p):
    blob = json.load(open(p, encoding="utf-8"))
    items = blob.get("items") or ({blob.get("itemID"): blob} if "listings" in blob else {})
    by_world = defaultdict(Counter)
    for item in items.values():
        for ls in (item.get("listings") or []):
            tot, tax = ls.get("total"), ls.get("tax")
            if not tot or tax is None:
                continue
            w = ls.get("worldName") or "?"
            by_world[w]["floor .05" if tax == math.floor(tot * 0.05) else "OTHER"] += 1
    print("\nI. DC-wide cache, by world:")
    for w in sorted(by_world):
        print(f"      {w:<14} {dict(by_world[w])}")
else:
    print("\nI. no DC listing cache at", p)

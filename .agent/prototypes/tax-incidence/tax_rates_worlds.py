"""#19 -- is the `0` a real rate or a missing-data sentinel?

Cactuar reports Crystarium = 0 while Adamantoise (same DC, same instant) reports 3.
The endpoint is populated by players visiting a Retainer Vocate, so 0 is most likely
"nobody has reported this city on this world" -- which would be a live trap for EMM.

Sample the rest of Aether plus two worlds on other DCs. ~10 requests, and it also
captures the cache headers, since EMM will have to poll this endpoint.
"""
import json, urllib.request, time

UA = "EMM-research/0.1 (wayfinder #19 tax incidence; contact via github.com/local-variable/EMM)"
WORLDS = ["Cactuar", "Adamantoise", "Faerie", "Gilgamesh", "Jenova",
          "Midgardsormr", "Sargatanas", "Siren",            # Aether
          "Behemoth", "Excalibur",                          # Primal
          "Lich", "Ravana"]                                 # Chaos / Materia
CITIES = ["Limsa Lominsa", "Gridania", "Ul'dah", "Ishgard",
          "Kugane", "Crystarium", "Old Sharlayan", "Tuliyollal"]

rows = {}
hdrs_seen = None
for w in WORLDS:
    try:
        req = urllib.request.Request(f"https://universalis.app/api/v2/tax-rates?world={w}",
                                     headers={"User-Agent": UA})
        with urllib.request.urlopen(req, timeout=30) as r:
            if hdrs_seen is None:
                hdrs_seen = dict(r.headers)
            rows[w] = json.loads(r.read().decode("utf-8"))
    except Exception as exc:
        rows[w] = {"ERROR": repr(exc)}
    time.sleep(0.5)

hdr = "world".ljust(14) + "".join(c[:13].ljust(15) for c in CITIES)
print(hdr)
print("-" * len(hdr))
zeros = 0
for w in WORLDS:
    d = rows[w]
    if "ERROR" in d:
        print(w.ljust(14) + d["ERROR"]); continue
    line = w.ljust(14)
    for c in CITIES:
        v = d.get(c, "-")
        line += (f"{v}%" if v != 0 else "0%  <-").ljust(15)
        if v == 0:
            zeros += 1
    print(line)

print(f"\nzero readings across {len(WORLDS)} worlds x {len(CITIES)} cities: {zeros}")
print("\ncache-relevant response headers:")
for k in ("Cache-Control", "ETag", "Expires", "Age", "Last-Modified", "Date", "Content-Type"):
    if hdrs_seen and k in hdrs_seen:
        print(f"   {k}: {hdrs_seen[k]}")
    else:
        print(f"   {k}: (absent)")

"""#19 -- the decisive experiment. Two GET requests, nothing more.

The corpus says every cached listing carries tax == floor(total * 0.05), in all 8 cities,
all 8 Aether worlds, across 17 upload months, 210,746 rows, zero exceptions.

If /api/v2/tax-rates reports any city at a rate OTHER than 5%, then that city's listings
demonstrably do NOT carry the game's rate -- which makes the listing `tax` field the
aggregator's computed model, not game ground truth.

Deliberately tiny: 2 requests total, well inside the standing rate ceiling.
"""
import json, urllib.request, time

UA = "EMM-research/0.1 (wayfinder #19 tax incidence; contact via github.com/local-variable/EMM)"

def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.status, dict(r.headers), json.loads(r.read().decode("utf-8"))

for world in ("Cactuar", "Adamantoise"):
    url = f"https://universalis.app/api/v2/tax-rates?world={world}"
    try:
        status, hdrs, body = get(url)
        print(f"== {world} == HTTP {status}")
        print("   ", json.dumps(body, ensure_ascii=False))
        rates = {k: v for k, v in body.items()} if isinstance(body, dict) else {}
        odd = {k: v for k, v in rates.items() if v != 5}
        print("    cities NOT at 5%:", odd if odd else "(none)")
    except Exception as exc:
        print(f"== {world} == FAILED: {exc!r}")
    time.sleep(1.0)

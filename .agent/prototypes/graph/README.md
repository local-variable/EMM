# Graph prototype

Backs wayfinder ticket [#12 "Design the price history graph and its confidence bands"](https://github.com/local-variable/EMM/issues/12).

**Local only, deliberately.** Under `.agent/`, which is gitignored, by the same decision that
keeps the #11 prototype local (2026-08-17). Do not commit it. Referenced from
`.agent/CONTINUITY.md` and to be referenced from #12's resolution so a later session can find it.

**Read [`DESIGN.md`](DESIGN.md) first** — the six answers the ticket asked for, as proposals,
with what is real and what is simulated.

## Opening it

The page loads `data.js` with a `<script>` tag, so it works from `file://` too, but a static
server is the reliable route (and what the screenshots used):

```powershell
python -m http.server 8765 --bind 127.0.0.1 --directory .agent/prototypes/graph
```

then open `http://127.0.0.1:8765/index.html`. Deep links carry the view state, e.g.
`#item=41145&win=90`, `#item=36060&q=both&win=30`, `#item=1621&win=all`,
`#item=41145&win=90&theme=light&marks=both`, `#layers=+dos,-ribbon`.

## Regenerating the data

`extract.py` reads **only** the cached corpus at `../undercut-formula/data_all/` and the
model code beside it (`models.py`, `stream.py`, `wtype.py`, `common.py`). Nothing is
fetched. It takes about a minute (the daily estimate series runs a block bootstrap per day
per Ware).

```powershell
python .agent/prototypes/graph/extract.py
```

Wares are listed in `ITEMS` at the top of the script — the maintainer's own seven from the
#11 prototype plus four picked to cover the regimes the ticket names (a Consumable that
trades in both Qualities, a big-ticket Collectible, a Gear piece with HQ dominant, and an
Item with Listings but no Sales and a stale upload).

## Files

| file | what it is |
|---|---|
| `DESIGN.md` | the proposal, for review |
| `index.html` | the prototype — self-contained canvas rendering, no dependencies |
| `data.js` | ~410 KB: 15 Wares, Sales (buyer hashed), Listings (retainer names dropped, own flagged), aggregates, per-day `models.estimate()` output, the Strategy replay |
| `extract.py` | builds `data.js` from the corpus |

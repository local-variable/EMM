# Undercut formula prototype

Backs wayfinder ticket [#11 "Design the undercut protection formula"](https://github.com/local-variable/EMM/issues/11).

**Published code, unpublished data (revised 2026-08-19, superseding the local-only decision
of 2026-08-17).** The scripts, findings and results in this directory are committed. **The
cached market corpus is not, and must never be.**

## The corpus — read this before fetching anything

`data/`, `data_all/` and `data_adam/` are **deliberately absent from the repository** and are
excluded by `.gitignore`. They hold roughly **832 MB across ~2,740 files**: the entire
16,843-item marketable catalogue on **Cactuar** and **Adamantoise** at ~180 days of history
each, a 90-day Aether data-centre history for a stratified subset, and World-independent game
metadata for every item.

**Do not re-scrape.** A full pull is about **1,200 requests per World and takes the best part
of an hour** at the self-imposed ceiling of two connections and ~4 req/s. The corpus is not
cheaply reproducible, and re-fetching it because it looked missing is the specific mistake
this note exists to prevent.

- **Where it lives:** on the maintainer's working copy, at this directory — `.agent/prototypes/undercut-formula/data*/`. It is not on any remote, by decision: the repository is public, ~832 MB is permanent in every clone, and the aggregator publishes no data licence (recorded UNCONFIRMED on [#3](https://github.com/local-variable/EMM/issues/3)).
- **If you have a copy elsewhere:** point `EMM_CORPUS` at it for the tax-incidence probes, and `EMM_DATA` at the specific cache directory for the scripts here.
- **If you genuinely have no copy:** re-fetch only the slice you need. The fetcher is parameterised by World and writes to its own directory, so a third World leaves existing caches untouched; item metadata is World-independent and should be copied, never re-requested.
- **What the build actually needs:** not this corpus. The committed fixture slice specified for the test suite is a few hundred kilobytes and covers liquid, thin, never-traded and provisional-only Wares. Reach for the full corpus only for coverage scoring and backtest calibration, which run on demand rather than in the suite.

**Read [`REVIEW.md`](REVIEW.md) first** — it is the findings document, written
methods-and-limitations first because the maintainer reviews it as a statistician.

**Scope: Aether only — Cactuar and Adamantoise.** No other data centre is fetched.

> **DO NOT RE-DOWNLOAD THE CATALOGUE.** Standing instruction from the maintainer
> (2026-08-17): the caches below are complete and must not be refetched until a test
> genuinely needs fresh data. A full pull is ~1,200 requests and ~280 MB per world.
>
> | cache | world | size |
> |---|---|---|
> | `data_all/` | Cactuar + Aether DC history | ~283 MB + ~186 MB |
> | `data_adam/` | Adamantoise | ~280 MB |
> | `data/` | Cactuar, seeded 1,000-item sample (v2) | ~14 MB |
>
> A new world writes to its own directory and leaves the others alone:
> `EMM_WORLD=<name> EMM_DATA=<dir> EMM_FETCH_DC=0 python fetch_all.py`. Copy `items.json`
> from an existing cache first — the game metadata is world-independent and re-fetching it
> is 169 needless XIVAPI requests.

## Re-running

The current work is **v3, the full catalogue**: `fetch_all.py` → `analyse_all.py` →
`dc_reversion.py`, all reading `data_all/`. The v2 stage scripts below read `data/`, the
seeded 1,000-item sample, and are kept because the two are meant to stay comparable.

```bash
python fetch_all.py       # v3: whole catalogue, 16,843 items, ~283 MB, resumable
```

```bash
python analyse_all.py     # v3: two streaming passes, tables 1-7
```

```bash
python dc_reversion.py    # v3: Aether backfill + tables 8a/8b
```

```bash
python recalibrate.py     # v4: the daily refit, and how far parameters drift
```

```bash
python compare_worlds.py  # v4: Cactuar against Adamantoise
```

### v2, the seeded sample

Pure standard-library Python 3.12; no numpy, scipy or pandas on this machine, so
everything is hand-rolled. Stages are ordered and cache to `data/`, so a re-run is cheap
and a failed fetch costs one batch rather than the run.

```bash
python sample.py          # stage 1: seeded 1000-item sample + game metadata
```

```bash
python bulk_fetch.py      # stage 2: Universalis aggregated / listings / 180d history
```

```bash
python report_types.py    # stage 3: ware types, and whether the split is real
```

```bash
python stacks.py          # stage 4: stack-size cut points
```

```bash
python precision.py       # stage 5: bootstrap precision and confidence rules
```

```bash
python typed_backtest.py  # stage 6: policy back-test per ware type
```

```bash
python index.py           # stage 7: world pricing index, DC gut check
```

`analyse.py`, `backtest.py`, `common.py`, `survey.py` and `fetch.py` are the **first**
prototype — seven Wares from the maintainer's own retainers. Superseded by the stages
above but kept, because the two biases found in `backtest.py` are documented in its
docstring and the lesson is the point.

## Files

| file | what it is |
|---|---|
| `REVIEW.md` | the findings, for review |
| **v3/v4 — full catalogue** | |
| `fetch_all.py` | whole marketable catalogue, 2 connections at ~4 req/s, resumable, world-parameterised |
| `stream.py` | streaming loader — the full cache does not fit in memory as parsed JSON |
| `models.py` | effective-n, block bootstrap, the six-rung estimate ladder, buyer-budget stack rule, policy scoring |
| `analyse_all.py` | two streaming passes, tables 1-7 |
| `dc_reversion.py` | Aether backfill and tables 8a/8b (shared vs split-half baseline) |
| `recalibrate.py` | `calibrate(as_of)` — the daily refit — plus the drift report, table 9 |
| `compare_worlds.py` | Cactuar against Adamantoise, tables 10-11 |
| `data_all/`, `data_adam/` | cached API responses — **do not refetch** |
| **v2 — seeded 1,000-item sample** | |
| `sample.py` | seeded sample plus XIVAPI metadata |
| `bulk_fetch.py` | throttled, resumable Universalis pull |
| `dataset.py` | folds the cache into one record per Ware (item + quality) |
| `wtype.py` | ware-type classifier and the Kruskal-Wallis test of whether it is real |
| `report_types.py` | tables A-C |
| `stacks.py` | tables D-G |
| `precision.py` | tables H-J |
| `typed_backtest.py` | per-type policy back-test |
| `index.py` | tables K-M |
| `data/` | cached API responses (~14 MB) |
| | |
| `results_*.txt` | captured output of each stage |

## Standing constraints honoured here

- Universalis limits are per IP: this issues one request at a time with a pause, roughly
  1 req/s against a published ceiling of 25.
- A descriptive `User-Agent` is sent on every request, as the operators ask.
- Reads only. Nothing here writes to any remote service.

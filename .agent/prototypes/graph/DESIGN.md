# The graph — design proposal for review

Ticket: **Design the price history graph and its confidence bands** (#12, `wayfinder:prototype`).
Status: **RESOLVED 2026-08-17.** All six answers in §2 approved point by point; resolution posted as
[comment 5319653714](https://github.com/local-variable/EMM/issues/12#issuecomment-5319653714), the
ticket closed, the map's Decisions-so-far updated. **Rulings on the §6 open questions:** the layer
controls are to be *improved beyond the prototype for better understanding* (a build direction, not a
fixed default set); candles stay an optional overlay; the band stays centred while the estimate trails;
**"Estimate" was added to `CONTEXT.md`** (45 terms on recount); the Days of Supply pane ships off by default; and
**the simulated Strategy replay is DROPPED as a feature** — not useful to the end user — while the
Player's own *recorded* acts remain a layer per the core-feature-set decision (the maintainer confirmed
that reading explicitly). One new requirement on approval: **the graph must pull up in an item overlay
when called upon** — handed to the UI-IA ticket. The text below is kept as written for review; where
it says "proposal" or "open", read the rulings above.

Everything below is a **proposal**. The prototype (`index.html`, open via a local static
server — see `README.md`) is the thing to react to; this file says what it is doing and why,
and lists what is simulated so nothing is mistaken for a real observation.

The brief for this session, in the maintainer's words: clean, readable, toggle-able, "the
works" — the fantasy equivalent of a stock chart, but better, because volume, volatility,
sales and trend need to be visible at once. Three references were supplied: a candlestick
chart with a moving average and a money-flow oscillator; a game auction-house "market
trend" chart with market value / min buyout / quantity lines and period toggles; and an
Undermine-Journal-style item page with a stats table (realm vs region) over a lowest-price
and quantity snapshot chart. What was taken from each, and what was deliberately not, is
in §8.

---

## 1. What is on the screen

One Ware at a time (an Item at a Quality — the sibling Quality overlays on request), one
World, in four stacked panes sharing a time axis, with a stats card beside them:

| pane | shows | why it earns the space |
|---|---|---|
| **Unit Price** (main) | every Sale as a dot sized by Stack · the *dispersion band* (where Sales landed) · the *Estimate* line with its *precision interval* · Listings still on the board, traced back to when they were put up · the board ladder in the right margin · the Player's acts | the founding brief's "price history with confidence bars", made honest |
| **acts lane** (only when there are acts) | Hold spans that have no Unit Price to sit at | keeps the mantra's "do nothing, and why" visible without cluttering the price pane |
| **Volume** | units sold per bucket, per Quality | velocity is what `soldPerDay`, Days of Supply and Slot Yield run on |
| **Spread** | the middle 50% of Sales as a share of the median, over time | the volatility measure — a Bollinger-width analogue that is a percentile, not a standard deviation, because these distributions are heavy-tailed |
| **Days of Supply** (off by default) | units on the board ÷ units sold per day | see §7 — its history cannot be honestly drawn from one Snapshot |

Controls, one row above the chart, scoping everything below: **Window** (7d · 14d · 30d ·
60d · 90d · 180d · All), **Bucket** (Auto · 6h · 1d · 3d · 1w), **Marks** (Sales · Candles ·
Both), **Unit Price axis** (Linear · Log), **Clip outliers**. Above that: the Ware picker,
**NQ · HQ · Both**, and the theme toggle. Every layer is a chip under the title and toggles
independently. Hover gives a crosshair with one tooltip listing every series at that time;
`←`/`→` step buckets; **Table view** renders the same numbers as a table.

The stats card leads with the **Estimate** as a hero number and its severity chip, then the
Sample, Freshness, medians at 7/30/90 days, sold per day, what is on the board, the Min
Listing (World), how much is listed below the Estimate, **Days of Supply** in bold (the
headline number by ruling), the Data Centre average Sale labelled as a Scope you would
travel to, the vendor prices, the Stack, and the ware type with its window and horizon.

## 2. The six questions the ticket asked

### 2.1 The series — Sales, Listings, or both?

**Both, but never as the same kind of mark.** Sales are *observations of what buyers paid*:
dots, one per Sale, sized by Stack, in the Quality's colour. Listings are *what sellers hope
for*: thin horizontal lines from the moment the Listing was put up (`lastReviewTime`) to
now, plus a ladder in the right margin showing the whole board at this instant. A Listing
is never drawn as a dot and never enters the band or the Estimate. The glossary already
separates the two (Sale vs Listing, Snapshot vs History); the chart keeps them visually
separate too.

The trend line is neither of these — it is the **Estimate**, the pricing engine's number,
evaluated once per UTC day on the Sales strictly before that day. It is drawn in the
Quality's colour at full weight so it reads as "same Ware, but computed". It comes from the
same code the pricer uses (`models.estimate` from the #11 prototype), so the chart cannot
disagree with the price.

### 2.2 The band — what does it mean?

The ticket lists three candidates and warns they mislead differently. The proposal is that
**two of them ship, drawn so they cannot be confused, and the third does not exist**:

| band | meaning | drawn as | default |
|---|---|---|---|
| **Where Sales landed** | the middle 50% (p25–p75) and middle 80% (p10–p90) of Sales in a window centred on that moment — a *description of dispersion* | soft fill in the Quality's colour, two nested tones; opacity rises with effective sample size | on |
| **Precision interval** | 80% moving-block bootstrap on the median — *how well the Estimate is pinned down by the Sales EMM has* | two dotted edges either side of the Estimate line and a faint fill; an interval, not a cloud | on |
| a predicted range | where the next Sale will land | **not drawn, and the UI says so** | — |

The legend and the stats card both say, in words, that the interval **is not a forecast**.
This is the direct consequence of the #11 finding: going from under 5 to over 25 effective
observations moved forward error only from 33% to 27%, so an interval measures estimation
error and says almost nothing about the next Sale. A "prediction band" would be a lie the
chart cannot back.

Why both bands and not one: they answer different questions a seller asks. "How spread out
are prices right now?" is the dispersion band (a wide band on a liquid Ware is a fact about
the market). "How sure is EMM of its number?" is the precision interval (narrow on a liquid
Ware, wide during a regime change even with many Sales — the Lamppost between Jul 28 and
Aug 5 is the example). Drawn the same way they would be read as one thing; drawn as fill vs
dotted edges they are not.

### 2.3 Sparse-data honesty

Six mechanisms, all visible in the prototype:

1. **Dots are always drawn.** Three Sales in a week look like three dots; three hundred look
   like a cloud. Nothing smooths them away.
2. **The dispersion band is absent below 5 effective observations** (the #11 gate: the
   geometric mean of rows × distinct sale days × distinct buyers, so three Sales to one buyer
   in one minute cannot support a band). Where the Sample is thin the band's window widens
   automatically up to the ware type's estimation window; if that still fails, the band
   breaks. Its opacity also fades with effective n. Wind-up Feo Ul (Jun 18 → Aug 2) shows the
   gap.
3. **The Estimate line breaks where no Sales rung applies** and is **dashed where
   provisional** (priced from a widened 90/180-day window). It never bridges a gap. This is
   the "the model interpolates, the rendering must not" ruling made literal.
4. **Candles are optional and honest**: a bucket with fewer than 3 Sales is drawn as a tick,
   never a body, so a candle drawn from two Sales cannot look like one drawn from two hundred.
   Dots stay the primary mark; candles are an overlay.
5. **Freshness is on the time axis.** The region since the last Universalis upload is hatched
   and labelled "no upload for …" — nothing has been observed there. Blunt Goblin Longsword
   (26 days stale) shows it in warning colour.
6. **The Sample cap is marked.** Where the corpus truncated a busy Ware's History
   (Quicktongue Materia XI: 3,000 Sales), a dashed line says "History begins here — Sample
   cap". In the product the same marker belongs at the start of whatever EMM holds.

And the stats card carries the #11 severity ladder as a coloured chip and a warning box:
`ok` (from own-World Sales, enough evidence), `caution` (own Sales, window widened),
`warning` (no Sales — priced from asks or the Data Centre), `severe` (vendor price / nothing).
The four cases are all in the prototype: Lamppost, Gold Thumb's Mallet NQ, Blunt Goblin
Longsword, Everseeker's Fishing Rod NQ. **The warning strings are the unapproved copy from
`models.py` and remain a gate.**

### 2.4 HQ vs NQ

**Overlay, selected by a three-way control (NQ · HQ · Both), never a facet.** A Ware is an
Item at a Quality, so the two are separate series with separate Estimates and separate
bands; but the question a seller actually asks — "what is the HQ premium, and is it moving?"
— needs them on one axis. Facets destroy the comparison; a single merged series is the
ambiguity the glossary exists to forbid. Colours are fixed to the entity: NQ is blue, HQ is
orange, in both themes, whatever is toggled. Items that cannot be HQ have the control
disabled. Tsai tou Vounou and Gold Thumb's Mallet are the examples.

### 2.5 Time axis and range

Presets 7d · 14d · 30d · 60d · 90d · 180d · All. **The default opens on the window the
Estimate was priced from** — 30d for most types, 60d for Gear — rounded up to a preset, so
the picture and the number agree; the estimation window is shaded on longer views ("Estimate
window · 30d") so the reader sees which Sales the number came from. Bucket width defaults
to Auto (6h at 7d, 1d at 30d, 3d at 90d, 6d at 180d) and can be forced. Universalis holds
multi-year, second-resolution history, so "All" is bounded only by what EMM has stored; the
corpus here holds 180 days.

### 2.6 Rendering

**ImPlot, via the binding Dalamud already ships — no new dependency.** Verified on the
development machine: `Dalamud.Bindings.ImPlot.dll` and `cimplot.dll` sit in
`addon\Hooks\dev\` at the same commit as the running Dalamud (`15.0.3.2+83042016d0…`); the
native library reports **ImPlot 0.14**. String scan of the binding confirms every primitive
the design needs: `BeginSubplots` + `SetupAxisLinks` (stacked panes on one time axis),
`PlotScatter`, `PlotShaded`, `PlotLine`, `PlotStairs`, `PlotBars`, `PlotErrorBars`,
`Annotation`, `TagX`/`TagY` (the axis value tags), `DragLineX`, `SetupAxisScale` (log,
time), and `GetPlotDrawList` + `PlotToPixels` for what ImPlot has no built-in for: dots with
per-point radius, hollow/filled candles, the ▲▼★‖ act glyphs, the hatch, and the ladder in
the margin.

Licences, from the primary sources: **ImPlot — MIT (Evan Pezent, 2020)**; **cimplot — MIT
(Victor Bombí, 2020)**. Both compatible with the repository's GPL-3.0. Dalamud itself is
AGPL-3.0, which every Dalamud plugin already lives with.

Hand-drawn draw-list rendering for the whole chart was considered and rejected: axes,
zoom/pan, tick formatting, legends and hover are exactly what ImPlot does well, and the
custom marks compose on top of its draw list. The efficiency constraint from #9 (regenerated
on the fly, never 15k points a frame) is met by pre-rolled tiers — the prototype already
buckets and bands once per view change and draws from the result; the plugin does the same
with tiers kept incrementally as Sales arrive, and downsampled dots (or a density mark)
past a per-frame budget.

## 3. What is real and what is simulated

| element | real? |
|---|---|
| Sales, Listings, aggregates, upload times, item metadata | **real** — cached Universalis and XIVAPI data for Cactuar, captured 2026-08-17 (the #11 corpus; nothing re-fetched) |
| the Estimate line, precision interval, effective n, severity | **real model output** — `models.estimate()` from #11, evaluated once per day; historical points restricted to the three own-World Sales rungs (the live book / Data Centre / vendor rungs have no history and would be anachronistic) |
| dispersion band, spread, volume, medians, Days of Supply now | **computed live from the real Sales/Listings** in the page |
| "your Listing" markers | **real** — the maintainer's own Retainers' Listings, reduced to a boolean flag; no names exported |
| ▲ List ▼ Reprice ★ Sold ‖ Hold | **SIMULATED** — Table 7's Strategy for the type (undercut the cheapest recent Sale by one gil, never below *floor* × the trailing median; else Hold with a reason and a review time; a Listing sells at the first later Sale at or above its Unit Price) replayed over the real History. Offered only on single-unit Wares because that fill rule flatters commodities (documented in #11). Labelled "simulated" in the legend and the footer. |
| Days of Supply *history* | **a lower bound only** — see §7 |

Buyer names are hashed, retainer names dropped, nothing exported names a person, a
Character or a Retainer.

## 4. Palette

From the dataviz reference instance, validated with its script rather than by eye:
NQ blue `#2a78d6` / `#3987e5`, HQ orange `#eb6834` / `#d95926`, Player acts aqua `#1baf7a`
/ `#199e70` (light / dark). All-pairs check passes in both modes (worst CVD ΔE 9.2 light /
9.4 dark, normal-vision 24.0 / 20.9). Violet was tried for the acts first and **failed the
dark-mode check against blue** (protan ΔE 1.9) — the validator caught what looked fine.
Aqua sits at 2.74:1 on the light surface, so the relief rule applies: acts always carry a
shape (▲▼★‖) and a label, never colour alone. Status colours (`ok`/`caution`/`warning`/
`severe`) are the reference status set and are never used for a series. Dark is the default
because the in-game UI is dark; light is a toggle.

## 5. What was learned building it — for the statistician

- **The Estimate is a trailing statistic and the chart shows its lag.** On the Lamppost the
  market moved 80k → 15k (June) → 70k (mid-July) → 40k (August); the 30-day trailing median
  crosses each regime change roughly half a window late, and its precision interval balloons
  during the transition (Jul 28 – Aug 5) even with 27 Sales in the window. This is not a
  rendering artefact — it is what the #11 estimator does, made visible. It sharpens the
  "AR/MA over History" clause from #10: a level-shift-aware component (or a shorter window
  the daily refit can choose) is where the model would need to go, and the graph is the
  place that decision would be seen. Flagged, not decided.
- **A percentile band from few points can be narrow by chance** — the #11 near-duplicate
  finding. The prototype guards it two ways: the band needs 5 *effective* observations, and
  it fades with effective n. Whether both are needed once the precision interval is also on
  screen is a judgement call for review.
- **The DC gut check does not belong on the chart.** It sits in the stats card as a Scope
  the Player would travel to, per the reversion test that retracted its predictive value.

## 6. Open questions for the maintainer

1. **Default layers.** Currently on: Sales, both bands, Estimate, Listings, ladder, acts,
   volume, spread. Is that the right first screen, or should the precision interval start
   off and be a "show me the uncertainty" toggle?
2. **Candles.** Kept as an optional overlay with the ≥3-Sales rule. Drop them entirely, or
   promote to a default for the stock-chart-literate?
3. **The band's window.** It is centred (looks at Sales either side of a moment) because it
   is descriptive; the Estimate is trailing because it is a price. Two different windows on
   one chart — acceptable once labelled, or should the band trail too?
4. **"Estimate"** is not a glossary term. `Reference Price` is defined as an *observed*
   number, which this is not. Propose adding **Estimate** to `CONTEXT.md` (the model's
   fitted number for a Ware, always carrying its Sample and severity) — or name it something
   else. Not added; a gate.
5. **Days of Supply pane** — keep off-by-default with the lower-bound caveat, or drop until
   EMM has its own Snapshot history?
6. **The replay layer** — is a Strategy replayed over History useful as a permanent feature
   ("what would this Strategy have done here?"), or only as a prototype device? It is
   effectively the backtest replay UI that #9 deferred below the line.

## 7. Known limitations of the prototype

- **Days of Supply history is a lower bound.** Only Listings still on the board can be
  traced back; those that sold since are invisible, so the historic depth is understated
  and the pane shows a spurious rise near the present. EMM will store its own Snapshots on
  every refresh, so this becomes real from install day. Off by default for that reason.
- **Own acts are simulated.** EMM does not exist yet, so there is no Proposal Ledger to draw
  from. The replay is grounded in real History and the #11 Strategy, but it is not the
  Player's record.
- **One World.** Cactuar only, per the #11 scope. The design does not depend on the World.
- **Web canvas, not ImPlot.** The prototype answers "how should it look and behave"; the
  ImPlot build is #14/build-ticket work. Every mark used has a named ImPlot route (§2.6).

## 8. What was taken from the references, and what was not

- **Candlestick + moving average + oscillator** → the Estimate line is the moving-average
  role (a robust trailing median with an honest interval rather than a mean); candles are an
  optional overlay with the thin-bucket rule; the oscillator role is filled by **Spread**
  (volatility) and **Volume**, both computable from Sales — a money-flow oscillator needs
  Listing history EMM will only have from install day, so it is not faked.
- **Market Value / Min Buyout / Quantity with period toggles** → the period toggles are
  adopted as-is; "quantity" became the volume pane plus units-on-the-board; "min buyout"
  became the Min Listing tag and the ladder rather than a line, because a line implies a
  history the Snapshot does not have. **The reference's dual axis was not adopted** — price
  and quantity on two y-scales invents a correlation; here they are separate panes on one
  time axis.
- **Undermine-Journal item page** → the stats card is its descendant: current, median, mean
  → Estimate, medians at three windows, Min Listing; "realm vs region" → World vs Data
  Centre, with the Data Centre explicitly labelled a Scope you would travel to; "available"
  → on the board, listed below the Estimate, Days of Supply. Its hourly lowest-price
  snapshot series is exactly what EMM's own stored Snapshots will produce, so the pane exists
  and is off until that history is real.

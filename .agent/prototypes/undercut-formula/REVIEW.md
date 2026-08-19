# Undercut formula prototype — for statistical review (v4, two worlds)

Ticket: **Design the undercut protection formula** (#11, `wayfinder:prototype`, claimed).
Status: **nothing posted to the tracker, nothing closed.** Held for review.

Scope: **Aether only — Cactuar and Adamantoise.** No other data centre is touched.

| version | scope | what changed |
|---|---|---|
| v1 | 7 Wares from your own retainers | first pass; two biases found in it, documented below |
| v2 | seeded sample, 1,000 items → 1,479 Wares | ware types introduced |
| v3 | whole catalogue, 16,843 items → 25,242 Wares | effective-n, buyer-budget stacks, opportunity-cost scoring, model-suggested presets |
| **v4** | **+ Adamantoise, a second Aether world** | **provisional estimates always produced; n_eff-only gate; daily recalibration machinery; per-Ware opportunity rate found degenerate; cross-world transfer test** |

Where versions disagree, the later one wins. v3 tracked v2 closely on the numbers that
carried over — 31.6% of Wares clear n≥5 against v2's 31.8% — which is a reasonable
check that the seeded sample was not misleading.

Re-run instructions and a file map are in [`README.md`](README.md).

---

## 1. What changed because of your answers

| your ruling | what it became |
|---|---|
| all three weight into effective-n | `models.effective_n()` — weighted geometric mean of rows × distinct sale days × distinct buyers, bounded above by the row count |
| credit the held slot | back-test scores every policy over the same elapsed time, held slot-days credited at an opportunity rate (§4.8 — the choice of rate turned out to decide the answer) |
| stack must respond to unit price | stack chosen against a per-type target **transaction** value, so quantity falls automatically as unit price rises (§4.3) |
| model should populate the defaults | Table 7, with runner-up and margin so a close call is visible |
| run the DC test | §4.6 — as a partial rank correlation, and it mostly does not survive |
| Materia is its own type | kept, and §4.3 shows it was the right call |
| **a provisional estimate is always produced** | six-rung ladder in `models.estimate()`, which never returns nothing; §4.7 measures what each rung costs |
| **gate on n_eff alone** | the precision interval is computed and reported but no longer gates; it feeds the graph band and the warning |
| **formulas refit daily, never constant** | `recalibrate.calibrate(as_of)`; §4.9 measures how far they actually drift |
| **opportunity rate per-Ware** | implemented, and found **degenerate** — §4.8, with the resolution |
| **add Adamantoise** | §4.10, the transfer test |
| **do not re-download the catalogue** | caches are complete and pinned; `fetch_all.py` is world-parameterised so a new world never touches them |

---

## 2. Coverage — the number that should drive scoping

25,242 Wares (items that can be HQ contribute two).

| ware type | Wares | any sale | n≥5/30d | n_eff≥5 | listed now | no data |
|---|---:|---:|---:|---:|---:|---:|
| Materia | 238 | 225 | 172 | 170 | 214 | 13 |
| Material | 3,440 | 2,952 | 2,116 | 1,867 | 2,697 | 488 |
| Consumable | 3,404 | 2,342 | 936 | 748 | 1,800 | 1,062 |
| Gear | 14,429 | 10,995 | 2,925 | 2,171 | 6,019 | 3,434 |
| Furnishing | 2,490 | 2,235 | 1,016 | 872 | 1,824 | 255 |
| Collectible | 1,115 | 1,112 | 772 | 692 | 1,001 | 3 |
| Miscellany | 126 | 107 | 52 | 47 | 84 | 19 |
| **all** | **25,242** | **19,968** | **7,989** | **6,567** | **13,639** | **5,274** |

**31.6% of Wares clear n≥5 in 30 days; 26.0% clear n_eff≥5; 21% had no sale at all in
180 days.** Gear is 20% and 24% — and Gear is 57% of the catalogue by Ware count.

(Counts move by one or two between runs because every window is measured back from the
moment of running. Nothing here is sensitive to that, but it is why two runs of the same
script do not print identical integers.)

EMM will decline to price roughly three quarters of the marketable universe on any given
day. That is not a defect of the estimator, it is what the market is. It argues that
"decline, with a reason" must be a designed, first-class outcome rather than an error
path — which the Hold ruling already anticipated.

---

## 3. Method, and every bias I know about

### 3.1 Estimators

Rank-based throughout, on logs where a scale-free comparison is wanted, because these
distributions are heavy-tailed.

- **Type separation**: Kruskal–Wallis with tie correction → epsilon-squared.
- **Stack direction**: Spearman across Wares, plus a **paired within-Ware sign test** with
  an exact binomial p, because the unpaired form confounds bucket with Ware.
- **Precision**: **moving-block bootstrap**, block length `n^(1/3)`, 200 replicates, 80%
  interval. Block rather than i.i.d. because inter-arrival CV sits well above 1 on every
  type — sales clump. Measured cost of ignoring that: the block interval is 1.05–1.34×
  wider at the median, up to 2.2–3.1× at p90.
- **DC test**: partial Spearman, controlling for the World's own deviation from its
  180-day level.

### 3.2 Biases found in earlier versions and corrected

1. **Selection bias on aggressive policies** (v1) — they priced off a trailing 24h window,
   so on a slow Ware they were only scored on days following a sale. Fixed with a common
   decision-point set.
2. **Fill rule false for commodities** (v1) — "sells at the first later sale ≥ P" assumes
   the observed sale price is the cheapest listing, true only for unit-traded Wares. Fixed
   by pinning each stack-traded Ware to its **modal stack bucket**, so a 99-stack is
   compared against other 99-stacks.
3. **Hold-rate selection effect** (v2, the one you agreed needed fixing) — a holding policy
   was scored only on points it chose to act on. Fixed: held slot-days are credited at the
   type's opportunity rate, taken from the best policy that *never* holds, so the
   alternative is not defined in terms of the behaviour under test.

### 3.3 Biases still standing

- Universalis history is a union of overlapping **20-sale windows, not a census**. Missing
  sales inflate every days-to-sell figure, worst on the busiest Wares.
- EMM's own listing would have moved the book. Here it does not.
- Ties and queue position among equal prices are ignored.
- The opportunity rate is a **median across Wares of the type**, so a Ware whose slot could
  earn far more than its type's median is under-credited for holding, and vice versa.
- One World, one 30-day forward window for the DC test. Single observation of a
  relationship, not an estimate of its stability.

---

## 4. The headline results

### 4.1 The type split is real for timing and quantity, not for price dispersion

| metric | ε² | reading |
|---|---:|---|
| units/sale | 0.766 | large |
| unit price | 0.318 | large |
| burstiness CV | 0.272 | large |
| sales/day | 0.207 | large |
| IQR/median | 0.050 | small |
| days of supply | 0.028 | small |
| 30d/180d drift | 0.006 | negligible |

Unchanged from v2 on ten times the data. **Type sets structure — windows, horizons, stack
policy, confidence bars. The Ware sets level and width.** A per-type price band would be
unjustified, and a per-type *drift* assumption would be close to meaningless.

### 4.2 Effective-n works, but sample size predicts error far less than one would hope

Estimate on days 60→30 back, check against days 30→0. 8,305 Wares with both windows.

| measure | Spearman with \|error\| |
|---|---:|
| log raw n | −0.032 |
| **log n_eff** | **−0.055** |

n_eff is the better predictor, as you expected from weighting all three. But look at the
levels:

| n_eff band | median \|error\| | Wares |
|---|---:|---:|
| < 5 | 33.3% | 1,848 |
| 5–10 | 33.5% | 2,459 |
| 10–25 | 32.1% | 2,247 |
| 25+ | 26.6% | 1,751 |

**Going from under 5 observations to over 25 buys a drop from 33% to 27% median error.**
The market moves for reasons no amount of sampling anticipates. Two consequences I'd draw,
and want checked: a precision interval is honest about *estimation* error but says almost
nothing about *forward* error, so the graph band must not be read as a forecast; and a
confidence rule should be understood as protecting against embarrassment on thin data, not
as buying accuracy.

### 4.3 Buyer budget — and Materia is exactly the exception you named

ρ across Wares, median unit price against median stack size sold:

| type | ρ(price, stack) | median transaction value | p90 |
|---|---:|---:|---:|
| Gear | **−0.48** | 29,045 | 91,575 |
| Material | **−0.39** | 4,290 | 87,000 |
| Consumable | −0.27 | 6,194 | 127,296 |
| Furnishing | −0.15 | 13,735 | 90,993 |
| Collectible | −0.04 | 18,000 | 575,000 |
| **Materia** | **+0.02** | 1,800 | 32,900 |

Materia is the only type with **no budget effect whatsoever** — buyers take 99-stacks of it
regardless of unit price. Everywhere else stack size falls as unit price rises, which is
what your 20k-a-unit objection predicted.

So the stack rule is not a constant: **quantity ≈ target transaction value ÷ unit price**,
clamped to the item's stack size, with the target fitted per type. A 20,000 gil/unit ware
is offered in small stacks and a 50 gil/unit ware in large ones, and nothing is hardcoded.

Sell-through still shows the effect you originally described — Materia 99-stacks are 8.0%
of the book and 1.1% of sales, a clear ratio of **0.14**, the worst in the catalogue. Both
things are true at once: full stacks of Materia sit, *and* Materia buyers are the ones
least deterred by stack value. The reconciliation is that Materia's unit price is low
enough (median transaction 1,800 gil) that budget never binds — what deters the buyer is
being made to take 99 when they wanted 5.

### 4.4 With the selection effect removed, holding still wins — on every type

This was the weakest joint in v2 and the correction strengthened it rather than dissolving
it. Best policy per type on the total metric, with held slot-days credited:

| type | Wares | horizon | best | rel | hold | runner-up | naive undercut |
|---|---:|---:|---|---:|---:|---|---:|
| Materia | 171 | 3d | floor 95% | 0.97 | 69% | floor 90% (+0.08) | 0.68 |
| Material | 1,961 | 9d | floor 95% | 0.92 | 73% | floor 90% (+0.08) | 0.63 |
| Consumable | 714 | 39d | floor 70% | 0.73 | 61% | trimmed mean (+0.02) | 0.43 |
| Gear | 1,195 | 60d | floor 80% | 0.79 | 63% | floor 70% (+0.02) | 0.57 |
| Furnishing | 1,259 | 27d | floor 95% | 0.73 | 80% | floor 90% (+0.00) | 0.60 |
| Collectible | 856 | 19d | floor 95% | 0.83 | 71% | floor 90% (+0.05) | 0.76 |
| Miscellany | 53 | 20d | floor 95% | 0.99 | 46% | floor 70% (+0.07) | 0.77 |

Two caveats I want on the record rather than buried. **Consumable is a coin-toss** — floor
70% at 0.73 against trimmed mean at 0.71, and on the per-unit metric trimmed mean wins
clearly (0.98 vs 0.78). **Furnishing's top two are tied** at 0.73. I would not defend
either pick as more than "start here".

Per-type horizons span **3 days (Materia) to 60 (Gear)**, a 20× range, and opportunity
rates span 130 gil/slot-day (Materia) to 6,873 (Gear). A single global setting for either
would be wrong nearly everywhere.

The naive undercut policies are consistently mid-table on the total metric and bottom on
per-unit (0.29–0.64). On Consumables `undercut −5%` realises **0.29** of the best policy
per unit sold.

### 4.5 Precision bar costs

Share of **all** Wares of the type that clear each bar, on top of the n_eff ≥ 5 floor:

| type | ±5% | ±10% | ±15% | ±25% | ±50% |
|---|---:|---:|---:|---:|---:|
| Materia | 20.6% | 33.6% | 46.2% | 58.8% | 67.2% |
| Collectible | 18.9% | 29.4% | 38.0% | 46.6% | 56.0% |
| Furnishing | 10.5% | 15.7% | 20.0% | 25.7% | 31.6% |
| Material | 8.1% | 16.3% | 24.3% | 35.2% | 46.9% |
| Consumable | 4.6% | 7.2% | 9.4% | 13.0% | 18.0% |
| Gear | 4.5% | 6.8% | 8.4% | 10.7% | 13.3% |

A single global bar has wildly different coverage by type, which is the argument for
per-type bars. ±15% is what Table 7 proposes; on Gear that admits 8.4% of Wares, and I
think that is correct rather than alarming — see §4.2 on what precision does and does not
buy.

### 4.6 The DC as a gut check — mostly does not survive its own control

You asked for this test and it produced a result I have to walk back part of the way.

The question: does the Aether gap predict what Cactuar does next, **after** Cactuar's own
deviation from its 180-day level is accounted for? Partial Spearman, 764 items backfilled
with Aether history.

The first run said yes, clearly. Then I noticed the design flaw: the same 30-day median
sits in the denominator of both predictors and the numerator of the outcome, so ordinary
noise in that single estimate drags the correlation negative on its own. Classic division
bias. The fix is a split-half baseline — build the predictors from days 30→15 back and
measure the outcome against days 15→0, so no estimate is shared.

| ware type | shared baseline (biased) | **split-half (trustworthy)** |
|---|---:|---:|
| Materia | −0.461 | **−0.301** |
| Consumable | −0.314 | **+0.040** |
| Gear | −0.114 | **+0.093** |
| Furnishing | −0.056 | **+0.043** |
| Collectible | −0.178 | **−0.071** |
| Miscellany (n=25) | −0.574 | −0.199 |
| **ALL** | **−0.185** | **−0.007** |

**Most of the apparent signal was the artefact.** Overall the DC gap adds essentially
nothing once the bias is removed. It survives only on **Materia** (−0.301), the
fastest-trading type, and marginally on Collectible.

What I think this means, and want your view on:

- The **reachability** argument for the DC ruling stands untouched and is measured
  independently — the DC's cheapest listing is on Cactuar only 12–29% of the time and the
  local cheapest sits at 1.8–2.7× it, so DC `minListing` is not a price a local seller can
  be undercut by, whatever its predictive value. That was never a prediction claim.
- The **predictive** justification for a DC gut check does **not** hold in general. On this
  evidence I would ship it for Materia and leave it off elsewhere, or leave it off
  entirely until a second world and a second window are measured.
- Cactuar's own deviation from its 180-day level *does* survive as a predictor on Gear
  (−0.279), Collectible (−0.376) and Materia (−0.282). The mean-reversion story is real;
  it just does not need the data centre to tell it.

Both panels are printed, deliberately, so the size of the artefact is visible rather than
quietly corrected.

---

### 4.7 EMM always produces an estimate — and what the weaker ones cost

Your rule: the estimate is never withheld; where the normal machinery cannot produce a
valid one, it is produced anyway and marked with a warning. `models.estimate()` now never
returns `None`. It walks a six-rung ladder, each rung weaker than the last and each saying
so in words the warning box can use:

Estimate made as of 30 days ago from data strictly before it, scored against what the
following 30 days actually did:

| basis | Wares | share | median error | p90 error |
|---|---:|---:|---:|---:|
| 30-day history *(confident)* | 6,455 | 62.4% | **31.3%** | 141% |
| 90-day history | 3,131 | 30.3% | 39.4% | 179% |
| 180-day history | 628 | 6.1% | 50.0% | 300% |
| live listings (asks, not sales) | 79 | 0.8% | 50.5% | **2020%** |
| data centre | 31 | 0.3% | 50.0% | 200% |
| vendor price | 18 | 0.2% | **83.3%** | 442% |
| **all provisional** | **3,887** | **37.6%** | **41.2%** | |

**The yellow box can be honest.** A provisional estimate is meaningfully worse — 41.2%
against 31.3% median error — but it is not nonsense, which is what makes the rule
defensible rather than merely kind.

**Severity is graded, not flat** (agreed: a provisional estimate has to be grounded in
reality). Error differs by an order of magnitude across the provisional rungs, so one
warning for all of them would mislead:

| severity | rungs | what it means |
|---|---|---|
| `ok` | 30-day own sales | enough independent evidence |
| `caution` | 90/180-day own sales | still real sales, older or thinner |
| `warning` | live listings, data centre | not this world's sales — asks, or another market |
| `severe` | vendor price, nothing | **not a market price at all** |

The data-centre rung is explicitly a **cold-start seed**: it stands in only while the home
world is silent and is replaced the moment that world has sales of its own. Its predictive
value beyond the world's own history did not survive its control (§4.6), so it is a
starting point, never an ongoing signal.

Caveat: only Wares that subsequently traded can be scored at all, so this table is
conditioned on later activity and is, if anything, generous.

The warning strings live in `models.py` and are **proposals** — user-facing copy is a gate.

### 4.8 Per-Ware opportunity rate is degenerate, and the fix is the allocator

You ruled the opportunity rate should be per-Ware. Implemented literally, it breaks the
comparison, and I think the reason is interesting enough to be worth the detour.

Crediting a held slot at *what this Ware would earn if listed* refunds the Hold exactly
what it gave up. Holding then costs nothing by construction, and the metric goes flat:

| scoring | Materia spread across policies | Material spread |
|---|---:|---:|
| portfolio rate (a typical other Ware) | **0.50** | **0.45** |
| own rate (this Ware) | 0.36 | 0.28 |

Under own-rate scoring the four floor levels compress to 0.93–0.95 on Materia and
0.89–0.90 on Material — indistinguishable, and the ordering even inverts on noise. Under
the portfolio rate they separate cleanly, 0.74 to 0.97.

The economics: when EMM holds, the slot's alternative use is **another ware from the
player's stock**, not the ware being declined. So the right rate is neither per-type nor
per-Ware — it is **per-player, per-slot: the rate the player's own sell space can achieve**,
which is precisely what the Slot Yield allocator ranks. That *does* make the back-test and
the allocator the same machine, which was the point of your ruling; it just lands one level
up from the Ware. Until a portfolio exists, the per-type median is the closest available
proxy and is what Table 7 uses. Both columns are printed so the difference stays visible.

### 4.9 The parameters do drift, but slowly — daily is cheap insurance, not urgency

Your rule that the formulas refit daily is implemented as `recalibrate.calibrate(as_of)`,
computing every per-type parameter from data strictly before its own date — the shape the
daily job takes. Run at 12 weekly points on a 30-day window:

| parameter | worst drift across 12 weeks | typical week-to-week move |
|---|---|---:|
| stack target | Materia 1,494 → 2,400 gil (43% range) | 1–12% |
| horizon | Furnishing 16 → 26 days (50% range) | 0–13% |
| median unit price | Furnishing 26% range, Materia 23% | 1–7% |

**The honest reading is more nuanced than "they never remain constant".** They don't — but
the drift is *slow and cumulative* rather than jumpy. A constant set at install would be
badly wrong within a quarter; it would not be wrong by Tuesday. That argues for daily as
cheap insurance and for the machinery existing from the start (which is your point), rather
than for daily being urgent in itself.

One limitation the drift table exposed: **both horizon clamps bind at the extremes.**
Materia pins at the 3-day floor at every refit and Gear at the 60-day ceiling at every
refit, so Gear's true horizon may be longer than the model can currently express, and I
cannot tell from this data how much longer.

### 4.10 Cactuar against Adamantoise — structure transfers, prices do not

Both worlds on Aether, both full catalogues, 25,242 Wares each.

**Type-level parameters agree, and closely:**

| parameter | Cactuar | Adamantoise | ratio |
|---|---:|---:|---:|
| Materia horizon | 3d | 3d | 1.00 |
| Material horizon | 9d | 9d | 1.00 |
| Gear horizon | 60d | 60d | 1.00 |
| Consumable horizon | 39d | 45d | 1.15 |
| Materia stack target | 2,000 | 1,999 | 1.00 |
| Material stack target | 4,950 | 4,990 | 1.01 |
| Gear stack target | 29,875 | 19,998 | 0.67 |
| Material coverage (n_eff≥5) | 54.3% | 54.9% | 1.01 |
| Gear coverage | 15.0% | 17.4% | 1.16 |

Every real type agrees within about 20% on horizon, stack target and coverage. The only
sharp disagreement is Miscellany (126 Wares — too small to mean anything) and Gear's stack
target, which rests on 86 stackable oddities.

**Individual Ware prices do not transfer at all:**

| type | p10 | median | p90 | share differing >25% |
|---|---:|---:|---:|---:|
| Materia | 0.78 | 1.02 | 1.64 | 29% |
| Material | 0.53 | 1.00 | 2.04 | 51% |
| Consumable | 0.31 | 0.98 | 2.51 | 67% |
| Gear | 0.45 | 1.00 | 2.44 | 65% |
| **all (10,487 Wares)** | **0.46** | **1.00** | **2.22** | **59%** |

The median ratio is **1.00** — neither world is systematically dearer, which is why the
type-level figures agree. But **59% of individual Wares differ by more than 25%**, with a
p10/p90 range of roughly 0.46 to 2.22.

**This is the same conclusion §4.1 reached from ε², arrived at independently:** type sets
structure, the Ware sets level. Now it has a shipping consequence. **The type-level
parameters can ship as defaults**; **no per-Ware price can ever ship** — it has to be
fitted on the player's own world, which is what the daily recalibration is for.

**And the recommended policy itself mostly does not transfer.** Refitting the whole model
on Adamantoise:

| type | Cactuar pick | Cactuar margin | Adamantoise pick | replicated? |
|---|---|---:|---|---|
| Materia | floor 95% | +0.08 | floor 95% | **yes** |
| Material | floor 95% | +0.08 | floor 95% | **yes** |
| Furnishing | floor 95% | +0.00 ⚠ | floor 95% | yes — but on a tied margin |
| Gear | floor 80% | +0.02 ⚠ | floor 70% | same family, different level |
| Consumable | floor 70% | +0.02 ⚠ | median | **no** |
| Collectible | floor 95% | +0.05 | undercut −5% | **no** |
| Miscellany | floor 95% | +0.07 | q25 | no (53 Wares — ignore) |

Only the two picks with the largest margins survived. **I would not read the ⚠ flag as
having predicted this** — Furnishing replicated on a tied margin, which is luck, and
Collectible failed at +0.05, which the flag did not catch. The defensible line is simpler:
**below about +0.08 the pick is not identified by one world's data**, whatever the flag says.

The consequence is real and cuts against shipping Table 7 wholesale: **the policy choice is
itself largely world-specific**, not just its parameters. Only Materia and Material — the
two fastest-trading types — have a pick that survives a second world. That is an argument
for the daily per-world refit doing the choosing, with Table 7 as a cold-start seed rather
than a setting.

Two worlds on one data centre is enough to detect disagreement and nowhere near enough to
establish agreement. Read the matches as "no evidence against", not as "transfers"; a third
world on another DC could break any of them.

---

## 5. Model-suggested starting defaults

Emitted by the model, per your ruling that it should populate these rather than leave them
blank. **Suggestions for approval, not settled defaults** — user-facing behaviour is a gate.

| ware type | policy | window | horizon | n_eff gate | stack rule | runner-up |
|---|---|---:|---:|---:|---|---|
| Materia | floor 95% | 30d | 3d | 5 | qty ≈ 1,800 gil ÷ unit price | floor 90% (+0.08) |
| Material | floor 95% | 30d | 9d | 5 | qty ≈ 4,290 gil ÷ unit price | floor 90% (+0.08) |
| Consumable | floor 70% | 39d | 39d | 5 | qty ≈ 6,195 gil ÷ unit price | trimmed mean (+0.02) ⚠ |
| Gear | floor 80% | 60d | 60d | 5 | single unit (1% stackable) | floor 70% (+0.02) ⚠ |
| Furnishing | floor 95% | 30d | 27d | 5 | single unit (21% stackable) | floor 90% (+0.00) ⚠ |
| Collectible | floor 95% | 30d | 19d | 5 | single unit (97% of sales qty 1) | floor 90% (+0.05) |
| Miscellany | floor 95% | 30d | 20d | 5 | single unit | floor 70% (+0.07) |

⚠ = too close to call from this data. Furnishing's top two are **tied**.

**Read this table as a cold-start seed, not as settings.** §4.10 refit the whole model on
Adamantoise and only **Materia and Material** — the two picks with margins of +0.08 — chose
the same policy there. Below roughly +0.08 the pick is not identified by one world's data.
The daily refit should be doing this choosing on the player's own world; this table is what
EMM starts from on day one, before it has any of its own evidence.

**Act/decline gates on n_eff alone**, per your ruling — the precision interval is computed
and reported but does not gate. It feeds the graph band and the warning box. Every Ware
still receives an estimate either way (§4.7).

Every value in this table is **fitted, not constant**, and is refitted by
`recalibrate.calibrate()` on the daily cycle (§4.9).

The stack rule needed **two** guards, both found by inspection rather than by any test.
Nominal stackability alone puts a stack rule on Collectibles (stackable, but 97% of sales
are single units); observed behaviour alone puts one on Gear (whose stack sales come from
86 oddities out of 14,429 Wares). Both tests must pass.

---

## 6. Settled

Everything raised for review has now been ruled on. Recorded here so a later session does
not reopen it:

| question | ruling |
|---|---|
| effective-n composition | all three components weight in; geometric mean, bounded by row count |
| act/decline gate | **n_eff alone**; the precision interval feeds the graph band and the warning, and does not gate |
| withholding an estimate | **never** — a provisional estimate is always produced, marked, and graded by severity |
| grounding the provisional estimate | severity is graded `ok` / `caution` / `warning` / `severe`; the vendor rung is `severe` and says plainly it is not a market price |
| opportunity rate | **portfolio level** — the slot's alternative use is other stock, which is the Slot Yield allocator; per-Ware is degenerate (§4.8) |
| stack size | target **transaction** value ÷ unit price, fitted per type; Materia exempt from the budget effect |
| Gear horizon ceiling | **60 days stands** — a product decision about how long EMM will wait, not a limitation to fix |
| the data centre | **cold-start seed only** — stands in while the home world is silent, replaced by home-world data, never an ongoing signal |
| Table 7 | **cold-start seed, not settings** — the daily refit chooses on the player's own world |
| recalibration | daily, from the day's pulled data; parameters are never constants |
| measurement scope | Aether only — Cactuar and Adamantoise; do not re-download the catalogue |

**Still a gate, and deliberately not assumed:** the warning strings in `models.py` and
Table 7's contents are user-facing copy and behaviour. They are proposals until approved
individually. So is closing #11.

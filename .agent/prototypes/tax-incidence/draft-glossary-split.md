# DRAFT — glossary split for the two levies

**Status: DRAFT, HELD. Nothing in `CONTEXT.md` has been touched.** The vocabulary is fixed and
binding, so this needs sign-off before it is applied. Naming is user-facing copy and is gated
separately from the structure.

Arising from [#19](https://github.com/local-variable/EMM/issues/19). Full reasoning in
`docs/research/tax-incidence.md`.

---

## Why the current entry cannot stand

```
**Tax**:
The levy on a marketboard transaction, at the rate of the city the selling Retainer is
stationed in. It varies (3–5%) and is published in game by the Retainer Vocate, so EMM
reads the live rate rather than assuming one.
```

Three problems, in increasing order of severity:

1. **"3–5%" is wrong** — the observed range is **0–5%**. `0` also doubles as the API's no-data
   sentinel, which the entry cannot express.
2. **"at the rate of the city the selling Retainer is stationed in" is only half true.** It holds
   for the seller's levy. The buyer's is flat, everywhere, and does not vary by city at all.
3. **"The levy" — singular — is the real problem.** There are two, with different rates, different
   directions and different payers. Every money term hangs off this one word, so a single term
   quietly makes the two arithmetics interchangeable. The glossary exists so that errors read
   wrong in a sentence; *"Net Proceeds is after Tax"* currently reads fine while being ambiguous
   about which levy, which is exactly the failure mode this vocabulary was adopted to prevent.

---

## Naming — three candidates, one recommended

I generated three and am recommending the first rather than presenting it as settled.

| | Buyer side | Seller side | Trade-off |
|---|---|---|---|
| **A — recommended** | **Buyer Fee** | **Seller Tax** | Names the payer in the term, so no sentence can be ambiguous. Slightly more verbose than the game's own words. |
| B | **Fee** | **Tax** | Mirrors the game's wording exactly (Addon #943 *tax*, #1963 *fee*). But bare "Fee" is vague in isolation, and "Tax" keeps its current overloaded meaning in every doc already written. |
| C | **Purchase Fee** | **Sale Tax** | Names the event rather than the party. Reads well, but "Sale Tax" invites confusion with real-world sales tax, which is buyer-side — the exact inversion we are trying to prevent. |

**Recommending A.** The whole point of the cluster is that a wrong figure should produce a sentence
that reads wrong, and only A guarantees that.

---

## Proposed replacement — Money cluster

Replacing the single `**Tax**` entry with two:

```
**Buyer Fee**:
The levy a buyer pays on top of a Listing's asking price. Flat, and the same in every city —
it does not vary with where the selling Retainer is stationed. The game calls it a fee, and
transmits it per Listing, so EMM reads the figure rather than computing one.
_Avoid_: tax, buyer tax, GST, sales tax

**Seller Tax**:
The levy deducted from what a seller banks, at the rate of the city the selling Retainer is
stationed in. It varies (0–5%), expires — the game publishes it with a validity window — and
is read at list time or from the Retainer Vocate, so EMM reads the live rate rather than
assuming one. A rate of zero is indistinguishable from an unknown rate in aggregator data and
must never be treated as one.
_Avoid_: tax, fee, commission, market tax
```

### Consequential edits to four existing entries

Each is a minimal substitution; the shape of every definition is preserved.

| Term | Now | Proposed |
|---|---|---|
| **Buyer Cost** | "What a buyer actually pays for a Listing, **after Tax**." | "…**after the Buyer Fee**." |
| **Net Proceeds** | "What a seller actually banks from a Sale, **after Tax**." | "…**after Seller Tax**." |
| **Cost Basis** | "What the Player paid to acquire a unit of a Ware." | "…to acquire a unit of a Ware, **including the Buyer Fee** — the game transmits it with the purchase, so it is never estimated." |
| **Minimum Margin** | "Measured on Net Proceeds, so **Tax sits inside** the margin, not outside it." | "…so **Seller Tax sits inside** the margin, not outside it." |

### One clarifying sentence for Profit

**Profit** needs no structural change — but the two-levy finding makes it *more* correct than it
was, and that is worth saying out loud, because it is the property that makes the split safe:

```
**Profit**:
Net Proceeds minus Cost Basis. Reserved for exactly this — a figure that ignores either levy
or Cost Basis is never called Profit. Both levies are already inside it: Seller Tax through
Net Proceeds, Buyer Fee through Cost Basis. A flip therefore nets both without any special
case.
_Avoid_: margin, gain, earnings
```

---

## What this changes elsewhere

- **Term count 45 → 46.** The map's Notes state the count and would need the same edit.
- **No UI strings are affected yet** — nothing shipped displays either term. The scaffold's copy
  was cut back to section names only, so this lands before anything user-facing exists. Good
  timing: applying it later would be a rename across the UI.
- **The expression engine gains two Value Sources, not one** (#9 specified a live `taxRate`);
  they should be named to match whatever is chosen here.
- **Docs already written keep the old single term.** `docs/research/` notes #3, #4, #6 and #16 all
  say "Tax" in the old sense. They are dated research records rather than living specs, so the
  cheapest correct move is to leave them and let this note carry the correction — but that is a
  call to make deliberately rather than by default.

---

## Not proposed

- **No change to Floor or Ceiling.** Both are Unit Price limits and neither mentions the levy.
- **No new term for the mannequin exemption.** It is a condition on the Seller Tax, not a
  separate concept, and nothing in v0.1 sells from mannequins.
- **No term for the rate's validity window.** `Freshness` already covers "how old is this
  reading", and adding a second staleness concept would dilute it.

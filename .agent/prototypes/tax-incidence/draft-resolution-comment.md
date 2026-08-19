## Resolution

**There are two levies, not one.** That single fact dissolves the contradiction this ticket was opened on: both bodies of evidence were correct, about different charges.

| | **Buyer fee** | **Seller tax** |
|---|---|---|
| Rate | ~5%, flat everywhere | 0% / 3% / 5%, per city, **expiring** |
| Direction | **added on top** of the asking price | **deducted** from proceeds |
| The game's own word | *fee* | *tax* |
| Surfaces as | `MarketBoardListing.TotalTax`, `IMarketBoardPurchaseHandler.TotalTax` | `IMarketTaxRates`, `/api/v2/tax-rates`, `AddonRetainerSell.Tax` |

Worked from two directions that met in the middle — an empirical pass over the cached market corpus (**210,746 listing rows, 31,189 sale rows, two Worlds**) plus 46 live rate requests and a symbol scan of the installed Dalamud assemblies, and a source pass over FFXIVClientStructs, Dalamud, Universalis, Allagan Market and the game's own Excel strings. The two halves confirmed each other independently.

---

### 1. Who bears the levy — **both**, by two different charges

**Confirmed from the game's own strings:**

- Addon **#943** — `When the item is sold, you will be charged a UNKNOWN% tax (…).` Second person, addressed **to the seller**, sitting in the `RetainerSell` dialog among #933 `Asking Price`, #935 `Total`, #936 `Tax (UNKNOWN%)`.
- Addon **#1963 / #1964 / #11513** — `Purchase <item> for 0 gil (0 fee included)?` The **buyer's** total has a fee *included*.
- Addon **#6945** — `No taxes will be collected on items sold from mannequins.`

**The clinching symbol:** the function that fetches the vocate rates is named **`GetMarketSellTaxRates`** (`ida/data.yml:17743`, on `CustomTalkEventHandler`). *Sell.* The entire per-city rate structure belongs to the **seller** leg — the buyer leg was never city-dependent at all.

So **`(after fees)` in LogMessage 745/748 is literal, not vestigial.** The hypothesis recorded when this ticket was created — "there may be a levy on each leg" — was right.

**What this does to the evidence cited in the ticket:**

- The `(after fees)` chat line — correct, and it means what it says.
- `IMarketBoardPurchaseHandler.TotalTax` and the cost-basis formula `(PricePerUnit × Qty + TotalTax) / Qty` — correct, **for the buyer**, unchanged.
- Universalis v2 `tax` and v3's `× 1.05` — **evidence of nothing.** See §3.
- **Allagan Market's inference never proved seller-side deduction.** `SaleTrackerService.cs:337` is `if (potentialSales - actualSalesAmount >= 0)` — an *at-least* test, which full-price proceeds also satisfy. It is insensitive to exactly the distinction at issue: not independent evidence, but the same guess this ticket exists to check.

*Still open (live check, cheap):* that the buyer fee is genuinely flat 5% and not the **selling** city's rate. Every source says flat; no source is the game's arithmetic.

---

### 2. Rate dynamics — **it expires, and the game says so**

`MarketTaxRates.cs:114` reads an **expiry timestamp** off the CustomTalk payload after the eight city rates:

```csharp
ValidUntil = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32()).UtcDateTime;
```

Corroborated by Addon **#969** (`Market tax reduced by … until …!`) and **#974** (`Currently, market tax is reduced.`). A rate is **a reading with an expiry**, not a constant.

Measured across all 32 North-American Worlds:

- **Limsa Lominsa, Gridania and Ul'dah read 5% in all 32, without a single exception.**
- The observed value set is **{0, 3, 5}** — the range is **0–5**, not the 3–5 this ticket supposed.
- Expansion cities vary World to World, but see §4: an unknown share of that variation is staleness rather than genuine difference.

**Cadence and driver: UNCONFIRMED.** No primary source. Two community sources say *daily* and *weekly* respectively and contradict each other; Grand Company standing plays no part in any of them. The only thing to rely on is the `ValidUntil` the packet itself carries — which is also the cheapest way to settle it (§6).

---

### 3. Field semantics — `tax` and `TotalTax` are different things, and one is a fiction

**`TotalTax` is genuinely server-transmitted.** Traced end to end: `NetworkHandlers.cs:607` fires from `infoProxyItemSearch + 0x5680`; `InfoProxyItemSearch.cs:46` puts `LastPurchasedMarketboardItem` at exactly that offset; `MarketBoardPurchaseHandler.Read` walks the struct's 0x24 bytes with `TotalTax` at `0x1C`; and `MarketBoardCurrentOfferings.cs:55` reads it straight out of the offerings packet, where `MarketBoardListing.TotalTax` sits at `0x8C` between `UnitPrice` and `Quantity`. It is the **buyer fee**, and its `RetainerCityId` is the **seller's** town.

**Universalis's `tax` is computed by the aggregator.** `Universalis.Application/Util.cs:25-29`:

```csharp
public static int CalculateTax(int unitPrice, int quantity)
{
    const double taxRate = 0.05;
    return (int)Math.Floor(quantity * unitPrice * taxRate);
}
```

Applied at **response-render time** (`CurrentlyShownControllerBase.cs:128, :352`), with v3 doing the same via `Math.Ceiling(… × 1.05)`.

**And the data never existed upstream:** `UniversalisMarketBoardUploader.cs:53-68` builds each entry with **no tax field at all**. The packet it reads *does* carry `TotalTax` — Dalamud drops it on the floor.

This was measured before it was read. Over the cached corpus: `tax == floor(total × 0.05)` on **210,746 of 210,746** listings, all 8 cities, both Worlds, across **474.6 days** of upload dates in 17 monthly buckets, **zero deviations** — and `total == pricePerUnit × quantity` on every row, so the levy is separate and additive. The one-request check that proved it independently: **Ishgard is at 3%, yet all 19,150 cached Ishgard listings read 5%.**

> **So EMM must never read `listing.tax`.** It models the buyer leg with a constant, knows nothing of the seller tax, and is wrong for five of the eight cities today.

---

### 4. Getting the live rate without a Vocate visit

**Best route — `AddonRetainerSell.Tax`, read at list time.** EMM is already standing in that addon whenever it lists:

```csharp
[FieldOffset(0x268)] public AtkComponentNumericInput* AskingPrice;
[FieldOffset(0x278)] public AtkTextNode* Total;
[FieldOffset(0x280)] public AtkTextNode* Tax;
```

The rate is recoverable arithmetically as `Tax ÷ Total`, or possibly **read directly** — Addon #936 is `Tax (UNKNOWN%)`, so the game renders the percentage into the label. *(Which node that pointer targets is a one-line live check.)* It yields the rate only for the town of the retainer currently open — which is exactly the scope EMM needs, since it prices for retainers it is actually working.

**There is no rate table in the client structs at all.** Exhaustive grep of FFXIVClientStructs, independently confirmed by a symbol scan of the installed `FFXIVClientStructs.dll`, finds only `TotalTax` (gil *amounts*, buyer-side), the `AddonRetainerSell.Tax` node, `GetMarketSellTaxRates` as a bare address, and Chocobo Taxi false positives. **The hoped-for `InfoProxyItemSearch` route does not exist.** There is no Excel sheet of rates either.

**`TaxRatesReceived` is a windfall, not a source.** It fires only on a vocate visit (`CustomTalk` EntryId 9 / scene 7 / yieldId 8, plus EntryId 581 for Misfrith in the Crystarium), and **Dalamud fires and forgets — no caching, no replay**. When it does fire, persist all eight rates *with* their `ValidUntil`.

**`/api/v2/tax-rates` is the fallback, and it has two failure modes:**

- **`0` is both a legal rate and the no-data sentinel, and the API cannot distinguish them.** `TaxRatesController` returns an all-zeros view rather than a 404 for a never-uploaded World; the merge path writes `uploaded ?? existing ?? 0`. Measured signature matches — zeros are sporadic and World-specific (Crystarium on Cactuar, Kugane on Excalibur, Crystarium on Ravana) while neighbouring Worlds report 3% for the same city at the same instant. **A naive read computes zero tax for Crystarium on Cactuar.**
- **Staleness is unbounded, undated and undetectable** — worse, because a stale non-zero value looks healthy (Midgardsormr read Tuliyollal at 5% against eleven Worlds at 3%). `TaxRatesStore` writes a Redis hash with **no TTL and no timestamp**, and **Dalamud never uploads `ValidUntil`**, so the game's own expiry is destroyed in transit and Universalis cannot know when its numbers lapsed even in principle. No `Cache-Control`/`ETag`/`Age` either.

*Naming trap:* the **upload** schema says `sharlayan`; the **response** says `Old Sharlayan`.

---

### 5. What EMM should do

1. **Never read `listing.tax`.**
2. **Model two levies.** `netProceeds = ask × qty × (1 − sellerTax)`; `buyerOutlay = ask × qty × (1 + buyerFee)`. **A flip nets both.** One scalar cannot express this — and every plugin in the ecosystem currently uses one scalar.
3. **Default seller tax = 5%, the worst case** — the confirmed pin for the ARR trio and the ceiling everywhere else, so assuming it can only *understate* proceeds, the conservative direction for a Floor. **Never default to 3%:** that is prior art's bug.
4. **Default buyer fee = 5%**, labelled as an assumption.
5. **Treat `0` as unknown, never as a rate.**
6. **Acquisition ladder:** `AddonRetainerSell.Tax` at list time → a persisted `TaxRatesReceived` capture with its `ValidUntil` → `/api/v2/tax-rates`, zeros discarded → the 5% default.
7. **Grade an assumed rate**, per #11's rule that a provisional estimate is always produced and always graded. A Floor computed on an assumed rate must say so.
8. **Map town ids independently of `RetainerTown`** — see below.

**Two traps worth stating on their own:**

> **`RetainerTown` has no Tuliyollal.** The enum (`RetainerManager.cs:60`) stops at `OldSharlayan = 12`; the `Town` sheet gives **row 14 = Tuliyollal**. A retainer in the current expansion's hub city falls through any `switch` over that enum — silently assigning the wrong rate to the most populated retainer city, which is precisely the failure this ticket exists to prevent.

> **Universalis sale history carries no `total` and no `tax` at all** (verified over 31,189 rows: only `pricePerUnit`, `quantity`, `hq`, `buyerName`, `onMannequin`, `timestamp`). So **EMM can never read what a past market sale netted** — it must reconstruct proceeds by applying a rate it cannot know for that moment, because the rate expires and nothing stores its history. Every historical Net Proceeds is a **model output, not an observation**; label it by Source, and never blend it with measured Own Sales.

**Prior art, for the gap inventory:** Allagan Market hardcodes 5%, or 3% for Kugane / Crystarium / Old Sharlayan, with a `// TODO: Use real percents later`. That is **wrong for Ishgard and Tuliyollal**, which are also reduced on most Worlds — including Tuliyollal, where a large share of active retainers sit. Reading the rate costs one request per World.

---

### 6. What only a live session can settle

Ordered by cost; the first three are nearly free and need no transaction.

1. **Is the buyer fee flat 5%, or the selling city's rate?** Open any board, find a listing whose `TownId` is a currently-reduced city, compare `MarketBoardListing.TotalTax` against `UnitPrice × Quantity`.
2. **Which node does `AddonRetainerSell.Tax` point at** — the `Tax (N%)` label, or the gil value? Does `Total` show gross or net?
3. **Cadence, in one reading:** capture a single `ValidUntil` at a vocate and see how far out it points. Settles daily-vs-weekly on its own.
4. **Does the seller's chat line report gross or net?** List at a known price, let it sell, compare the LogMessage 745/748 figure and the retainer's gil delta against `ask × qty` and `ask × qty × (1 − r)`. Also settles whether the deduction is computed on the asking price or on the buyer's tax-inclusive total.
5. **Rounding direction** on the seller deduction. Universalis floors and Allagan Market floors; neither is game evidence.
6. **Mannequin exemption** — which side it exempts, and whether `IsMannequin` listings really carry `TotalTax == 0`.

---

### 7. How much this matters

The ticket assumed *"a 5% error is roughly half the edge on a typical flip."* Measured rather than repeated, on 3,694 NQ and 830 HQ Wares (buy at lowest listing, sell at the 30-day median, netting a 5% buyer fee in and a 3% seller tax out):

| | NQ | HQ |
|---|---|---|
| Median **gross** edge | −0.35% | −0.96% |
| Median **net** edge | **−7.94%** | **−8.51%** |
| Share with no gross edge | 55.4% | 56.0% |
| Share with no **net** edge | **64.3%** | **70.4%** |
| **Profitable gross, unprofitable net** | **20.0%** | **32.6%** |

**The last row is the headline.** Of the Wares that look profitable before tax, **a fifth of NQ and a third of HQ actually lose money** once both levies are counted — a plugin confidently recommending a losing trade on a large minority of its apparently-good candidates. That is the gap this project already catalogued in prior art (*"no cost basis, so it reports revenue and tax but never profit"*) expressed as a measured quantity.

On the original framing: a 5-point error in the seller leg is ~17% (NQ) / ~29% (HQ) of the median gross edge — **overstated at the median**, but **understated in the tail**, exceeding the entire edge on a quarter of HQ Wares.

*Method and biases, since this is a statistical claim:* `minListing` as the buy proxy is the least stable statistic on the board and **overstates** achievable edge; the sell proxy is survivorship-biased toward Wares that sell; the two proxies are measured at different times; Wares are not independent, so no interval is quoted. **Every figure above is a lower bound.** It is a scale check, not a backtest.

*Incidental, and larger than the tax leg:* over half of qualifying Wares have the lowest current listing sitting **at or above** the median of the last 30 days' sales, before any tax at all. The standing posture — *the second mouse gets the cheese* — as a measured number rather than an assertion. Partly an artefact of comparing an unstable floor against a 30-day median, so the sign is clear and the magnitude soft.

---

### Consequence for the glossary

`CONTEXT.md` has a single binding term **Tax**, and **Buyer Cost**, **Net Proceeds**, **Profit** and **Minimum Margin** are all defined against it. One term cannot carry two levies with different rates, different directions and different payers — and the vocabulary exists precisely so arithmetic errors show up as sentences that read wrong.

**Recommended:** split **Tax** into two terms mirroring the game's own words — a **fee** the buyer pays on top, a **tax** the seller pays out of proceeds — and restate Buyer Cost against the first, Net Proceeds against the second. **Profit** then correctly nets both legs, because Cost Basis already includes the buyer fee via `TotalTax`.

Full research note, method, biases and reproduction scripts are held locally with the other research artefacts.

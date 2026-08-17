# Market companion plugins — what exists, what exposes IPC, where the gaps are

Research for issue #4. Investigated 2026-08-17.

## Headline

All four plugin names in the founding brief are **real**, all four are installed, and all four
are on the **official** Dalamud repository at **API level 15**. The earlier note that
"Allagan Market" and "MarketBoard" might be misremembered names was a stale snapshot, not an
error in the brief. The record is corrected below.

Of the whole ecosystem surveyed, **exactly one plugin exposes a Dalamud IPC surface: Allagan
Tools (`InventoryTools`)**. Every other market plugin examined — including Allagan Market —
registers no call gates at all. Integration with the rest therefore means *reading their files*
or *nothing*.

The buy side of the market is close to unserved in-game, and what does exist is either a thin
client for an external web service or computes profit **gross of tax**. That is EMM's clearest
opening.

---

## Sources and pins

Read from source at the tip commits below, and cross-checked against the shipped binaries and
the live on-disk data stores.

| Repository | Commit | Committed |
| --- | --- | --- |
| `Critical-Impact/AllaganMarket` | `ae150f9` | 2026-08-09T05:32:34Z |
| `Critical-Impact/InventoryTools` | `052739d` | 2026-08-08T08:03:07Z |
| `Critical-Impact/CriticalCommonLib` | `6e4756b` | 2026-08-08T08:01:17Z |
| `Critical-Impact/AllaganItemSearch` | `e25d3a8` | 2026-08-08T07:24:12Z |
| `Kouzukii/ffxiv-priceinsight` | `481392f` | 2026-05-02T11:06:33Z |
| `caitlyn-gg/PriceCheck` | `b43c61e` | 2026-04-29T22:35:20Z |
| `fmauNeko/MarketBoardPlugin` | `45dd411` | 2026-04-30T08:09:56Z |
| `Elypha/SimpleMarketBoard` | `ff1694c` | 2026-06-21T05:49:14Z |
| `epinephren/TopSellingItems` | `b56d3da` | 2026-05-26T09:38:09Z |
| `ff14-advanced-market-search/SaddlebagExchangeMarketboardPlugin` | `91148bf` | 2026-05-05T19:40:57Z |
| `tesu/PennyPincher` | `164e362` | 2026-04-30T15:26:34Z |

Other primary sources:

- Official plugin listing: `https://kamori.goats.dev/Plugin/PluginMaster` — 480 entries,
  retrieved 2026-08-17. 25 match market/price/retainer/flip terms.
- Manifests for closed-source entries: `goatcorp/DalamudPluginsD17`,
  `stable/<InternalName>/manifest.toml` (this is how the repo URLs for Saddlebag Exchange,
  TopSellingItems and Penny Pincher were recovered — their store entries have an empty `RepoUrl`).
- Local manifests: `%APPDATA%\XIVLauncher\installedPlugins\<name>\<version>\<name>.json`.
- Local data stores: `%APPDATA%\XIVLauncher\pluginConfigs\<name>\`.

**Binary verification.** A UTF-16 + ASCII string scan of each shipped DLL under
`installedPlugins` was used to confirm the IPC gate names actually present in the installed
build, rather than trusting repo tips:

| Shipped DLL | `*.<gate>` strings found |
| --- | --- |
| `InventoryTools\1.15.0.10\InventoryTools.dll` | 17 distinct `AllaganTools.*` gates |
| `AllaganMarket\1.4.0.2\AllaganMarket.dll` | none |
| `PriceInsight\2.11.5.0\PriceInsight.dll` | none |
| `PriceCheck\2.7.6.0\PriceCheck.dll` | none |
| `MarketBoardPlugin\1.13.0.0\MarketBoardPlugin.dll` | none |

A source-level grep for `GetIpcProvider` / `GetIpcSubscriber` / `ICallGate` across every `.cs`
file in `ffxiv-priceinsight`, `PriceCheck`, `MarketBoardPlugin`, `SimpleMarketBoard` and
`AllaganMarket` returns **zero hits**. The binary scan and the source scan agree.

---

## Summary table

| Plugin | Internal | Author | API | Maintained | IPC | Local historical data | Buy side |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Allagan Market | `AllaganMarket` | Critical_Impact | 15 | Yes (2026-08-09) | **No** | **Yes** — own sales, CSV | None |
| Allagan Tools | `InventoryTools` | Critical_Impact | 15 | Yes (2026-08-08) | **Yes — 17 gates** | Inventory + market cache, CSV | None |
| Price Insight | `PriceInsight` | Kouzukii | 15 | Yes (2026-05-02) | No | No — 90 min in-memory only | None |
| PriceCheck | `PriceCheck` | Infi, kalilistic, Caitlyn | 15 | Low (2026-04-29) | No | No | None |
| Market board | `MarketBoardPlugin` | fmauNeko | 15 | Yes (2026-04-30) | No | No | Browse + shopping list |
| Allagan Item Search | `AllaganItemSearch` | Critical_Impact | 15 | Yes (2026-08-08) | No | No | None |

---

## 1. Allagan Market (`AllaganMarket` 1.4.0.2) — the prior art

Same author as Allagan Tools. AGPL-3.0. Official repo, `testing-live` channel, API 15, actively
maintained (the changelog's `[4.0.2] - 2026-08-08` entry is a signature fix for game patch 7.55;
the versioning offset between changelog and plugin version is documented in its own changelog
preamble).

### 1a. What it actually does

Read from source, not from the store listing.

**Sales tracking (`Services/SaleTrackerService.cs`)**

- Holds a 20-slot array per retainer (`SaleItem[20]`) mirroring the retainer's market listings.
- Snapshots are taken **only when the retainer's market list is opened in game**
  (`RetainerMarketService.OnUpdated -> MarketOpened`).
- A sale is *inferred*, not observed: on each snapshot it diffs the previous 20 slots against the
  new ones, and where a slot went from occupied to empty it checks whether the retainer's gil
  went up by at least the expected post-tax amount. If yes, a `SoldItem` is recorded; if gil is
  unchanged it assumes the item was delisted.
- Tax is **hardcoded**: `0.05`, or `0.03` if the retainer is in Kugane, the Crystarium or
  Old Sharlayan. The source carries `// TODO: Use real percents later`.
- Emits `ItemSold` and `SnapshotCreated` events **internally only** — C# events on a service,
  not IPC.

**Undercut detection (`Services/UndercutService.cs`, 37 KB — the largest file in the plugin)**

- Subscribes to the **Universalis websocket** for `ListingsAdd` / `ListingsRemove` on the home
  world, and additionally queues REST price checks against the Universalis API.
- Also ingests prices decoded from the game's own market board packets
  (`Services/MarketPriceUpdaterService.cs` hooks the item-request-start packet handler by
  signature, and reads `InventoryManager->GetRetainerMarketPrice(i)` for the 20 slots).
- Maintains a price cache keyed `(worldId, itemId, isHq)` with a `MarketPriceCacheType` provenance
  tag: `Game`, `UniversalisWS`, `UniversalisReq`, `Override`.
- Answers `IsItemUndercut`, `GetUndercutBy`, `GetRecommendedUnitPrice`, `NeedsUpdate`,
  `GetLastUpdateTime`.

**UI**

- One main window, three tabs: **Currently Selling**, **Sale History**, **Sale Summary**.
- Sale Summary aggregates `TotalQuantity`, `TotalEarned`, `TotalTaxPaid`, grouped by item /
  world / retainer / owner / quality, over a date range or relative time window
  (`Models/SaleSummary.cs`).
- Two overlays: a per-item retainer-sell overlay and a retainer sell-list overlay, both anchored
  to the game's retainer windows.
- Menu bar with CSV export (current sales, history, sale summary — each in "all" and "filtered"
  variants) and one bulk-ish action, `Edit → Mark all visible sale items as updated`.
- DTR bar entry, title-screen menu button, chat notifications on sale and on undercut,
  highlighting of undercut rows in the retainer list.
- Commands: `/allaganmarket`, `/amarket`, `/amconfig`, `/amdebug`.

**The one write action.** `Windows/RetainerSellOverlayWindow.cs:238-248` — a `Copy to Game`
button that sets `retainerSellAddon->AskingPrice->SetValue(recommendedPrice)`. It works on
**one item at a time**, and only while that item's sell dialog is already open on screen.

**Interop with Allagan Tools.** `Services/ATService.cs` is 33 lines long and does exactly one
thing: on an `OpenMoreInformation` message it runs the chat command `/moreinfo <itemId>`. That
is the entire integration between the two plugins — a text command, not IPC, not a data
exchange. **The Allagan Tools + Allagan Market pairing does not solve inventory sourcing.**

### 1b. What it does NOT do — the gap inventory

This is the highest-value section. Every item verified against source.

**Pricing rules — a hard ceiling.**

- **Undercut amount is a single global integer.** `Settings/UndercutBySetting.cs`, key
  `UndercutBy`, default `5`. One number for every item, every retainer, every world.
- **Rounding is a single global integer.** `Settings/RoundToSetting.cs`, key `RoundTo`,
  default `1`, plus a global round-up/down flag.
- **The only per-item override in the entire plugin** is the undercut *comparison mode*
  (`Any` / `MatchingQuality` / `NqOnly` / `HqOnly`), stored as
  `Dictionary<uint, UndercutComparison> UndercutComparisonSettings` keyed by item id
  (`Configuration.cs:205-222`). Nothing else can be overridden per item.
- **No floor price. No price ceiling. No minimum margin. No "never sell below X".** There is no
  configuration key of any kind for a price bound. A stale or manipulated lowest listing will
  produce a recommendation that races to the bottom with nothing to stop it.
- **No item groups, no categories, no tags, no rule sets.** Rules cannot be scoped to anything
  narrower than "global" or wider than "one item id".
- **No rule precedence** — there is no rule system, so nothing to order. One global value with a
  single per-item exception on one field.
- **No cost basis anywhere.** `SoldItem` carries `RetainerId, WorldId, ItemId, IsHq, Quantity,
  UnitPrice, TaxRate, SoldAt` and derives `Total` and `TotalIncTax`. There is no acquisition
  cost field, no crafting cost, no COGS. **The Sale Summary reports revenue and tax paid — it
  cannot report profit.**

**Bulk operations — essentially absent.**

- No bulk reprice. No "reprice everything undercut on this retainer". No "apply recommended
  price to all". The only multi-item action in the UI is `Mark all visible sale items as
  updated`, which mutates a staleness timestamp, not a price.
- No cross-retainer operations of any kind. Every action is scoped to the retainer currently in
  front of you.

**Safety and reversibility — absent.**

- **No dry run / preview.** The recommendation is displayed, then `Copy to Game` writes it
  straight into the asking-price field. There is no batch to inspect before committing.
- **No undo.** No price history per listing, no rollback. `SaleItem` keeps `ListedAt` and
  `UpdatedAt` but not the previous price, so a bad write is unrecoverable from the plugin's own
  data.
- **No sanity guard.** Nothing rejects an absurd recommendation.

**Scheduling — absent.** No timers, no "reprice on login", no recurring job. `ItemUpdatePeriod`
(default 300 minutes) only decides when a row turns **yellow** to nag you; it triggers nothing.

**Buy side — completely absent.** No listing scanner, no watchlist, no buy ceiling, no flip
margin, no arbitrage view, no purchase tracking, no budget. The plugin's entire data model is
`SaleItem` / `SoldItem` / `MarketPriceCache`; nothing represents an intent to buy.

**Filtering — much weaker than the README claims.** The README advertises filtering "by various
criteria such as item, date range, world, and more". The actual filter object
(`Filtering/SaleFilter.cs`) has exactly three fields: `CharacterId`, `WorldId`, `ShowEmpty`.
Item and date-range filtering exist only inside the separate Sale Summary aggregation
(`Models/SaleSummary.cs`), not over the Currently Selling or Sale History tables.

**Data integrity gaps, from its own source.**

- `SoldAt` is `DateTime.Now` **at detection time**, set in the `SoldItem(SaleItem)` constructor —
  i.e. when you next visited the retainer, not when the item sold. Every timestamp in the sales
  history is late by an unbounded and unrecorded amount. Time-of-day or day-of-week analysis on
  this data is not sound.
- Sales are only detected on retainer visit. Sell, delist, and relist between two visits and the
  plugin cannot see it — the source logs `"Item has been switched, AM does not know about this."`
- Sale attribution depends on a gil delta, so anything else that changes retainer gil between
  visits can mask or fabricate a sale.
- `// TODO: Add reconcilation tab` — the author has flagged the unreconciled case and not built
  the UI for it.

### 1c. Where it is awkward to use

- **Every price change is a manual, per-item, in-dialog action.** To reprice n undercut items you
  must visit each retainer, open each item's sell dialog, click `Copy to Game`, confirm. Twenty
  slots per retainer, multiple retainers, multiple characters.
- **The plugin nags but cannot act.** Stale-price highlighting, DTR entries, chat notifications
  on undercut and on login all point at work the plugin then makes you do by hand.
- **Undercut settings are all-or-nothing.** A single `UndercutBy` across a portfolio spanning
  cheap consumables and multi-million-gil items is wrong at one end or the other. The only way to
  vary it is not to use it.
- **Initial setup requires visiting every retainer** ("visit each of your retainers to perform an
  initial scan"), and data quality degrades continuously until you do it again.
- **The sale history has no notion of "why".** No cost, no source, no linkage to a craft or a
  purchase — so the numbers cannot be turned into a decision.

### 1d. Local data store — yes, and it is readable

Confirmed on disk at `%APPDATA%\XIVLauncher\pluginConfigs\AllaganMarket\`:

```
SaleItems.csv          active listings
SoldItems.csv          historical sales
MarketPriceCache.csv   last observed price per (item, quality, world)
```

Written by `Services/ConfigurationLoaderService.cs:120-132` via `AllaganLib`'s `CsvLoaderService`,
saved on dirty-flag from `Services/AutoSaveService.cs` and again on plugin shutdown. Headerless,
positional, `CultureInfo.InvariantCulture` for numbers but **`DateTime.ToString(InvariantCulture)`
for timestamps** — a locale-invariant but not ISO-8601 format, so a reader must parse invariant.

Column orders, from the `FromCsv`/`ToCsv` implementations:

`SoldItems.csv` — `Models/SoldItem.cs:72-97`

| # | Field | Notes |
| --- | --- | --- |
| 0 | `RetainerId` (u64) | |
| 1 | `WorldId` (u32) | |
| 2 | `ItemId` (u32) | |
| 3 | `IsHq` | `1` / `0` |
| 4 | `Quantity` (u32) | |
| 5 | `UnitPrice` (u32) | |
| 6 | `TaxRate` (u32) | |
| 7 | `SoldAt` | invariant `DateTime`; **detection time, not sale time** |

`SaleItems.csv` — `Models/SaleItem.cs:180-211`. Same first six columns, then index 6 is an
**always-empty** legacy "Undercut By?" column, 7 `ListedAt`, 8 `UpdatedAt`, 9 `MenuIndex`.

`MarketPriceCache.csv` — `Models/MarketPriceCache.cs:63-86`: `ItemId, IsHq (Y/N), WorldId,
Type (0-3 enum), LastUpdated, UnitCost, OwnPrice (Y/N)`. Note the header helper
`GetHeaders()` omits `IsHq` and so does not match the seven columns actually written — do not
trust it, trust `FromCsv`.

On this machine all three files are 0 bytes: the plugin was installed today and no retainer has
been visited yet.

**Verdict on the store:** importing `SoldItems.csv` once, at first run, is cheap and is worth
doing — it seeds EMM's own history for players who already run Allagan Market. Two caveats that
must be surfaced in the UI: the timestamps are detection-time, and there is no cost basis, so
imported rows can support revenue history but not margin history.

---

## 2. Allagan Tools (`InventoryTools` 1.15.0.10) — the only real integration surface

GPL-3.0, official repo, `testing-live`, API 15, last commit 2026-08-08. The most-starred plugin
in this survey (57).

### 2a. IPC surface — 17 gates, verified in the shipped DLL

Registered in `InventoryTools/IPC/IPCService.cs:452-538`. Signatures are the `ICallGateProvider`
type arguments, last type = return.

**Inventory queries — this is what answers "what does the player have to sell".**

```
AllaganTools.IsInitialized             ()                                     -> bool
AllaganTools.CurrentCharacter          ()                                     -> ulong
AllaganTools.GetCharactersOwnedByActive(bool includeOwner)                    -> HashSet<ulong>
AllaganTools.GetCharacterItems         (ulong characterId)                    -> HashSet<ulong[]>
AllaganTools.GetCharacterItemsByType   (ulong characterId, uint inventoryType)-> HashSet<ulong[]>
AllaganTools.ItemCount                 (uint itemId, ulong characterId, int inventoryType) -> uint
AllaganTools.ItemCountHQ               (uint itemId, ulong characterId, int inventoryType) -> uint
AllaganTools.ItemCountOwned            (uint itemId, bool currentCharacterOnly, uint[] inventoryTypes) -> uint
AllaganTools.InventoryCountByType      (uint inventoryType, ulong? characterId)   -> uint
AllaganTools.InventoryCountByTypes     (uint[] inventoryTypes, ulong? characterId)-> uint
```

`inventoryType == -1` on `ItemCount`/`ItemCountHQ` means "any container".

**Lists and craft lists**

```
AllaganTools.GetSearchFilters      ()                                  -> Dictionary<string,string>   // key -> name
AllaganTools.GetCraftLists         ()                                  -> Dictionary<string,string>
AllaganTools.GetFilterItems        (string filterKey)                  -> Dictionary<uint,uint>       // itemId -> qty
AllaganTools.GetCraftItems         (string filterKey)                  -> Dictionary<uint,uint>
AllaganTools.GetRetrievalItems     ()                                  -> Dictionary<uint,uint>
AllaganTools.AddNewCraftList       (string name, Dictionary<uint,uint>)-> string                      // returns key
AllaganTools.AddItemToCraftList    (string key, uint itemId, uint qty) -> bool
AllaganTools.RemoveItemFromCraftList(string key, uint itemId, uint qty)-> bool
AllaganTools.EnableUiFilter / DisableUiFilter / ToggleUiFilter         (string key) -> bool
AllaganTools.EnableBackgroundFilter / DisableBackgroundFilter / ToggleBackgroundFilter (string key) -> bool
AllaganTools.EnableCraftList / DisableCraftList / ToggleCraftList      (string key) -> bool
```

**Events (subscribe with `GetIpcSubscriber<...>`)**

```
AllaganTools.Initialized    (bool)                                                -> bool
AllaganTools.RetainerChanged(ulong? retainerId)                                   -> bool
AllaganTools.ItemAdded      ((uint itemId, InventoryItem.ItemFlags, ulong charId, uint qty)) -> bool
AllaganTools.ItemRemoved    ((uint itemId, InventoryItem.ItemFlags, ulong charId, uint qty)) -> bool
```

Caution on the two item events: the tuple's second element is
`FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags`. Passing a struct type across a
call gate couples EMM to that exact type identity. Prefer polling `GetCharacterItems` on
`RetainerChanged` over binding the tuple events, unless a compatibility test proves otherwise.

**The `ulong[]` item layout** returned by `GetCharacterItems` — from
`CriticalCommonLib/Models/InventoryItem.cs`, `ToNumeric()`:

```
0 Container   1 Slot            2 ItemId        3 Quantity      4 Spiritbond
5 Condition   6 Flags           7-11 Materia0-4 12-16 MateriaLevel0-4
17 Stain      18 Stain2         19 GlamourId    20 SortedContainer
21 SortedCategory  22 SortedSlotIndex  23 RetainerId  24 RetainerMarketPrice
25+ GearSets (variable length)
```

Index **24 is the item's current retainer market price** — so the same call that answers "what do
I own" also answers "what is it currently listed at", for free.

Bugs worth knowing: `IPCService.cs:514` re-registers `_getCraftItems` a second time (harmless,
overwrites itself), and `StopAsync` does not unregister `_initialized`. Neither affects a
consumer.

### 2b. Local data store

`%APPDATA%\XIVLauncher\pluginConfigs\InventoryTools\`:

- **`inventories.csv`** — 142 KB and populated on this machine. Same positional layout as
  `ToNumeric()` above. A full cross-character, cross-retainer inventory snapshot on disk.
- **`market_cache.csv`** — 121 KB and populated. Schema from
  `CriticalCommonLib/MarketBoard/MarketPricing.cs:29-41`:
  `ItemId, WorldId, AveragePriceNq, AveragePriceHq, MinPriceNq, MinPriceHq, SevenDaySellCount,
  LastSellDate, LastUpdate, Available`.
- `history.csv` — 0 bytes here.

`market_cache.csv` is a strictly richer market snapshot than Allagan Market's
`MarketPriceCache.csv`: it carries **per-quality** average and minimum prices, a **seven-day sell
count** (velocity) and a **listing count**, all derived from Universalis
(`MarketPricing.FromApi`). It is still a snapshot per item/world, **not a time series** — it is
overwritten, not appended. Neither plugin gives EMM a price history it would not have to build.

### 2c. Value assessment

Genuine, and the strongest in the survey. Allagan Tools already solves multi-character,
multi-retainer inventory aggregation — a large, fiddly, entirely non-differentiating problem.
Consuming its IPC is not duplicating Universalis; it is duplicating nothing.

The risk is a hard dependency on a plugin the player may not have. Mitigation: treat it as an
**optional enrichment source** behind `AllaganTools.IsInitialized`, with EMM's own inventory
reader as the baseline path.

---

## 3. Price Insight (`PriceInsight` 2.11.5.0)

MIT, official repo, stable, API 15, last commit 2026-05-02. Kouzukii.

Shows Universalis prices on item tooltips on hover; Alt refreshes. Its data model
(`MarketBoardData.cs`) is rich for a tooltip — minimum price, most recent purchase, average sale
price and **daily sale velocity**, each split world / datacenter / region and NQ / HQ.

- **No IPC** (verified in source and binary).
- **No persistence at all** — `ItemPriceLookup.cs` uses an in-memory cache with
  `TimeSpan.FromMinutes(90)`, and there is no `pluginConfigs\PriceInsight` directory on this
  machine. Nothing survives a restart.
- Its value is presentation. The underlying numbers are a straight Universalis read that EMM will
  be making anyway.

**Integrating adds nothing.** It has no surface to integrate with, and no data EMM would not
already hold. Drop.

## 4. PriceCheck (`PriceCheck` 2.7.6.0)

MIT, official repo, stable, API 15. Last commit 2026-04-29 — that date is shared with several
other plugins in this survey and looks like the API-15 mass bump, so **active maintenance beyond
API bumps is UNCONFIRMED**. Authorship has passed through three hands (Infi, kalilistic, Caitlyn).

Keybind- or context-menu-driven price check against Universalis, with chat/toast/overlay output
and configurable thresholds. No IPC, no historical store (only a 1.4 KB `PriceCheck.json` config).

Functionally a subset of Price Insight. **Drop.**

## 5. Market board (`MarketBoardPlugin` 1.13.0.0)

MIT, official repo, stable, API 15, last commit 2026-04-30. fmauNeko. 41 stars — the second most
popular here.

`/pmb` opens a browsable market board from anywhere: search, cross-world listings, recent history,
and a **shopping list** (`GUI/MarketBoardShoppingListWindow.cs`) that accumulates name / price /
world rows you intend to buy.

- **No IPC.**
- **No historical store.** The shopping list persists as `SavedItem` entries in its config; the
  market data is fetched per view.
- The shopping list is buy-side in the weakest sense: a manual scratchpad. No margin, no ceiling,
  no alerting, no tax.

**Drop as an integration.** Worth keeping as a UX reference for "browse the board from anywhere",
which is a workflow EMM's buy side will need to beat.

## 6. Allagan Item Search (`AllaganItemSearch` 1.3.0.2)

AGPL-3.0, official repo, `testing-live`, API 15, last commit 2026-08-08. Critical_Impact.

An item catalogue search over the game's Excel sheets. No market data, no IPC, no store. Not
market-related despite the family name.

**Out of scope.** Its only relevance is that its `/moreinfo`-style item windows are the target of
Allagan Market's one interop hook.

---

## 7. Plugins not on the ticket's list that EMM should know about

From the 480-entry official listing, the market-relevant remainder:

| Plugin | Author | API | What it is | Relevance |
| --- | --- | --- | --- | --- |
| **Saddlebag Exchange** | Saddlebag Exchange | 15 | Thin client over `api.saddlebagexchange.com` | **Closest competitor** |
| **TopSellingItems** | epinephren | 15 | Universalis scanner: sales/day rankings, flip and cross-DC opportunity rows | **Closest buy-side prior art** |
| **Penny Pincher** | tesu | 15 | Copies "lowest minus one" to clipboard when you open the compare-prices window | Undercut UX baseline |
| SimpleMarketBoard | Elypha | 15 | Compact hover price-check window | No IPC (verified) |
| Easy MBHistory | Infi | 15 | Copies market board transaction history to clipboard | Export-only |
| Retainer Sales | Populo | 15 | Shows which retainers sold something while logged off | Narrow subset of Allagan Market |
| WachuMakeyMaking? | Nesrie (ArcaneAj) | 15 | Optimal craft from current inventory, Universalis-priced | Crafting — out of EMM's scope |
| Tracky | Infi | 15 | Tracks currency, desynth, coffers, ventures | Adjacent telemetry |
| Accountant | Ottermandias | 15 | Retainer venture / airship / submarine timers | Adjacent |
| Market Uploader | Zhyra | **8** | Uploads to `market.xivhub.org` | **Dead** — API 8 vs current 15 |

### The one that matters: Saddlebag Exchange

`SaddlebagExchangeMarketboardPlugin`, Apache-2.0, listed on the official repo but with an empty
`RepoUrl` in the store; the source was recovered from its D17 manifest.

It is a **thin ImGui client over a remote HTTP API** — all analysis happens on their servers.
`Services/SaddlebagApiService.cs` posts to four endpoints on `https://api.saddlebagexchange.com`:

```
POST /api/scan              reselling search  -> buy elsewhere, sell on home server
POST /api/ffxivmarketshare  market overview by revenue / sales
POST /api/v2/craftsim       crafting profit
POST /api/v2/shoppinglist   shopping list generator
```

Its reselling request (`Models/ResellingParams.cs`) is genuinely sophisticated on inputs —
`PreferredRoi`, `MinProfitAmount`, `MinDesiredAvgPpu`, `MinStackSize`, `HoursAgo`, `MinSales`,
`Hq`, `HomeServer`, `Filters[]`, `RegionWide`, `IncludeVendor`, `ShowOutStock`.

Where it is weak, and where EMM differs:

- **It is not a local tool.** No player inventory, no retainer state, no own-sales history. It
  cannot tell you what *you* have or what *you* sold.
- **Cross-server by design.** `BuyServer` in the result implies travelling to buy — which is
  explicitly out of EMM's scope.
- **Network dependency and third-party data policy.** Every query leaves the machine.
- Only 1 star, last commit 2026-05-05.

### TopSellingItems — and the tax hole

Ranks items by estimated sales/day computed locally from Universalis sale history, and produces
`FlipOpportunityRow` (`SalesPerDay, AverageSalePrice, MinListingPrice, EstimatedProfit,
ProfitPercent, FlipScore`) and `CrossDcOpportunityRow` (`HomeScope, BestBuyScope, BestBuyPrice,
EstimatedProfit, RouteScore`).

The profit maths, from `Services/MarketScanService.cs`:

```csharp
var estimatedProfit = homeAverageSalePrice - bestBuy.MinListingPrice;
var profitPercent   = estimatedProfit / bestBuy.MinListingPrice;
```

**A grep for `tax` across the whole service returns zero hits.** Its "profit" is gross — the 5% /
3% market tax on the sale leg is simply not modelled. On a 10% nominal margin, that is half the
edge, unaccounted.

---

## 8. Buy-side coverage across the ecosystem

Asked as an explicit question, the answer is: **nobody does this well in game.**

| Capability | Anyone? |
| --- | --- |
| Underpriced-listing detection within reach | Partial — TopSellingItems ranks candidates; Saddlebag scans server-side |
| **Flip margin net of tax** | **No one.** TopSellingItems computes gross; Saddlebag's `Profit` is server-computed and its tax treatment is **UNCONFIRMED** |
| Buy ceilings / max-price rules | No one |
| Watchlists with alerting | No one |
| Budget or exposure limits | No one |
| Purchase tracking / cost basis | **No one** — no plugin in the survey records what you paid |
| Linking purchases to later sales (realised margin) | No one |
| Buy-side that knows your own inventory and retainer state | No one |

The absence of **cost basis anywhere in the ecosystem** is the single most important finding on
the buy side. It is why no existing plugin can report profit, only revenue — and it is a gap EMM
closes by construction the moment it records purchases and sales in one ledger.

Note the scope boundary: the two plugins with real buy-side ambition both lean on **cross-server
buying**, which requires DC travel and is out of EMM's scope. Buy-side confined to the reachable
market — own world and same-DC where no travel is implied — is therefore not merely underserved,
it is **unserved**.

---

## 9. Recommendation

### Build for v0.1

1. **Allagan Tools IPC as an optional inventory source.** The only real IPC surface in the
   ecosystem, 17 gates, verified present in the shipped 1.15.0.10 binary. Gate every call behind
   `AllaganTools.IsInitialized`, subscribe to `AllaganTools.RetainerChanged`, read
   `GetCharactersOwnedByActive` + `GetCharacterItems`, and take index 24 for the current listing
   price. Ship EMM's own inventory reader as the baseline so the dependency is never hard.
2. **One-time import of `SoldItems.csv`** from `pluginConfigs\AllaganMarket\`. Cheap plumbing,
   seeds sales history for existing Allagan Market users. Label imported rows explicitly:
   timestamps are detection-time and there is no cost basis.

### Defer

3. **Reading `pluginConfigs\InventoryTools\market_cache.csv`.** Richer than Allagan Market's cache
   (per-quality averages and minimums, seven-day sell count, listing count) but it is a snapshot,
   not a series, and EMM will be talking to Universalis directly regardless. Revisit only if a
   cold-start seed for velocity proves worth it.
4. **Universalis websocket ingestion.** Allagan Market proves the pattern works
   (`UndercutService` subscribes to `ListingsAdd` / `ListingsRemove` for the home world). EMM will
   want it for live undercut detection, but REST polling is enough to ship.

### Drop

5. **Price Insight, PriceCheck, Market board, Allagan Item Search** — as integrations. No IPC, no
   persisted data, nothing EMM would not already hold. Their only remaining value is as UX
   reference.
6. **Any dependency on Saddlebag Exchange or TopSellingItems.** Competitors, not components; one
   is a remote-API client, the other is unlicensed with no tax modelling.
7. **Market Uploader** — API 8, and uploading is out of scope anyway.

### Differentiation targets, straight from the gap inventory

Allagan Market's ceilings, restated as things EMM must have:

- Per-item and per-group pricing rules with explicit precedence — against one global `UndercutBy`
  integer and one per-item comparison-mode override.
- Floor prices and minimum margins — against nothing.
- Bulk reprice across all retainers and characters — against one item at a time, in-dialog.
- Preview before commit, and undo after — against neither.
- Scheduling — against a colour change on a stale row.
- Profit, not revenue: cost basis recorded at acquisition, margin net of the real tax rate —
  against a hardcoded `0.05`/`0.03` and a `// TODO: Use real percents later`.
- Buy side at all — against nothing, anywhere in the ecosystem.

### Open items

- **UNCONFIRMED:** whether Saddlebag Exchange's server-computed `Profit` is net of market tax.
  Only their API can answer it, and it is not needed for a v0.1 decision.
- **UNCONFIRMED:** whether PriceCheck is maintained beyond API bumps.
- Dalamud core already ships a Universalis uploader
  (`Dalamud/Game/Network/Internal/MarketBoardUploaders/Universalis/`), so any market board EMM
  opens contributes to Universalis with no work — and no reason for EMM to upload anything itself,
  which is out of scope regardless.

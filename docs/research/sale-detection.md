# How EMM detects that one of its listings has sold

Research note resolving issue [#16](https://github.com/local-variable/EMM/issues/16).

- **Written:** 2026-08-17 (UTC)
- **Environment assumed:** Dalamud API level 15, FFXIVClientStructs 7.55.1.8875, game patch 7.55
- **Builds on:** [`autoretainer-ipc.md`](autoretainer-ipc.md) (#2), [`market-data-sources.md`](market-data-sources.md) (#3), [`companion-plugins.md`](companion-plugins.md) (#4)

Everything below is read from source at the pins in [§8](#8-sources-and-pins), cross-checked against the
binaries and game data installed on this machine. Claims that were **not** verified at runtime in a
live game session are marked as such — this investigation was static.

---

## Summary

| | Mechanism | Verdict |
|---|---|---|
| **Primary** | **Slot-level diff of the `RetainerMarket` inventory container (20 slots) plus its parallel price array, snapshotted every time EMM is handed a retainer** | Recommended. Full per-item attribution: item, HQ, quantity, unit price. Recovers every offline sale individually, not a net figure. |
| **Fallback / cheap trigger** | **`RetainerManager.Retainer.MarketItemCount` delta, readable for *all* retainers at the summoning bell without opening any of them** | Recommended as a *trigger* and as a coverage check. Count only — no attribution, and it nets sales against relists. |
| **Enrichment (online only)** | **`XivChatType.RetainerSale` (71) chat messages** | Worth consuming. Gives item + exact after-fee gil + a real timestamp, but only for sales that happen while that character is logged in. |
| **Corroboration only** | Retainer gil delta | Not a primary signal. AutoRetainer's own itinerary moves retainer gil *before* EMM's window ([§3.3](#33-gil-delta--rejected-as-a-primary-signal)). |
| **Does not exist** | Sale history / transaction records in FFXIVClientStructs; a market-sale letter | Confirmed absent ([§2.4](#24-what-does-not-exist)). |
| **Not a sale signal** | Universalis history | A union of 20-sale samples, not a census of your own sales — established in [#3 §3.2](market-data-sources.md#32-sampling-structure-not-just-staleness). |

Headline answers to the four questions the ticket demands:

1. **Attribution — yes, fully.** Item ID, HQ flag, quantity and unit price all come out of the diff, because the previous snapshot holds the complete slot state and FFXIV listings are bought whole ([§4.1](#41-attribution)).
2. **Missed sales across sessions — all of them, individually.** N slots emptying between visits produce N sale rows, not one net delta. What is lost is *when* each sale happened and in what order ([§4.2](#42-missed-sales-across-sessions)).
3. **Sale vs cancellation vs expiry — separable, with one residual ambiguity.** Expiry is structurally distinct and cannot be confused with either. Sale vs cancellation is decided by EMM's own action log first, and by a returned-to-inventory / gil check second ([§4.3](#43-sale-vs-cancellation-vs-expiry)).
4. **Cost basis — capturable, and nothing in the ecosystem does it.** `IMarketBoard.PurchaseRequested` hands EMM unit price, quantity, HQ and tax for every market purchase the player makes ([§4.4](#44-cost-basis-what-to-capture-at-buy-time)).

---

## 1. Where EMM gets its observation window

From [#2](autoretainer-ipc.md), AutoRetainer hands EMM a retainer that is **already selected, with its
`SelectString` menu open**, and blocks indefinitely until EMM calls `FinishRetainerPostProcess()`
([`SchedulerMain.cs:193`](https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Scheduler/SchedulerMain.cs#L193)).
Everything in [§2](#2-what-the-game-exposes) is read from that state, or from the summoning-bell
retainer list that precedes it.

**The market data is present at `SelectString` time — EMM does not have to open the sell list to read it.**
Allagan Market's `MarketPriceUpdaterService` registers on `AddonEvent.PostShow` for `SelectString` and,
whenever a retainer is active, reads all twenty market prices straight out of `InventoryManager`
([`MarketPriceUpdaterService.cs:63`, `:67-78`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/MarketPriceUpdaterService.cs#L63-L78)).
Its `RetainerMarketService` likewise loads the twenty item slots on `SelectString` `PostSetup`
([`RetainerMarketService.cs:383-407`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/RetainerMarketService.cs#L383-L407)),
gated on having seen the server's `ContainerInfo` packet for that retainer
([`InventoryService.cs:117-144`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/InventoryService.cs#L117-L144)).
The container is delivered per retainer-open — `loadedInventories` is cleared whenever the active
retainer changes ([`InventoryService.cs:121-126`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/InventoryService.cs#L121-L126)).

> ⚠️ **Runtime-unverified.** That the container and price array are populated *at the moment AutoRetainer
> fires the postprocess event* is inferred from Allagan Market's hook points, not observed in a live
> session. EMM must guard on `InventoryContainer->IsLoaded` (the pattern used at
> [`InventoryService.cs:53-68`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/InventoryService.cs#L53-L68))
> and fall back to snapshotting after it opens `RetainerSellList` for repricing, which it will be doing
> anyway.

---

## 2. What the game exposes

### 2.1 The retainer's market listings — the authoritative source

Listings live in an ordinary inventory container:

```
RetainerMarket = 12002        // InventoryType.cs:60
```
— [`FFXIVClientStructs/FFXIV/Client/Game/InventoryType.cs:60`](https://github.com/aers/FFXIVClientStructs/blob/c827f21/FFXIVClientStructs/FFXIV/Client/Game/InventoryType.cs#L60).
Dalamud mirrors it as `GameInventoryType.RetainerMarket = 12002`, reachable through
`IGameInventory.GetInventoryItems(GameInventoryType)`
([`IGameInventory.cs`](https://github.com/goatcorp/Dalamud/blob/8304201/Dalamud/Plugin/Services/IGameInventory.cs),
[API docs](https://dalamud.dev/api/Dalamud.Game.Inventory/Enums/GameInventoryType/)).

The container holds twenty slots and carries the *item* side of each listing — item ID, quantity,
HQ flag via `InventoryItem.ItemFlags.HighQuality`, materia, stains, condition, spiritbond. It does **not**
carry the price. Prices live in a parallel twenty-entry array on `InventoryManager`:

```csharp
[FieldOffset(0x21B8), FixedSizeArray] internal FixedSizeArray20<ulong> _retainerMarketPrices;   // :42
public partial ulong GetRetainerMarketPrice(short slot);                                        // :138
public partial void  SetRetainerMarketPrice(short slot, uint price);                            // :144
```
— [`InventoryManager.cs:42, :138, :144`](https://github.com/aers/FFXIVClientStructs/blob/c827f21/FFXIVClientStructs/FFXIV/Client/Game/InventoryManager.cs#L42).
There is no Dalamud service for the price side; it must be read from FFXIVClientStructs.
Related movers, for completeness:
`MoveToRetainerMarket(srcInv, srcSlot, dstInv, dstSlot, quantity, unitPrice)`,
`MoveFromRetainerMarketToRetainerInventory`, `MoveFromRetainerMarketToPlayerInventory`
([`InventoryManager.cs:98-104`](https://github.com/aers/FFXIVClientStructs/blob/c827f21/FFXIVClientStructs/FFXIV/Client/Game/InventoryManager.cs#L98-L104)).

So **`(RetainerMarket[i], GetRetainerMarketPrice(i))` for `i` in `0..19` is a complete listing snapshot**:
item, HQ, quantity, unit price, per slot. That is exactly what Allagan Market's `SaleItem` records
([`SaleItem.cs:91-109`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Models/SaleItem.cs#L91-L109)).

Slot index is stable and is the correct diff key. Note that the **UI order is not the slot order** —
Allagan Market keeps a separate `MenuIndex` derived from an ATK order lookup
([`RetainerMarketService.cs:309-326`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/RetainerMarketService.cs#L309-L326)).
Diff on the container index, not on what the window shows.

### 2.2 The per-retainer summary — readable for every retainer, without opening any of them

```csharp
public partial struct Retainer {                       // RetainerManager.cs:45
    [FieldOffset(0x00)] public ulong RetainerId;
    [FieldOffset(0x08)] ... _name;                     // 32-byte string
    [FieldOffset(0x28)] public bool Available;
    [FieldOffset(0x29)] public byte ClassJob;
    [FieldOffset(0x2A)] public byte Level;
    [FieldOffset(0x2B)] public byte ItemCount;
    [FieldOffset(0x2C)] public uint Gil;
    [FieldOffset(0x30)] public RetainerTown Town;
    [FieldOffset(0x31)] public byte MarketItemCount;
    [FieldOffset(0x34)] public uint MarketExpire;      // 7 Days after last opened retainer
    [FieldOffset(0x38)] public ushort VentureId;
    [FieldOffset(0x3C)] public uint VentureComplete;
}
```
— [`RetainerManager.cs:45-58`](https://github.com/aers/FFXIVClientStructs/blob/c827f21/FFXIVClientStructs/FFXIV/Client/Game/RetainerManager.cs#L45-L58).
The `MarketExpire` comment is upstream's, not this note's. All twelve fields, and the accessors
`GetRetainerCount()`, `GetRetainerBySortedIndex(uint)`, `GetActiveRetainer()`, are present in the
shipped 7.55.1.8875 assembly (verified by a symbol scan of
`%APPDATA%\XIVLauncher\addon\Hooks\dev\FFXIVClientStructs.dll`, 2026-08-17).

**This is the cheap signal, and it covers every retainer at once.** The Retainer Sales plugin reads it
for all retainers on `RetainerList` `PreSetup` — i.e. the instant the summoning-bell list opens, before
any retainer is selected:

```csharp
var manager = RetainerManager.Instance();
for (uint i = 0; i < manager->GetRetainerCount(); ++i) {
    var retainer = manager->GetRetainerBySortedIndex(i);
    RetainerSaleNumbers[retainer->NameString] = retainer->MarketItemCount;
    if (Configuration.ItemsForSale[retainer->NameString] > retainer->MarketItemCount) { /* sold */ }
```
— [`RetainerSales.cs:53-100`](https://github.com/Populo/RetainerSales/blob/a9b62a8/RetainerSales/RetainerSales.cs#L53-L100).

AutoRetainer reads the same struct through a thin wrapper
([`GameRetainerManager.cs:40-41`](https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Internal/GameRetainerManager.cs#L40-L41))
and persists `MBItems = ret.MarkerItemCount` into `OfflineRetainerData`
([`OfflineDataManager.cs:142`](https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Modules/OfflineDataManager.cs#L142)).

### 2.3 The `RetainerSale` chat channel

The game emits a chat message on a dedicated channel when a listing sells.
`LogKind` 71 corresponds to Dalamud's `XivChatType.RetainerSale = 71`
([`XivChatType.cs`](https://github.com/goatcorp/Dalamud/blob/8304201/Dalamud/Game/Text/XivChatType.cs)).
The `LogMessage` sheet contains **exactly five** rows on that channel (queried against the live
`LogMessage` sheet via xivapi v2, 2026-08-17; parameters render blank in the raw sheet text):

| Row | Text |
|---:|---|
| 745 | ` you put up for sale in the  markets has sold for 0 gil (after fees).` |
| 748 | ` you put up for sale in the  markets has sold for 0 gil (after fees).` |
| 384 | `All your items up for sale in the  markets have sold.` |
| 6081 | ` you put up for sale on your mannequin has sold for 0 gil.` |
| 6082 | `Multiple items put up for sale on your mannequin have sold for 0 gil.` |

What this gives EMM, per sale: **an item link payload (item ID + HQ), the city, and the exact
post-tax gil credited**, with a real wall-clock timestamp. What it does **not** give: the quantity,
the unit price, the retainer name, or the container slot. With several retainers stationed in the same
city, the message cannot be attributed to a retainer at all.

> **UNCONFIRMED:** why rows 745 and 748 carry identical text (plausibly a singular/plural or
> quality split), and under exactly what condition row 384 fires rather than a per-item message.
> Both need a live observation.

> **UNCONFIRMED, and load-bearing:** whether these messages are replayed on login for sales that
> occurred while the character was offline. There is no "sales are waiting" message anywhere on the
> channel — the five rows above are the complete set — and two independently maintained plugins that
> exist specifically to answer "what sold while I was away" both implement state diffing instead of
> reading channel 71 (Allagan Market's gil-delta snapshot, Retainer Sales' count delta). If the
> channel replayed a backlog, both would be strictly worse than a two-line chat listener. **Design as
> if it does not replay**; treat any messages that do arrive as a bonus.

### 2.4 What does not exist

- **No sale history or transaction record in FFXIVClientStructs.** A search of the whole
  `FFXIV/Client` tree for `Sold` / `RetainerSell` / `Transaction` / `History` turns up only
  `AddonRetainerSell` (a UI window), `ShopEventHandler` (NPC vendors) and
  `InfoProxyItemSearch.ProcessItemHistory` — which is the market board's *public* last-20-sales list for
  a searched item, not your own ledger. This confirms the finding in
  [#3 §2.3](market-data-sources.md#23-reading-listings-out-of-memory-instead) from the other direction.
- **No letter.** Market proceeds are not mailed. `LogMessage` 4578 reads *"Gil earned from market sales
  has been entrusted to your retainer. The amount earned exceeded your retainer's gil limit. Excess gil
  has been discarded."* — the gil goes straight into `Retainer.Gil`. `LetterDataModule` is a bare size
  stub in FFXIVClientStructs with no mapped fields anyway
  ([`LetterDataModule.cs:10-15`](https://github.com/aers/FFXIVClientStructs/blob/c827f21/FFXIVClientStructs/FFXIV/Client/UI/Misc/LetterDataModule.cs#L10-L15)).
- **No AutoRetainer event.** Established in [#2 §4](autoretainer-ipc.md): no sold event, no
  venture-complete event, no listing-changed event, and `FinishRetainerPostProcess()` is `void`.

Two further game rules fall out of the `LogMessage` sheet and matter for accounting:

- **The retainer gil cap can silently eat proceeds.** Row 4578 (above) and row 420, *"Unable to sell
  item. Your retainer cannot carry any more gil."* A retainer at the cap will have sale proceeds
  **discarded**, which breaks any gil-based reconciliation and, worse, loses the player real money.
  EMM should warn when a retainer's gil approaches the cap.
- **Quantity cannot be edited in place.** Row 492: *"You must remove an item from the markets before
  adjusting its details."* Price can be adjusted on a live listing; quantity cannot. A quantity change
  is therefore a delist-and-relist, and will look like a slot transition in the diff.

---

## 3. The candidates, evaluated

### 3.1 Retainer sell-list diffing — **recommended primary**

Snapshot `(item, HQ, quantity, unitPrice)` for all twenty `RetainerMarket` slots on every retainer
visit; persist per `(characterContentId, retainerId, slot)`; diff on the next visit.

Allagan Market is the working proof that the shape is sound
([`SaleTrackerService.CreateSnapshot`, `:295-410`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/SaleTrackerService.cs#L295-L410)).
Its transitions:

| Previous slot | New slot | Allagan Market's conclusion | Source |
|---|---|---|---|
| occupied | empty | sale *if* retainer gil rose by ≥ the expected post-tax amount; delist if gil unchanged | [`:308-358`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/SaleTrackerService.cs#L308-L358) |
| occupied | occupied, different item | `"Item has been switched, AM does not know about this."` | [`:366-369`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/SaleTrackerService.cs#L366-L369) |
| occupied | occupied, price differs | price update | [`:370-373`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/SaleTrackerService.cs#L370-L373) |
| occupied | occupied, otherwise differs | `"Could not reconcile item."` + `// TODO: Add reconcilation tab` | [`:374-378`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/SaleTrackerService.cs#L374-L378) |

EMM should keep the transition table and replace the decision rule. Allagan Market's weaknesses are
in the rule, not the mechanism: the tax rate is hardcoded `0.05`, or `0.03` for Kugane / the Crystarium
/ Old Sharlayan, carrying its own `// TODO: Use real percents later`
([`:313-324`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/SaleTrackerService.cs#L313-L324)),
and the sale/delist decision rests entirely on a gil delta that [§3.3](#33-gil-delta--rejected-as-a-primary-signal)
shows is not available to an AutoRetainer-driven plugin.

**Why the diff is strong for EMM specifically:** EMM is the thing that created the listings. It knows
what it listed, at what price, in which slot, when, and against which acquisition record. The diff is
not reverse-engineering an unknown state — it is confirming or refuting EMM's own expectation.

### 3.2 `MarketItemCount` delta at the bell — **recommended fallback and trigger**

`RetainerManager.Retainer.MarketItemCount` is readable for every retainer the moment the summoning-bell
list opens ([§2.2](#22-the-per-retainer-summary--readable-for-every-retainer-without-opening-any-of-them)).
A drop since the last recorded value means at least one listing left that retainer's board.

Value: it is the only signal that covers retainers EMM is **not** about to visit, so it answers "is my
stored snapshot for retainer X stale?" before EMM has spent a single UI interaction. It is the right
input to "which retainers are worth visiting" and to a coverage warning in the UI.

Limits, all real:

- **Count only.** No item, no price, no quantity.
- **It nets.** One sale plus one relist between visits is a delta of zero.
- **It is not trustworthy after expiry.** Retainer Sales explicitly refuses to call a count drop a sale
  when `now > MarketExpire`, logging `"Retainer market expired, skipping."`
  ([`RetainerSales.cs:69-88`](https://github.com/Populo/RetainerSales/blob/a9b62a8/RetainerSales/RetainerSales.cs#L69-L88)).
  Adopt the same guard.

> **UNCONFIRMED:** whether `MarketItemCount` counts *occupied `RetainerMarket` slots* or *listings
> actively offered on the board*. The two diverge after expiry, and Retainer Sales' guard is only
> necessary if it is the latter. Resolve by observing one retainer past its `MarketExpire`.

### 3.3 Gil delta — rejected as a primary signal

The ticket flags ventures and manual activity as the noise sources. There is a much harder problem:
**AutoRetainer moves the retainer's gil itself, before EMM's window opens.** The per-retainer sequence is

```
… venture reassign … entrust duplicates … withdraw / deposit gil … auto-vendor …
TaskPostprocessRetainerIPC.Enqueue(retainer);      // <- EMM's window
```
— [`SchedulerMain.cs:174-193`](https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Scheduler/SchedulerMain.cs#L174-L193).

`TaskWithdrawGil` empties the retainer's gil by a configured percentage
([`TaskWithdrawGil.cs`](https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Scheduler/Tasks/TaskWithdrawGil.cs)),
`TaskDepositGil` pushes player gil in the other way
([`TaskDepositGil.cs`](https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Scheduler/Tasks/TaskDepositGil.cs)),
and the auto-vendor step sells inventory to an NPC through the retainer
([`TaskVendorItems.cs`](https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Scheduler/Tasks/TaskVendorItems.cs)),
crediting the retainer. Any of the three, running before EMM sees the retainer, destroys the delta.

EMM *can* tell when this applies: `AutoRetainer.GetAdditionalRetainerData(cid, retainerName)` returns
`AdditionalRetainerData` with `WithdrawGil`, `WithdrawGilPercent`, `Deposit`, `EntrustDuplicates` and
`EntrustPlan` ([`AdditionalRetainerData.cs`](https://github.com/PunishXIV/AutoRetainerAPI/blob/7ccf0f6/AutoRetainerAPI/Configuration/AdditionalRetainerData.cs);
read-modify-write must complete in one framework tick, see [#2 §5](autoretainer-ipc.md)).

**Use gil only as a tie-break, only when EMM has confirmed that no gil-moving step ran for that
retainer since the last snapshot, and never as the thing that turns a slot transition into a sale.**
Two further reasons to keep it subordinate: proceeds above the retainer's gil cap are discarded
outright (`LogMessage` 4578), and gil says nothing about *which* of several emptied slots sold.

### 3.4 The `RetainerSale` chat channel — worthwhile enrichment

Subscribe to `IChatGui.ChatMessage` and filter `XivChatType.RetainerSale`. For a sale that lands while
the character is online this yields the one thing the diff can never produce: **a true sale timestamp**,
plus the exact post-fee gil, which sidesteps tax estimation entirely.

Correlate optimistically — match `(itemId, HQ, city)` against EMM's open listings, and reconcile
properly at the next visit when the diff arrives. Do not build re-listing on this channel: it is
online-only, gives no quantity, and cannot name the retainer ([§2.3](#23-the-retainersale-chat-channel)).

### 3.5 `OfflineRetainerData.MBItems` — redundant, and slower than reading the struct directly

`MBItems` is `MarketItemCount` copied into AutoRetainer's config
([`OfflineDataManager.cs:142`](https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Modules/OfflineDataManager.cs#L142)),
reachable through `AutoRetainer.GetOfflineCharacterData(ulong cid)`. Refresh cadence, from
[`OfflineDataManager.Tick`, `:32-53`](https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Modules/OfflineDataManager.cs#L32-L53):

- written on every tick while `ConditionFlag.OccupiedSummoningBell` is set;
- otherwise written at most once per second, and only while MultiMode is active, AutoRetainer is busy,
  its window is open, or the character is logging out;
- persisted to disk at most once per five minutes;
- the whole write is gated on `GameRetainerManager.Ready && Count > 0 && Player.IsInHomeWorld`, and it
  **clears and rebuilds** `data.RetainerData` each time
  ([`:119-145`](https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Modules/OfflineDataManager.cs#L119-L145)).

So for the current character it is strictly worse than reading `RetainerManager` directly — same number,
one IPC hop later. Its **one genuine use** is cross-character: `GetRegisteredCharacters()` plus
`GetOfflineCharacterData(cid)` gives EMM a last-known `MBItems` and `Gil` for alts it has not logged into,
which is enough to show "retainer X on character Y may have sold something" in a dashboard. It is a
stale snapshot from AutoRetainer's last visit, so it is a display convenience, not telemetry.

### 3.6 Universalis — not usable for own-sale detection

Covered in [#3](market-data-sources.md). Recorded history is a union of overlapping 20-sale client
uploads, so it is a sample rather than a census; and it carries buyer names, not seller identity.
EMM's own listings do not even appear on the board while the retainer is recalled. It cannot confirm a
sale of yours, and must not be used to try.

---

## 4. The four questions, answered

### 4.1 Attribution

**EMM can determine which item sold and at what price — exactly, not approximately.**

The previous snapshot for the emptied slot holds `itemId`, `isHq`, `quantity` and `unitPrice`. FFXIV
listings are purchased whole — a listing cannot be partially bought, which is why Allagan Market treats
occupied→empty as the only sale transition and computes `Total = Quantity × UnitPrice`
([`SaleTrackerService.cs:335-338`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Services/SaleTrackerService.cs#L335-L338),
[`SaleItem.cs:109`](https://github.com/Critical-Impact/AllaganMarket/blob/ae150f9/AllaganMarket/Models/SaleItem.cs#L109)).
Gross revenue is therefore exact.

Net revenue needs the tax rate. Three sources, in decreasing order of quality:

1. **The `RetainerSale` chat message**, when the sale happened online — it states the after-fee gil
   directly, no estimation ([§2.3](#23-the-retainersale-chat-channel)).
2. **The sell dialog**, at list time. `AddonRetainerSell` exposes `AskingPrice`, `Total` and `Tax` text
   nodes ([`AddonRetainerSell.cs:13-21`](https://github.com/aers/FFXIVClientStructs/blob/c827f21/FFXIVClientStructs/FFXIV/Client/UI/AddonRetainerSell.cs#L13-L21)).
   EMM drives that dialog, so it can record the real figure the game computed rather than guessing.
   Caveat: city tax rates change over time, so the rate captured at list time may not be the rate
   applied at sale time.
3. **Universalis `GET /api/v2/tax-rates?world=…`**, keyed by city ([#3 §1.5](market-data-sources.md#15-supporting-endpoints)),
   or `IMarketBoard.TaxRatesReceived`, which per Dalamud's own documentation only fires when accessing a
   retainer vocate ([#3 §2.1](market-data-sources.md#21-there-is-a-first-party-service-imarketboard)) —
   too rare to depend on.

**Do not hardcode 5%/3% the way Allagan Market does.** Record gross and the tax basis separately so a
later correction is possible.

### 4.2 Missed sales across sessions

**The diff recovers every sale individually, not a net difference.** Three items selling while offline
empty three distinct slots, and each emptied slot carries its own complete previous state. There is no
netting, because the unit of comparison is the slot, not a count.

This is the decisive advantage of the diff over `MarketItemCount`, which *does* net
([§3.2](#32-marketitemcount-delta-at-the-bell--recommended-fallback-and-trigger)).

What is genuinely lost:

- **Sale time.** The diff timestamps at detection, exactly the flaw recorded against Allagan Market's
  `SoldAt` in [#4 §1b](companion-plugins.md#1b-what-it-does-not-do--the-gap-inventory). EMM must store
  `detectedAt` and a separate, nullable `soldAt` populated only from a chat message. Never present
  detection time as sale time, and disable time-of-day and day-of-week analytics for rows without a
  true `soldAt`.
- **Ordering** of multiple offline sales.
- **Slot churn.** If a slot sold *and* was relisted with something else between two EMM visits, the
  diff sees only `occupied → occupied, different item` and the sale is unrecoverable — Allagan Market's
  `"Item has been switched"` case. Because EMM performs its own relisting, this only occurs when the
  player relists manually with EMM disabled. Surface it as an explicit unreconciled row rather than
  silently dropping it; the `// TODO: Add reconcilation tab` left unbuilt in the prior art is precisely
  this gap.
- **Anything on a character EMM never visits.** No visit, no snapshot, no diff.

### 4.3 Sale vs cancellation vs expiry

**Expiry is structurally distinct and cannot be confused with a sale.** Per upstream's own annotation,
`MarketExpire` is *"7 Days after last opened retainer"*
([`RetainerManager.cs:55`](https://github.com/aers/FFXIVClientStructs/blob/c827f21/FFXIVClientStructs/FFXIV/Client/Game/RetainerManager.cs#L55)).
When it lapses the retainer stops offering its listings, but the items remain in the retainer's sell
list; summoning the retainer puts them back on the board. So expiry does **not** empty a
`RetainerMarket` slot — it produces no diff at all. EMM detects it as a clock comparison,
`now > MarketExpire`, and the correct response is "visit this retainer to re-offer", **not** "re-list".
This is also a scheduling input: any retainer visited more often than weekly never expires, which for an
AutoRetainer-driven setup is essentially all of them.

That leaves **sale vs cancellation** on an emptied slot. Three tests, in order:

1. **EMM's own action log.** EMM performs the delists. A slot EMM emptied is a cancellation, full stop.
   Note that repricing does *not* empty a slot — price is editable in place — whereas a quantity change
   is a delist-and-relist (`LogMessage` 492).
2. **Where the item went.** A cancelled listing returns to the retainer's inventory
   (`RetainerPage1..7`, `InventoryType` 10000–10006); a sold one does not, and the retainer's gil rises
   instead. Snapshot the retainer inventory in the same pass and check for a matching
   `(itemId, isHq)` quantity increase. This is a strictly better discriminator than a bare gil delta
   because it survives AutoRetainer's gil handling, and it is available in the same window.
3. **Gil**, only under the conditions in [§3.3](#33-gil-delta--rejected-as-a-primary-signal).

Residual ambiguity: a manual delist performed by the player at a bell **while EMM is not running**,
where the returned item is then also moved or sold before EMM's next visit, defeats all three tests.
Classify it `Unknown` and show it; do not guess, and do not re-list on it.

### 4.4 Cost basis — what to capture at buy time

[#4 §8](companion-plugins.md#8-buy-side-coverage-across-the-ecosystem) established that **no plugin in
the ecosystem records purchase price**, which is why none can report profit. The hooks to close that
gap already exist in Dalamud and are first-party.

**Market board purchases — the complete record is handed to EMM.**

`IMarketBoard.PurchaseRequested` fires with `IMarketBoardPurchaseHandler`:

```
RetainerId, ListingId, CatalogId (item id), ItemQuantity, PricePerUnit, IsHq, TotalTax, RetainerCityId
```
— [`IMarketBoardPurchaseHandler.cs`](https://github.com/goatcorp/Dalamud/blob/8304201/Dalamud/Game/Network/Structures/IMarketBoardPurchaseHandler.cs).
`IMarketBoard.ItemPurchased` then fires with `IMarketBoardPurchase` — `CatalogId`, `ItemQuantity` only
— on the server's confirmation
([`IMarketBoardPurchase.cs`](https://github.com/goatcorp/Dalamud/blob/8304201/Dalamud/Game/Network/Structures/IMarketBoardPurchase.cs)).
Dalamud hooks the outgoing side at `InfoProxyItemSearch.SendPurchaseRequestPacket` and the incoming side
at `PacketDispatcher.HandleMarketBoardPurchasePacket`
([`NetworkHandlers.cs`](https://github.com/goatcorp/Dalamud/blob/8304201/Dalamud/Game/Network/Internal/NetworkHandlers.cs)),
so both are the local player's own purchases.

**Pair them.** The request carries the price; the confirmation carries the fact that it succeeded.
Purchases fail routinely — `LogMessage` 386 *"Unable to make market purchase."*, 388 *"…Inventory is
full."*, 390 *"Unable to complete market transaction."* — so a request alone must not be booked as a
cost. Same-frame verification is available from memory:
`InfoProxyItemSearch.LastPurchasedMarketboardItem` carries
`RetainerId, ListingId, ItemId, Quantity, UnitPrice, TotalTax, ContainerIndex, IsHqItem, TownId` with a
`Present => ListingId != 0` guard
([`InfoProxyItemSearch.cs:126-138`](https://github.com/aers/FFXIVClientStructs/blob/c827f21/FFXIVClientStructs/FFXIV/Client/UI/Info/InfoProxyItemSearch.cs#L126-L138)).

**Book the buyer's tax.** The buyer pays `PricePerUnit × ItemQuantity + TotalTax`. Unit cost basis is
that total divided by quantity — not `PricePerUnit`. Getting this wrong understates cost by the full
tax rate and is the same class of error as TopSellingItems' tax-free "profit"
([#4 §7](companion-plugins.md#topsellingitems--and-the-tax-hole)).

**The minimum acquisition record**, written at buy time and carried through to the sale:

| Field | Source |
|---|---|
| `itemId`, `isHq` | `IMarketBoardPurchaseHandler.CatalogId` / `.IsHq` |
| `quantity` | `.ItemQuantity` |
| `unitPricePaid`, `taxPaid` | `.PricePerUnit`, `.TotalTax` |
| `acquiredAt` | wall clock at the confirmation event |
| `worldId`, `cityId` | `IClientState`, `.RetainerCityId` |
| `sourceListingId`, `sourceRetainerId` | `.ListingId`, `.RetainerId` |
| `acquisitionKind` | `MarketBoard` \| `Vendor` \| `Craft` \| `Gather` \| `Venture` \| `Other` |
| `characterContentId` | `IClientState.LocalContentId` |

**Non-market acquisitions.** NPC vendor purchases are recoverable from
`ShopEventHandler`: `TransactionType` (`1 = buying`), `TransactionItemId`, `TransactionItemCount`,
`BuyItemIndex` into an item array whose entries carry `PriceBuy`
([`ShopEventHandler.cs:49-70`](https://github.com/aers/FFXIVClientStructs/blob/c827f21/FFXIVClientStructs/FFXIV/Client/Game/Event/ShopEventHandler.cs#L49-L70)),
with the static prices also available from the `GilShop` / `GilShopItem` Excel sheets. Crafted,
gathered and venture-sourced items have **no** acquisition hook and no natural cost; the ledger must
allow a null or user-supplied basis and the analytics must report coverage ("cost basis known for N% of
units sold") rather than silently treating unknown as zero.

**Design consequence for the ledger.** Because listings are bought whole and stacks are merged and split
freely, a sold stack of 30 will usually not map to one purchase. Track acquisitions as lots and consume
them with a declared, visible policy — FIFO is the defensible default — rather than pretending a sale
has a single cost. Record the policy alongside the numbers so realised margin is reproducible.

---

## 5. Recommended design

```
  summoning bell opens (RetainerList)
        │
        ├─ read RetainerManager for ALL retainers:
        │     MarketItemCount, Gil, MarketExpire, ItemCount
        │  → coverage + staleness map; flag count drops; flag now > MarketExpire
        │
        ▼
  AutoRetainer opens retainer N, fires OnRetainerReadyToPostprocess(name)
        │                                      ┌─────────────────────────────┐
        ├─ SNAPSHOT (before touching anything):│ RetainerMarket[0..19]       │
        │     item, HQ, quantity  ────────────►│ + GetRetainerMarketPrice(i) │
        │     retainer inventory 10000..10006  │ + Retainer.Gil              │
        │                                      └─────────────────────────────┘
        ├─ DIFF against the stored snapshot → sale / delist / reprice / unreconciled
        ├─ decide + apply repricing, delists, relists   (EMM logs every action it takes)
        ├─ SNAPSHOT again (post-action baseline)
        └─ FinishRetainerPostProcess()   ← in a finally; AutoRetainer blocks forever otherwise

  in parallel, whenever online:
     IChatGui.ChatMessage, XivChatType.RetainerSale (71)
        → true soldAt + exact after-fee gil, matched opportunistically to open listings

  buy side:
     IMarketBoard.PurchaseRequested  → price, quantity, HQ, tax   ┐
     IMarketBoard.ItemPurchased      → confirmation               ┘ → acquisition lot
```

Rules that fall out of the research:

1. **Snapshot before EMM acts, and again after.** The pre-action snapshot is the diff baseline; the
   post-action one prevents EMM's own writes from being read back as market events next visit.
2. **`FinishRetainerPostProcess()` in a `finally`.** AutoRetainer waits with
   `timeLimitMS: int.MaxValue` and disarms its own bailout while EMM holds the lock
   ([#2 §6](autoretainer-ipc.md)). A snapshot or diff that throws must not hang the player's retainer run.
3. **Key retainers by `RetainerId`, not by name.** AutoRetainer's events carry only the name string
   ([#2 §5](autoretainer-ipc.md)), so EMM resolves name → `RetainerManager.Retainer.RetainerId` on entry
   and stores against the ID. Names are user-changeable; IDs are not.
4. **Store gross and tax basis separately.** Never bake an assumed rate into a stored revenue figure.
5. **Keep `detectedAt` and `soldAt` as distinct, and let `soldAt` be null.**
6. **Give unreconciled transitions a first-class state and a UI.** The prior art logged them and
   shipped a `// TODO`. They are the honest measure of how much EMM does not know.
7. **Never re-list on an inference.** Re-list on `Sold` (confirmed) or on an explicit user action.
   `Unknown`, `Delisted` and `Expired` each get their own behaviour, and `Expired` means "visit the
   retainer", not "create a new listing".

---

## 6. What EMM will not be able to know

Stated plainly, because the re-listing and the analytics are built on top of these:

1. **When a sale happened, unless it happened while that character was online.** Offline sales are
   timestamped at detection. Any hour-of-day, day-of-week or time-to-sell analytic must either exclude
   them or be labelled as an upper bound on elapsed listing time.
2. **The order of several offline sales** on the same retainer.
3. **Who bought it.** Not exposed for your own sales on any channel. (The public market history carries
   buyer names for the last 20 sales of an item, but that is not a record of yours and is subject to the
   per-retainer public/private sale-history setting — `LogMessage` 4104/4105.)
4. **Whether the sale was undercut-driven.** EMM knows its own price and the board's floor at the times
   it looked; it cannot know the board state at the instant of sale.
5. **Anything about a character or retainer EMM has never visited** beyond a last-known count. No visit,
   no snapshot.
6. **A sale that was followed by a manual relist into the same slot before EMM's next visit.** Recorded
   as unreconciled; the sale itself is unrecoverable.
7. **A manual delist performed with EMM not running, where the returned item then moves on** — sale and
   cancellation become indistinguishable.
8. **Exact net proceeds for an offline sale, if the tax rate changed between listing and sale.** EMM has
   the gross exactly; the net is an estimate unless a chat message covered it.
9. **Proceeds lost to the retainer gil cap.** The game discards the excess (`LogMessage` 4578) and there
   is no record of how much. EMM can warn beforehand; it cannot reconstruct the loss afterwards.
10. **Cost basis for crafted, gathered or venture-sourced goods**, without user input. Only market and
    NPC-vendor purchases carry an observable price.

---

## 7. Open items

- **UNCONFIRMED:** whether `RetainerSale` (71) messages are replayed on login for offline sales. Design
  assumes not ([§2.3](#23-the-retainersale-chat-channel)). One live session with an offline sale settles it.
- **UNCONFIRMED:** whether `MarketItemCount` counts occupied slots or actively-offered listings; they
  diverge after `MarketExpire` ([§3.2](#32-marketitemcount-delta-at-the-bell--recommended-fallback-and-trigger)).
- **UNCONFIRMED:** the distinction between `LogMessage` 745 and 748, and the trigger condition for 384.
- **UNCONFIRMED (runtime):** that the `RetainerMarket` container and `_retainerMarketPrices` are
  populated at the exact instant `OnRetainerReadyToPostprocess` fires, as opposed to only after
  `RetainerSellList` has been opened once ([§1](#1-where-emm-gets-its-observation-window)). Guard on
  `IsLoaded` regardless.
- **UNCONFIRMED:** whether `MarketExpire` is refreshed by AutoRetainer merely opening the retainer, or
  requires the sell list to be opened. The upstream comment says "last opened retainer", which suggests
  the former.
- **Not measurable:** the true sale time of an offline sale, by any means available to a plugin.

---

## 8. Sources and pins

| Artifact | Pin | Date |
|---|---|---|
| `aers/FFXIVClientStructs` | `c827f21e38bb72c0e2f691c86f1e9228f7d482e0` | 2026-08-17T01:27:57Z |
| `goatcorp/Dalamud` master | `83042016d0e9996dc44c9f7fd96a8d33a5e586f2` | 2026-08-14T16:25:22Z |
| `Critical-Impact/AllaganMarket` | `ae150f9366e3c42173362c566f7817c8de56fbd7` | 2026-08-09 |
| `PunishXIV/AutoRetainer` | `c281a92977a742bcd1911a263332cd4f32227dec` | 2026-08-08T15:56:20+03:00 |
| `PunishXIV/AutoRetainerAPI` | `7ccf0f6b4c7923821a43ed1e92456c9d5d7132f2` | 2026-08-08T04:02:29+03:00 |
| `Populo/RetainerSales` | `a9b62a89a04625262e6265ed982c975fe6b5a7a5` | 2026-04-29T18:12:58-04:00 |

Locally installed builds cross-checked, 2026-08-17:
`Dalamud.dll` 15.0.3.2, `FFXIVClientStructs.dll` 7.55.1.8875 (both under
`%APPDATA%\XIVLauncher\addon\Hooks\dev`); `AutoRetainer` 4.6.1.27, `AllaganMarket` 1.4.0.2,
`InventoryTools` 1.15.0.10 (under `%APPDATA%\XIVLauncher\installedPlugins`). Presence of
`MarketItemCount`, `MarketExpire`, `GetRetainerMarketPrice`, `SetRetainerMarketPrice`,
`LastPurchasedMarketboardItem`, `_retainerMarketPrices` and `MoveToRetainerMarket` in the shipped
FFXIVClientStructs assembly was confirmed by a symbol scan of the DLL.

Game data (`LogMessage` sheet rows 384, 386, 388, 390, 420, 492, 745, 748, 4104, 4105, 4578, 6081, 6082;
`Addon` sheet) was queried live against `https://v2.xivapi.com/api/sheet/LogMessage/{row}` and
`https://v2.xivapi.com/api/search`, 2026-08-17. Sheet text renders parameters blank, hence the empty
slots and literal `0 gil` in the quoted strings.

The official plugin listing `https://kamori.goats.dev/Plugin/PluginMaster` (480 entries, retrieved
2026-08-17) was used to locate Retainer Sales (`RetainerSales`, Populo, API 15).

Dalamud API documentation: [`IMarketBoard`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IMarketBoard/),
[`IGameInventory`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IGameInventory/),
[`GameInventoryType`](https://dalamud.dev/api/Dalamud.Game.Inventory/Enums/GameInventoryType/).

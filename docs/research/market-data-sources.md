# Marketboard data sources available to a Dalamud plugin

Research note resolving issue [#3](https://github.com/local-variable/EMM/issues/3).

- **Written:** 2026-08-17 (UTC)
- **Environment assumed:** Dalamud API level 15, .NET 10.0.0
- **Scope note:** EMM **consumes** market data only. Uploading observed data to Universalis is out of scope and is not covered here (see [§6](#6-why-emm-must-not-upload)).

All live-API measurements in this note were taken on **2026-08-17T01:50–02:10Z** against `https://universalis.app`. All local reflection results were taken against the Dalamud dev assemblies installed at `%APPDATA%\XIVLauncher\addon\Hooks\dev` (file timestamps 2026-08-14).

---

## Summary of the recommendation

| Need | Source | Why |
|---|---|---|
| Price history graph, confidence bands, undercut floor statistics | **Universalis REST v2** `/api/v2/history/{worldDcRegion}/{itemIds}` | Only source with multi-year, per-sale depth. |
| Cheap cross-world / cross-DC "where is it cheapest" summary | **Universalis REST v2** `/api/v2/aggregated/{worldDcRegion}/{itemIds}` | Server-side cached; explicitly recommended over `CurrentlyShown` when individual rows are not needed. |
| Full listing rows for the undercut calculation | **Universalis REST v2** `/api/v2/{worldDcRegion}/{itemIds}` | Only endpoint returning individual listings with retainer / HQ / quantity. |
| Ground-truth listings and last-20 sales for the item the player is looking at right now | **Dalamud `IMarketBoard`** + **FFXIVClientStructs `InfoProxyItemSearch`** | Zero latency, zero staleness, no rate limit. Only available for items the player actually opens. |
| Item names, IDs, marketability, stack sizes, HQ capability | **Lumina Excel sheets via `IDataManager`** | Ships with the game client; no network call. |
| Live invalidation of cached Universalis data | **Universalis WebSocket** `wss://universalis.app/api/ws` | Optional. Recommended as a cache-invalidation signal only, not a primary feed. |

---

## 1. Universalis

### 1.1 Versioned API surface

Universalis publishes OpenAPI 3.0.1 documents per API version. Verified live:

| Spec | URL | Reported `info.version` |
|---|---|---|
| v1 | `https://universalis.app/swagger/v1/swagger.json` | `v1` |
| v2 | `https://universalis.app/swagger/v2/swagger.json` | `v2` |
| v3 | `https://universalis.app/swagger/v3/swagger.json` | `v3` |

The human-facing docs site is <https://docs.universalis.app/> (a Swagger-UI-style renderer of the same specs; `https://universalis.app/docs/index.html` 301-redirects there).

**v2 paths** (from `swagger/v2/swagger.json`, retrieved 2026-08-17):

```
GET /api/v2/{worldDcRegion}/{itemIds}
GET /api/v2/history/{worldDcRegion}/{itemIds}
GET /api/v2/aggregated/{worldDcRegion}/{itemIds}
GET /api/v2/marketable
GET /api/v2/tax-rates
GET /api/v2/worlds
GET /api/v2/data-centers
GET /api/v2/lists/{listId}
GET /api/v2/extra/content/{contentId}
GET /api/v2/extra/stats/least-recently-updated
GET /api/v2/extra/stats/most-recently-updated
GET /api/v2/extra/stats/recently-updated
GET /api/v2/extra/stats/upload-history
GET /api/v2/extra/stats/uploader-upload-counts
GET /api/v2/extra/stats/world-upload-counts
GET /api/v2/ws                                   (WebSocket upgrade; hidden from the spec in release builds)
```

**v3 paths** (from `swagger/v3/swagger.json`, retrieved 2026-08-17):

```
GET /api/v3/game/worlds
GET /api/v3/game/data-centers
GET /api/v3/game/marketable-items
GET /api/v3/market/overview/{servers}/{itemId}
GET /api/v3/market/sales/{servers}/{itemId}      (cursor-paginated, 100 sales/page)
GET /api/v3/misc/time-zones
```

**Recommendation: build on v2.** v3 is live and reachable but is a much smaller surface, has no aggregated endpoint, caps `overview` history at 20 sales per world ([`OverviewController.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V3/Market/OverviewController.cs) uses `Count = 20`), and — importantly — reports a **tax-inclusive** unit price:

```csharp
var total = (int)Math.Ceiling(listing.PricePerUnit * listing.Quantity * 1.05);
// ...
PricePerUnit = total / Convert.ToDecimal(listing.Quantity),
```

— [`OverviewController.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V3/Market/OverviewController.cs)

v2 instead returns the raw `pricePerUnit` with `tax` as a separate field, which is what an undercut calculation needs. Mixing the two would produce a silent 5% error. The stability guarantees of v3 are **UNCONFIRMED** — it is not linked from the docs landing page.

### 1.2 Current listings — `GET /api/v2/{worldDcRegion}/{itemIds}`

> "Retrieves the data currently shown on the market board for the requested item and world or data center. Up to 100 item IDs can be comma-separated in order to retrieve data for multiple items at once."
> — `swagger/v2/swagger.json`

Path parameters:

- `worldDcRegion` — world, DC, **or region**, by ID or name. Documented region names: `Japan`, `Europe`, `North-America`, `Oceania`, `China`, `中国`.
- `itemIds` — one ID, or up to 100 comma-separated.

Query parameters (all from the v2 spec):

| Parameter | Default | Notes |
|---|---|---|
| `listings` | all | Listings returned per item. |
| `entries` | **5** | Recent history entries returned per item. |
| `hq` | both | Filter HQ/NQ. |
| `statsWithin` | **7 days** (ms) | Window for the computed statistics. |
| `entriesWithin` | unset (seconds) | Window for the returned recent entries. |
| `fields` | all | Comma-separated projection, e.g. `listings.pricePerUnit`. **For multi-item queries the prefix changes to `items.`** (e.g. `items.listings.pricePerUnit`). |

Response — `CurrentlyShownView` (single item). Field names verified from the v2 spec:

```
itemID, worldID, worldName, dcName, regionName, lastUploadTime (ms since epoch),
listings[], recentHistory[],
currentAveragePrice / …NQ / …HQ        (listing averages)
averagePrice / …NQ / …HQ               (sale averages)
minPrice / …NQ / …HQ, maxPrice / …NQ / …HQ
regularSaleVelocity / nqSaleVelocity / hqSaleVelocity
stackSizeHistogram / …NQ / …HQ
worldUploadTimes                       (map of worldId -> ms, on DC requests)
listingsCount, recentHistoryCount, unitsForSale, unitsSold,
hasData                                (false = never updated; useful for new items)
```

`listings[]` is `ListingView`:

```
listingID, lastReviewTime (seconds), pricePerUnit, quantity, total, tax,
hq, isCrafted, onMannequin, stainID, materia[],
retainerID, retainerName, retainerCity, sellerID (SHA256), creatorID (SHA256), creatorName,
worldID, worldName                     (populated on DC/region requests)
```

`recentHistory[]` is `SaleView`: `hq, pricePerUnit, quantity, total, timestamp (seconds), onMannequin, buyerName, worldID, worldName`.

For multi-item requests the envelope is `CurrentlyShownMultiViewV2`: `itemIDs[]`, `items` (keyed by item ID), `unresolvedItems[]`, `worldID`/`worldName`/`dcName`/`regionName`. Invalid IDs in a batch land in `unresolvedItems` rather than producing a 404.

Note the caveat Universalis attaches to the velocity fields on this endpoint:

> "This number will tend to be the same for every item, because the number of shown sales is the same and over the same period. This statistic is more useful in historical queries."
> — `regularSaleVelocity` description, `swagger/v2/swagger.json`

**Do not use `regularSaleVelocity` from `CurrentlyShown`.** Take velocity from `/history` or `/aggregated`.

### 1.3 Sale history — `GET /api/v2/history/{worldDcRegion}/{itemIds}`

Query parameters (v2 spec):

| Parameter | Default | Max / notes |
|---|---|---|
| `entriesToReturn` | **1800** | **max 99999** |
| `entriesWithin` | **7 days** (seconds) | Window before `entriesUntil` (or now). Negative ignored. |
| `entriesUntil` | now | UNIX seconds. Enables backward paging. |
| `statsWithin` | 7 days (ms) | Window for the computed stats. |
| `minSalePrice` / `maxSalePrice` | — | Inclusive unit-price filter. |

Response — `HistoryView`: `itemID, worldID, worldName, dcName, regionName, lastUploadTime, entries[], stackSizeHistogram(/NQ/HQ), regularSaleVelocity / nqSaleVelocity / hqSaleVelocity`.

`entries[]` is `MinimizedSaleView`: `hq, pricePerUnit, quantity, buyerName, onMannequin, timestamp (seconds), worldID, worldName`.

> ⚠️ **`quantity` is the stack size of the sale, and `pricePerUnit` is per unit.** Units sold in a window is `Σ quantity`, not `count(entries)`. A gil-weighted mean must weight by `quantity`.

**The two defaults are the trap.** A naive `GET /api/v2/history/{world}/{item}` returns at most 1800 entries *and* only the last 7 days. Deep history requires setting both `entriesToReturn` and `entriesWithin` explicitly.

#### History depth and granularity — measured

**Granularity is one row per individual sale, timestamped to the second.** There is no server-side bucketing, no daily/hourly rollup endpoint, and no OHLC. Any candle, moving average, or band EMM draws must be computed client-side from raw sale rows.

**Depth is "everything Universalis has ever recorded for that item on that world"** — there is no documented retention cutoff, and in practice it is multi-year. Measured 2026-08-17 with `entriesToReturn=99999&entriesWithin=999999999`:

| World / item | Entries returned | Oldest entry | Newest entry |
|---|---|---|---|
| Jenova / 5 | 6,892 | 2022-10-31T09:11Z | 2026-08-15T06:48Z |
| Jenova / 5057 | 14,785 | 2022-11-01T23:52Z | 2026-08-16T21:06Z |
| Jenova / 44104 | 2,085 | 2024-07-02T21:06Z | 2026-08-15T22:08Z |
| Gilgamesh / 5 | 5,678 | **2020-10-21T11:53Z** | 2026-08-14T23:01Z |
| Cerberus / 5 | 5,033 | 2022-10-30T23:52Z | 2026-08-15T18:03Z |

So the *practical* ceiling is the 99999-entry cap plus whatever the item's history actually contains — for the items sampled, 2–6 years and 2k–15k rows. Older-than-2022 data exists (Gilgamesh/5 reaches 2020), so there is no hard global cutoff. **A single request can therefore cover the entire usable history of a typical item.** For pathological items, page backwards with `entriesUntil`.

**Caveat that limits how deep a graph should honestly go:** history is a union of client uploads, and each upload can carry at most **20 sales** (see [§2.2](#22-what-the-client-actually-sends)). Old, thin regions of the series are sparse samples, not a census. Recommend rendering full depth but only drawing confidence bands where sample density supports them.

#### Per-world and per-DC aggregation is server-side

Confirmed live. `GET /api/v2/history/Aether/44104?entriesToReturn=3&entriesWithin=604800` returns `dcName: "Aether"` with `worldName`/`worldID` stamped on each entry (`Gilgamesh`/63, `Midgardsormr`/65, …). The same works for `/api/v2/{worldDcRegion}/{itemIds}`, and for regions. **EMM does not need to fan out per world and merge.**

### 1.4 Aggregated — `GET /api/v2/aggregated/{worldDcRegion}/{itemIds}`

> "AverageSalePrice and DailySaleVelocity are calculated based on sales of the last 4 days. **This API uses only cached values and is therefore strongly preferred over CurrentlyShown if individual sales/listings are not required.**"
> — `swagger/v2/swagger.json` (emphasis added)

This is the only explicit caching guidance Universalis publishes. Up to 100 item IDs per call.

Shape: `results[]` of `{ itemId, nq: AggregatedResult, hq: AggregatedResult, worldUploadTimes[] }`, plus `failedItems[]`. Each `AggregatedResult` carries `minListing`, `medianListing`, `recentPurchase`, `averageSalePrice`, `dailySaleVelocity`, and each of those has `world` / `dc` / `region` sub-entries.

- `minListing.{world,dc,region}` → `{ price, worldId }`
- `recentPurchase.{world,dc,region}` → `{ price, timestamp (ms), worldId }`
- `averageSalePrice.{world,dc,region}` → `{ price }`
- `dailySaleVelocity.{world,dc,region}` → `{ quantity }`

> ⚠️ **`medianListing` is declared in the schema but never populated.** [`AggregatedMarketBoardDataController.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V2/AggregatedMarketBoardDataController.cs) assigns `MinListing`, `RecentPurchase`, `AverageSalePrice` and `DailySaleVelocity` in `GetAggregatedResult`, and has no `MedianListing =` assignment anywhere. Confirmed empirically: `medianListing` was absent from live responses for both `Jenova/5` and `Aether/44104`. **Do not build the undercut floor on it.**

Also note the `hq` block is `{}` (all sub-objects empty) for items that cannot be HQ, and `world` entries are absent entirely on a DC/region-scoped request.

### 1.5 Supporting endpoints

- `GET /api/v2/marketable` → flat array of marketable item IDs. Measured 2026-08-17: **16,843 IDs**. Useful as a cross-check against the local Lumina computation ([§4](#4-item-metadata-from-lumina)), but Lumina should be the source of truth since it needs no network call.
- `GET /api/v2/tax-rates?world=…` → `TaxRatesView`, keyed by city name: `Limsa Lominsa, Gridania, Ul'dah, Ishgard, Kugane, Crystarium, Old Sharlayan, Tuliyollal`. Values are percent retainer tax. Per the spec, "This data is provided by the Retainer Vocate in each major city."
- `GET /api/v2/worlds`, `GET /api/v2/data-centers` → ID/name maps.
- `GET /api/v2/extra/stats/{least,most}-recently-updated?world=…&entries=…` (default 50, **max 200**) → per-item upload times. Useful for freshness diagnostics.

### 1.6 WebSocket — `wss://universalis.app/api/ws`

Routed by [`WebSocketController.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V2/WebSocketController.cs) at `api/ws` (v1) and `api/v{version}/ws` (v2). It is marked `[ApiExplorerSettings(IgnoreApi = true)]` outside DEBUG builds, which is why it does not appear in the published spec — but it is a real, maintained endpoint.

Protocol, from [`SocketClient.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Realtime/SocketClient.cs) and [`EventCondition.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Realtime/Messages/EventCondition.cs):

- **Frames are BSON, binary WebSocket message type.** Not JSON.
- Client → server messages: `{ event: "subscribe" | "unsubscribe", channel: "<channel>" }`. Any other `event` value gets a `SubscribeFailure` reply.
- **Inbound client messages are capped at 1 KB** (`var buf = new byte[1024]`).
- Channel grammar: `"<a>/<b>{filter=value, filter=value}"`. The parser's own example is `"listings/add{world=74, item=5}"`.
- Available channels (one class each under `Realtime/Messages/`): `listings/add`, `listings/remove`, `sales/add`, `item/update`. Supported filters on all four: `world`, `item`.
- Subscribing to a bare prefix (e.g. `listings`) matches both `add` and `remove`; the server keeps the more/less specific condition rather than duplicating.
- Message payloads reuse the v1 view types: `ListingsAdd`/`ListingsRemove` carry `item`, `world`, `listings[]` (`ListingView`); `SalesAdd` carries `item`, `world`, `sales[]` (`SaleView`); `ItemUpdate` carries only `item`, `world`.
- Server keep-alive interval is 2 minutes (`app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromMinutes(2) })` in [`Startup.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Startup.cs)).
- **Slow clients are dropped, then disconnected.** Per-client outbound buffer is 512 messages with `BoundedChannelFullMode.DropOldest`; a watchdog aborts the connection if the queue stays saturated for 30 s. The source comments size the buffer against "~28msg/s" steady-state per client as of 2026-05-05.

There is a known upstream defect: [Universalis issue #1346](https://github.com/Universalis-FFXIV/Universalis/issues/1346) reports that `listings/add` sends the full current state and `listings/remove` the full previous state, rather than the deltas the names imply. **Treat WS payloads as "something changed for (item, world)" and re-fetch over REST**, rather than applying them as diffs.

### 1.7 Rate limits, caching, and terms

**Rate limits** (verbatim from the <https://docs.universalis.app/> landing copy, extracted from the page bundle on 2026-08-17):

> "There is a rate limit of 25 req/s (50 req/s burst) on the API, and 15 req/s (30 req/s burst) on the website itself, if you're scraping instead. The number of simultaneous connections per IP is capped to 8."

So, for EMM:

- **25 req/s sustained, 50 burst, 8 concurrent connections per IP.**
- These are per-IP, and a household or shared connection may host several plugin users. Budget well under the ceiling — a client-side limiter of a few requests per second with at most 2–4 concurrent connections is ample and leaves headroom.
- The **100-item batch** on `CurrentlyShown`, `history`, and `aggregated` is the real lever: a 500-item watchlist is 5 requests, not 500.

**Caching:** Universalis publishes no HTTP cache headers. Measured 2026-08-17 on `/api/v2/Jenova/5` and `/api/v2/history/Jenova/5`: no `Cache-Control`, no `ETag`, no `Expires`; `cf-cache-status: DYNAMIC` on every response, including `/api/v2/aggregated/...`. Round-trip times observed were 141–239 ms. **Caching is entirely EMM's responsibility.** The only server-side guidance is the aggregated endpoint's "uses only cached values and is therefore strongly preferred over CurrentlyShown" note.

**Terms of use:** the Universalis *source* is MIT (`info.license` in every spec; [LICENSE](https://github.com/Universalis-FFXIV/Universalis/blob/master/LICENSE)). A separate, formal terms-of-service or data-licence document for the *API data* is **UNCONFIRMED** — none was found on <https://universalis.app/about>, <https://docs.universalis.app/>, or in the repository. The docs page does make one request:

> "If you use this API heavily for your projects, please consider supporting the website on Liberapay, Ko-fi, or Patreon…"
> — <https://docs.universalis.app/>

Practical obligations to adopt regardless: stay within the published limits, cache aggressively, and send a descriptive `User-Agent`. Universalis reads `User-Agent` off every request and records it (`UserAgentMetrics.RecordUserAgentRequest(userAgent, …)` in [`CurrentlyShownController.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V2/CurrentlyShownController.cs)); it is not enforced, but an identifiable UA is how the operators diagnose a misbehaving client instead of blocking an IP range. Also credit Universalis visibly in the UI.

---

## 2. In-game data via Dalamud and FFXIVClientStructs

### 2.1 There is a first-party service: `IMarketBoard`

**Yes — there is a supported way to read listings and sale history, and EMM does not need to write any packet-interception code.** Dalamud exposes [`Dalamud.Plugin.Services.IMarketBoard`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Plugin/Services/IMarketBoard.cs) ([API docs](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IMarketBoard/)), injected like any other Dalamud service:

| Event | Delegate payload | Fires when |
|---|---|---|
| `OfferingsReceived` | `IMarketBoardCurrentOfferings` | "current offerings are received for a specific item on the market board" |
| `HistoryReceived` | `IMarketBoardHistory` | "historical sale listings are received for a specific item" |
| `ItemPurchased` | `IMarketBoardPurchase` | an item is purchased |
| `PurchaseRequested` | `IMarketBoardPurchaseHandler` | the player requests a purchase |
| `TaxRatesReceived` | `IMarketTaxRates` | "These events only occur when accessing a retainer vocate and requesting the tax rates." |

`IMarketBoardItemListing` exposes: `ItemId, IsHq, ItemQuantity, PricePerUnit, TotalTax, ListingId, RetainerId, RetainerName, RetainerCityId, ArtisanId, Materia[], MateriaCount, OnMannequin, Stain1Id, Stain2Id`.

`IMarketBoardHistoryListing` exposes: `SalePrice, Quantity, IsHq, OnMannequin, PurchaseTime (DateTime), BuyerName`.

**On "without packet interception":** Dalamud implements this by hooking the *client's own* packet-handling functions — [`NetworkHandlers.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Internal/NetworkHandlers.cs) installs hooks on `PacketDispatcher.HandleMarketBoardItemRequestStartPacket`, `PacketDispatcher.HandleMarketBoardPurchasePacket`, `InfoProxyItemSearch.ProcessItemHistory`, `InfoProxyItemSearch.AddPage` and `InfoProxyItemSearch.SendPurchaseRequestPacket`, and republishes them as Rx observables that [`MarketBoard.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Marketboard/MarketBoard.cs) fans out to plugins. This is function hooking inside the client, not wire sniffing, and — decisively for EMM — **it is Dalamud's code, not the plugin's**. From EMM's side it is a `+=` on an event.

### 2.2 What the client actually sends

Two hard structural caps, read out of Dalamud's parsers:

- **Sale history: at most 20 entries per item.** [`MarketBoardHistory.Read`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Structures/MarketBoardHistory.cs) loops `for (var i = 0; i < 20; i++)` and breaks early on a zero price ("no price means we reached the end of available listings").
- **Listings arrive in pages of 10.** [`MarketBoardCurrentOfferings.Read`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Structures/MarketBoardCurrentOfferings.cs) loops `for (var i = 0; i < 10; i++)`. `OfferingsReceived` therefore fires once per page; group by `IMarketBoardCurrentOfferings.RequestId` and accumulate.

The 20-entry cap is the single most important fact in this document. It is the reason Universalis history is a *sample*, not a census (see [§3](#3-freshness-and-coverage)), and it is why in-game data can never substitute for Universalis for a history graph.

### 2.3 Reading listings out of memory instead

Verified locally by reflection over the shipped `FFXIVClientStructs.dll` (**version 7.55.1.8875**, [repo](https://github.com/aers/FFXIVClientStructs), MIT):

`FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch` — `InfoProxyItemSearch.Instance()`:

```
field  uint  SearchItemId
field  uint  ListingCount
field  bool  WaitingForListings
field  byte  CurrentRequestId / NextRequestId
prop   Span<MarketBoardListing>  Listings
prop   Span<...>                 RetainerListings, PlayerRetainers, WishlistItems
field  uint  RetainerListingCount, PlayerRetainerCount, WishlistSize
field  LastPurchasedMarketboardItem  LastPurchasedMarketboardItem
```

`MarketBoardListing`: `ItemId, UnitPrice, Quantity, TotalTax, IsHqItem, IsMannequin, ListingId, RetainerId, RetainerName (Utf8String as CharacterName), ContentId, ArtisanId, TownId, Stain0Id, Stain1Id, MateriaCount, Materia, Durability, Spiritbond, ContainerIndex`, plus a computed `IsSellingAsSet`.

`AgentItemSearch.Instance()` additionally exposes paging state (`ListingCurrentPage`, `ListingPageCount`, `ListingPageLoaded`, `ListingPageItemCount`) and `ResultItemId`.

This is the cleaner read path for *listings*: once the search settles, `InfoProxyItemSearch->Listings[0..ListingCount]` is the complete current set for the searched item, with no page reassembly. Read it on the framework thread.

**There is no equivalent for sale history.** Reflection over the whole assembly turned up exactly one history-related symbol — `InfoProxyItemSearch.Delegates.ProcessItemHistory` — and no struct or field holding the parsed rows. **For in-game sale history, `IMarketBoard.HistoryReceived` is the only supported route.**

### 2.4 What in-game data is good for

- Ground truth for the item the player is currently looking at: zero staleness, no rate limit, includes the player's own retainer listings.
- Correcting a stale Universalis snapshot the moment the player opens the board.
- Validating EMM's undercut recommendation against reality before the player commits.

What it is **not** good for: history graphs (20 entries), watchlists (the player must open each item), or cross-world comparison (the board is DC-scoped to where the player is standing).

---

## 3. Freshness and coverage

### 3.1 Measurements

Universalis exposes `lastUploadTime` (ms since epoch) on `CurrentlyShownView` and `HistoryView`, `worldUploadTimes` on DC-scoped requests, and `worldUploadTimes[]` on aggregated results. `hasData: false` marks items never updated at all.

**Method.** Sampled 200 item IDs uniformly at random from the 16,843 in `/api/v2/marketable`, then requested `/api/v2/{world}/{ids}?listings=0&entries=0` in two 100-item batches per world, and computed the age of `lastUploadTime` at 2026-08-17T01:58Z. "Never" = `lastUploadTime` absent/zero.

| World | Uploads recorded (all time) | n with data | Never uploaded | Median age | p75 | p90 | Max |
|---|---:|---:|---:|---:|---:|---:|---:|
| Balmung (busiest NA) | 78.6 M | 162 | 38 / 200 (19%) | **20.1 h** | 75.4 h | 198 h | 1,860 h |
| Zurvan (quiet, Materia) | 3.8 M | 95 | **105 / 200 (53%)** | **571 h (24 d)** | 1,874 h (78 d) | 13,414 h (1.5 y) | 31,507 h (3.6 y) |

A random marketable item is mostly junk nobody trades, so a second pass restricted the sample to items that are *actively* traded: the 200 most-recently-updated items on Balmung (`/api/v2/extra/stats/most-recently-updated?world=Balmung&entries=200`), then measured those same items' freshness elsewhere.

| World | n with data | Never uploaded | Median age | p75 | p90 |
|---|---:|---:|---:|---:|---:|
| Jenova (busy NA) | 199 | 1 / 200 | **1.8 h** | 6.2 h | 25.6 h |
| Bismarck (quiet, Materia) | 155 | 45 / 200 (23%) | **119 h (5.0 d)** | 280 h | 814 h (34 d) |
| Zurvan (quiet, Materia) | 144 | 56 / 200 (28%) | **130 h (5.4 d)** | 311 h | 819 h (34 d) |

**Two orders of magnitude.** For actively traded items, a busy world's data is ~2 hours old at the median; a quiet world's is ~5 days, with a p90 past a month, and roughly a quarter of the items have never been uploaded at all.

### 3.2 Sampling structure, not just staleness

Beyond age, the *shape* of the data differs. Because each upload carries at most 20 sales ([§2.2](#22-what-the-client-actually-sends)), the recorded series is a union of overlapping 20-sale windows. If more than 20 sales occur on a world between two player visits, the excess is lost permanently — Universalis never sees it and there is no way to detect the loss from the API.

Recorded sales per day over the trailing 30 days on Jenova (2026-08-17), showing that busy items do exceed the 20-per-upload window (so multiple uploads/day are landing) but volume is modest:

| Item | Entries in 30 d | Days with ≥1 sale | Mean/day | Max/day |
|---|---:|---:|---:|---:|
| 5 | 101 | 25 / 30 | 4.0 | 13 |
| 5057 | 191 | 28 / 30 | 6.8 | 17 |
| 44104 | 32 | 11 / 30 | 2.9 | 8 |
| 10335 | 737 | 29 / 30 | 25.4 | 62 |

The undercount rate is **not measurable from the API** — Universalis's own `regularSaleVelocity` is computed from the same recorded entries, so it cannot serve as an independent check. Treat the 20-entry cap as a known upward bias on prices in thin data (a burst of cheap sales between visits is invisible) and state it as a limitation rather than trying to correct for it.

### 3.3 What this means for honest confidence intervals

1. **Every price EMM shows needs an age.** `lastUploadTime` is available on every relevant response; surface it, and use `worldUploadTimes` for the per-world breakdown on DC queries. Never render a bare number.
2. **Widen bands by sample size, not by a fixed percentage.** With `entries[]` in hand, EMM has the actual `n` in every window. A bootstrap or a `t`-interval over the trailing window's per-unit prices is defensible; a hardcoded ±10% is not.
3. **Suppress, don't guess.** With `hasData: false` — 19–53% of random marketable items depending on world — the correct output is "no data", not an extrapolation from the DC.
4. **Make the staleness threshold world-relative.** A 24-hour-old quote is unremarkable on Zurvan and stale on Balmung. Calibrate against the observed per-world upload rate (`/api/v2/extra/stats/least-recently-updated` gives a cheap live read) rather than a single global constant.
5. **Confidence bands should stop where the data thins.** Given 20-sale sampling, drawing a tight band over a period with three recorded sales is dishonest. Render the line, drop the band, and label the gap.
6. **The DC fallback is a different estimator.** When world data is missing, a DC-wide figure is legitimate but must be labelled as such — cross-world arbitrage means DC and world prices are correlated, not interchangeable.

### 3.4 What the undercut floor should be built on

Given the above, the defensible construction is:

- **Floor input:** `minListing` from `/api/v2/aggregated` for the cheap path, or `min(listings[].pricePerUnit)` from `/api/v2/{world}/{item}` when the actual rows are needed (they are, if EMM wants to skip the player's own retainer listings). `medianListing` is unusable — it is never populated ([§1.4](#14-aggregated--get-apiv2aggregatedworlddcregionitemids)).
- **Sanity band:** a quantile of trailing *sale* prices from `/api/v2/history` (weighted by `quantity`), used to flag a listing floor that has been dragged below what the item actually sells for.
- **Confidence in the floor:** a function of the world's `lastUploadTime` age and the count of listings behind it — not a constant.
- **Overridden on sight:** when `IMarketBoard.OfferingsReceived` fires for the item, the in-game listings supersede the Universalis floor unconditionally.

---

## 4. Item metadata from Lumina

Access is through [`IDataManager`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IDataManager/):

```csharp
ExcelSheet<T> GetExcelSheet<T>(ClientLanguage? language = null, string? name = null)
    where T : struct, IExcelRow<T>;
SubrowExcelSheet<T> GetSubrowExcelSheet<T>(ClientLanguage? language = null, string? name = null)
    where T : struct, IExcelSubrow<T>;
ExcelModule Excel { get; }
GameData GameData { get; }
ClientLanguage Language { get; }
bool HasModifiedGameDataFiles { get; }
```

Fields below were verified by reflecting the `Lumina.Excel.dll` (**assembly version 7.0.0.0**) shipped in the local Dalamud dev install, so they match what API 15 actually resolves rather than a docs snapshot.

`Lumina.Excel.Sheets.Item` — relevant members:

| Member | Type | Use |
|---|---|---|
| `RowId` | `uint` | The item ID Universalis keys on. |
| `Name` | `ReadOnlySeString` | Display name. Also `Singular`, `Plural`, `Description`. |
| `StackSize` | `uint` | Max stack — bounds the quantity picker and per-unit maths. |
| `IsUntradable` | `bool` | Hard exclusion. |
| `ItemSearchCategory` | `RowRef<ItemSearchCategory>` | **Marketability.** |
| `CanBeHq` | `bool` | Whether to split HQ/NQ series at all. |
| `IsCollectable` | `bool` | Collectables behave differently. |
| `IsUnique`, `IsIndisposable` | `bool` | Further exclusions. |
| `ItemUICategory` | `RowRef<ItemUICategory>` | Browse/grouping UI. |
| `LevelItem` | `RowRef<ItemLevel>` | Item level. |
| `Icon` | `ushort` | Icon ID for `ITextureProvider`. |
| `PriceLow`, `PriceMid` | `uint` | Vendor prices — a useful lower bound sanity check. |
| `Rarity` | `byte` | Rarity tier. |

**Marketability rule.** Use the same predicate Universalis uses, so EMM's item universe matches the API's:

```csharp
item.ItemSearchCategory.Value.RowId >= 1
```

— [`LuminaGameDataProvider.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.GameData/LuminaGameDataProvider.cs), `LoadMarketableItems`. The same file's `LoadMarketableItemStackSizes` reads `i.StackSize` under the identical filter.

`IsUntradable` is a *separate* flag and is not redundant — belt and braces is to require `ItemSearchCategory.RowId >= 1 && !IsUntradable`. Cross-check the resulting count against `/api/v2/marketable` (16,843 as of 2026-08-17) at startup and log a warning on divergence; a mismatch means the local game data and the Universalis backend are on different patches.

`Lumina.Excel.Sheets.ItemSearchCategory` exposes `RowId`, `Name`, `Category`, `Order`, `Icon`, `ClassJob` — enough to build a marketboard-shaped category tree matching the in-game search UI.

`Name` is a `ReadOnlySeString`, not a `string`; call `.ExtractText()` for a plain display string, or use `ISeStringEvaluator` where payloads matter. Sheets are language-aware via the `ClientLanguage` parameter, so EMM can match `IDataManager.Language` without shipping its own name tables.

---

## 5. Version context

| Component | Version | Source |
|---|---|---|
| Dalamud | 15.0.3.2 → **API level 15** | `<DalamudVersion>` in [`Dalamud.csproj`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Dalamud.csproj); `DalamudApiLevel = typeof(PluginManager).Assembly.GetName().Version!.Major` in [`PluginManager.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Plugin/Internal/PluginManager.cs) |
| Target framework | `net10.0-windows`, SDK 10.0.0 | [`Directory.Build.props`](https://github.com/goatcorp/Dalamud/blob/master/Directory.Build.props), [`global.json`](https://github.com/goatcorp/Dalamud/blob/master/global.json) |
| Dalamud v15 release | 2026-04-29; first game version Patch 7.5 | <https://dalamud.dev/versions/v15/> |
| FFXIVClientStructs (shipped in dev install) | 7.55.1.8875 | local reflection, 2026-08-17 |
| Lumina.Excel (shipped in dev install) | 7.0.0.0 | local reflection, 2026-08-17 |
| Universalis API | v2 current, v3 live but minimal | `swagger/v{1,2,3}/swagger.json` |

Dalamud only loads plugins whose API level is the current one or one behind (`effectiveDalamudApiLevel < DalamudApiLevel - 1` is rejected in `PluginManager.cs`), so EMM should target 15 and expect to re-target on each major.

---

## 6. Why EMM must not upload

Out of scope per the ticket, and there is a second reason worth recording: **Dalamud already does it.** [`UniversalisMarketBoardUploader.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Internal/MarketBoardUploaders/Universalis/UniversalisMarketBoardUploader.cs) is a built-in, first-party uploader that posts to `https://universalis.app` whenever a user opens a marketboard. Every Dalamud user is already contributing. An EMM uploader would duplicate rows, distort upload-count statistics, and add nothing.

Relatedly, Dalamud's plugin rules put data sent to third-party backends under case-by-case review by the Plugin Approval Committee, weighing necessity and abuse potential (<https://dalamud.dev/plugin-publishing/restrictions/>). Read-only consumption of a public API avoids that review surface entirely.

---

## 7. Recommended architecture

```
                     ┌──────────────────────────────┐
   player opens MB → │ IMarketBoard (Dalamud API 15)│ ── ground truth, item in view
                     │ InfoProxyItemSearch (FFXIVCS)│    zero staleness, no limits
                     └──────────────┬───────────────┘
                                    │ overrides
                                    ▼
   ┌────────────────────────────────────────────────────────────────┐
   │  EMM local store (SQLite or equivalent)                        │
   │  • sale rows, keyed (item, world, timestamp)                   │
   │  • listing snapshots + fetch time                              │
   │  • per-(item, world) lastUploadTime and freshness class        │
   └───────────────▲──────────────────────────────┬─────────────────┘
                   │ backfill / refresh           │ invalidate
                   │                              │
   ┌───────────────┴──────────────┐   ┌───────────┴──────────────────┐
   │ Universalis REST v2          │   │ Universalis WS (optional)    │
   │ • /aggregated  (100 ids)     │   │ wss://universalis.app/api/ws │
   │ • /{worldDcRegion} (100 ids) │   │ BSON; listings/*, sales/add, │
   │ • /history     (100 ids)     │   │ item/update; {world=,item=}  │
   └──────────────────────────────┘   └──────────────────────────────┘

   Lumina via IDataManager → item names, IDs, StackSize, marketability, icons
```

**Fetch policy:**

1. **Watchlist sweep** — `/api/v2/aggregated/{dc}/{up to 100 ids}`. Cheapest per item, server-cached, and gives world/DC/region min-listing and velocity in one call.
2. **Item detail on open** — `/api/v2/{world|dc}/{item}` for listing rows plus `/api/v2/history/{world|dc}/{item}` with explicit `entriesToReturn` and `entriesWithin`.
3. **History backfill, once per item** — one deep call, then persist locally and only ever fetch the delta with `entriesWithin` narrowed to "since my newest stored row". History rows are immutable, so this is a pure append.
4. **In-game override** — whenever `OfferingsReceived` / `HistoryReceived` fires, write through to the store and mark that (item, world) authoritative-as-of-now.
5. **WebSocket** — subscribe narrowly (`sales/add{world=N}`, `listings/add{world=N}`) *only* for the active world, and treat every frame as a cache-invalidation ping rather than a payload, given upstream issue #1346. Skip entirely in v1 if it adds risk; REST plus in-game override already covers the use cases.

**Client-side limiter:** a token bucket well under 25 req/s, max 2–4 concurrent connections, exponential backoff on 429/5xx, and a descriptive `User-Agent` identifying the plugin and version.

---

## 8. Open items

- **UNCONFIRMED:** whether a formal terms-of-service or data licence exists for Universalis API *data* (as distinct from the MIT-licensed source).
- **UNCONFIRMED:** stability guarantees for the v3 API; it is undocumented on the docs landing page and not advertised.
- **UNCONFIRMED:** whether `medianListing` is planned to be populated, or is vestigial schema. The controller has no assignment for it today.
- **UNCONFIRMED:** whether Universalis applies any server-side cap below the documented `entriesToReturn=99999`. The largest single response observed here was 14,785 entries; nothing was found that saturated the cap.
- **Not measurable:** the fraction of real sales missed because of the 20-entry client cap. No independent ground truth is reachable from the API.

---

## Sources

**Universalis**
- OpenAPI specs: `https://universalis.app/swagger/v1/swagger.json`, `/v2/`, `/v3/` (retrieved 2026-08-17)
- Docs site: <https://docs.universalis.app/> (rate limits, item/world ID mapping guidance)
- About: <https://universalis.app/about>
- Repository (branch `v2`, MIT): <https://github.com/Universalis-FFXIV/Universalis>
  - [`Controllers/V2/AggregatedMarketBoardDataController.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V2/AggregatedMarketBoardDataController.cs)
  - [`Controllers/V2/CurrentlyShownController.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V2/CurrentlyShownController.cs)
  - [`Controllers/V2/WebSocketController.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V2/WebSocketController.cs)
  - [`Controllers/V3/Market/OverviewController.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V3/Market/OverviewController.cs), [`SalesController.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Controllers/V3/Market/SalesController.cs)
  - [`Realtime/SocketClient.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Realtime/SocketClient.cs), [`Realtime/Messages/EventCondition.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Realtime/Messages/EventCondition.cs), [`Realtime/Messages/`](https://github.com/Universalis-FFXIV/Universalis/tree/v2/src/Universalis.Application/Realtime/Messages)
  - [`Startup.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.Application/Startup.cs)
  - [`GameData/LuminaGameDataProvider.cs`](https://github.com/Universalis-FFXIV/Universalis/blob/v2/src/Universalis.GameData/LuminaGameDataProvider.cs)
  - [Issue #1346 — WebSocket listings payload semantics](https://github.com/Universalis-FFXIV/Universalis/issues/1346)

**Dalamud**
- <https://dalamud.dev/> — [v15 release notes](https://dalamud.dev/versions/v15/), [`IMarketBoard`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IMarketBoard/), [`IDataManager`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IDataManager/), [plugin restrictions](https://dalamud.dev/plugin-publishing/restrictions/)
- Repository: <https://github.com/goatcorp/Dalamud>
  - [`Plugin/Services/IMarketBoard.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Plugin/Services/IMarketBoard.cs), [`Game/Marketboard/MarketBoard.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Marketboard/MarketBoard.cs)
  - [`Game/Network/Internal/NetworkHandlers.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Internal/NetworkHandlers.cs)
  - [`Game/Network/Structures/MarketBoardHistory.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Structures/MarketBoardHistory.cs), [`MarketBoardCurrentOfferings.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Structures/MarketBoardCurrentOfferings.cs), [`IMarketBoardCurrentOfferings.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Structures/IMarketBoardCurrentOfferings.cs), [`IMarketBoardHistory.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Structures/IMarketBoardHistory.cs)
  - [`Game/Network/Internal/MarketBoardUploaders/Universalis/UniversalisMarketBoardUploader.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Internal/MarketBoardUploaders/Universalis/UniversalisMarketBoardUploader.cs)
  - [`Dalamud.csproj`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Dalamud.csproj), [`Directory.Build.props`](https://github.com/goatcorp/Dalamud/blob/master/Directory.Build.props), [`global.json`](https://github.com/goatcorp/Dalamud/blob/master/global.json), [`Plugin/Internal/PluginManager.cs`](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Plugin/Internal/PluginManager.cs)

**Game structures and data**
- FFXIVClientStructs: <https://github.com/aers/FFXIVClientStructs> (MIT). Member lists in [§2.3](#23-reading-listings-out-of-memory-instead) were read by reflection from the locally installed `FFXIVClientStructs.dll` 7.55.1.8875.
- Lumina: <https://lumina.xiv.dev/> · <https://github.com/NotAdam/Lumina> · <https://github.com/NotAdam/Lumina.Excel>. Sheet members in [§4](#4-item-metadata-from-lumina) were read by reflection from the locally installed `Lumina.Excel.dll` 7.0.0.0.

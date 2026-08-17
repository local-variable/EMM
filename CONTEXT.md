# Eorzean Market Master

The domain of buying and selling on the Final Fantasy XIV marketboard: what is for sale, what it sold for, what a Player owns, and what price to ask. This file is a glossary and nothing else — no implementation detail, no architecture.

## Language

### Trade objects

**Item**:
A catalogue entry in the game's item table, identified by an item ID. Quality-agnostic — one Item covers both its HQ and NQ forms.
_Avoid_: product, commodity, SKU

**Quality**:
High Quality or Normal Quality — the game's own distinction, and the dimension that separates two Wares of one Item. Not every Item can be HQ.
_Avoid_: grade, tier, rarity (a separate game concept)

**Ware**:
An Item at a Quality — the thing a price attaches to. An HQ tincture and its NQ counterpart are two Wares of one Item. Every price, Floor, and History in EMM is a fact about a Ware, never about an Item.
_Avoid_: SKU, article, variant, good

**Listing**:
One live offer on a board: a Stack of a Ware at a Unit Price, put up by a Retainer.
_Avoid_: offer, post, ask

**Sale**:
One completed transaction — a Stack of a Ware that changed hands at a Unit Price, observed after the fact.
_Avoid_: trade, transaction, purchase

**Category**:
The game's own classification of an Item, used for browsing and reporting. Distinct from a Group: a Category is given by the game and cannot be edited, a Group is the Player's own.
_Avoid_: type, kind

### Places and scope

**World**:
A single game server, and the only place a board physically exists. A Retainer lists on its owner's home World and nowhere else.
_Avoid_: server, shard, realm

**Data Centre**:
A group of Worlds a Character can travel between. An aggregation scope for analysis, not a board.
_Avoid_: cluster (the abbreviation DC is fine in a label, not in prose)

**Region**:
A group of Data Centres. An aggregation scope only.

**Market**:
The live set of Listings for one Ware on one World's board. Every price EMM asks the Player to act on is a price in a Market.
_Avoid_: board (that is the in-game UI), exchange

**Scope**:
The breadth a price, series, or comparison is measured over — World, Data Centre, or Region. Carried alongside every figure EMM shows: a Data Centre or Region figure describes Markets elsewhere, and acting on it means travelling, so it is never quoted as a price available here.
_Avoid_: level, range, granularity

### Ownership

**Player**:
The person EMM works for. The top of the ownership chain; EMM knows exactly one.
_Avoid_: user, account, owner

**Character**:
One playable character, resident on one home World, owning up to ten Retainers.
_Avoid_: alt, toon, account

**Retainer**:
An NPC that holds stock for a Character and lists it on that Character's home World. Identified by its name within its Character — the automation surface EMM drives keys on the name, not an id.
_Avoid_: seller, vendor, mule

**Holdings**:
Every unit of every Ware the Player owns anywhere — character bags, Retainer stock, and units currently listed — across all Characters and all Worlds. The thing Profit is measured against.
_Avoid_: portfolio, estate (means housing), stash

### Money

**Unit Price**:
Gil for one unit of a Ware. Every comparison, Floor, and Undercut in EMM is expressed in Unit Price. The bare word "price" is not used — it hides whether a figure is per unit or per Stack, gross or net.
_Avoid_: price, ppu, cost

**Stack**:
The number of units in one Listing. A Listing is bought whole — a Stack of twenty cannot be sold three at a time — so Stack size sets the gil a buyer must commit, independent of Unit Price.
_Avoid_: quantity, bundle, lot

**Tax**:
The levy on a marketboard transaction, at the rate of the city the selling Retainer is stationed in. It varies (3–5%) and is published in game by the Retainer Vocate, so EMM reads the live rate rather than assuming one.

**Buyer Cost**:
What a buyer actually pays for a Listing, after Tax. The figure a buy decision is judged against.
_Avoid_: total, landed cost

**Net Proceeds**:
What a seller actually banks from a Sale, after Tax. Revenue, not Profit — it says nothing about what the units cost to acquire.
_Avoid_: revenue, earnings, take, net profit

**Cost Basis**:
What the Player paid to acquire a unit of a Ware. Recorded at acquisition, because it cannot be recovered afterwards.
_Avoid_: COGS, acquisition price, purchase price

**Profit**:
Net Proceeds minus Cost Basis. Reserved for exactly this — a figure that ignores either Tax or Cost Basis is never called Profit.
_Avoid_: margin, gain, earnings

### Pricing

**Competing Listing**:
A Listing in a Market that is not the Player's own. Every Undercut calculation reads Competing Listings only — including the Player's own would make EMM undercut itself.
_Avoid_: rival listing, other listing

**Reference Price**:
The observed number a pricing decision is measured against, always qualified by Ware and Scope — for example the lowest competing Unit Price on the home World. Never the bare "market price".
_Avoid_: market price, going rate, fair value

**Undercut**:
Pricing below the lowest competing Unit Price in a Market, by a chosen amount.
_Avoid_: beat, snipe

**Floor**:
The lowest Unit Price EMM will list a Ware at, set by the Player. A hard stop: undercutting halts here instead of chasing a price war downward.
_Avoid_: minimum, reserve, stop

**Ceiling**:
The highest Unit Price EMM will pay for a Ware. The buy-side mirror of a Floor.
_Avoid_: maximum, budget, cap

**Minimum Margin**:
The smallest acceptable Profit on a Sale, expressed against Cost Basis — a Floor derived rather than set by hand. Measured on Net Proceeds, so Tax sits inside the margin, not outside it. Where both apply, the binding limit is whichever is higher.
_Avoid_: markup, minimum profit, threshold

**Strategy**:
A named, reusable bundle of pricing rules — Undercut behaviour, Floor, Ceiling, Minimum Margin, Scope, and how often to act — applied to many Wares at once. Fast-moving stock and one-off items are different Strategies, not different kinds of Ware.
_Avoid_: profile, preset, policy, ruleset

**Group**:
A named set of Wares that share a Strategy, so enrolling a new Ware is a single act. A Strategy set directly on a Ware wins over the one its Group carries.
_Avoid_: tag, bucket, class

### Acting on a Market

**List**:
Put units of a Ware up for sale on a Market for the first time.
_Avoid_: post, sell, put up

**Reprice**:
Change the Unit Price of an existing Listing. The act undercut protection performs, repeatedly and in bulk.
_Avoid_: adjust, update, re-list

**Relist**:
Put a withdrawn Ware back up for sale. Distinct from Reprice — the Ware left the Market and returned.
_Avoid_: repost, renew

**Delist**:
Withdraw a Listing from a Market, returning the units to the Retainer.
_Avoid_: cancel, pull, remove

**Proposal**:
A change EMM has computed but not yet made — a Reprice, a List, a purchase. The unit of preview, dry run, and undo: the Player sees every Proposal before it becomes an act, and can reverse it after.
_Avoid_: suggestion, pending change, plan, draft

### Observation

**Source**:
Where an observation came from — the aggregator, a board the Player opened in game, or an imported store from another plugin. Each Source carries its own Freshness and its own trustworthiness, and a figure blended from several says so.
_Avoid_: provider, feed, backend

**Snapshot**:
The Listings for a Ware in a Market as observed at a single moment, carrying the time it was observed. Never called "current" — by the time it is read it is already a record of the past.
_Avoid_: current data, live prices, state

**History**:
The accumulated series of Sales for a Ware at a Scope. EMM builds it: no Source supplies rollups, so every average, band, and candle is computed from individual Sales.
_Avoid_: chart data, series, trend

**Freshness**:
How old an observation is, attached to every figure EMM shows. What counts as stale is calibrated per World — upload rates differ between busy and quiet Worlds by two orders of magnitude, so one global threshold would be wrong nearly everywhere.
_Avoid_: staleness, age, last updated

**Sample**:
The standing acknowledgement that History is a sample of Sales and never a census — the game client transmits only a small window of recent sales per visit, and Sales missed between visits can be neither counted nor detected. EMM shows the depth it has and claims confidence only where the Sample supports it.
_Avoid_: dataset, record, census

**Own Sale**:
A Sale made by one of the Player's Retainers, detected by EMM. Ground truth, and the only kind of Sale that can carry a Cost Basis — so the only kind from which Profit can be computed.
_Avoid_: my sale, local sale

**Market Sale**:
A Sale made by anyone, observed through a Source. Sampled, never complete, and carrying no Cost Basis.
_Avoid_: public sale, history entry

### Scope of the effort

**Core Feature**:
A capability that passes one of two admission tests: it is *load-bearing* (the gil loop cannot close end to end without it) or *differentiating* (it clears the bar of exceeding existing marketboard plugins). A capability that is neither is not core, however appealing.
_Avoid_: must-have, MVP feature, key feature

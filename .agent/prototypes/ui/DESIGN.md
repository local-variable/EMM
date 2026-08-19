# EMM UI information architecture — proposals for ticket #13

**Status: still held at the gate.** The seven open questions of §10 have been ruled on and the
prototype rebuilt to match (see §12). Everything else — the screens, the labels, the strings —
remains a proposal. Ticket #13 is `wayfinder:prototype`, i.e. HITL: the point of the artifact
is to be reacted to, not to be right.

Backs [#13 "Design EMM's UI information architecture"](https://github.com/local-variable/EMM/issues/13).
Prototype at `.agent/prototypes/ui/` — local and gitignored, by the same decision as the
#11 and #12 prototypes.

> **Read §12 first if you have seen this document before.** The rulings changed the *model* of
> the sell space, not only its appearance, so §6 has been rewritten rather than annotated.

---

## 0. What the maintainer asked for, and where each ask landed

| Brief | Where it is in the prototype |
|---|---|
| Style inspired by Henchman, not a copy | §1 — its *shell* is borrowed, its palette is not |
| Main window attaches to the retainer base menu, on the right, configurable elsewhere | §2, scene 1; the setting is in scene 6 → **Window & docking** |
| Config opens anywhere, moveable | scene 6 |
| A tab for pricing insights that flips on hover, or on search | §5, scene 2 |
| A tab for sell spaces — search and assignment per Retainer | §6, scene 3, and the authoring in scene 4 |
| A Tasks tab as the main one | §4, scene 1 |
| Anything else as you see fit | §7 — two further tabs (a third, Ledger, was ruled out) |

---

## 1. The style

Henchman's shell is the part worth borrowing, and it is the part I borrowed. Reading its
`Underlings` shell library, the ergonomics are:

- a **collapsible left nav rail** of icon-labelled categories (`DrawCollapsedNavItem` /
  `DrawExpandedNavItem`, `collapsedWidth` / `expandedWidth`) — icon-only when tight, icon+label
  when not;
- a **content header** and a **footer** framing the working area (`DrawContentHeader`,
  `DrawFooter`);
- **cards, sections and info boxes** rather than a wall of controls (`DrawInfoBox`,
  `DrawCollapsibleSection`, `BackgroundCard`);
- a **separate compact status window**, toggled from the main one and back;
- a **semantic palette** rather than raw colours — `TextPrimary` / `TextSecondary` /
  `TextDisabled`, `SuccessGreen` / `WarningYellow` / `ErrorRed`, one accent.

EMM takes all five and changes the one thing that would make it look like a clone: **the
accent**. Henchman is pink on near-black. EMM is the approved icon's palette — **gold on deep
navy, with the green price series from the #12 graph**. The severity colours are #12's, not
new ones, so a `caution` chip in the queue is the same yellow as a `caution` band on the chart.

**Ruled: the icon rail.** The tab strip has been removed from the prototype entirely rather than
left as a switch, so nothing downstream can quietly assume it.

Both light and dark are drawn. Dalamud's own style is user-editable, so EMM cannot assume a
dark background.

## 2. Where the window lives

**Default: docked to the right edge of the game's retainer list — the summoning-bell menu —
appearing with it, following it, matching its height.** This is the #9 "product shape" ruling
made literal: the surface *is* the retainer menu, and the window is not something you go and
open.

**Ruled: it matches the retainer list's height, and expands from there.** That height is short —
about 220px — so the compact state is not the workspace with less room in it, it is **a summary
strip**: the Proposal and Hold counts, the top four acts, one Apply button, and `⤢`. No rail, no
scope select, no search, no footer. One click expands past the game window into everything in
§3–§8, and the dock survives the expansion. Both states are in the prototype (scene 1, then the
button).

The expand control is **a labelled `⤢ Details` button in the accent colour**, not another grey
glyph in a row of window controls — diving into the detail is the main move out of the compact
state, so it should not have to be found. It is outlined rather than filled so it does not
compete with `Apply`, which is the primary action on that screen.

Configurable, in **Window & docking**: which addon to attach to (retainer list, retainer sell
list, market board, or nothing), which edge, whether to match height, and a remembered position
per attachment so a free window and a docked one do not fight over geometry.

Two further surfaces, both optional and both off the critical path:

- **Status window** — a ~270px strip: which Retainer EMM is working, progress through the acts,
  how long is left on the hard window ceiling, when the next sweep is. This is the thing you
  glance at while AutoRetainer runs. **Kept, on the ruling.**
- **Toasts** — see §9. Exactly one thing EMM will interrupt for.

**Removed: the server-info bar entry.** It was drawn as `EMM 12 ▲` in the earlier draft and is
gone from the stage and from the settings.

## 3. Multi-Retainer, multi-Character — the answer to the wall of tabs

Two controls, no nesting:

1. A **scope select** in the content header: `Player ▸ All Characters (2) · 6 Retainers`, or one
   Character.
2. A **Retainer strip** below it: one chip per Retainer showing postings occupied against
   postings granted, a capacity bar, and a badge of pending Proposals. Clicking a chip filters
   every tab.

**The strip never wraps** — it is a filter, and a filter that reflows the page under it is worse
than one that scrolls. **And it is hidden entirely on the Sell Space tab**, which already lists
the Retainers down its left edge; showing both repeated the same information twice on one screen
and took room the inventory grid and the board allocation wanted.

The queue itself stays **flat and ranked across all Retainers**, with the Retainer as a column.
That is deliberate: Slot Yield ranks globally, so grouping by Retainer would hide the comparison
the allocator exists to make.

## 4. Tasks — the primary view

**What a player sees on opening EMM is the work queue**, ranked by Slot Yield, with the act, the
Ware, the Retainer, the price move, the Slot Yield and the Days of Supply on one line, and a
checkbox. One button applies the checked ones.

Load-bearing details:

- **Hold is a section, not an omission.** Held rows sit in their own block, open by default,
  each with its reason. The mantra says the engine returns "do nothing, and why" — so the UI has
  to have somewhere to put it.
- **Days of Supply sits next to Slot Yield deliberately.** See §9 — the prototype's own data
  shows a case where they disagree loudly, and the maintainer should be able to see that on the
  row rather than discover it later.
- **`Updated` never appears here.** The queue shows `Reprice` and `Relist` distinctly; the union
  is a display convention of the chat log only, per the `CONTEXT.md` note.
- **Since the last visit** is a strip at the bottom: Own Sales detected, Net Proceeds, postings
  freed. `Sold` is retrospective and must not be mixed into the acts above it.

**First open, nothing configured** (scene 3) is the #9 on-ramp made concrete: three findings
EMM can produce with zero setup, then two choices — pick a Strategy (editable there and then),
press one button to enrol what the Retainers already list. No badge, no chat log, no status
window until there is something to say.

## 5. Pricing — the flip

The tab is a **Ware list on the left and one large card on the right**. The card has two faces:

- **Front** — the portfolio: Holdings valued at Estimate, how many Wares are priced with
  confidence versus provisionally, postings occupied, and the severity distribution as bars.
- **Back** — one Ware's History, the #12 chart in compact form: Sale dots sized by Stack, the
  Estimate as the trend line with a dark halo so it survives a dense Sale cloud, the 80%
  precision interval as a dotted-edge band labelled **not a forecast**, a units/day strip, and
  the Freshness gap hatched on the time axis.

**Hovering a Ware turns the card over, and clicking does the same.** Searching filters the list.
Click matters because of the narrow layout: when the window is under ~720px the list becomes a
**horizontal scrolling strip of Ware chips above the chart**, and on a strip you scroll sideways
past three Wares to reach the fourth — passing over them is not choosing them.

**The Ware selector is never the thing that gets dropped.** It is the only way to change Ware, so
it survives every width; the self-test asserts it has a real clickable height at all three.

**This tab is now the only place a Ware's History is read.** The floating item overlay is gone
(§9), so the queue's zoom control and a search hit both land here rather than opening a second
window.

Below the chart, the stats the ticket asked to live with the graph: Lowest Competing, Days of
Supply, sold per day, units on the board, the Data Centre average — labelled as a Scope you
would have to travel to, never as a price available here — and the Estimate's basis.

**Honest note on the flip.** ImGui cannot rotate a window, so a literal 3D flip is not
buildable as drawn. The build options are a cross-fade, a horizontal slide, or a genuine flip
faked by scaling the card's draw list horizontally through zero. The prototype uses a real
rotation because that is the *feel* being proposed; if the feel matters more than the mechanism,
the slide is the cheap version and looks nearly the same at 300ms.

## 6. Sell Space — rewritten after the ruling

**The sell space is the Retainer's own item allocation. It is not the market list.** This is the
correction that changed the most, and it changes the model rather than the drawing: what the
Player marks is stock sitting in the Retainer's inventory, and **what appears on the board is an
outcome of that marking** — EMM picks which of the granted stock takes the 20 postings, most
revenue first. The earlier draft had the tab marking market slots directly, which quietly
encoded a one-mark-per-posting model the #9 addendum explicitly rules out.

Four consequences, all now in the prototype:

1. **Seven pages, 25 slots each** (`RetainerPage1..7`). The space is marked per page and totals
   across them, so a Retainer whose stock is spread does not have to be tidied first. The page
   tabs carry their own mark counts, so you can see where the space is without paging through.
2. **Icons, not labels.** An inventory grid reads by icon; three-letter abbreviations were a
   prototype shortcut and a bad one. The tiles carry the type glyph, the HQ mark and the stack
   count in the game's own corner positions. *(They are stand-ins — the real build reads the
   icon id off the Item row. See §11.)*
3. **The board is drawn beside the space, never as the space.** In the tab it is a 20-slot
   strip under the heading "what that allocation put on the board", with the count of granted
   stock slots it was filled from. In scene 4 it is the Retainer's own sell list, shown next to
   the inventory because it is related — the callout says so in as many words.
4. **Slot count drives the Strategy, not only capacity.** The tab reads the marked count back as
   intent — many slots means the Ware is being run as a commodity, one slot as a single sale —
   and shows the Strategy that assignment produces, overridable per Ware. This closes what was
   open question 4.

Fill order stays **a property of the Strategy, shown read-only here**, because #9 ruled it
belongs there and a control in this tab would contradict it.

**The tab is about space, not about Wares.** An earlier draft carried a search-and-assign table
for putting a Ware into the space; it has been removed. Enrolment happens by dropping stock into
a granted slot, and choosing *what* to sell is the Pricing and Tasks tabs' work — a Ware picker
here was answering a question this screen does not ask.

## 7. The other two tabs

- **Holdings** — every Ware held, by Retainer, with Cost Basis, Estimate, Net Proceeds, Profit
  and Days of Supply. It exists because **Profit is only computable where Cost Basis exists**,
  and the coverage number ("Cost Basis on 12 of 16") is the honest headline. Rows without one
  say `derived`, pointing at the derived Floor rather than a blank.
- **Strategies** — the Strategy/Group editor, each Strategy showing its Floor and its ask **as
  the actual expression**, with "show me why". **Ruled in as a tab**, on the expectation that it
  grows into user-derived Strategies customised against whatever data is available — which is
  work you do, not a decision you make once, so it belongs in the main window.

**Ledger is gone.** Ruled out: it does not earn its space in v0.1. The recorder still exists —
#9 put it above the line as an *irreversible* capture, and unattended buying still has to earn
its Mandate from it — but the *view* over it waits.

### Scan — added on the ruling

A tab with a loading-bar icon that owns **where every figure came from, how old it is, and what
asking for more costs**:

- **Refresh now**, scoped (the sell space / this Character / a watchlist), stating the request
  count and the expected seconds *before* you press it, with a progress bar while it runs.
- Four headline numbers: freshest figure, oldest figure and how many Wares are stale, when the
  next automatic sweep is, and requests used in the last hour against the published limit.
- **A Source table** — Universalis at World scope, Universalis at Data Centre scope marked
  *cold start only*, boards you opened in game marked *ground truth, overrides the aggregator*,
  your own Sales marked *the only Source that carries Cost Basis*, and imported stores marked
  *detection time, not Sale time*. Each with its own Freshness. This is `CONTEXT.md`'s **Source**
  and **Freshness** made into a screen.
- **Backfill as its own budget**, with its own progress and a pause: one-time, throttled,
  resumable, visible, never on a silent timer.
- **What the screen will not let you do** — the rate ceiling, the two-connection cap and the
  fifteen-minute floor are stated as *not settings*, with the reason: the limits are per address,
  and the address may be a household.

Every number on it is bounded by #10's API-citizenship ruling rather than invented: ~4 req/s
against a published 25, ≤2 connections against a cap of 8, 100-Item batches, and a manual
refresh exempt from the interval because a human pressing a button is not a timer.

## 8. Settings depth

Config is a **separate, free, moveable window with its own rail** — General, Window & docking,
Strategies & Groups, Value Sources, Mandates & Guardrails, Sell space defaults, Data & Sources,
Alerts. The rule keeping the common path clear is:

> The main window holds work you *do*. The config window holds decisions you *make once*.

Four pages carry the load:

- **Mandates & Guardrails** — each Mandate an independent switch, all off by default, `Buy`
  visibly locked; the six guardrails shown with editable *values* and non-editable *existence*.
- **Value Sources** — every named source in a table with its live value for the Ware in view,
  then the Floor expression you are actually running. Reading the preset you are already using
  is the entire tutorial, per #9: no advanced mode, no second UI.
- **Data & Sources** — **now carries the scan rate**: automatic sweep cadence (fitted per World,
  or pinned), requests per second and simultaneous connections *shown with their ceilings as the
  top option so the limit is visible rather than hidden*, whether opening a Retainer triggers a
  bounded top-up, whether a board you opened overrides the aggregator, the backfill switch, and
  which Sources are allowed at all.
- **Sell space defaults** — what a newly drawn space starts as, whether dropping stock in enrols
  it, whether slot count is read as intent, and a placeholder for granting space across
  Retainers, which is **not designed yet** and is labelled as such in the UI.

*(Three of these pages previously fell through to General — a prototype bug, not a design
choice. They render distinctly now.)*

---

## 8b. Alerting, narrowed — and this supersedes part of #9

#9 settled on **four interrupts plus a digest**: a deal that will be gone, a guardrail trip, the
retainer gil cap approaching, and market expiry due. **Three of the four have been cut.** What
remains:

- **EMM acted on a figure the data does not back** — a provisional Estimate became a real
  Listing. This is the new one, and it is the ruling's own test: *toast only on acting on
  something that is not backed by data*.
- **A guardrail tripped** — EMM suspended itself.

Everything else is a digest line or the `[EMM]` chat log. The reasoning that survives from #9 is
its own: an alert earns its place only if it fires in unattended running and essentially never
in manual running. A deal, a gil cap and an expiry are all readable where you would go looking
anyway, and none is worth stopping a fight for. **The "you have been undercut" alert remains
deliberately absent** — it is the flagship notification of every competing plugin and trains the
exact reflex the mantra rejects.

Two things follow that are worth stating plainly. **This narrows a decided ticket**, so it wants
recording as an amendment to #9 rather than absorbed silently. And the gil-cap alert was the one
guarding real gil evaporating — proceeds above the retainer cap are *discarded outright*, per
`LogMessage` 4578 — so with it cut, that warning has to live somewhere it will actually be read.
The Retainer chips and the Holdings tab are the obvious candidates; neither carries it yet.

## 9. The item overlay is gone — and it supersedes a #12 requirement

The floating per-Ware overlay has been removed on the ruling that it does not earn its place.

**Flagging it because it reverses a decision, not a drawing:** closing #12 handed #13 an explicit
new requirement — *"the graph must be able to pull up in an item overlay when needed / called
upon"*. Removing the overlay supersedes that requirement. The underlying need is still met, by
the Pricing tab: the queue's zoom control and a search hit both flip that card to the Ware,
expanding the docked window on the way if it was compact. What is lost is looking at a Ware's
History **while a game window is open in front of you** — the case the overlay was invented for.
If that case matters, it comes back; if it does not, the Pricing tab is the simpler answer and
one fewer window to manage.

## 9b. What the prototype turned up that was not in the brief

**Slot Yield and Days of Supply disagree loudly on the real data, and the disagreement is
visible on the row.** With the Player's Holdings invented but the market real, the top-ranked
Proposal is a Wind-up Feo Ul at ~529k gil/slot-day sitting on **110 days of supply**. The
formula's denominator is `unitsListedBelow + Stack`; nothing is listed below the undercut price,
so the denominator is 1 and a thin, slow, high-ticket Ware outranks Materia that clears in
hours. This is the same weakness [#11 recorded](https://github.com/local-variable/EMM/issues/11)
from the other direction ("Slot Yield is a RATE, so it will systematically under-price
non-replenishable stock") — here it over-ranks instead. **Not a UI bug and not fixed here**;
what the UI does about it is show both numbers side by side so the contradiction is legible.
Whether the allocator should be changed is #11's ground, not this ticket's.

**Sold-per-day is computed from the Sales, not from the aggregator's velocity field.** The first
build of the prototype used the aggregate's own velocity and produced a 2.8M gil/slot-day
headline on the first-run screen. #3 had already found that field "will tend to be the same for
every item" and warned against it. Recomputing units/day from the Sale rows in the window fixed
the number and is what EMM would do anyway, since it builds its own History.

**Docked width is the real constraint on the queue, not the number of columns.** Attached to the
retainer list on a 1380px screen, EMM gets about 830px, and the rail takes 146 of it. Eight
columns fit; nine do not. Anything the queue wants to add has to displace something.

## 10. The seven open questions, as ruled

| # | Question | Ruling | What changed |
|---|---|---|---|
| 1 | Rail or tab strip? | **Icon rail** | Tab strip removed outright, not left as an option |
| 2 | Does `Strategies` deserve a tab? | **Yes** — expected to grow into user-derived Strategies customised against available data | Tab kept |
| 3 | Is `Ledger` a v0.1 tab? | **No** — it does not hold useful space at the moment | Tab removed; the recorder is untouched |
| 4 | Does slot count drive Strategy, or only cap capacity? | **It drives it too**, from what is entering and available | §6 rewritten; the tab reads the count back as intent and shows the resulting Strategy |
| 5 | Where do severity warnings surface? | **Toasts** — the FFXIV-native surface | Toasts carry the warning; §12 raises the volume problem this creates |
| 6 | Docked height? | **Match the retainer list, then expand** | A purpose-built compact strip, plus `⤢` |
| 7 | Is the item overlay pinnable? | **No** | Pin button removed |

## 10b. Known defects in the prototype — found by the maintainer, and by me

Recording these rather than fixing them silently, because two of them were mistaken for design.

**Reported and fixed:**

| Defect | Cause | Fixed |
|---|---|---|
| Three settings pages all showed *General* | `renderConfig` had no branch for them, so they fell through to `else` | yes |
| Inventory page tabs in scene 4 did nothing | drawn as `<span>`, never wired | yes — clickable, and they page the grid |
| Charts came up blank on a tab switch | canvas measured while its pane was still `display:none`, so it had zero size | yes — draws once the box is real |
| Status strip covered the main window | both `position:absolute` with no stacking order | yes — the main window is always above it |
| Retainer strip wrapped to two rows | `flex-wrap:wrap` | yes — one row, scrolls |
| Compact first-run looked squashed | the full workspace crammed into ~220px | yes — replaced by a Setup button and a wizard |
| Window buttons did nothing | decorative | yes — minimise, close, size and the status-strip buttons all act |
| **Black text on every dropdown and text field** | `button` inherited colour, `select` / `input` / `option` do not, so they fell back to the platform's black-on-white | yes — every native control is pinned to the theme tokens |
| **Minimise clipped the window badly** | it hid the main window outright, leaving an orphaned strip in the corner | yes — minimise is now ImGui's own collapse: the window stays where it is and shrinks to its title bar, carrying a `12 to do` count |
| **Every nav item stayed disabled forever after visiting first run** | setup greys the rail, and nothing re-enabled it — and the selector caught the *settings* rail too, so the config window died with it | yes — found by the self-test below, not by eye |
| **The plugin mark read as a minus button** | it was a gold square with a dark bar through it, sitting in a title bar — so it looked like a control and got clicked | yes — it is the approved icon in miniature now: a bell carrying a green price series |
| **No resize grip once the docked window was expanded** | the grip was tied to the free-window scene rather than to "is this window visible" | yes — the grip is present wherever the window is, except minimised |
| **Dense layouts overflowed a narrow window** | the queue and Sell Space grids assumed ~1000px; docked, EMM gets under 700 | yes — two breakpoints driven by the content area's own width, shedding columns in order of how little they are missed |
| **`hidden` panels rendered anyway** | `.warnbox { display:flex }` outbids the `hidden` attribute, so two explanation panels were permanently open | yes — `[hidden] { display:none !important }` |
| **The whole script died silently partway through** | a `const` referenced above its own declaration inside `setScene`; the page still rendered, so nothing looked wrong | yes — and a global error handler now surfaces any script error in the tab title, because a headless render swallows the console |
| **The Pricing tab lost its Ware selector when narrow** | the list was stacked above the chart with no height of its own, so `overflow:auto` collapsed it to a sliver — leaving **no way to change Ware at all** | yes — narrow, the list becomes a horizontal scrolling strip of Ware chips that is always visible, and click selects as well as hover |

**There is now a self-test: open `#selftest=1`. It is at 89 cases and all of them pass.**

It drives all five scenes, all six tabs, the rail and its collapse, the Retainer chips, the scope
select, the search, every window button, every settings page, the whole setup wizard and both
ways out of it, the sell-space page tabs and all three marking gestures, the resize grip in every
state including an actual drag, and both themes. On top of the things you can click, it asserts
things the eye is bad at:

- **the charts have actually drawn pixels** — in both themes;
- **no control renders dark-on-dark**;
- **no block overflows its pane**, checked for every tab at three window sizes;
- **the last queue row is not sliced against the footer**, and a scrollable pane shows its cue;
- **`hidden` really hides**;
- **the plugin mark is not a button**;
- **no visible button is a dead click** — everything either acts or says it is not built.

It has paid for itself three times. It caught the disabled-nav bug above, which left **five of six
tabs and the whole settings window dead** after a visit to first run. It caught the narrow-window
overflow that the screenshots had been hiding. And when the harness itself went quiet, the failure
turned out to be a silent script death that had been there for a while.

Two lessons worth keeping. **A test that cannot fail is worse than no test** — the first version
treated a returned string as a pass, so seven real failures rendered as ticks. And **build the
report as you go, not at the end**: when the harness threw, there was nothing on screen at all and
no way to see how far it had got.

Keep it passing, and add a case whenever a defect is found by hand.

**Still wrong, and known:**

- The **flip** re-renders the whole card, so a fast hover down the list can interrupt an
  animation mid-turn. Cosmetic, and an ImGui build would not do it this way anyway.
- **Drag-marking** in the Sell Space grid re-renders on every slot the pointer crosses. Fine at
  25 tiles, wrong in principle.
- **`state.grantedSlots` is not per Retainer** — switching Retainer resets the marks rather than
  remembering each one's. The tab reads correctly; the persistence is faked.
- **Numbers in the Scan tab and the "since the last visit" strip are invented**, not derived.
  §11 says which is which.
- **The `⤢ Details` expansion is animation-free**; the real one should not jump.
- **No keyboard path anywhere.** Nothing is reachable by tab-and-enter, and a real Dalamud window
  needs at least Escape to close. This is the largest remaining gap and the self-test does not
  cover it.
- **No empty states** beyond first run: a Retainer with nothing marked, or a search matching
  nothing, both render as bare space.
- **The queue still slices a row mid-scroll.** That is ordinary scrolling and the fade says so,
  but a build could snap to row boundaries.
- **Settings changes do not propagate.** Toggling *Read slot count as intent* off does not change
  what the Sell Space tab then says — the switches move, nothing downstream listens.
- **Unbuilt controls announce themselves rather than acting** — `New Group`, `Assign`, `Edit`,
  `Apply this plan`, `Connect…`. That is deliberate: a dead click reads as a broken feature, and
  saying "not built in the prototype" is honest about which is which.

## 11. What is real and what is invented

**Real** — every Ware, Sale, Listing, Estimate, precision interval, severity grade, Lowest
Competing Listing, Data Centre average and Freshness, from the Cactuar corpus cached
2026-08-17 via the #12 extraction. The charts are drawing actual `models.estimate()` output.

**Invented** — the Player. Character names, Retainer names, which Retainer holds what, units
held, Cost Basis, the sell-space marks, the Ledger rows, the guardrail values, and the "since
the last visit" counters. The maintainer's own Retainer and Character names are deliberately
*not* used, so a screenshot of this prototype carries nothing personal.

**Derived, not measured** — the Proposals. Acts, target Unit Prices, Floors and Slot Yields are
computed in `build_data.py` from the real Estimates and the invented Holdings, using the #9
formula and the #11 per-type floors. They are plausible, not authoritative.

**Stand-ins** — the item icon tiles. FFXIV item icons live in the game's packed data and would
need a `.tex` extraction to show here, so each tile is drawn from the Ware's structural type: a
glyph, a hue, the HQ mark and the stack count in the game's own corner positions. The real build
reads `Item.Icon` and hands it to Dalamud's texture provider. The tiles are here to settle
**that the grid reads by icon rather than by text**, not to propose these particular pictures.

---

## 11b. The third round — what changed and why

**Setup replaces the squashed first run (§4).** Docked and unconfigured there is no room for
findings, so the compact pane is now one sentence and a **Setup** button. Pressing it expands the
window and runs a four-step wizard: **your Retainers** (which ones EMM may touch, with each one's
Tax) → **a Strategy per Retainer** → **sell space** → **the first scan**, which states its request
count and expected time before it runs and ends by saying plainly that nothing will act until a
Mandate is granted. The rail is greyed for the duration: setup is a path, not a place to browse.

**Strategies are named for their goal, there are six, and one is yours (§7).** *Undercut to Sell
Fast · Hold for a Better Price · Follow the Estimate · Protect the Margin · Clear Dead Stock ·
Advanced (yours)*. Each card states what it is trying to do in a sentence, then its Floor and ask
as the actual expressions. **This supersedes the `Early Bird` / `Second Mouse` / `Balanced` names
approved in #9** — those were evocative where the ruling asks for clear, and there are now more
than three. Flagged because they were explicitly approved copy; the new names are proposals.

**A Strategy is assigned per Retainer, and the levels blend.** The Retainer's Strategy is the
overall one; **every Group starts at *Inherit*** and nothing competes until a Group is
deliberately given its own. Where one is, **local beats global** — a Ware beats its Group, a Group
beats the Retainer. The settings page draws the three levels as a chain and says so; each Group
row shows its effect in words (*follows whatever the Retainer runs* / *overrides on 14 Wares*),
and the Retainer table carries an **Overrides** column counting how many of its Wares are
currently running something else. The point is that a Retainer running *Undercut to Sell Fast*
can hold its chase items patiently as **one** Group override, changing nothing else it sells.

**Pricing became one Retainer's shelf (§5).** The summary blocks are gone. The Retainer strip
now drives the tab: pick a Retainer and you get the Wares it is selling *right now*, each row
carrying its Retainer, its asking price and its stack, and the card holds nothing but that Ware's
History. Hovering still turns it over.

**The window is resizable, not two fixed sizes (§2).** A size button cycles default → large →
fill, and there is a drag grip on the corner. Screen space is a real constraint and a bigger
screen should buy a bigger window rather than more empty margin.

**Groups: the seven structural types are built in, and Players can define their own.** The types
are classified from game data alone — search category, stack size, quality flag — so a Ware that
has never traded still lands in one on install day. On top of that, a Group can be a hand-picked
list *or a rule*: the example in the prototype is **High-value cosmetics**,
`estimate > 500000` and Category in Minion/Mount/Orchestrion, pointed at *Hold for a Better
Price*.

**Across-Retainer space is designed now, not a placeholder (§6).** Drawing stays the per-Retainer
act and always wins; what sits under it is a **space plan** — per Retainer, how many stock pages
and postings EMM gets and what the Retainer is reserved for — plus **per-Group allocation across
all Retainers**. The question that design exists to answer is **spill**: a sell space is per
Retainer, so a Group that outgrows one must either wait for a posting, move to a Retainer with
room, or displace something with a lower Slot Yield. That last option is what lets the allocator
work across the whole Player rather than one Retainer at a time, and it is the setting most worth
arguing about.

**Data & Sources lists the plugins on this machine as checkboxes.** Allagan Tools (IPC, Holdings
across alts) and Allagan Market (your own past Sales) are on; its price cache is off and marked
low-value because it is an overwritten snapshot; Allagan Item Search, Price Insight, PriceCheck
and Market board are greyed with the reason — they expose no interface and keep no store worth
reading, so there is nothing to connect to.

**Discord is in General, marked v0.2.** Digest, guardrail trips, and the same
acted-on-unbacked-data event that raises the toast. The reply-to-approve switch is off and last,
with a note saying why: replying to a message would let Discord move gil, which is a different
kind of trust from anything else EMM does.

## 12. The second round of rulings, and what they left open

**Ruled and applied:** the server-info-bar entry removed (status window kept); the item overlay
removed (§9); alerting narrowed to two interrupts (§8b); Data & Sources given scan-rate controls;
Sell space defaults, Data & Sources and Strategies & Groups fixed so they no longer fall through
to General; a **Scan** tab added; the expand control made prominent; the Retainer strip stopped
from wrapping and hidden on the Sell Space tab.

**Ruled on the four consequences:**

1. **Toast only when EMM acts on something the data does not back.** Applied literally: one
   toast, raised on a *Listing made from a provisional Estimate*. Severity everywhere else is an
   inline chip on the figure. This settles the volume problem — #11 measured 16% severe and 7%
   warning across 4,000 Wares, and toasting each on a sweep would be unusable, but only a
   fraction of those are ever *acted on*.
2. **The compact strip's four rows stand**, with the expand control made prominent enough to be
   the obvious next move. Still ranked by Slot Yield.
3. **The layout complaint is applied** — strip does not wrap, and is gone from Sell Space, which
   spends the room on the inventory grid and the board allocation.
4. **Real item icons with names and details on hover are anticipated.** The tiles stay
   stand-ins here; the build reads `Item.Icon` through Dalamud's texture provider and hangs a
   tooltip off each.

**Still open, and none of it is mine to settle:**

- **The gil-cap warning lost its home.** Cutting that interrupt removed the only thing watching
  a Retainer's gil approach the cap, above which proceeds are *discarded outright*. It needs
  somewhere to be read — the Retainer chips or Holdings are the candidates. Not built.
- **Granting sell space across Retainers is not designed.** With up to thirty Retainers, drawing
  on each is unreasonable. The settings page carries a labelled placeholder, nothing more.
- **Every string is still unapproved**, including the single toast, and #11's severity strings
  remain unapproved separately.
- **§8b narrows #9 and §9 supersedes a #12 requirement.** Both are recorded as amendments rather
  than absorbed quietly, and both belong in #13's resolution comment when it is written.

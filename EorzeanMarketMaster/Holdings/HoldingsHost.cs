using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Holdings;
using EorzeanMarketMaster.Core.Store;

namespace EorzeanMarketMaster.Holdings;

/// <summary>
/// What one press of the refresh control actually managed.
/// </summary>
/// <param name="Retainers">How many Retainers the bell reported.</param>
/// <param name="ReadInFull">
/// How many of them EMM read the contents of. At most one - the open one - because that is all the
/// game will load.
/// </param>
/// <param name="FromCompanion">How many places the companion covered.</param>
/// <param name="Refusal">Why nothing happened, or null where something did.</param>
internal sealed record HoldingsScan(
    int Retainers,
    int ReadInFull,
    int FromCompanion,
    string? Refusal);

/// <summary>
/// The plugin side of Holdings: reads the game, keeps the ledger, and persists what it read.
///
/// <b>It ticks whether or not EMM's window is open, and that is the point.</b> A Retainer's stock
/// is readable only while that Retainer is open, so the chance to read one arrives when the Player
/// opens it - not when they happen to be looking at EMM. A host that only ran behind an open
/// window would miss every Retainer the Player visited normally, which is most of them.
///
/// <b>Store access goes through the gate <see cref="Store.StoreHost"/> owns</b>, taken with a zero
/// timeout and given up on rather than waited for, the same as every other reader. A refresh in
/// flight holds it for its whole run; a frame that blocked on one would be a frozen game. What is
/// read is held in memory anyway, so a skipped write is a write next second.
///
/// <b>Writes are not per tick.</b> The ledger is updated every second because it is free; the
/// store is written only when the contents changed, or when the recorded age has drifted far
/// enough that leaving it would misstate the Freshness after a restart.
/// </summary>
internal sealed class HoldingsHost : IDisposable
{
    /// <summary>
    /// How often the game is read. A second: the containers are cheap, and this is what makes an
    /// opened Retainer get captured while it is open rather than after it has closed.
    /// </summary>
    private static readonly TimeSpan CaptureInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far a recorded age may drift behind the real one before an unchanged reading is written
    /// again. A minute, which bounds how stale the stored Freshness can be without turning an
    /// idling Player into a write per second.
    /// </summary>
    private static readonly TimeSpan FreshnessDrift = TimeSpan.FromMinutes(1);

    /// <summary>How often the companion is asked whether it is there. Not per frame: it is an IPC call.</summary>
    private static readonly TimeSpan CompanionPoll = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the bell has to sit unchanged before EMM calls it idle.
    ///
    /// Three seconds, which is a judgement rather than a measurement and is written here so it can
    /// be argued with. It is set above the gap between one Retainer closing and the next opening
    /// during an automated run - so the control does not flicker enabled between Retainers - and
    /// below the patience of somebody who has walked to a bell to press it.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    private readonly MarketStore store;
    private readonly SemaphoreSlim storeGate;
    private readonly CompanionHoldings companion = new();
    private readonly HoldingsLedger ledger = new();
    private readonly RetainerWalk walk;

    /// <summary>What the store is believed to hold, so an unchanged reading is not rewritten.</summary>
    private readonly Dictionary<HoldingsPlaceKey, HoldingsReading> onDisk = [];

    /// <summary>Readings waiting for the store gate. Keyed, so a place never queues twice.</summary>
    private readonly Dictionary<HoldingsPlaceKey, HoldingsReading> pending = [];

    private bool loaded;
    private RetainerRoster? roster;
    private DateTimeOffset nextCapture = DateTimeOffset.MinValue;
    private DateTimeOffset nextCompanionPoll = DateTimeOffset.MinValue;

    /// <summary>What the bell looked like last time it was checked, and when it last changed.</summary>
    private string bellState = string.Empty;
    private DateTimeOffset settledAt = DateTimeOffset.MaxValue;

    /// <summary>Why the open Retainer could not be read, so a change of answer can be logged once.</summary>
    private string? refusal;

    /// <summary>Whether a walk was running last tick, so its ending can be noticed.</summary>
    private bool wasWalking;

    internal HoldingsHost(MarketStore store, SemaphoreSlim storeGate)
    {
        this.store = store;
        this.storeGate = storeGate;

        // The walk reads Retainers inside windows AutoRetainer hands over. Attached at load
        // because the control has to be able to say whether it can do the full job before it is
        // pressed - and because attaching is only a subscription, which cannot interrupt anything.
        walk = new RetainerWalk(
            () => Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.CharacterName : null,
            reading => Keep(reading));

        walk.Attach();
    }

    /// <summary>
    /// Everything EMM holds, unfiltered. What the totals and the Retainer standings are drawn from,
    /// because those answer questions about coverage rather than about what is on screen.
    /// </summary>
    internal HoldingsView View { get; private set; } = HoldingsView.Empty;

    /// <summary>
    /// The same view narrowed to what the Player has asked to see. Recomputed when the view or a
    /// filter changes rather than per frame - the filtering is a re-roll of every line, and a frame
    /// that did it again for an unchanged answer would be doing it sixty times a second.
    /// </summary>
    internal HoldingsView Shown { get; private set; } = HoldingsView.Empty;

    /// <summary>
    /// The Ware types present in what EMM holds, in the seven-way structural order.
    ///
    /// <b>Types rather than the game's own Categories</b>, which was the first spelling and was
    /// wrong: the Item table has some fifty of them, so a Player with a mixed inventory got a combo
    /// with forty entries and no two Wares in it that wanted treating alike. The seven are the
    /// grouping EMM already reasons in - it is what the fitted model recalibrates per - so
    /// filtering by them shows the Player the same partition the plugin thinks in.
    ///
    /// Built from the Holdings rather than from the whole table: a filter offering a type the
    /// Player owns nothing in is a control whose every use produces an empty list.
    /// </summary>
    internal IReadOnlyList<WareType> Types { get; private set; } = [];

    /// <summary>The Ware type being shown, or null for all of them.</summary>
    internal WareType? Type { get; private set; }

    /// <summary>The location being shown, or null for everywhere.</summary>
    internal ShownLocation? Location { get; private set; }

    /// <summary>Every location the Player could narrow to: the bags, then each Retainer read.</summary>
    internal IReadOnlyList<ShownLocation> Locations { get; private set; } = [];

    /// <summary>Narrows the list to one Ware type, or to all of them with null.</summary>
    /// <param name="type">The type.</param>
    internal void Show(WareType? type)
    {
        if (type == Type)
        {
            return;
        }

        Type = type;
        Refilter();
    }

    /// <summary>Narrows the list to one location, or to everywhere with null.</summary>
    /// <param name="location">The location.</param>
    internal void Show(ShownLocation? location)
    {
        if (location == Location)
        {
            return;
        }

        Location = location;
        Refilter();
    }

    /// <summary>What the last press of the refresh control managed, or null before one.</summary>
    internal HoldingsScan? LastScan { get; private set; }

    /// <summary>
    /// Whether the Player is at a summoning bell, which is the only place every Retainer's state
    /// can be read at once.
    /// </summary>
    internal static bool AtTheBell => Plugin.Condition[ConditionFlag.OccupiedSummoningBell];

    /// <summary>
    /// Why the refresh cannot run right now, or null where it can.
    ///
    /// <b>The bell has to be quiet, not merely open.</b> Something else is usually driving it -
    /// the automation plugin walks every Retainer in turn, opening and closing each one - and while
    /// that is happening the Retainer list is not a stable thing to read: the counts are mid-flight,
    /// and a Player invited to open a Retainer by hand to "help" is a Player whose retainer run
    /// gets a stuck window closed out from under it by the automation's own watchdog.
    ///
    /// EMM does not ask the automation plugin whether it is busy, because it does not have to and
    /// because that would be a dependency where there is currently none. It watches the bell
    /// instead: while any of the things a run moves is still moving, this is not idle. That answer
    /// is also right when the thing moving the bell is the Player.
    ///
    /// Nothing is lost by waiting, either - an open Retainer is captured by the ordinary tick every
    /// second whether or not anyone presses anything.
    /// </summary>
    internal string? CannotScan { get; private set; } = "not at a summoning bell";

    /// <summary>Whether the optional inventory companion is installed and ready. Polled, not asked per frame.</summary>
    internal bool CompanionReady { get; private set; }

    /// <summary>
    /// Whether EMM can have every Retainer opened for it, rather than only the one that already is.
    ///
    /// This is the capability tier made visible. With the automation plugin present the refresh
    /// reads every Retainer's contents; without it, the same press reads the counts and whatever is
    /// open. EMM does not hard-depend on it either way - its absence lowers what the control can do
    /// and changes nothing else.
    /// </summary>
    internal bool CanWalk => walk.Available;

    /// <summary>Whether a walk is running right now.</summary>
    internal bool Walking => walk.Walking;

    /// <summary>How the current or last walk went.</summary>
    internal (int Read, int Missed) WalkProgress => (walk.Read, walk.Missed);

    /// <summary>Stops a walk in progress. Retainers still to come are handed straight back.</summary>
    internal void StopWalking() => walk.Stop();

    /// <summary>Whether EMM has managed to load what it previously stored.</summary>
    internal bool Loaded => loaded;

    /// <summary>
    /// Reads what is readable and rebuilds the view. Called once a frame; does almost nothing most
    /// frames.
    /// </summary>
    /// <param name="now">The instant readings are stamped and ages measured from.</param>
    internal void Tick(DateTimeOffset now)
    {
        if (now < nextCapture)
        {
            return;
        }

        nextCapture = now + CaptureInterval;

        Load();

        var character = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.CharacterName : null;
        var open = character is null ? null : GameHoldings.Open(character);

        // A walk ends by going quiet rather than by announcing itself, so the roster is re-read on
        // the tick after it stops. Every Retainer it opened moved that Retainer's expiry, and a
        // count taken before the walk would be graded against contents read during it.
        if (wasWalking && !walk.Walking && character is not null)
        {
            roster = GameHoldings.Roster(character, now);
        }

        wasWalking = walk.Walking;

        if (character is not null)
        {
            Capture(character, now);

            // The roster is per Character, so a switch invalidates it rather than leaving one
            // Character's Retainers standing beside another's Holdings.
            if (roster is not null && !string.Equals(roster.Character, character, StringComparison.Ordinal))
            {
                roster = null;
            }
        }

        if (now >= nextCompanionPoll)
        {
            nextCompanionPoll = now + CompanionPoll;
            CompanionReady = companion.Ready;
        }

        Settle(open, now);
        Publish(HoldingsView.Build(ledger, roster, open, now));

        Flush();
    }

    /// <summary>
    /// Watches the bell for movement, and works out whether the refresh may run.
    ///
    /// The fingerprint is deliberately coarse - which Retainer is open and how many the list
    /// reports - because the question is only "is something driving this right now". Anything
    /// finer would reset the timer on noise; anything coarser would miss a run entirely.
    /// </summary>
    private void Settle(RetainerId? open, DateTimeOffset now)
    {
        var was = CannotScan;

        if (!AtTheBell)
        {
            bellState = string.Empty;
            settledAt = DateTimeOffset.MaxValue;
            CannotScan = "not at a summoning bell";
        }
        else
        {
            var atList = GameHoldings.AtTheRetainerList;
            var state = $"{atList}/{open?.Retainer ?? "-"}/{roster?.Retainers.Count ?? -1}";

            if (state != bellState)
            {
                bellState = state;
                settledAt = now;
            }

            CannotScan =
                walk.Walking ? "EMM is reading every Retainer now"
                : !atList ? "the Retainer list is not up - back out to it and EMM will read it"
                : now - settledAt < Quiet ? "the bell is still busy"
                : null;
        }

        // Logged on change, because a control that is disabled and does not say why in a place the
        // maintainer can read afterwards is one that gets reported as "the button is broken" - and
        // that is exactly how this arrived.
        if (CannotScan != was)
        {
            Plugin.Log.Information(
                "EMM holdings: refresh is {State}", CannotScan is null ? "ready" : $"held back - {CannotScan}");
        }
    }

    /// <summary>
    /// Refreshes every Retainer's state from the summoning bell.
    ///
    /// <b>What it can refresh is counts, and it says so.</b> The bell exposes every Retainer's
    /// Listing count, item count, gil and market expiry without any of them being opened, and it
    /// exposes no contents at all - the game loads a Retainer's containers when that Retainer is
    /// opened and not before. So this brings every Retainer's counts up to the moment, reads the
    /// contents of whichever one happens to be open, asks the companion for whatever it covers,
    /// and then grades what EMM already held against the fresh counts. A Retainer whose count no
    /// longer matches what EMM last saw listed is marked as such rather than left looking current.
    /// </summary>
    /// <param name="now">The instant the refresh runs at.</param>
    internal void Scan(DateTimeOffset now)
    {
        if (CannotScan is { } blocked)
        {
            LastScan = new HoldingsScan(0, 0, 0, blocked);
            return;
        }

        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.CharacterName is not { Length: > 0 } character)
        {
            LastScan = new HoldingsScan(0, 0, 0, "not logged in");
            return;
        }

        roster = GameHoldings.Roster(character, now);

        var readInFull = Capture(character, now) ? 1 : 0;
        var fromCompanion = 0;

        foreach (var reading in companion.Read(character, GameHoldings.RetainerIds(character), now))
        {
            if (Keep(reading))
            {
                fromCompanion++;
            }
        }

        // The part that answers the control's own label. The counts above are what the bell gives
        // for free; the contents need every Retainer opened, and this is where that is asked for.
        // Where the automation plugin is not there to do the walking, the refresh is still worth
        // pressing - it just does the smaller job, and the surface says which one happened.
        var walking = walk.Request();

        Publish(HoldingsView.Build(ledger, roster, GameHoldings.Open(character), now));

        LastScan = new HoldingsScan(roster?.Retainers.Count ?? 0, readInFull, fromCompanion, null);

        Plugin.Log.Information(
            "EMM holdings: refreshed {Retainers} Retainers at the bell - {Read} read in full, " +
            "{Companion} covered by {CompanionName}, walk {Walk}",
            LastScan.Retainers, readInFull, fromCompanion, CompanionHoldings.PluginName,
            walking ? "requested" : "unavailable");

        Flush();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // First, and it matters more than the write below: the walk may be holding a Retainer
        // window open, and AutoRetainer stays blocked with its own bailout suppressed until that
        // window is handed back. Unloading without closing it would stop the Player's retainer run
        // with nothing left running that could release it.
        walk.Dispose();

        // A blocking wait, unlike every other path here, and bounded. This runs while EMM is
        // unloading: the alternative to waiting a moment for the gate is discarding a reading of a
        // Retainer that cannot be read again without the Player going back to a bell.
        if (pending.Count == 0 || !storeGate.Wait(TimeSpan.FromSeconds(2)))
        {
            return;
        }

        try
        {
            Write();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EMM holdings: the last write before unloading failed");
        }
        finally
        {
            storeGate.Release();
        }
    }

    /// <summary>
    /// Rehydrates the ledger from the store, once, as soon as the gate allows.
    ///
    /// Not in the constructor: EMM loads while a refresh may already be running, and a constructor
    /// that blocked on the store gate would hold the whole plugin load behind a network sweep.
    /// </summary>
    private void Load()
    {
        if (loaded || !storeGate.Wait(0))
        {
            return;
        }

        try
        {
            foreach (var reading in store.ReadHoldings())
            {
                onDisk[reading.Place] = reading;
                ledger.Record(reading);
            }

            loaded = true;

            Plugin.Log.Information(
                "EMM holdings: {Places} places loaded from the store", onDisk.Count);
        }
        catch (Exception ex)
        {
            // Marked loaded anyway. A store that cannot be read will not start being readable on
            // the next frame, and retrying every second would turn one fault into a log flood.
            loaded = true;
            Plugin.Log.Error(ex, "EMM holdings: what EMM had stored could not be read");
        }
        finally
        {
            storeGate.Release();
        }
    }

    /// <summary>
    /// Takes a rebuilt view, works out what the filter controls can offer over it, and narrows it.
    ///
    /// The two control populations are derived from the Holdings rather than from the game: a
    /// Category filter listing every Category in the Item table, or a location filter listing a
    /// Retainer EMM has never read, would be controls whose every use produced an empty list.
    /// </summary>
    private void Publish(HoldingsView rebuilt)
    {
        View = rebuilt;

        // Ordered by the enum rather than alphabetically: the seven are a deliberate sequence, from
        // the fastest-moving bulk to the one-offs, and shuffling them into alphabetical order would
        // throw away the only structure the list has.
        Types =
        [
            .. rebuilt.Wares
                .Select(ware => ItemFacts.Type(ware.Ware.ItemId))
                .Distinct()
                .Order(),
        ];

        Locations =
        [
            ShownLocation.Bags,
            .. rebuilt.Wares
                .SelectMany(ware => ware.Places)
                .Select(row => row.Retainer)
                .OfType<RetainerId>()
                .Distinct()
                .OrderBy(retainer => retainer.Character, StringComparer.Ordinal)
                .ThenBy(retainer => retainer.Retainer, StringComparer.Ordinal)
                .Select(ShownLocation.Of),
        ];

        // A filter whose subject has gone - a Category nothing is left in, a Retainer no longer
        // held - is cleared rather than left selected. Otherwise the Player is looking at an empty
        // table with no indication that the emptiness is the control's fault.
        if (Type is { } shownType && !Types.Contains(shownType))
        {
            Type = null;
        }

        if (Location is { } shown && !Locations.Contains(shown))
        {
            Location = null;
        }

        Refilter();
    }

    /// <summary>Narrows the view to what the controls are asking for.</summary>
    private void Refilter() =>
        Shown = Type is null && Location is null
            ? View
            : View.Where(row =>
                (Type is not { } type || ItemFacts.Type(row.Ware.ItemId) == type) &&
                (Location is not { } only || only.Keeps(row)));

    /// <summary>
    /// Reads everything the game will let EMM read right now.
    ///
    /// Opportunistic, and the only kind available. EMM reads what the game has already loaded
    /// because the Player opened it; it does not open anything itself. Bags come along either way -
    /// they are free once this is running, and a Player standing at a bell has usually just moved
    /// something into or out of them.
    /// </summary>
    /// <returns>Whether a Retainer's contents were read in full.</returns>
    private bool Capture(string character, DateTimeOffset now)
    {
        var attempt = GameHoldings.OpenRetainer(character, now);
        var read = Keep(attempt.Reading);

        // Logged on change rather than every second. "EMM did not read that Retainer" is the one
        // thing on this surface with no other way of being seen - the tab shows the Retainer's
        // previous contents either way, and a Player watching a refresh do nothing has nowhere
        // else to look for why.
        if (attempt.Refused != refusal)
        {
            refusal = attempt.Refused;

            if (refusal is not null)
            {
                Plugin.Log.Information("EMM holdings: the open Retainer was not read - {Reason}", refusal);
            }
        }

        Keep(GameHoldings.Bags(character, now));

        return read;
    }

    /// <summary>
    /// Files a reading and queues it for the store where it says something new.
    /// </summary>
    /// <returns>Whether the ledger kept it.</returns>
    private bool Keep(HoldingsReading? reading)
    {
        if (reading is null || !ledger.Record(reading))
        {
            return false;
        }

        if (WorthWriting(reading))
        {
            pending[reading.Place] = reading;
        }

        return true;
    }

    /// <summary>
    /// Whether the store needs this reading.
    ///
    /// Contents first, age second. An unchanged Retainer read every second must not be written
    /// every second - but leaving the stored copy alone forever would mean a restart reporting a
    /// Freshness hours older than the one on screen, which is a figure nobody measured.
    /// </summary>
    private bool WorthWriting(HoldingsReading reading)
    {
        var stored = pending.GetValueOrDefault(reading.Place) ?? onDisk.GetValueOrDefault(reading.Place);

        if (stored is null || stored.Source != reading.Source || !stored.Held.SequenceEqual(reading.Held))
        {
            return true;
        }

        return (reading.TrueAsOf, stored.TrueAsOf) switch
        {
            ({ } fresh, { } old) => fresh - old >= FreshnessDrift,
            _ => reading.ObservedAt - stored.ObservedAt >= FreshnessDrift,
        };
    }

    private void Flush()
    {
        if (pending.Count == 0 || !storeGate.Wait(0))
        {
            return;
        }

        try
        {
            Write();
        }
        catch (Exception ex)
        {
            // The readings stay pending, so the next tick tries again. Losing them would lose a
            // Retainer's contents, and those cannot be fetched from anywhere.
            Plugin.Log.Error(ex, "EMM holdings: writing to the store failed");
        }
        finally
        {
            storeGate.Release();
        }
    }

    private void Write()
    {
        foreach (var reading in pending.Values.ToList())
        {
            store.WriteHoldings(reading);
            onDisk[reading.Place] = reading;
            pending.Remove(reading.Place);
        }
    }
}

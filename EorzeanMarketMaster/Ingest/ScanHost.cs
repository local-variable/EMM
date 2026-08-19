using System;
using System.Threading;
using System.Threading.Tasks;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Ingest;
using EorzeanMarketMaster.Core.Store;

namespace EorzeanMarketMaster.Ingest;

/// <summary>
/// Everything the Scan surface needs to draw, taken once and then read from as often as a frame
/// likes.
/// </summary>
/// <param name="World">The World being read, or null where nobody is logged in.</param>
/// <param name="Ware">The Ware the Player has selected.</param>
/// <param name="Reading">What the store holds for it, with its age and grade.</param>
/// <param name="PointCost">What refreshing that one Ware would cost.</param>
/// <param name="SweepVerdict">Whether a ring-wide refresh may start, when, and what it would cost.</param>
/// <param name="TrackedWares">How many Wares a ring-wide refresh would cover.</param>
/// <param name="LastReport">What the last refresh did.</param>
/// <param name="Running">Whether a refresh is in flight.</param>
internal sealed record ScanView(
    WorldId? World,
    WareId Ware,
    MarketReading? Reading,
    RefreshCost? PointCost,
    RefreshVerdict? SweepVerdict,
    int TrackedWares,
    IngestReport? LastReport,
    bool Running)
{
    /// <summary>Nothing seen yet.</summary>
    internal static ScanView Empty { get; } =
        new(null, new WareId(0, Quality.Normal), null, null, null, 0, null, false);
}

/// <summary>
/// The plugin side of ingest: holds the sweep floor across a session, drives a refresh off the
/// framework thread, and keeps a view of the store for the Scan surface to draw.
///
/// <b>Store access is serialised through the gate <see cref="Store.StoreHost"/> owns.</b> The
/// store is one SQLite connection and a connection is not something two threads may share, so
/// every touch of it goes through that one gate: a refresh holds it for its whole run, and the
/// surface's periodic read gives up immediately rather than waiting, showing the previous reading
/// for as long as a refresh is in flight. A frame that blocked on a sweep would be a frozen game.
/// The gate used to live here, which was true only while this was the store's one reader.
///
/// <b>Nothing but the framework thread writes <see cref="View"/>.</b> The refresh task publishes
/// its report to one field and the next tick folds it in. A record swapped from two threads would
/// be a read-modify-write race whose worst case is a lost update - cosmetic here, and exactly the
/// kind of thing that stops being cosmetic three tickets later.
///
/// <b>The sweep floor is carried across a restart.</b> It is written to the plugin's own
/// configuration when a sweep starts and read back when EMM loads, because a floor that resets on
/// reload is one a plugin reload steps straight over.
/// </summary>
internal sealed class ScanHost : IDisposable
{
    /// <summary>
    /// How often the selected Ware's reading is rebuilt. Once a second: the figures it shows are
    /// hours old by nature, and a query per frame would tell nobody anything.
    /// </summary>
    private static readonly TimeSpan ReadingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often the World-wide figures are rebuilt - the Freshness calibration and the tracked
    /// population behind a sweep. Both walk far more of the store than one Ware's newest
    /// observation does, and neither moves on a scale a Player would notice.
    /// </summary>
    private static readonly TimeSpan PopulationInterval = TimeSpan.FromSeconds(30);

    /// <summary>How far back the surface looks for an observation of the selected Ware.</summary>
    private static readonly TimeSpan ReadWindow = TimeSpan.FromDays(30);

    private readonly MarketStore store;
    private readonly Configuration configuration;
    private readonly Selection selection;
    private readonly UniversalisTransport transport = new();
    private readonly RealPacing pacing = new();
    private readonly SweepGate gate;
    private readonly SemaphoreSlim storeGate;
    private readonly CancellationTokenSource cancellation = new();

    private int selectedRevision = -1;
    private WorldId? calibratedFor;
    private WorldFreshness? calibration;
    private FetchPlan? sweepPlan;
    private int trackedWares;
    private DateTimeOffset nextReading = DateTimeOffset.MinValue;
    private DateTimeOffset nextPopulation = DateTimeOffset.MinValue;
    private Task? running;

    /// <summary>Written by the refresh task, read by the framework thread. Nothing else crosses.</summary>
    private volatile IngestReport? lastReport;

    internal ScanHost(MarketStore store, SemaphoreSlim storeGate, Configuration configuration, Selection selection)
    {
        this.store = store;
        this.storeGate = storeGate;
        this.configuration = configuration;
        this.selection = selection;

        gate = new SweepGate(configuration.LastSweepStartedAt);
    }

    /// <summary>What the surface draws. Replaced wholesale, so a frame never sees half an update.</summary>
    internal ScanView View { get; private set; } = ScanView.Empty;

    /// <summary>Which Ware the Player is looking at.</summary>
    internal WareId Selected => selection.Ware;

    /// <summary>Points every surface at a different Ware and forces this one's reading to be rebuilt.</summary>
    /// <param name="ware">The Ware.</param>
    internal void Select(WareId ware) => selection.Select(ware);

    /// <summary>
    /// Rebuilds the view where it is due. Called once a frame; does almost nothing most frames.
    /// </summary>
    /// <param name="now">The instant to measure ages and countdowns from.</param>
    /// <param name="world">The Player's home World, or null where nobody is logged in.</param>
    internal void Tick(DateTimeOffset now, WorldId? world)
    {
        if (running is { IsCompleted: true })
        {
            running = null;
            nextReading = DateTimeOffset.MinValue;
            nextPopulation = DateTimeOffset.MinValue;
        }

        // The Ware is shared with every other surface, so it can change without this host having
        // been the one asked. Watching the revision is what keeps the reading in step with what
        // the Player picked, wherever they picked it.
        if (selectedRevision != selection.Revision)
        {
            selectedRevision = selection.Revision;
            nextReading = DateTimeOffset.MinValue;
        }

        if (world is not { } here)
        {
            View = ScanView.Empty with { Ware = selection.Ware, LastReport = lastReport };
            return;
        }

        // A World change invalidates everything World-shaped, however recently it was computed.
        if (calibratedFor != here)
        {
            calibratedFor = here;
            calibration = null;
            sweepPlan = null;
            trackedWares = 0;
            nextReading = DateTimeOffset.MinValue;
            nextPopulation = DateTimeOffset.MinValue;
        }

        if (now >= nextReading && running is null && storeGate.Wait(0))
        {
            try
            {
                if (now >= nextPopulation)
                {
                    nextPopulation = now + PopulationInterval;
                    calibration = StoredMarket.Calibrate(store, here, now);

                    var tracked = store.TrackedWares(here);

                    // Logged on change rather than every poll. What a sweep would cover is the one
                    // fact behind the Scan surface that has no other way of being seen: a tracked
                    // population of zero and a control that failed to draw look identical from the
                    // outside, and the first in-game pass was spent telling them apart.
                    if (tracked.Count != trackedWares)
                    {
                        Plugin.Log.Information(
                            "EMM scan: World {World} tracks {Tracked} Wares (was {Previous})",
                            here.Id, tracked.Count, trackedWares);
                    }

                    trackedWares = tracked.Count;
                    sweepPlan = tracked.Count == 0
                        ? null
                        : FetchPlan.For(here, tracked, ListingWindow.Standard, HistoryWindow.Backfill);
                }

                nextReading = now + ReadingInterval;

                var ware = selection.Ware;

                View = new ScanView(
                    here,
                    ware,
                    StoredMarket.Latest(
                        store, ware, here, calibration ?? Uncalibrated(here), now, ReadWindow),
                    FetchPlan.For(
                        here, [ware], ListingWindow.Standard, HistoryFor(here, ware, now)).Cost,
                    sweepPlan is null ? null : gate.Assess(sweepPlan, now),
                    trackedWares,
                    lastReport,
                    Running: false);

                return;
            }
            finally
            {
                storeGate.Release();
            }
        }

        // Between reads there is still one thing that has to move, or a queued sweep would appear
        // frozen: its countdown. Assessing a plan already in hand touches nothing but arithmetic.
        View = View with
        {
            SweepVerdict = sweepPlan is null ? null : gate.Assess(sweepPlan, now),
            LastReport = lastReport,
            Running = running is not null,
        };
    }

    /// <summary>
    /// Refreshes the selected Ware alone. Immediate: a point lookup is not a sweep.
    /// </summary>
    /// <param name="now">The instant the refresh is taken to start at.</param>
    /// <param name="world">The World whose Market to read.</param>
    internal void RefreshPoint(DateTimeOffset now, WorldId world)
    {
        if (running is not null || !storeGate.Wait(0))
        {
            return;
        }

        FetchPlan plan;

        try
        {
            var ware = selection.Ware;

            plan = FetchPlan.For(world, [ware], ListingWindow.Standard, HistoryFor(world, ware, now));
        }
        finally
        {
            storeGate.Release();
        }

        Start(plan, now, gate);
    }

    /// <summary>
    /// Refreshes everything the store already tracks on this World.
    ///
    /// A sweep, and it obeys the fifteen-minute floor. The population is what EMM has observed so
    /// far rather than a seeded ring - the seeded population arrives with its own ticket, and this
    /// will run over that instead when it does.
    /// </summary>
    /// <param name="now">The instant the refresh is taken to start at.</param>
    /// <param name="ignoringTheFloor">
    /// Runs against a gate that has never seen a sweep, so the fifteen-minute floor does not apply.
    /// DEBUG BUILDS ONLY - see <see cref="Ui.ScanTab"/>, which is the only caller that ever passes
    /// true and is itself compiled out of a release.
    ///
    /// Note what this deliberately does NOT do: it does not touch the real gate, so a forced run
    /// neither clears nor extends the genuine floor and nothing is written to the configuration.
    /// The rule in Core is not weakened, bypassed or given a back door - the caller is simply
    /// handed a different gate, and Core still has exactly one rule about what a gate permits.
    /// </param>
    internal void RefreshRing(DateTimeOffset now, bool ignoringTheFloor = false)
    {
        if (sweepPlan is not { } plan)
        {
            return;
        }

        if (ignoringTheFloor)
        {
            Plugin.Log.Information(
                "EMM scan: refreshing everything while IGNORING the {Minutes}-minute floor - a debug " +
                "build only affordance. The real floor is untouched and still reads {LastSweep}.",
                Citizenship.SweepFloor.TotalMinutes,
                gate.LastSweepStartedAt?.ToString("O") ?? "never");
        }

        Start(plan, now, ignoringTheFloor ? new SweepGate() : gate);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        cancellation.Cancel();

        try
        {
            // The store is disposed right after this, and disposing a connection out from under a
            // write in flight is how a database gets a torn page.
            running?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // A cancelled refresh is the expected outcome here, not a problem to report.
        }

        cancellation.Dispose();
        transport.Dispose();

        // The store gate is NOT disposed here. It belongs to StoreHost, which outlives this and
        // hands the same one to every other reader; disposing a borrowed gate would take the graph
        // down with the scan.
    }

    /// <summary>A World EMM has seen nothing of, so that a reading can still be produced for it.</summary>
    private static WorldFreshness Uncalibrated(WorldId world) => WorldFreshness.From(world, []);

    /// <summary>
    /// The history window for one Ware: the whole backfill the first time, and only what is newer
    /// than the store's newest Sale after that. Reads the store, so callers hold the gate.
    /// </summary>
    private HistoryWindow HistoryFor(WorldId world, WareId ware, DateTimeOffset now) =>
        store.NewestSaleAt(ware, world) is { } newest
            ? HistoryWindow.Since(newest, now)
            : HistoryWindow.Backfill;

    /// <param name="against">
    /// The gate this run answers to. Always the real one, except for the debug-only forced refresh
    /// which is handed a fresh gate rather than a weakened rule.
    /// </param>
    private void Start(FetchPlan plan, DateTimeOffset now, SweepGate against)
    {
        if (running is not null)
        {
            return;
        }

        // Assessed here as well as inside the ingest, so that a queued sweep does not even take
        // the store gate. The ingest asks again and is the one that decides.
        if (!against.Assess(plan, now).MayStartNow)
        {
            return;
        }

        running = Task.Run(async () =>
        {
            await storeGate.WaitAsync(cancellation.Token).ConfigureAwait(false);

            try
            {
                lastReport = await AggregatorIngest
                    .Run(plan, against, transport, pacing, store, now, cancellation.Token)
                    .ConfigureAwait(false);

                // Only ever the real gate reaches the configuration. A forced run answers to a
                // throwaway one, so it cannot move the floor the Player is actually held to.
                if (gate.LastSweepStartedAt != configuration.LastSweepStartedAt)
                {
                    configuration.LastSweepStartedAt = gate.LastSweepStartedAt;
                    configuration.Save();
                }
            }
            catch (OperationCanceledException)
            {
                // EMM is unloading. Nothing to report to a window that is going away.
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "EMM refresh failed");
            }
            finally
            {
                storeGate.Release();
            }
        });
    }
}

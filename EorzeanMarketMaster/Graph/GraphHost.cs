using System;
using System.Collections.Generic;
using System.Threading;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Graph;
using EorzeanMarketMaster.Core.Store;

namespace EorzeanMarketMaster.Graph;

/// <summary>
/// The plugin side of the graph: reads the store, hands the surface a view, and does neither more
/// often than it needs to.
///
/// <b>Nothing here decides what the picture says.</b> Every rule the graph rests on - which slices
/// are occupied, where the line breaks, how big a dot is, whether a Listing's start is traced or
/// assumed - is arithmetic in Core, asserted with no client running. What is left here is the two
/// things Core is not allowed to do: touch the store, and know what a frame is.
///
/// <b>Reads are not on a timer for their own sake.</b> The view is rebuilt when the Player changes
/// something - the Ware, the window, which Qualities - and otherwise only every few seconds, to
/// pick up Sales a refresh has since written. A query per frame against a series that moves hourly
/// would be a cost with no reader, and the same reasoning is why the Scan surface reads once a
/// second rather than every frame.
/// </summary>
internal sealed class GraphHost
{
    /// <summary>
    /// How often the view is rebuilt when nothing has been touched. Slow on purpose: what changes
    /// underneath is a refresh writing Sales, and those arrive hourly at best.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly MarketStore store;
    private readonly SemaphoreSlim storeGate;
    private readonly Selection selection;

    private DateTimeOffset nextRead = DateTimeOffset.MinValue;
    private int readRevision = -1;
    private WorldId? readWorld;
    private GraphWindow window = GraphWindow.Default;
    private QualityOverlay overlay = QualityOverlay.Both;
    private bool settingsChanged = true;
    private bool axisMoved = true;

    internal GraphHost(MarketStore store, SemaphoreSlim storeGate, Selection selection)
    {
        this.store = store;
        this.storeGate = storeGate;
        this.selection = selection;
    }

    /// <summary>
    /// What the surface draws, or null before the first read has happened or while nobody is
    /// logged in. Replaced wholesale, so a frame never sees half an update.
    /// </summary>
    internal GraphView? View { get; private set; }

    /// <summary>Which time preset is open.</summary>
    internal GraphWindow Window => window;

    /// <summary>The Item on show. Both its Wares are candidates for a layer.</summary>
    internal uint Item => selection.Ware.ItemId;

    /// <summary>
    /// Which of the Item's two Wares the shared selection is on.
    ///
    /// The graph is drawn per Item, so this is not what decides which layers appear - the overlay
    /// is. It is what anything illustrating the selection has to use, because an Item has two icons
    /// and picking the wrong one contradicts the Scan section looking at the same Ware.
    /// </summary>
    internal Quality Quality => selection.Ware.Quality;

    /// <summary>
    /// Points every surface at a different Item, keeping the Quality the Player last chose.
    ///
    /// The graph is drawn per Item because HQ and NQ are an overlay on one axis, but the selection
    /// it shares with the Scan section is a Ware - so the Quality has to come from somewhere, and
    /// the honest somewhere is whatever it already was.
    /// </summary>
    /// <param name="itemId">The Item.</param>
    internal void Select(uint itemId) => selection.Select(new WareId(itemId, selection.Ware.Quality));

    /// <summary>Which Wares of the Item are drawn.</summary>
    internal QualityOverlay Overlay => overlay;

    /// <summary>
    /// Whether the plot should have its time axis forced this frame, consuming the answer.
    ///
    /// The bounds are forced on the frame a preset moved and left alone afterwards, so that
    /// choosing a window applies it while dragging the plot still works. The "has it been applied"
    /// flag lives here rather than beside the drawing code because the drawing code is static and
    /// a static field remembering which plot it last drew is a bug waiting for a second window -
    /// which is exactly what the graph decision asked for when it required the graph to open as an
    /// item overlay too.
    /// </summary>
    /// <returns>Whether to force the axis.</returns>
    internal bool TakeAxisForce()
    {
        if (!axisMoved)
        {
            return false;
        }

        axisMoved = false;

        return true;
    }

    /// <summary>Opens a different stretch of History.</summary>
    /// <param name="preset">The window.</param>
    internal void Show(GraphWindow preset)
    {
        if (preset == window)
        {
            return;
        }

        window = preset;
        settingsChanged = true;
    }

    /// <summary>Chooses which of the Item's two Wares are drawn.</summary>
    /// <param name="wanted">The overlay.</param>
    internal void Show(QualityOverlay wanted)
    {
        if (wanted == overlay)
        {
            return;
        }

        overlay = wanted;
        settingsChanged = true;
    }

    /// <summary>
    /// Rebuilds the view where it is due. Called once a frame; does almost nothing most frames.
    /// </summary>
    /// <param name="now">The instant the window runs up to.</param>
    /// <param name="world">The Player's home World, or null where nobody is logged in.</param>
    internal void Tick(DateTimeOffset now, WorldId? world)
    {
        if (world is not { } here)
        {
            View = null;
            readWorld = null;
            return;
        }

        var due = settingsChanged
                  || readRevision != selection.Revision
                  || readWorld != here
                  || now >= nextRead;

        if (!due || !storeGate.Wait(0))
        {
            return;
        }

        try
        {
            Rebuild(here, now);
        }
        catch (Exception ex)
        {
            // A store that cannot be read is not a reason to take the window down. The surface
            // draws the absence; the log carries the cause.
            Plugin.Log.Error(ex, "EMM graph: reading the store failed");
            View = null;
        }
        finally
        {
            settingsChanged = false;
            readRevision = selection.Revision;
            readWorld = here;
            nextRead = now + Interval;

            storeGate.Release();
        }
    }

    private void Rebuild(WorldId here, DateTimeOffset now)
    {
        var item = selection.Ware.ItemId;
        var nq = new WareId(item, Quality.Normal);
        var hq = new WareId(item, Quality.High);

        // Both Wares are read whatever the overlay shows, and that is deliberate: the "all of
        // History" window is bounded by the oldest Sale of EITHER, so a picture drawn from one of
        // them would silently change its own time axis when the other was toggled on.
        var earliest = Earliest(store.OldestSaleAt(nq, here), store.OldestSaleAt(hq, here));
        var from = window.From(now, earliest);

        var histories = new[]
        {
            store.ReadSales(nq, here, from, now),
            store.ReadSales(hq, here, from, now),
        };

        // The board as last observed, which is what a Listing line is a statement about. Bounded
        // by the same window so a Snapshot older than the picture does not draw Listings onto it.
        var boards = new List<Snapshot>();

        foreach (var ware in new[] { nq, hq })
        {
            if (store.LatestSnapshot(ware, here, from) is { } board)
            {
                boards.Add(board);
            }
        }

        var rebuilt = GraphView.Build(item, here, window, overlay, histories, boards, null, now);

        if (View is null || View.From != rebuilt.From || View.ToExclusive != rebuilt.ToExclusive)
        {
            axisMoved = true;
        }

        View = rebuilt;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? left, DateTimeOffset? right) =>
        (left, right) switch
        {
            (null, null) => null,
            ({ } only, null) => only,
            (null, { } only) => only,
            ({ } a, { } b) => a < b ? a : b,
        };
}

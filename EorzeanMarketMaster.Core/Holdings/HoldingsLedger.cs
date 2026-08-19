namespace EorzeanMarketMaster.Core.Holdings;

/// <summary>
/// The most recent reading EMM holds of every place, and the one rule that decides which reading
/// that is.
///
/// <b>Readings replace, they do not merge.</b> A reading is the complete contents of its place, so
/// a newer one supersedes the previous one entirely rather than being folded into it. Merging row
/// by row would keep a Ware alive after it had sold, and "the Listing that would not go away" is
/// the defect this class exists to make impossible rather than to remember not to write.
///
/// <b>An undated reading fills a gap and never displaces a dated one.</b> That is the whole of the
/// companion plugin's standing here, and it is the ticket's own wording made into arithmetic: its
/// interface is an additional Source whose absence changes nothing but coverage. It answers
/// instantly with whatever it is holding and cannot say when it last looked, so it has no age to
/// compare - and a Source with no age must never be allowed to overwrite one EMM can date, in
/// either direction. Where EMM has read a Retainer itself, EMM's reading stands however old it is,
/// and it says how old it is. Where EMM has never opened that Retainer, the companion is the only
/// thing there is, and coverage with an unknown age beats no coverage at all.
/// </summary>
public sealed class HoldingsLedger
{
    private readonly Dictionary<HoldingsPlaceKey, HoldingsReading> latest = [];

    /// <summary>An empty ledger: EMM has looked nowhere.</summary>
    public HoldingsLedger()
    {
    }

    /// <summary>
    /// Rebuilds a ledger from readings held somewhere else - the store, at load.
    /// </summary>
    /// <param name="readings">
    /// The readings, in any order. Where two cover the same place the same rule decides, so a
    /// store that somehow held both does not depend on which came back first.
    /// </param>
    /// <returns>The ledger.</returns>
    public static HoldingsLedger From(IEnumerable<HoldingsReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var ledger = new HoldingsLedger();

        foreach (var reading in readings)
        {
            ledger.Record(reading);
        }

        return ledger;
    }

    /// <summary>
    /// Every unit the Player is known to own, flattened for the decision seam.
    ///
    /// Ordered so two ledgers built from the same readings in different orders produce the same
    /// list: a state handed to the engine that varies with dictionary iteration order would make a
    /// reproducible decision impossible to reproduce.
    /// </summary>
    /// <returns>The Holdings, ordered by Character, then Retainer, then Ware, then place.</returns>
    public IReadOnlyList<Holding> Holdings() =>
    [
        .. latest.Values
            .SelectMany(reading => reading.Holdings())
            .OrderBy(h => h.Character, StringComparer.Ordinal)
            .ThenBy(h => h.Retainer?.Retainer ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(h => h.Ware.ItemId)
            .ThenBy(h => h.Ware.Quality)
            .ThenBy(h => h.Place),
    ];

    /// <summary>What EMM last read of one place, or null where it has never read it.</summary>
    /// <param name="place">The place.</param>
    /// <returns>The reading, or null.</returns>
    public HoldingsReading? Of(HoldingsPlaceKey place) =>
        latest.TryGetValue(place, out var reading) ? reading : null;

    /// <summary>
    /// Files a reading, keeping it only where it is a better statement about its place than the
    /// one already held.
    /// </summary>
    /// <param name="reading">The reading.</param>
    /// <returns>Whether it was kept. False means the ledger already held something better.</returns>
    public bool Record(HoldingsReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        if (!Supersedes(reading, Of(reading.Place)))
        {
            return false;
        }

        latest[reading.Place] = reading;

        return true;
    }

    /// <summary>
    /// Whether a reading should replace the one held.
    ///
    /// Four cases, written out rather than collapsed, because the interesting one is the third and
    /// a shorter spelling hides it. Comparing on <see cref="HoldingsReading.TrueAsOf"/> and never
    /// on <see cref="HoldingsReading.ObservedAt"/> is the point: the moment EMM asked is a fact
    /// about EMM, and every undated reading would win on it forever.
    /// </summary>
    private static bool Supersedes(HoldingsReading candidate, HoldingsReading? held)
    {
        // Nothing held. Anything at all is coverage.
        if (held is null)
        {
            return true;
        }

        return (candidate.TrueAsOf, held.TrueAsOf) switch
        {
            // Both dated: the one that describes a later moment.
            ({ } fresh, { } stale) => fresh > stale,

            // Dated over undated, whatever the clock says about when each was asked for.
            ({ }, null) => true,

            // Undated over dated: never. This is the companion's whole standing.
            (null, { }) => false,

            // Both undated, so there is no age to compare and the later ASK is all there is. Not
            // a claim that it is newer - only that re-asking the same Source is worth more than
            // holding its previous answer, which is what makes a refresh do anything at all.
            (null, null) => candidate.ObservedAt > held.ObservedAt,
        };
    }
}

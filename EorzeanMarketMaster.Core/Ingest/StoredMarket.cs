using EorzeanMarketMaster.Core.Store;

namespace EorzeanMarketMaster.Core.Ingest;

/// <summary>
/// The newest thing EMM holds about one Ware in one Market, with its age and its grade attached.
/// </summary>
/// <param name="Ware">The Ware.</param>
/// <param name="World">The Market's World.</param>
/// <param name="Snapshot">The newest Snapshot, or null where the store holds none.</param>
/// <param name="Age">
/// How old the figure is. Measured from when the Source last learned anything where it says so,
/// and from when EMM observed it otherwise - a board the Player opened is its own upload, so the
/// two coincide there rather than one being missing.
/// </param>
/// <param name="Grade">How that age reads against this World.</param>
/// <param name="Calibration">The World's own distribution, so a surface can show what it was judged against.</param>
public sealed record MarketReading(
    WareId Ware,
    WorldId World,
    Snapshot? Snapshot,
    TimeSpan? Age,
    FreshnessGrade Grade,
    WorldFreshness Calibration)
{
    /// <summary>Whether EMM holds anything at all about this Ware here.</summary>
    public bool HasObservation => Snapshot is not null;

    /// <summary>Where the figure came from, or null where there is no figure.</summary>
    public Source? Source => Snapshot?.Source;
}

/// <summary>
/// Reading the store, which is the only way EMM ever reads a Market.
///
/// <b>There is no offline mode here because there is no online one.</b> Every figure EMM shows
/// comes out of the store; fetching is a separate act that puts things into it. So an outage
/// degrades the figures - they get older, and their Freshness says so - rather than degrading the
/// plugin, and nothing in this file can tell whether a network exists.
///
/// That is a design choice with a cost worth stating: a Ware EMM has never observed reads as
/// nothing at all rather than as a live lookup, even where the network is fine. The alternative -
/// a read path that reaches for the aggregator when the store is thin - would put a network call
/// behind a UI draw and make every figure's provenance depend on when it was looked at.
/// </summary>
public static class StoredMarket
{
    /// <summary>
    /// The window a World's Freshness is calibrated over.
    ///
    /// Thirty days, which is also the raw Snapshot retention window - so the calibration is built
    /// from exactly the rows that are still there, and does not silently shrink as older weeks are
    /// dropped.
    /// </summary>
    public static TimeSpan CalibrationWindow { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Builds one World's Freshness calibration from what EMM has observed there.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="world">The World.</param>
    /// <param name="now">The instant to look back from.</param>
    /// <returns>The calibration, which reports itself uncalibrated where there is too little.</returns>
    public static WorldFreshness Calibrate(MarketStore store, WorldId world, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);

        return WorldFreshness.From(world, store.ReadUploadAges(world, now - CalibrationWindow, now));
    }

    /// <summary>
    /// The newest Snapshot EMM holds for one Ware in one Market, graded.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="ware">The Ware.</param>
    /// <param name="world">The World.</param>
    /// <param name="calibration">That World's calibration, from <see cref="Calibrate"/>.</param>
    /// <param name="now">The instant to measure the age from.</param>
    /// <param name="within">How far back to look for an observation.</param>
    /// <returns>The reading. Never null, and empty rather than absent where nothing is held.</returns>
    public static MarketReading Latest(
        MarketStore store,
        WareId ware,
        WorldId world,
        WorldFreshness calibration,
        DateTimeOffset now,
        TimeSpan within)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(calibration);

        var newest = store.LatestSnapshot(ware, world, now - within);

        if (newest is null)
        {
            return new MarketReading(ware, world, null, null, FreshnessGrade.Uncalibrated, calibration);
        }

        var age = now - (newest.UploadedAt ?? newest.ObservedAt);

        return new MarketReading(ware, world, newest, age, calibration.Grade(age), calibration);
    }
}

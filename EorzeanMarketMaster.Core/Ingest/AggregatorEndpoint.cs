using System.Globalization;

namespace EorzeanMarketMaster.Core.Ingest;

/// <summary>
/// One of the aggregator's read endpoints, with the two facts a plan needs about it: how many
/// Items fit in a request, and what a request costs in bytes.
///
/// Both are measured off the cached corpus rather than assumed, and they differ by an order of
/// magnitude between the two endpoints - which is the reason a plan has to know which endpoint it
/// is planning for instead of counting requests and calling that the cost.
/// </summary>
/// <param name="Name">What the endpoint is called, for a report a human reads.</param>
/// <param name="BatchSize">
/// Items per request. The endpoint's own documented maximum: batching is the single biggest lever
/// EMM has, since a 4,000-Ware sweep is 36 requests at 100 rather than 4,000 at one.
/// </param>
/// <param name="MeasuredBytesPerItem">
/// Bytes of response per Item, measured. See <see cref="Listings"/> and <see cref="History"/> for
/// what was measured and under which query parameters, because the number means nothing without
/// them.
/// </param>
public sealed record AggregatorEndpoint(string Name, int BatchSize, int MeasuredBytesPerItem)
{
    /// <summary>
    /// Current listings - <c>/api/v2/{world}/{itemIds}</c>. The only endpoint returning individual
    /// Listings, which is what an Undercut needs; the aggregated endpoint's <c>minListing</c>
    /// cannot say how many units sit below a price.
    ///
    /// <b>2,599 bytes per Item, measured</b>: 43,773,052 bytes across 169 cached responses covering
    /// the whole 16,843-Item marketable catalogue on one World, fetched at
    /// <c>listings=40&amp;entries=0</c>. It under-states EMM's own cost slightly, because EMM asks
    /// for more Listings than that - 345 of those 16,843 Items (2.05%) came back holding exactly
    /// 40 and were therefore truncated. Median was 3 Listings and the 90th percentile 17, so the
    /// difference is confined to a thin, liquid tail.
    /// </summary>
    public static AggregatorEndpoint Listings { get; } = new("listings", 100, 2_599);

    /// <summary>
    /// Sale history - <c>/api/v2/history/{world}/{itemIds}</c>. Batches at 20 rather than 100, so
    /// the same population costs five times the requests here.
    ///
    /// <b>13,447 bytes per Item, measured</b>: 226,489,787 bytes across 843 cached responses over
    /// the same catalogue, fetched at <c>entriesToReturn=3000&amp;entriesWithin=180 days</c>. That
    /// is the deep-backfill shape, and it is the anchor <see cref="HistoryWindow"/> scales from -
    /// an incremental refresh over an hour is a small fraction of it, which is precisely what
    /// makes setting <c>entriesWithin</c> explicitly worth doing rather than a nicety.
    /// </summary>
    public static AggregatorEndpoint History { get; } = new("history", 20, 13_447);

    /// <summary>How many requests this endpoint needs to cover a number of Items.</summary>
    /// <param name="items">Distinct Items to cover.</param>
    /// <returns>The request count.</returns>
    public int RequestsFor(int items) => items <= 0 ? 0 : ((items - 1) / BatchSize) + 1;
}

/// <summary>
/// How much of the current board a listings request asks for.
///
/// Every field is sent explicitly, including the ones whose default EMM would have been happy
/// with. The point is not that the defaults are wrong here - it is that a window parameter left
/// unsent is a window nobody has read, and the sister endpoint's defaults silently truncate a
/// request for "the history" into a request for the last week. A rule that applies to the
/// parameter that bites and not to its neighbours is a rule nobody remembers to apply.
/// </summary>
/// <param name="Listings">
/// Listings returned per Item. 100 rather than the endpoint's "all", so the figure is stated;
/// 2.05% of the catalogue was measured holding more than 40, and how many hold more than 100 is
/// UNMEASURED - the cached corpus was itself fetched at 40 and cannot answer it.
/// </param>
/// <param name="Entries">
/// Recent sale entries returned per Item. Zero, deliberately: History comes from the history
/// endpoint, and asking for it here would pay for the same rows twice.
/// </param>
/// <param name="StatsWithin">
/// The window the endpoint computes its own statistics over. EMM ignores every one of them - it
/// builds its History from individual Sales because no Source publishes rollups - so this exists
/// to be stated rather than to be used.
/// </param>
public sealed record ListingWindow(int Listings, int Entries, TimeSpan StatsWithin)
{
    /// <summary>The window EMM asks for.</summary>
    public static ListingWindow Standard { get; } = new(100, 0, TimeSpan.FromDays(7));
}

/// <summary>
/// How much history a history request asks for, and the parameter pair the research note called
/// "the trap": left unset, <c>entriesToReturn</c> caps at 1,800 rows and <c>entriesWithin</c> caps
/// at seven days, so a bare request for a Ware's history returns a week of it and says nothing
/// about what it left behind.
/// </summary>
/// <param name="EntriesToReturn">
/// Rows per Item, at the endpoint's documented maximum of 99,999. A cap that is hit is a silent
/// truncation: at 3,000 it was measured biting on 2.27% of a liquid population against 0.50%
/// catalogue-wide, so the cap is set where nothing observed comes near it.
/// </param>
/// <param name="Within">How far back to reach.</param>
/// <param name="StatsWithin">Stated for the same reason as on <see cref="ListingWindow"/>.</param>
public sealed record HistoryWindow(int EntriesToReturn, TimeSpan Within, TimeSpan StatsWithin)
{
    /// <summary>The documented maximum for <c>entriesToReturn</c>.</summary>
    public const int MaximumEntries = 99_999;

    /// <summary>The window the bytes-per-Item measurement was taken over.</summary>
    internal static TimeSpan MeasuredOver { get; } = TimeSpan.FromDays(180);

    /// <summary>
    /// The first fetch of a Ware: 180 days, everything in it. Paid once - History rows are
    /// immutable, so every later fetch is a delta.
    /// </summary>
    public static HistoryWindow Backfill { get; } =
        new(MaximumEntries, TimeSpan.FromDays(180), TimeSpan.FromDays(7));

    /// <summary>
    /// The window that reaches back to just before the newest Sale already stored, so a refresh
    /// pulls the delta rather than the series.
    ///
    /// It deliberately overlaps rather than starting exactly at the newest stored row. Duplicate
    /// Sales are free - the store ignores a row it already holds - while a gap is permanent, since
    /// nothing ever revisits the window that was skipped.
    /// </summary>
    /// <param name="newestStored">The newest Sale the store holds for this Ware.</param>
    /// <param name="now">The instant the refresh is being planned at.</param>
    /// <returns>A window covering everything since that Sale, with an hour of overlap.</returns>
    public static HistoryWindow Since(DateTimeOffset newestStored, DateTimeOffset now)
    {
        var overlap = TimeSpan.FromHours(1);
        var elapsed = now - newestStored + overlap;

        if (elapsed >= Backfill.Within)
        {
            return Backfill;
        }

        // Clamped, because a Sale stamped in the future - the Source's clock against this machine's
        // - would otherwise produce a negative window, and the endpoint IGNORES a negative
        // entriesWithin and falls back to its seven-day default. That is the silent truncation this
        // whole type exists to prevent, arriving through the back door.
        return new HistoryWindow(
            MaximumEntries,
            elapsed < overlap ? overlap : elapsed,
            TimeSpan.FromDays(7));
    }

    /// <summary>
    /// What this window is expected to cost per Item, scaled from the measured 180-day figure.
    ///
    /// Linear in the window length, which assumes Sales arrive at a steady rate. They do not -
    /// a Ware has quiet months - so this is an estimate and is labelled as one everywhere it is
    /// shown. What keeps it honest is that the ingest reports the bytes it actually received, so
    /// the estimate is checkable against the outcome rather than merely plausible.
    /// </summary>
    /// <param name="endpoint">The history endpoint, for its measured anchor.</param>
    /// <returns>Estimated bytes per Item.</returns>
    public long EstimatedBytesPerItem(AggregatorEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var share = Within.TotalSeconds / MeasuredOver.TotalSeconds;

        return (long)Math.Ceiling(endpoint.MeasuredBytesPerItem * share);
    }
}

/// <summary>
/// Builds the addresses EMM asks for. Here rather than beside the HTTP client on purpose: which
/// window parameters are sent is a decision this ticket is accountable for, and it belongs
/// somewhere a test with no network can read it back.
/// </summary>
public static class AggregatorAddress
{
    /// <summary>
    /// Where the aggregator lives. The v2 API, and not v3: v3 has no aggregated endpoint, caps its
    /// overview at 20 sales, and reports a Unit Price with the Buyer Fee already added - which
    /// would put a silent 5% into every Undercut EMM computed.
    /// </summary>
    public static Uri Root { get; } = new("https://universalis.app/api/v2/");

    /// <summary>
    /// The address for a listings request.
    /// </summary>
    /// <param name="world">The World whose board to read. By id, never by name - names are localised.</param>
    /// <param name="itemIds">The Items, at most <see cref="AggregatorEndpoint.BatchSize"/> of them.</param>
    /// <param name="window">How much to ask for.</param>
    /// <returns>The address.</returns>
    public static Uri Listings(WorldId world, IReadOnlyList<uint> itemIds, ListingWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return new Uri(
            Root,
            $"{world.Id}/{Join(itemIds)}" +
            $"?listings={window.Listings.ToString(CultureInfo.InvariantCulture)}" +
            $"&entries={window.Entries.ToString(CultureInfo.InvariantCulture)}" +
            $"&statsWithin={((long)window.StatsWithin.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// The address for a history request.
    /// </summary>
    /// <param name="world">The World whose Sales to read.</param>
    /// <param name="itemIds">The Items, at most <see cref="AggregatorEndpoint.BatchSize"/> of them.</param>
    /// <param name="window">How far back to reach, and how many rows to allow.</param>
    /// <returns>The address.</returns>
    public static Uri History(WorldId world, IReadOnlyList<uint> itemIds, HistoryWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return new Uri(
            Root,
            $"history/{world.Id}/{Join(itemIds)}" +
            $"?entriesToReturn={window.EntriesToReturn.ToString(CultureInfo.InvariantCulture)}" +
            $"&entriesWithin={((long)window.Within.TotalSeconds).ToString(CultureInfo.InvariantCulture)}" +
            $"&statsWithin={((long)window.StatsWithin.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)}");
    }

    private static string Join(IReadOnlyList<uint> itemIds)
    {
        Guard.NotEmpty(itemIds, nameof(itemIds), "A request has to name at least one Item.");

        return string.Join(',', itemIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }
}

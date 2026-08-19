namespace EorzeanMarketMaster.Core.Ingest;

/// <summary>
/// Whether a refresh is a point lookup or a sweep. Not a label the caller chooses - see
/// <see cref="FetchPlan.For"/>, which derives it from what the plan actually covers, because the
/// two are treated differently and the difference is worth something to bypass.
/// </summary>
public enum RefreshKind
{
    /// <summary>
    /// One Item's worth of work. Immediate: the fifteen-minute floor was written against sweeps,
    /// and a Player who opens a Ware and wants its board is not walking a population.
    /// </summary>
    Point,

    /// <summary>Anything wider. Obeys the floor, queues, and states its cost first.</summary>
    Sweep,
}

/// <summary>
/// What one endpoint's share of a refresh will cost.
/// </summary>
/// <param name="Endpoint">Which endpoint.</param>
/// <param name="Requests">How many requests, after batching.</param>
/// <param name="EstimatedBytes">
/// What the responses are expected to weigh, from that endpoint's measured bytes per Item. An
/// estimate, and never rendered as anything else - the ingest reports the bytes it really received
/// so that the two can be compared.
/// </param>
public sealed record CostLine(AggregatorEndpoint Endpoint, int Requests, long EstimatedBytes);

/// <summary>
/// The price of a refresh, before it is paid.
///
/// Broken out per endpoint rather than totalled, because the two differ by an order of magnitude
/// per Item and by a factor of five in requests - a Player told only "36 requests" has been told
/// the cheap half of the story.
/// </summary>
/// <param name="Lines">One line per endpoint the refresh touches.</param>
public sealed record RefreshCost(IReadOnlyList<CostLine> Lines)
{
    /// <summary>The per-endpoint breakdown.</summary>
    public IReadOnlyList<CostLine> Lines { get; } = Guard.CopyOf(Lines, nameof(Lines));

    /// <summary>Requests in total.</summary>
    public int Requests => Lines.Sum(line => line.Requests);

    /// <summary>Estimated bytes in total.</summary>
    public long EstimatedBytes => Lines.Sum(line => line.EstimatedBytes);

    /// <summary>
    /// The shortest this refresh can take, which is the pacing alone: requests are separated by
    /// <see cref="Citizenship.MinimumInterval"/> and the first one does not wait. It excludes the
    /// time the responses themselves take, so it is a floor on the wall clock and not a forecast
    /// of it - a countdown that ran out early would be the wrong error to make.
    /// </summary>
    public TimeSpan PacingFloor =>
        Requests <= 1 ? TimeSpan.Zero : (Requests - 1) * Citizenship.MinimumInterval;
}

/// <summary>
/// One request: which endpoint, which Items, and the address it resolves to.
/// </summary>
/// <param name="Endpoint">The endpoint being asked.</param>
/// <param name="World">The World whose Market is being read.</param>
/// <param name="ItemIds">The Items in this request.</param>
/// <param name="Wares">
/// The Wares those Items were asked for on behalf of. Kept alongside the Items rather than derived
/// afterwards, because it is what lets the parser tell "this Ware had nothing listed" from "this
/// Ware was never asked about" - two facts a response cannot distinguish on its own.
/// </param>
/// <param name="Address">Where to send it.</param>
public sealed record FetchBatch(
    AggregatorEndpoint Endpoint,
    WorldId World,
    IReadOnlyList<uint> ItemIds,
    IReadOnlyList<WareId> Wares,
    Uri Address)
{
    /// <summary>The Items in this request.</summary>
    public IReadOnlyList<uint> ItemIds { get; } = Guard.NotEmpty(ItemIds, nameof(ItemIds), "A request covers at least one Item.");

    /// <summary>The Wares behind them.</summary>
    public IReadOnlyList<WareId> Wares { get; } = Guard.NotEmpty(Wares, nameof(Wares), "A request is made on behalf of at least one Ware.");
}

/// <summary>
/// A refresh, planned but not yet run: what will be asked, in how many requests, for how many
/// bytes, and whether it is allowed to start yet.
///
/// The plan exists as a separate thing from the running of it so that the cost can be put in front
/// of the Player before the button is pressed rather than reported after. A cost that arrives with
/// the result is not a choice.
///
/// <b>Batching is by Ware and paid for by Item.</b> An Item's HQ and NQ Wares are two different
/// things to price and one thing to fetch - they arrive in the same response - so a plan over
/// 4,000 Wares does not cost 4,000 Items of traffic. Measured on the catalogue: 5,752 active Wares
/// touch 5,107 distinct Items.
/// </summary>
/// <param name="World">The World being read.</param>
/// <param name="Kind">Point or sweep, derived rather than declared.</param>
/// <param name="Wares">The Wares the refresh covers.</param>
/// <param name="Batches">The requests, in the order they will be sent.</param>
/// <param name="Cost">What it will cost.</param>
public sealed record FetchPlan(
    WorldId World,
    RefreshKind Kind,
    IReadOnlyList<WareId> Wares,
    IReadOnlyList<FetchBatch> Batches,
    RefreshCost Cost)
{
    /// <summary>The Wares covered.</summary>
    public IReadOnlyList<WareId> Wares { get; } = Guard.CopyOf(Wares, nameof(Wares));

    /// <summary>The requests, in order.</summary>
    public IReadOnlyList<FetchBatch> Batches { get; } = Guard.CopyOf(Batches, nameof(Batches));

    /// <summary>
    /// Plans a refresh of a set of Wares on one World: their Listings and their Sales.
    /// </summary>
    /// <param name="world">The World whose Market to read.</param>
    /// <param name="wares">The Wares to refresh. Duplicates and repeated Items collapse.</param>
    /// <param name="listings">How much of the board to ask for.</param>
    /// <param name="history">How far back to reach for Sales.</param>
    /// <returns>The plan, with its cost already computed.</returns>
    public static FetchPlan For(
        WorldId world,
        IReadOnlyList<WareId> wares,
        ListingWindow listings,
        HistoryWindow history)
    {
        Guard.NotEmpty(wares, nameof(wares), "A refresh has to name at least one Ware.");
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(history);

        // Ordered so that a plan is reproducible: the same Wares in a different order produce the
        // same requests, which is what lets a cost be quoted, remembered, and then checked.
        var distinctWares = wares.Distinct().OrderBy(w => w.ItemId).ThenBy(w => w.Quality).ToList();
        var itemIds = distinctWares.Select(w => w.ItemId).Distinct().OrderBy(id => id).ToList();

        var waresByItem = distinctWares.ToLookup(ware => ware.ItemId);
        var batches = new List<FetchBatch>();

        batches.AddRange(Batch(AggregatorEndpoint.Listings, world, itemIds, waresByItem,
            ids => AggregatorAddress.Listings(world, ids, listings)));

        batches.AddRange(Batch(AggregatorEndpoint.History, world, itemIds, waresByItem,
            ids => AggregatorAddress.History(world, ids, history)));

        var cost = new RefreshCost(
        [
            new CostLine(
                AggregatorEndpoint.Listings,
                AggregatorEndpoint.Listings.RequestsFor(itemIds.Count),
                (long)itemIds.Count * AggregatorEndpoint.Listings.MeasuredBytesPerItem),
            new CostLine(
                AggregatorEndpoint.History,
                AggregatorEndpoint.History.RequestsFor(itemIds.Count),
                itemIds.Count * history.EstimatedBytesPerItem(AggregatorEndpoint.History)),
        ]);

        // Derived from the plan, never taken from the caller. "Point" buys an exemption from the
        // sweep floor, and an exemption anything can claim by asking for it is not a floor.
        var kind = itemIds.Count == 1 ? RefreshKind.Point : RefreshKind.Sweep;

        return new FetchPlan(world, kind, distinctWares, batches, cost);
    }

    /// <summary>
    /// Chunks the Items into requests and carries each request's Wares along with it.
    ///
    /// The Ware lookup is built once rather than scanned per chunk. Scanning looked tidier and was
    /// quadratic - a 4,000-Ware sweep is 240 chunks against 4,000 Wares, which is tens of millions
    /// of comparisons for a plan the Scan surface rebuilds while a frame is waiting on it.
    /// </summary>
    private static IEnumerable<FetchBatch> Batch(
        AggregatorEndpoint endpoint,
        WorldId world,
        IReadOnlyList<uint> itemIds,
        ILookup<uint, WareId> waresByItem,
        Func<IReadOnlyList<uint>, Uri> address)
    {
        for (var start = 0; start < itemIds.Count; start += endpoint.BatchSize)
        {
            var chunk = itemIds.Skip(start).Take(endpoint.BatchSize).ToList();
            var covered = chunk.SelectMany(id => waresByItem[id]).ToList();

            yield return new FetchBatch(endpoint, world, chunk, covered, address(chunk));
        }
    }
}

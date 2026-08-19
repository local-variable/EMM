namespace EorzeanMarketMaster.Core;

/// <summary>
/// The accumulated Sales of one Ware in one Market, oldest first.
///
/// EMM builds this. No Source supplies rollups - the aggregator hands back individual Sales and
/// the game client transmits its most recent few per visit - so every average, band and candle in
/// EMM is computed here from individual Sales rather than asked for. That is not a preference: a
/// requested rollup would be somebody else's arithmetic over a population EMM cannot see, and it
/// could not be recomputed when a later refresh fills in Sales that were missing the first time.
///
/// One World, because a Market is one World's board and a Sale on another World is a fact about a
/// Market the Player would have to travel to. A wider <see cref="Scope"/> is analysis over several
/// of these rather than a differently-shaped one of them, and is not built here - the ticket that
/// needs it can say what it means by it.
/// </summary>
/// <param name="Ware">Whose Sales these are.</param>
/// <param name="World">Which Market they happened in.</param>
/// <param name="Sales">The Sales, in any order. Held oldest first.</param>
public sealed record History(WareId Ware, WorldId World, IReadOnlyList<MarketSale> Sales)
{
    /// <summary>
    /// The Sales, oldest first and copied on the way in.
    ///
    /// Ordered here rather than by the caller because every reader of this type walks it in time
    /// order, and a store query, an import and a live fetch arrive in three different orders. A
    /// Sale belonging to another Ware or another Market is refused rather than filtered: a series
    /// quietly holding somebody else's Market is a wrong number that draws perfectly.
    /// </summary>
    public IReadOnlyList<MarketSale> Sales { get; } = Ordered(Sales, Ware, World);

    /// <summary>Units sold across the whole series. Summed, never counted - see <see cref="Rollup"/>.</summary>
    public int Units => Sales.Sum(sale => sale.Stack);

    /// <summary>When the oldest Sale held here happened, or null where there are none.</summary>
    public DateTimeOffset? FirstSaleAt => Sales.Count == 0 ? null : Sales[0].SoldAt;

    /// <summary>When the newest Sale held here happened, or null where there are none.</summary>
    public DateTimeOffset? LastSaleAt => Sales.Count == 0 ? null : Sales[^1].SoldAt;

    /// <summary>A Ware EMM holds no Sales for at all. A real answer, not a missing one.</summary>
    /// <param name="ware">The Ware.</param>
    /// <param name="world">The Market.</param>
    /// <returns>An empty series.</returns>
    public static History Empty(WareId ware, WorldId world) => new(ware, world, []);

    /// <summary>
    /// The Sales folded into slices of the time axis, oldest first.
    ///
    /// <b>A slice with no Sales in it produces no Rollup at all.</b> That is the whole behaviour
    /// worth testing here: a fold that emitted a zero-priced slice for a quiet week would put a
    /// price of nothing into the series, and a fold that emitted an interpolated one would invent
    /// Sales that never happened. A gap in the Sales is a gap in the Rollups, and what draws it as
    /// a gap on the graph is <c>BrokenLine</c>.
    /// </summary>
    /// <param name="width">How wide one slice is.</param>
    /// <param name="from">Inclusive lower bound. Sales before it are not folded.</param>
    /// <param name="toExclusive">Exclusive upper bound.</param>
    /// <returns>One Rollup per occupied slice, oldest first.</returns>
    public IReadOnlyList<Rollup> Rollups(RollupWidth width, DateTimeOffset from, DateTimeOffset toExclusive)
    {
        var folded = new List<Rollup>();

        var slice = default(DateTimeOffset);
        var sales = 0;
        var units = 0;
        var gil = 0L;
        var lowest = 0L;
        var highest = 0L;

        foreach (var sale in Sales)
        {
            if (sale.SoldAt < from || sale.SoldAt >= toExclusive)
            {
                continue;
            }

            var start = width.StartOf(sale.SoldAt);

            if (sales > 0 && start != slice)
            {
                folded.Add(new Rollup(
                    slice, width.Span, sales, units, gil, new UnitPrice(lowest), new UnitPrice(highest)));
                sales = 0;
            }

            if (sales == 0)
            {
                slice = start;
                units = 0;
                gil = 0L;
                lowest = long.MaxValue;
                highest = long.MinValue;
            }

            sales++;
            units += sale.Stack;
            gil += sale.UnitPrice.Gil * sale.Stack;
            lowest = Math.Min(lowest, sale.UnitPrice.Gil);
            highest = Math.Max(highest, sale.UnitPrice.Gil);
        }

        if (sales > 0)
        {
            folded.Add(new Rollup(
                slice, width.Span, sales, units, gil, new UnitPrice(lowest), new UnitPrice(highest)));
        }

        return folded;
    }

    private static IReadOnlyList<MarketSale> Ordered(IReadOnlyList<MarketSale> sales, WareId ware, WorldId world)
    {
        ArgumentNullException.ThrowIfNull(sales);

        foreach (var sale in sales)
        {
            if (sale.Ware != ware || sale.World != world)
            {
                throw new ArgumentException(
                    $"A History of {ware} in World {world.Id} was given a Sale of {sale.Ware} in World " +
                    $"{sale.World.Id}. A series holding another Market's Sales draws perfectly and is wrong.",
                    nameof(sales));
            }
        }

        return [.. sales.OrderBy(sale => sale.SoldAt)];
    }
}

/// <summary>
/// Every Sale that landed inside one slice of the time axis, folded into the figures a graph
/// draws.
///
/// Deliberately not a mean and a count. <see cref="Gil"/> is the sum of Unit Price times Stack
/// rather than an average, so a unit-weighted average survives the fold and can be recomputed
/// after two Rollups are added together. A row-weighted one would not survive it, and would be
/// wrong to begin with: a 1-unit Sale and a 99-unit Sale are not one observation each, and
/// everything downstream measures velocity in units. The same shape is what the store's daily
/// rollup table holds, so the fold and the stored fold cannot drift apart.
/// </summary>
/// <param name="Start">The slice's inclusive lower bound.</param>
/// <param name="Width">How wide the slice is.</param>
/// <param name="SaleCount">
/// How many Sales landed in it. At least one - an empty slice is no Rollup.
///
/// Named for the count rather than for what is counted, unlike the store column it folds into
/// (<c>sales</c>): <c>History.Sales</c> is a list of them one type away, and a member reading as a
/// collection while holding a number is the kind of thing that compiles.
/// </param>
/// <param name="Units">Units those Sales moved.</param>
/// <param name="Gil">Sum of Unit Price times Stack across them.</param>
/// <param name="Lowest">The cheapest Unit Price in the slice.</param>
/// <param name="Highest">The dearest Unit Price in the slice.</param>
public sealed record Rollup(
    DateTimeOffset Start,
    TimeSpan Width,
    int SaleCount,
    int Units,
    long Gil,
    UnitPrice Lowest,
    UnitPrice Highest)
{
    /// <summary>How many Sales landed in the slice. At least one.</summary>
    public int SaleCount { get; } = Guard.Positive(
        SaleCount, nameof(SaleCount), "A Rollup describes at least one Sale - an empty slice is no Rollup.");

    /// <summary>Units those Sales moved. At least one, since every Sale moves at least one.</summary>
    public int Units { get; } =
        Guard.Positive(Units, nameof(Units), "A Rollup covering at least one Sale moves at least one unit.");

    /// <summary>
    /// The cheapest Unit Price in the slice.
    ///
    /// Guarded against being dearer than <see cref="Highest"/> for the same reason the counts are
    /// guarded: the pair are the ends of a range, a candle is drawn from them the moment #33
    /// arrives, and a range drawn inside out is a mark that renders and means nothing.
    /// </summary>
    public UnitPrice Lowest { get; } = Lowest.Gil <= Highest.Gil
        ? Lowest
        : throw new ArgumentOutOfRangeException(
            nameof(Lowest), Lowest.Gil, "A Rollup's cheapest Sale cannot be dearer than its dearest.");

    /// <summary>The slice's exclusive upper bound.</summary>
    public DateTimeOffset End => Start + Width;

    /// <summary>
    /// The middle of the slice, which is where a mark describing the whole of it belongs. Drawing
    /// it at the start would put a figure covering a week at the moment the week began.
    /// </summary>
    public DateTimeOffset Middle => Start + (Width / 2);

    /// <summary>
    /// The unit-weighted mean Unit Price across the slice: gil moved divided by units moved.
    ///
    /// A <c>double</c> and not a <see cref="UnitPrice"/>, because it is a computed average rather
    /// than a price anything was ever bought at, and a type reserved for real prices should not be
    /// handed something nobody paid.
    /// </summary>
    public double MeanUnitPrice => (double)Gil / Units;
}

/// <summary>
/// How wide one slice of the time axis is.
///
/// Slices are anchored to the Unix epoch rather than to whatever moment the reader happened to
/// open the graph. That is what makes a Rollup reproducible: the same Sale falls in the same slice
/// no matter which window it is looked at through, so two readers comparing figures are comparing
/// the same arithmetic. An anchor at "now" would give every refresh a slightly different fold.
/// </summary>
public readonly record struct RollupWidth
{
    /// <summary>Wraps a slice width.</summary>
    /// <param name="span">How wide. Must be positive - a slice of no time holds nothing.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width is zero or negative.</exception>
    public RollupWidth(TimeSpan span)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(span, TimeSpan.Zero);
        Span = span;
    }

    /// <summary>Six hours. The finest slice offered, for a week or a fortnight of History.</summary>
    public static RollupWidth SixHours { get; } = new(TimeSpan.FromHours(6));

    /// <summary>One day.</summary>
    public static RollupWidth Day { get; } = new(TimeSpan.FromDays(1));

    /// <summary>Three days.</summary>
    public static RollupWidth ThreeDays { get; } = new(TimeSpan.FromDays(3));

    /// <summary>Six days. The coarsest slice offered, for half a year or everything held.</summary>
    public static RollupWidth SixDays { get; } = new(TimeSpan.FromDays(6));

    /// <summary>How wide the slice is.</summary>
    public TimeSpan Span { get; }

    /// <summary>
    /// The slice width that suits a window of this length.
    ///
    /// The four widths and the windows they were chosen for are the graph decision's own: six
    /// hours at a week, a day at thirty, three days at ninety, six days at a hundred and eighty.
    /// The fortnight and sixty-day presets were not given a width there and take the nearest
    /// stated one, so no preset is left to a guess made at a call site.
    /// </summary>
    /// <param name="window">How much of History is on screen.</param>
    /// <returns>The slice width.</returns>
    public static RollupWidth For(TimeSpan window) => window switch
    {
        { TotalDays: <= 14 } => SixHours,
        { TotalDays: <= 60 } => Day,
        { TotalDays: <= 90 } => ThreeDays,
        _ => SixDays,
    };

    /// <summary>
    /// The start of the slice an instant falls in.
    /// </summary>
    /// <param name="instant">The moment.</param>
    /// <returns>The slice's inclusive lower bound, in UTC.</returns>
    public DateTimeOffset StartOf(DateTimeOffset instant)
    {
        var since = instant.UtcDateTime.Ticks - DateTimeOffset.UnixEpoch.UtcDateTime.Ticks;

        // Integer floor division, and both halves of that matter. Floor rather than truncate,
        // because truncation rounds toward zero and would put anything before 1970 in the slice
        // AFTER the one it belongs to. Integer rather than Math.Floor over a double, because ticks
        // since the epoch passed 2^53 in 1998 - a double cannot hold this year to the second, so
        // the obvious spelling would land Sales in the wrong slice at the boundaries.
        var slices = since >= 0
            ? since / Span.Ticks
            : ((since - Span.Ticks) + 1) / Span.Ticks;

        return DateTimeOffset.UnixEpoch.AddTicks(slices * Span.Ticks);
    }
}

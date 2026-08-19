namespace EorzeanMarketMaster.Core.Graph;

/// <summary>
/// Which of an Item's two Wares the graph is showing.
///
/// An overlay and never a facet: a Ware is an Item at a Quality, so HQ and NQ are two series with
/// two of everything - but the question a seller actually asks is "what is the HQ premium, and is
/// it moving?", and that needs one axis. Side-by-side panes destroy the comparison; one merged
/// series is the ambiguity the glossary exists to forbid.
/// </summary>
public enum QualityOverlay
{
    /// <summary>The Normal Quality Ware alone.</summary>
    Normal,

    /// <summary>The High Quality Ware alone.</summary>
    High,

    /// <summary>Both, on one axis, in their own fixed colours.</summary>
    Both,
}

/// <summary>
/// One Quality's layer of the graph: its Sales as dots, its Listings as lines, and its Rollups as
/// a line broken wherever nothing was observed.
/// </summary>
/// <param name="Quality">Which Ware of the Item this layer is.</param>
/// <param name="Sales">Every Sale in the window, oldest first.</param>
/// <param name="Listings">Every Listing on the board as last observed, cheapest first.</param>
/// <param name="Rollups">The occupied slices, oldest first.</param>
/// <param name="Line">The Rollups laid across the whole window, holes and all.</param>
/// <param name="ObservedAt">When the board this layer's Listings came from was observed, or null where none was.</param>
/// <param name="DiscardedListings">How many Listings on that board were discarded as noise.</param>
/// <param name="DearestDiscarded">The dearest of those, or null where none was.</param>
public sealed record GraphLayer(
    Quality Quality,
    IReadOnlyList<SaleMark> Sales,
    IReadOnlyList<ListingLine> Listings,
    IReadOnlyList<Rollup> Rollups,
    BrokenLine Line,
    DateTimeOffset? ObservedAt,
    int DiscardedListings = 0,
    double? DearestDiscarded = null)
{
    /// <summary>Whether this layer would draw nothing at all.</summary>
    public bool IsEmpty => Sales.Count == 0 && Listings.Count == 0;

    /// <summary>Units sold across the window. Summed rather than counted, as velocity always is.</summary>
    public int Units => Sales.Sum(sale => sale.Stack);

    /// <summary>The gil this layer's Sales cover, or null where it has none.</summary>
    public DrawnRange? SaleRange => DrawnRange.Over(Sales.Select(sale => sale.UnitPrice));

    /// <summary>The gil this layer's Listings cover, or null where the board held none.</summary>
    public DrawnRange? ListingRange => DrawnRange.Over(Listings.Select(listing => listing.UnitPrice));
}

/// <summary>
/// The rule for a Listing priced so far above the market that it is not an offer to anybody.
///
/// <b>Measured on a real install rather than imagined:</b> a Ware whose dearest Sale in six months
/// was 3,400 gil had a Listing on the board at 999,999,999 - the game's own per-unit cap - put
/// there by somebody parking a stack of one. Two of the four Wares on that install carried one,
/// another at 10,000,000 against a dearest Sale of 150. These are not rare and they are not
/// mistakes; the marketboard has no upper bound worth the name and people use it.
///
/// <b>Such a Listing is discarded, not clipped.</b> Drawn against the axis it flattens every Sale,
/// every Rollup and every Listing anyone might actually trade against onto the bottom pixel.
/// Clipped to the top edge it is worse - it reads as a real ask at whatever the axis happens to
/// end at. So it does not reach the picture at all, and by the same rule it must not reach an
/// Undercut, a Reference Price or a Days of Supply either.
/// </summary>
public static class InsaneListing
{
    /// <summary>
    /// How far above the dearest Sale a Listing may sit and still count as an offer.
    ///
    /// <b>Ten, on the maintainer's ruling</b>, tightened from a hundred after seeing the rule work
    /// on a live board: "I would further cut this to 10x". The agent's own first figure was four
    /// and its second was the maintainer's hundred; ten is the settled one.
    ///
    /// <b>What tightening costs, stated rather than buried:</b> at ten, a Listing asking more than
    /// ten times the dearest Sale of the last window is thrown away, and some of those are real
    /// asks on a Ware whose market has moved. The protection is that the anchor is the
    /// <b>dearest</b> Sale rather than the median - the most permissive anchor available, and
    /// therefore the conservative choice for a rule whose job is to throw data away. On the board
    /// this was found on, ten and a hundred discard exactly the same two Listings.
    /// </summary>
    public const double Multiple = 10;

    /// <summary>
    /// Whether a Listing is priced so far above what the Ware actually sells for that it is noise.
    /// </summary>
    /// <param name="unitPrice">The Listing's Unit Price.</param>
    /// <param name="dearestSale">The dearest Sale observed, or null where there are none.</param>
    /// <returns>Whether to discard it.</returns>
    public static bool Is(double unitPrice, double? dearestSale) =>
        // With no Sales at all there is nothing to be a hundred times, and a board is the only
        // evidence EMM has for that Ware - so nothing is thrown away. A dearest Sale of zero gil
        // is the same case wearing a number.
        dearestSale is { } dearest && dearest > 0 && unitPrice > dearest * Multiple;
}

/// <summary>
/// One Item's market drawn honestly, and nothing more than that.
///
/// <b>What this deliberately does not carry is as load-bearing as what it does.</b> There is no
/// band here, no Estimate and no forecast - not a null one, not an empty one, not a placeholder
/// with a comment promising a later ticket will fill it in. #31 mints the Estimate, #33 the bands,
/// and until they exist the honest picture is the observations alone. A view that carried an empty
/// band would invite a renderer to draw a flat one, and a flat band is a confidence claim.
///
/// This lives in Core rather than beside the drawing code because every rule the graph decision
/// called honesty is arithmetic: which slices are occupied, where the line breaks, how big a dot
/// is, whether a Listing's start is traced or assumed. Arithmetic can be asserted with no client
/// running; a renderer cannot.
/// </summary>
/// <param name="ItemId">The Item both layers are Wares of.</param>
/// <param name="World">The Market being drawn.</param>
/// <param name="Window">Which preset is open.</param>
/// <param name="Width">How wide one slice of the time axis is.</param>
/// <param name="From">The window's inclusive lower bound.</param>
/// <param name="ToExclusive">The window's exclusive upper bound.</param>
/// <param name="Layers">One per Quality on show, Normal first where both are.</param>
public sealed record GraphView(
    uint ItemId,
    WorldId World,
    GraphWindow Window,
    RollupWidth Width,
    DateTimeOffset From,
    DateTimeOffset ToExclusive,
    IReadOnlyList<GraphLayer> Layers)
{
    /// <summary>Whether there is nothing at all to draw, on any layer.</summary>
    public bool IsEmpty => Layers.All(layer => layer.IsEmpty);

    /// <summary>
    /// What the price axis must cover, or null where nothing is drawn.
    ///
    /// Computed rather than left to the plotter to fit, and that is a correctness fix rather than
    /// a cosmetic one: only the Rollup line goes through a plotting primitive, so an automatic fit
    /// sees the slice means and not the Sales they were computed from. A Sale dearer than every
    /// slice mean - which is what an outlier IS - would then sit outside the axis and be clipped,
    /// and a Sale the picture silently does not draw is the one thing this graph may not do.
    ///
    /// Every mark on every layer is inside this, because a Listing that would not fit never
    /// reached a layer - see <see cref="InsaneListing"/>.
    /// </summary>
    public DrawnRange? Axis
    {
        get
        {
            DrawnRange? range = null;

            foreach (var layer in Layers)
            {
                foreach (var here in new[] { layer.SaleRange, layer.ListingRange })
                {
                    if (here is { } found)
                    {
                        range = range is { } seen ? seen.Covering(found) : found;
                    }
                }
            }

            return range;
        }
    }

    /// <summary>
    /// How many Listings were discarded as noise across every layer, and the dearest of them.
    ///
    /// Carried so the surface can say so. Discarding is the maintainer's ruling and the right
    /// call, but a board that holds four Listings and shows three without a word is a picture
    /// telling a small lie about the market - and this is the number that stops it.
    /// </summary>
    public int DiscardedListings => Layers.Sum(layer => layer.DiscardedListings);

    /// <summary>The dearest discarded Listing across every layer, or null where none was.</summary>
    public double? DearestDiscarded =>
        Layers.Select(layer => layer.DearestDiscarded).Where(price => price is not null).Max();

    /// <summary>The window's lower bound, in seconds since the Unix epoch.</summary>
    public double FromSeconds => From.ToUnixTimeSeconds();

    /// <summary>The window's upper bound, in seconds since the Unix epoch.</summary>
    public double ToSeconds => ToExclusive.ToUnixTimeSeconds();

    /// <summary>
    /// Builds the view from what the store held.
    /// </summary>
    /// <param name="itemId">The Item. Both its Wares are candidates for a layer.</param>
    /// <param name="world">The Market.</param>
    /// <param name="window">Which preset is open.</param>
    /// <param name="overlay">Which Wares to draw.</param>
    /// <param name="histories">
    /// The Sales, one series per Ware. A Ware with no series is drawn as an empty layer rather
    /// than omitted, so that "EMM holds nothing for the HQ Ware" and "you did not ask for the HQ
    /// Ware" stay different states on screen.
    /// </param>
    /// <param name="boards">
    /// The board as last observed, one Snapshot per Ware, or none. Only the newest is drawn: a
    /// Listing line is a statement about the board as it stands, and stacking every Snapshot EMM
    /// ever took would draw the same Listing once per observation.
    /// </param>
    /// <param name="width">The slice width, or null to take the one the window implies.</param>
    /// <param name="now">The moment the window runs up to. Injected, never read from a clock.</param>
    /// <returns>The view.</returns>
    public static GraphView Build(
        uint itemId,
        WorldId world,
        GraphWindow window,
        QualityOverlay overlay,
        IReadOnlyList<History> histories,
        IReadOnlyList<Snapshot> boards,
        RollupWidth? width,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(histories);
        ArgumentNullException.ThrowIfNull(boards);

        var qualities = overlay switch
        {
            QualityOverlay.Normal => new[] { Quality.Normal },
            QualityOverlay.High => [Quality.High],
            _ => [Quality.Normal, Quality.High],
        };

        var earliest = histories
            .Select(history => history.FirstSaleAt)
            .Where(at => at is not null)
            .Min();

        var from = window.From(now, earliest);
        var slice = width ?? RollupWidth.For(now - from);

        // The dearest Sale of EITHER Ware, and it is deliberately not per-Quality. The two are
        // drawn on one axis and an insane Listing wrecks that axis for both, so the Item is the
        // right unit to judge one against - and an HQ Ware with no Sales of its own still has its
        // Item's market to be measured by.
        var dearest = histories
            .Where(history => history.Ware.ItemId == itemId && history.World == world)
            .SelectMany(history => history.Sales)
            .Where(sale => sale.SoldAt >= from && sale.SoldAt < now)
            .Select(sale => (double?)sale.UnitPrice.Gil)
            .Max();

        var layers = qualities
            .Select(quality =>
                Layer(new WareId(itemId, quality), world, histories, boards, slice, from, now, dearest))
            .ToList();

        return new GraphView(itemId, world, window, slice, from, now, layers);
    }

    private static GraphLayer Layer(
        WareId ware,
        WorldId world,
        IReadOnlyList<History> histories,
        IReadOnlyList<Snapshot> boards,
        RollupWidth width,
        DateTimeOffset from,
        DateTimeOffset toExclusive,
        double? dearestSale)
    {
        var history = histories.FirstOrDefault(h => h.Ware == ware && h.World == world)
                      ?? History.Empty(ware, world);

        var sales = history.Sales
            .Where(sale => sale.SoldAt >= from && sale.SoldAt < toExclusive)
            .Select(sale => new SaleMark(sale.SoldAt.ToUnixTimeSeconds(), sale.UnitPrice.Gil, sale.Stack))
            .ToList();

        var rollups = history.Rollups(width, from, toExclusive);

        var board = boards
            .Where(snapshot => snapshot.Ware == ware && snapshot.World == world)
            .OrderByDescending(snapshot => snapshot.ObservedAt)
            .FirstOrDefault();

        var listings = board is null ? [] : Lines(board, from, toExclusive);

        // Discarded here rather than filtered at the renderer, so that nothing downstream of this
        // view can read a price that was ruled to be noise. The count survives; the price does not.
        var kept = listings.Where(line => !InsaneListing.Is(line.UnitPrice, dearestSale)).ToList();
        var discarded = listings.Where(line => InsaneListing.Is(line.UnitPrice, dearestSale)).ToList();

        return new GraphLayer(
            ware.Quality,
            sales,
            kept,
            rollups,
            BrokenLine.Over(rollups, width, from, toExclusive),
            board?.ObservedAt,
            discarded.Count,
            discarded.Count == 0 ? null : discarded.Max(line => line.UnitPrice));
    }

    /// <summary>
    /// The board's Listings as lines back to when each was put up.
    ///
    /// Clipped to the window rather than dropped: a Listing put up four months ago and still
    /// sitting there is one of the more telling things on the picture, and omitting it because its
    /// start falls off the left edge would hide exactly the case worth seeing.
    /// </summary>
    private static IReadOnlyList<ListingLine> Lines(
        Snapshot board,
        DateTimeOffset from,
        DateTimeOffset toExclusive)
    {
        var observed = board.ObservedAt;

        if (observed < from)
        {
            return [];
        }

        var to = observed > toExclusive ? toExclusive : observed;

        return
        [
            .. board.Listings.Select(listing =>
            {
                var traced = listing.LastReviewedAt is not null;
                var start = listing.LastReviewedAt ?? observed;

                if (start > observed)
                {
                    // A review time later than the observation is the Source contradicting itself.
                    // Believing it would draw a line running backwards, so the weaker claim - that
                    // it was on the board when EMM looked - is what gets drawn, and it is labelled
                    // as untraced rather than quietly corrected.
                    start = observed;
                    traced = false;
                }

                return new ListingLine(
                    Math.Max(start.ToUnixTimeSeconds(), from.ToUnixTimeSeconds()),
                    to.ToUnixTimeSeconds(),
                    listing.UnitPrice.Gil,
                    listing.Stack,
                    traced);
            }),
        ];
    }
}

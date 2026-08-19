using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Graph;
using Xunit;

namespace EorzeanMarketMaster.Tests.Graph;

/// <summary>
/// What the graph shows, and what it refuses to show.
/// </summary>
public class GraphViewTests
{
    private const uint ItemId = 5057;

    private static readonly WorldId World = new(79);
    private static readonly WareId Nq = new(ItemId, Quality.Normal);
    private static readonly WareId Hq = new(ItemId, Quality.High);
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BothQualitiesAreLayersOnOneViewRatherThanTwoPictures()
    {
        // An overlay and never a facet. The seller's question is "what is the HQ premium and is it
        // moving?", and that needs one axis; two panes destroy the comparison and one merged
        // series is the Ware/Item ambiguity the glossary exists to forbid.
        var view = Build(QualityOverlay.Both,
            [Sales(Nq, 100), Sales(Hq, 400)],
            []);

        Assert.Equal(2, view.Layers.Count);
        Assert.Equal(Quality.Normal, view.Layers[0].Quality);
        Assert.Equal(Quality.High, view.Layers[1].Quality);
        Assert.Equal(100, view.Layers[0].Sales[0].UnitPrice);
        Assert.Equal(400, view.Layers[1].Sales[0].UnitPrice);
    }

    [Fact]
    public void OneQualityAloneDrawsOneLayer()
    {
        var view = Build(QualityOverlay.High, [Sales(Nq, 100), Sales(Hq, 400)], []);

        var only = Assert.Single(view.Layers);

        Assert.Equal(Quality.High, only.Quality);
    }

    [Fact]
    public void AQualityWithNoSalesIsStillALayerSoThatItsAbsenceIsVisible()
    {
        // "EMM holds nothing for the HQ Ware" and "you did not ask for the HQ Ware" are different
        // states and must not render identically. Dropping the empty layer would collapse them.
        var view = Build(QualityOverlay.Both, [Sales(Nq, 100)], []);

        Assert.Equal(2, view.Layers.Count);
        Assert.True(view.Layers[1].IsEmpty);
        Assert.False(view.Layers[0].IsEmpty);
    }

    [Fact]
    public void ASaleIsADotAndAListingIsALineAndNeitherIsEverTheOther()
    {
        // A Sale is what a buyer paid; a Listing is what a seller hopes for, and most never become
        // the former. On one axis as one kind of mark, every median a reader takes off the picture
        // is wrong in the direction that costs gil.
        var board = new Snapshot(Nq, World, Now.AddHours(-1), Source.Aggregator, Now.AddHours(-2),
        [
            new Listing(new UnitPrice(900), 1, "Coriander", Now.AddDays(-3)),
            new Listing(new UnitPrice(950), 4, "Bloodstained", Now.AddDays(-1)),
        ]);

        var view = Build(QualityOverlay.Normal, [Sales(Nq, 100)], [board]);
        var layer = Assert.Single(view.Layers);

        Assert.Single(layer.Sales);
        Assert.Equal(2, layer.Listings.Count);
        Assert.DoesNotContain(layer.Listings, line => layer.Sales.Any(sale => sale.UnitPrice == line.UnitPrice));
    }

    [Fact]
    public void AListingRunsFromWhenItWasPutUpToWhenTheBoardWasObserved()
    {
        var reviewed = Now.AddDays(-3);
        var observed = Now.AddHours(-1);
        var board = new Snapshot(Nq, World, observed, Source.Aggregator, null,
            [new Listing(new UnitPrice(900), 1, "Coriander", reviewed)]);

        var line = Assert.Single(Assert.Single(Build(QualityOverlay.Normal, [], [board]).Layers).Listings);

        Assert.Equal(reviewed.ToUnixTimeSeconds(), line.From);
        Assert.Equal(observed.ToUnixTimeSeconds(), line.To);
        Assert.True(line.Traced);
    }

    [Fact]
    public void AListingWithNoReviewTimeStartsAtTheObservationAndSaysThatIsWhatItIs()
    {
        // The weaker claim - "it was on the board when EMM looked" - drawn as the weaker claim.
        // Back-projection from a review time is a lower bound at the best of times; inventing one
        // where the Source reported none would be a bound over nothing.
        var observed = Now.AddHours(-1);
        var board = new Snapshot(Nq, World, observed, Source.Aggregator, null,
            [new Listing(new UnitPrice(900), 1, null, null)]);

        var line = Assert.Single(Assert.Single(Build(QualityOverlay.Normal, [], [board]).Layers).Listings);

        Assert.Equal(observed.ToUnixTimeSeconds(), line.From);
        Assert.Equal(observed.ToUnixTimeSeconds(), line.To);
        Assert.False(line.Traced);
    }

    [Fact]
    public void AReviewTimeLaterThanTheObservationIsNotBelievedAndIsNotDrawnBackwards()
    {
        // The Source contradicting itself. Believed, this draws a line running right to left,
        // which is not a wrong number so much as a picture of an impossible one.
        var observed = Now.AddHours(-1);
        var board = new Snapshot(Nq, World, observed, Source.Aggregator, null,
            [new Listing(new UnitPrice(900), 1, null, observed.AddHours(2))]);

        var line = Assert.Single(Assert.Single(Build(QualityOverlay.Normal, [], [board]).Layers).Listings);

        Assert.True(line.From <= line.To);
        Assert.False(line.Traced);
    }

    [Fact]
    public void OnlyTheNewestBoardIsDrawnSoOneListingIsNotDrawnOncePerObservation()
    {
        var older = new Snapshot(Nq, World, Now.AddDays(-2), Source.Aggregator, null,
            [new Listing(new UnitPrice(800), 1, null, null)]);
        var newest = new Snapshot(Nq, World, Now.AddHours(-1), Source.Aggregator, null,
            [new Listing(new UnitPrice(900), 1, null, null)]);

        var layer = Assert.Single(Build(QualityOverlay.Normal, [], [older, newest]).Layers);

        Assert.Equal(900, Assert.Single(layer.Listings).UnitPrice);
        Assert.Equal(newest.ObservedAt, layer.ObservedAt);
    }

    [Fact]
    public void ADotGrowsWithTheSquareRootOfItsStackSoItsAreaTracksTheUnits()
    {
        // Radius proportional to Stack draws a 99-unit Sale with ten thousand times the ink of a
        // single unit, which reads as a hundred times the trade it was.
        var one = new SaleMark(0, 100, 1);
        var four = new SaleMark(0, 100, 4);
        var nine = new SaleMark(0, 100, 9);

        Assert.Equal(2f * one.Radius, four.Radius, 3);
        Assert.Equal(3f * one.Radius, nine.Radius, 3);
    }

    [Fact]
    public void ADotIsClampedAtBothEndsSoNoSaleIsInvisibleOrSwallowsItsNeighbours()
    {
        Assert.Equal(SaleMark.SmallestRadius, new SaleMark(0, 100, 1).Radius, 3);
        Assert.Equal(SaleMark.LargestRadius, new SaleMark(0, 100, 999).Radius, 3);
    }

    [Fact]
    public void TheAxisCoversEveryMarkAndNotJustTheFoldedLine()
    {
        // FOUND IN GAME. Only the Rollup line goes through a plotting primitive, so an automatic
        // fit sees the slice means and not the Sales they were computed from - and a Sale dearer
        // than every slice mean is exactly what an outlier is. Fitted, it sits above the axis and
        // is clipped, which is the one thing this graph may not do to a Sale.
        var history = new History(Nq, World,
        [
            new MarketSale(Nq, World, Now.AddDays(-2), new UnitPrice(100), 1, Source.Aggregator),
            new MarketSale(Nq, World, Now.AddDays(-2), new UnitPrice(100), 1, Source.Aggregator),
            new MarketSale(Nq, World, Now.AddDays(-2).AddHours(1), new UnitPrice(9_000), 1, Source.Aggregator),
        ]);

        var view = Build(QualityOverlay.Normal, [history], []);
        var layer = Assert.Single(view.Layers);

        // The slice mean is nowhere near the outlier, which is what makes the fit wrong.
        Assert.True(Assert.Single(layer.Rollups).MeanUnitPrice < 9_000);

        var drawn = Assert.NotNull(view.Axis);

        Assert.Equal(100, drawn.Lowest);
        Assert.Equal(9_000, drawn.Highest);
    }

    [Fact]
    public void AListingSittingWellAboveTheSalesIsInsideTheAxisRatherThanCroppedOffIt()
    {
        // The case the graph decision named as reading instantly - a Listing sitting well above
        // the Sale cloud. An axis that cropped it would remove the reason for drawing Listings at
        // all, so the discard rule has to be loose enough to keep an optimistic ask.
        var board = new Snapshot(Nq, World, Now.AddHours(-1), Source.Aggregator, null,
            [new Listing(new UnitPrice(500), 1, null, Now.AddDays(-40))]);

        var view = Build(QualityOverlay.Normal, [Sales(Nq, 100)], [board]);
        var drawn = Assert.NotNull(view.Axis);

        Assert.Equal(500, drawn.Highest);
        Assert.Equal(0, view.DiscardedListings);
    }

    [Fact]
    public void AListingPricedAtNothingAnybodyWouldPayIsThrownAwayRatherThanDrawn()
    {
        // FOUND IN GAME, ON A REAL BOARD. A Ware whose dearest Sale in six months was 3,400 gil
        // had a Listing at 999,999,999 - the game's own per-unit cap - and two of the four Wares
        // on that install carried one. Drawn against the axis it flattens every Sale, every Rollup
        // and every Listing anyone might actually trade against onto the bottom pixel.
        //
        // Discarded rather than clipped to the top edge, on the maintainer's ruling: a clipped one
        // reads as a real ask at whatever price the axis happens to end at, which is worse than
        // not drawing it.
        var board = new Snapshot(Nq, World, Now.AddHours(-1), Source.Aggregator, null,
        [
            new Listing(new UnitPrice(120), 1, null, null),
            new Listing(new UnitPrice(999_999_999), 1, null, null),
        ]);

        var view = Build(QualityOverlay.Normal, [Sales(Nq, 100)], [board]);
        var layer = Assert.Single(view.Layers);

        Assert.Equal(120, Assert.Single(layer.Listings).UnitPrice);
        Assert.Equal(1, view.DiscardedListings);
        Assert.Equal(999_999_999, view.DearestDiscarded);

        // And the axis is left describing the market rather than the noise.
        Assert.Equal(120, Assert.NotNull(view.Axis).Highest);
    }

    [Fact]
    public void TheDiscardThresholdIsTenTimesTheDearestSaleAndIsAssertedFromBothSides()
    {
        // An off-by-one on a rule that throws data away is the kind that is never noticed, so the
        // boundary is pinned from both sides rather than tested only where it bites.
        Assert.False(InsaneListing.Is(1_000, 100));
        Assert.True(InsaneListing.Is(1_001, 100));
    }

    [Fact]
    public void WithNoSalesAtAllNoListingIsThrownAway()
    {
        // There is nothing to be a hundred times, and the board is then the only evidence EMM has
        // for that Ware. Throwing it away would leave the picture blank while the store held a
        // perfectly good observation.
        Assert.False(InsaneListing.Is(999_999_999, null));
        Assert.False(InsaneListing.Is(999_999_999, 0));

        var board = new Snapshot(Nq, World, Now.AddHours(-1), Source.Aggregator, null,
            [new Listing(new UnitPrice(999_999_999), 1, null, null)]);

        var view = Build(QualityOverlay.Normal, [], [board]);

        Assert.Equal(0, view.DiscardedListings);
        Assert.Single(Assert.Single(view.Layers).Listings);
    }

    [Fact]
    public void AnInsaneListingIsJudgedAgainstTheItemsSalesRatherThanItsOwnQualitys()
    {
        // The two Qualities share one axis, so a Listing that wrecks it wrecks it for both. An HQ
        // Ware EMM holds no Sales for still has its Item's market to be measured against - judged
        // per-Quality it would escape the rule entirely and flatten the picture for the NQ Ware
        // beside it.
        var board = new Snapshot(Hq, World, Now.AddHours(-1), Source.Aggregator, null,
            [new Listing(new UnitPrice(999_999_999), 1, null, null)]);

        var view = Build(QualityOverlay.Both, [Sales(Nq, 100)], [board]);

        Assert.Equal(1, view.DiscardedListings);
    }

    [Fact]
    public void APaddedRangeLeavesRoomAtBothEndsAndNeverGoesBelowZero()
    {
        var padded = new DrawnRange(100, 200).Padded(0.1);

        Assert.Equal(90, padded.Lowest, 3);
        Assert.Equal(210, padded.Highest, 3);

        // Gil does not go negative, and an axis starting at -40 says it might.
        Assert.Equal(0, new DrawnRange(10, 20).Padded(1.0).Lowest, 3);

        // A Ware that traded at one price all month has a span of zero, and a padding taken from
        // the span would give it no room at all.
        var flat = new DrawnRange(300, 300).Padded(0.1);

        Assert.True(flat.Highest > flat.Lowest);
    }

    [Fact]
    public void AnEmptyPictureAsksTheAxisForNothing()
    {
        Assert.Null(GraphView.Build(ItemId, World, GraphWindow.Default, QualityOverlay.Both, [], [], null, Now).Axis);
    }

    [Fact]
    public void AMarkOfNoUnitsIsRefusedOnBothKindsOfMark()
    {
        // The same rule the Sale and the Listing themselves carry, applied to the marks drawn from
        // them - guarded on both rather than on whichever one was written first, which is the
        // uneven-guard trap an earlier ticket's review called its most valuable finding. A dot of
        // no units would also divide a radius by nothing.
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaleMark(0, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListingLine(0, 1, 100, 0, true));
    }

    [Fact]
    public void TheWindowPresetsRunFromAWeekToAllOfHistory()
    {
        Assert.Equal([7, 14, 30, 60, 90, 180], GraphWindow.Presets.Where(w => w.Days is not null).Select(w => w.Days!.Value));
        Assert.Equal(GraphWindow.All, GraphWindow.Presets[^1]);
        Assert.Null(GraphWindow.All.Days);
    }

    [Fact]
    public void TheDefaultWindowIsThirtyDaysAndIsOneOfThePresetsOffered()
    {
        // Stated rather than implied, and asserted to be reachable from the control: a default
        // nobody can get back to after moving off it is a trap rather than a default.
        Assert.Equal(30, GraphWindow.Default.Days);
        Assert.Contains(GraphWindow.Default, GraphWindow.Presets);
    }

    [Fact]
    public void AllOfHistoryIsBoundedByTheOldestSaleHeldRatherThanByAFixedSpan()
    {
        var history = new History(Nq, World,
        [
            new MarketSale(Nq, World, Now.AddDays(-400), new UnitPrice(100), 1, Source.Aggregator),
            new MarketSale(Nq, World, Now.AddDays(-1), new UnitPrice(120), 1, Source.Aggregator),
        ]);

        var view = GraphView.Build(ItemId, World, GraphWindow.All, QualityOverlay.Normal, [history], [], null, Now);

        Assert.Equal(Now.AddDays(-400), view.From);
        Assert.Equal(2, Assert.Single(view.Layers).Sales.Count);
    }

    [Fact]
    public void AllOfHistoryWithNothingStoredFallsBackToTheDefaultRatherThanToTheEpoch()
    {
        // A window running from 1970 is not "everything held", it is an empty axis four hundred
        // times too wide with a handful of pixels of data at the right edge.
        var view = GraphView.Build(ItemId, World, GraphWindow.All, QualityOverlay.Normal, [], [], null, Now);

        Assert.Equal(Now.AddDays(-30), view.From);
    }

    [Fact]
    public void TheSliceWidthFollowsTheWindowUnlessOneIsForced()
    {
        var auto = GraphView.Build(ItemId, World, GraphWindow.OfDays(7), QualityOverlay.Normal, [], [], null, Now);
        var forced = GraphView.Build(
            ItemId, World, GraphWindow.OfDays(7), QualityOverlay.Normal, [], [], RollupWidth.Day, Now);

        Assert.Equal(RollupWidth.SixHours, auto.Width);
        Assert.Equal(RollupWidth.Day, forced.Width);
    }

    [Fact]
    public void NothingOnTheGraphClaimsABandAnEstimateOrAForecast()
    {
        // The ticket's last acceptance criterion, asserted rather than promised. #31 mints the
        // Estimate and #33 the bands; until then the honest picture is the observations alone, and
        // a view carrying an empty band would invite a renderer to draw a flat one - which is a
        // confidence claim, made out of nothing.
        //
        // THE NEGATIVE CONTROL IS THE POINT. Core really does have these names, on types this
        // ticket does not touch, so the scan below is shown finding them before it is trusted to
        // report their absence anywhere else.
        var forbidden = new[] { "band", "estimate", "forecast", "predict", "confidence", "interval" };

        var graph = typeof(GraphView).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace == typeof(GraphView).Namespace)
            .SelectMany(type => type.GetMembers().Select(member => $"{type.Name}.{member.Name}"))
            .Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var control = typeof(Outcome)
            .GetMembers()
            .Select(member => member.Name)
            .Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.NotEmpty(control);
        Assert.Empty(graph);
    }

    private static GraphView Build(
        QualityOverlay overlay,
        IReadOnlyList<History> histories,
        IReadOnlyList<Snapshot> boards) =>
        GraphView.Build(ItemId, World, GraphWindow.OfDays(30), overlay, histories, boards, null, Now);

    private static History Sales(WareId ware, long gil) =>
        new(ware, World, [new MarketSale(ware, World, Now.AddDays(-2), new UnitPrice(gil), 1, Source.Aggregator)]);
}

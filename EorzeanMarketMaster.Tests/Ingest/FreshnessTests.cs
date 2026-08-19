using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Ingest;
using EorzeanMarketMaster.Core.Store;
using EorzeanMarketMaster.Tests.Store;
using Xunit;

namespace EorzeanMarketMaster.Tests.Ingest;

/// <summary>
/// Freshness, calibrated per World.
/// </summary>
public class FreshnessTests
{
    [Fact]
    public void TheSameAgeGradesDifferentlyOnABusyWorldAndAQuietOne()
    {
        // THE WHOLE POINT OF THE TYPE, and the reason a global threshold was rejected: for actively
        // traded Items, a busy World's data was measured at 1.8 hours old at the median against 130
        // hours on a quiet one - two orders of magnitude. A day-old figure is ordinary on one and
        // badly stale on the other, and no single number says both.
        var busy = WorldFreshness.From(new WorldId(40), Hours([1, 1, 2, 2, 2, 3, 3, 4, 5, 6]));
        var quiet = WorldFreshness.From(new WorldId(79), Hours([40, 80, 110, 120, 130, 140, 160, 200, 300, 800]));

        var aDayOld = TimeSpan.FromHours(24);

        Assert.Equal(FreshnessGrade.Stale, busy.Grade(aDayOld));
        Assert.Equal(FreshnessGrade.Fresh, quiet.Grade(aDayOld));
    }

    [Fact]
    public void TheBandsRunFreshThenAgingThenStaleAcrossTheWorldsOwnDistribution()
    {
        var world = WorldFreshness.From(new WorldId(79), Hours([1, 2, 3, 4, 5, 6, 7, 8, 9, 100]));

        Assert.Equal(TimeSpan.FromHours(5), world.Median);
        Assert.Equal(TimeSpan.FromHours(9), world.Ninetieth);

        Assert.Equal(FreshnessGrade.Fresh, world.Grade(TimeSpan.FromHours(5)));
        Assert.Equal(FreshnessGrade.Aging, world.Grade(TimeSpan.FromHours(6)));
        Assert.Equal(FreshnessGrade.Aging, world.Grade(TimeSpan.FromHours(9)));
        Assert.Equal(FreshnessGrade.Stale, world.Grade(TimeSpan.FromHours(10)));
    }

    [Fact]
    public void BelowTheMinimumSampleNoGradeIsClaimedAtAll()
    {
        // Falling back to a global default here would reintroduce exactly what this avoids, and it
        // would do it on the Worlds EMM knows least about. The age is still shown; the judgement is
        // withheld.
        var thin = WorldFreshness.From(new WorldId(79), Hours([1, 2, 3]));

        Assert.False(thin.IsCalibrated);
        Assert.Equal(FreshnessGrade.Uncalibrated, thin.Grade(TimeSpan.FromDays(400)));
        Assert.Equal(FreshnessGrade.Uncalibrated, thin.Grade(TimeSpan.Zero));
    }

    [Fact]
    public void OneMoreObservationIsWhatTurnsAnUncalibratedWorldIntoAGradedOne()
    {
        // Asserted from both sides of the threshold, because an off-by-one here is the difference
        // between withholding a judgement and inventing one.
        var below = WorldFreshness.From(new WorldId(79), Hours([1, 2, 3, 4, 5, 6, 7]));
        var at = WorldFreshness.From(new WorldId(79), Hours([1, 2, 3, 4, 5, 6, 7, 8]));

        Assert.Equal(WorldFreshness.MinimumSample - 1, below.Sample);
        Assert.False(below.IsCalibrated);
        Assert.True(at.IsCalibrated);
    }

    [Fact]
    public void EveryReportedPercentileIsAnAgeThatWasReallyObserved()
    {
        // Nearest-rank rather than interpolated, so a World's stated median is a figure that
        // happened rather than one between two that did.
        var world = WorldFreshness.From(new WorldId(79), Hours([1, 1, 1, 1, 1, 1, 1, 1, 1, 999]));

        Assert.Equal(TimeSpan.FromHours(1), world.Median);
        Assert.Equal(TimeSpan.FromHours(1), world.Ninetieth);
    }

    [Fact]
    public void AnUploadStampedAfterEmmReadItIsDroppedRatherThanCountedAsPerfectlyFresh()
    {
        // Clocks disagree. A negative age is a disagreement, not a figure, and letting it in would
        // drag a World's median toward zero and make everything else read as stale.
        var world = WorldFreshness.From(
            new WorldId(79),
            [.. Hours([1, 2, 3, 4, 5, 6, 7, 8]), TimeSpan.FromHours(-9)]);

        Assert.Equal(8, world.Sample);
    }

    [Fact]
    public void AWorldWithNothingObservedIsUncalibratedRatherThanInstantaneouslyFresh()
    {
        Assert.Equal(FreshnessGrade.Uncalibrated, WorldFreshness.From(new WorldId(79), []).Grade(TimeSpan.Zero));
    }

    [Fact]
    public void TheCalibrationIsBuiltFromTheStoresOwnObservationsOfThatWorld()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        // Ten observations on one World, each read at a known lag behind its upload; one on
        // another World, to prove the calibration does not pool them.
        for (var i = 1; i <= 10; i++)
        {
            var observedAt = IngestFixture.Instant.AddMinutes(-i);

            store.Write(new Snapshot(
                new WareId((uint)(2000 + i), Quality.Normal),
                IngestFixture.World,
                observedAt,
                Source.Aggregator,
                observedAt.AddHours(-i),
                [new Listing(new UnitPrice(100), 1, "Alderleaf", null)]));
        }

        store.Write(new Snapshot(
            new WareId(3000, Quality.Normal),
            new WorldId(40),
            IngestFixture.Instant,
            Source.Aggregator,
            IngestFixture.Instant.AddDays(-30),
            []));

        var calibration = StoredMarket.Calibrate(store, IngestFixture.World, IngestFixture.Instant.AddMinutes(1));

        Assert.Equal(10, calibration.Sample);
        Assert.Equal(TimeSpan.FromHours(5), calibration.Median);
        Assert.Equal(TimeSpan.FromHours(9), calibration.Ninetieth);
    }

    [Fact]
    public void ABoardWithManyListingsIsOneObservationRatherThanOnePerListing()
    {
        // A Snapshot is stored one row per Listing. Counted naively, the busiest Wares would set
        // the World's whole idea of what fresh means - forty rows from one board against one from
        // another is not a sample of forty boards.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.Write(new Snapshot(
            StoreFixture.Ware,
            IngestFixture.World,
            IngestFixture.Instant,
            Source.Aggregator,
            IngestFixture.Instant.AddHours(-2),
            [.. Enumerable.Range(1, 40).Select(i => new Listing(new UnitPrice(i * 10), i, null, null))]));

        var ages = store.ReadUploadAges(
            IngestFixture.World, IngestFixture.Instant.AddDays(-1), IngestFixture.Instant.AddDays(1));

        Assert.Equal([TimeSpan.FromHours(2)], ages);
    }

    [Fact]
    public void AnObservationWhoseSourceReportedNoUploadTimeContributesNothingRatherThanAgeZero()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.Write(new Snapshot(
            StoreFixture.Ware, IngestFixture.World, IngestFixture.Instant, Source.OpenedBoard, null, []));

        Assert.Empty(store.ReadUploadAges(
            IngestFixture.World, IngestFixture.Instant.AddDays(-1), IngestFixture.Instant.AddDays(1)));
    }

    private static IReadOnlyList<TimeSpan> Hours(IEnumerable<int> hours) =>
        [.. hours.Select(h => TimeSpan.FromHours(h))];
}

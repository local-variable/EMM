using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Ingest;
using Xunit;

namespace EorzeanMarketMaster.Tests.Ingest;

/// <summary>
/// Batching, the cost that is quoted from it, and the window parameters that go on every request.
/// </summary>
public class FetchPlanTests
{
    [Fact]
    public void AThousandWaresBecomeTenListingsRequestsRatherThanAThousand()
    {
        var plan = Plan(Wares(1_000));

        var listings = plan.Batches.Where(b => b.Endpoint == AggregatorEndpoint.Listings).ToList();

        Assert.Equal(10, listings.Count);
        Assert.All(listings, batch => Assert.True(batch.ItemIds.Count <= AggregatorEndpoint.Listings.BatchSize));
        Assert.Equal(1_000, listings.Sum(batch => batch.ItemIds.Count));
    }

    [Fact]
    public void HistoryBatchesAtTwentySoTheSamePopulationCostsFiveTimesTheRequests()
    {
        var plan = Plan(Wares(1_000));

        Assert.Equal(50, plan.Batches.Count(b => b.Endpoint == AggregatorEndpoint.History));
    }

    [Fact]
    public void BothWaresOfOneItemArePaidForOnce()
    {
        // The request bill is paid on Items; the prices are facts about Wares. Measured on the
        // catalogue, 5,752 active Wares touch only 5,107 distinct Items, so conflating the two
        // over-states the cost of every sweep EMM ever quotes.
        var plan = Plan([new WareId(1602, Quality.Normal), new WareId(1602, Quality.High)]);

        var batch = Assert.Single(plan.Batches, b => b.Endpoint == AggregatorEndpoint.Listings);

        Assert.Equal([1602u], batch.ItemIds);
        Assert.Equal(2, batch.Wares.Count);
        Assert.Equal(2, plan.Cost.Requests);
    }

    [Fact]
    public void ABatchIsFullAtItsSizeAndOneMoreItemCostsAnotherRequest()
    {
        Assert.Equal(1, AggregatorEndpoint.Listings.RequestsFor(100));
        Assert.Equal(2, AggregatorEndpoint.Listings.RequestsFor(101));
        Assert.Equal(1, AggregatorEndpoint.History.RequestsFor(20));
        Assert.Equal(2, AggregatorEndpoint.History.RequestsFor(21));
        Assert.Equal(0, AggregatorEndpoint.Listings.RequestsFor(0));
    }

    [Fact]
    public void TheCostIsBrokenOutPerEndpointBecauseTheTwoDifferByAnOrderOfMagnitude()
    {
        var plan = Plan(Wares(100));

        var listings = plan.Cost.Lines.Single(line => line.Endpoint == AggregatorEndpoint.Listings);
        var history = plan.Cost.Lines.Single(line => line.Endpoint == AggregatorEndpoint.History);

        // The measured figures: 2,599 bytes per Item for listings against 13,447 for a 180-day
        // history, and one request against five. A total would hide both.
        Assert.Equal(1, listings.Requests);
        Assert.Equal(5, history.Requests);
        Assert.True(history.EstimatedBytes > listings.EstimatedBytes * 4,
            $"history {history.EstimatedBytes} bytes against listings {listings.EstimatedBytes}");
    }

    [Fact]
    public void ThePacingFloorCountsTheGapsBetweenRequestsAndNotTheRequests()
    {
        // Six requests are five gaps. Counting six would make every countdown a second long.
        var plan = Plan(Wares(100));

        Assert.Equal(6, plan.Cost.Requests);
        Assert.Equal(5 * Citizenship.MinimumInterval, plan.Cost.PacingFloor);

        // One Ware is still two requests - its board and its Sales - so one gap.
        var single = Plan(Wares(1));

        Assert.Equal(2, single.Cost.Requests);
        Assert.Equal(Citizenship.MinimumInterval, single.Cost.PacingFloor);
    }

    [Fact]
    public void TheHistoryWindowIsSetExplicitlyBecauseItsDefaultsSilentlyTruncate()
    {
        // Left unset, entriesToReturn caps at 1,800 rows and entriesWithin at seven days - so a
        // bare request for "the history" returns a week of it and says nothing about the rest.
        // This is the trap the research note called the single most expensive default here.
        var address = Plan(Wares(1)).Batches
            .Single(b => b.Endpoint == AggregatorEndpoint.History)
            .Address;

        Assert.Contains("entriesToReturn=99999", address.Query, StringComparison.Ordinal);
        Assert.Contains("entriesWithin=15552000", address.Query, StringComparison.Ordinal);
        Assert.Contains("statsWithin=", address.Query, StringComparison.Ordinal);

        // And explicitly not the defaults, which is the whole point.
        Assert.DoesNotContain("entriesToReturn=1800", address.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("entriesWithin=604800&", address.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingWindowIsSetExplicitlyToo()
    {
        // The rule is applied to every window parameter rather than only to the one that bites,
        // because a rule with exceptions is one nobody applies to the next endpoint either.
        var address = Plan(Wares(1)).Batches
            .Single(b => b.Endpoint == AggregatorEndpoint.Listings)
            .Address;

        Assert.Contains("listings=100", address.Query, StringComparison.Ordinal);
        Assert.Contains("entries=0", address.Query, StringComparison.Ordinal);
        Assert.Contains("statsWithin=604800000", address.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorldIsAddressedByIdRatherThanByName()
    {
        // Names are localised display text and change; the id is what the store keys on.
        Assert.StartsWith(
            "/api/v2/79/",
            Plan(Wares(1)).Batches.First(b => b.Endpoint == AggregatorEndpoint.Listings).Address.AbsolutePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncrementalRefreshCostsAFractionOfABackfill()
    {
        // What makes a refresh affordable at all. The backfill is paid once per Ware; every
        // refresh after it reaches back only as far as the newest Sale already stored.
        var incremental = HistoryWindow.Since(
            IngestFixture.Instant.AddHours(-2), IngestFixture.Instant);

        var backfillBytes = HistoryWindow.Backfill.EstimatedBytesPerItem(AggregatorEndpoint.History);
        var incrementalBytes = incremental.EstimatedBytesPerItem(AggregatorEndpoint.History);

        Assert.Equal(TimeSpan.FromHours(3), incremental.Within);
        Assert.True(incrementalBytes * 100 < backfillBytes,
            $"incremental {incrementalBytes} bytes/Item against backfill {backfillBytes}");
    }

    [Fact]
    public void AnIncrementalWindowThatWouldReachPastTheBackfillIsJustTheBackfill()
    {
        // A Ware last seen a year ago does not get a year-long window: nothing beyond the backfill
        // depth is being kept anyway, and asking for it is bytes spent on rows that get evicted.
        var window = HistoryWindow.Since(IngestFixture.Instant.AddYears(-1), IngestFixture.Instant);

        Assert.Equal(HistoryWindow.Backfill.Within, window.Within);
    }

    [Fact]
    public void TheWindowOverlapsTheNewestStoredSaleRatherThanStartingAtIt()
    {
        // A duplicate Sale is free - the store ignores a row it already holds - while a gap is
        // permanent, because nothing ever revisits the window that was skipped.
        var window = HistoryWindow.Since(IngestFixture.Instant, IngestFixture.Instant);

        Assert.Equal(TimeSpan.FromHours(1), window.Within);
    }

    [Fact]
    public void ASaleStampedInTheFutureDoesNotProduceAWindowTheEndpointWouldIgnore()
    {
        // Clocks disagree, and the endpoint IGNORES a negative entriesWithin - falling back to its
        // seven-day default, which is the silent truncation the explicit window exists to prevent
        // arriving through the back door. The window is clamped instead.
        var skewed = HistoryWindow.Since(
            IngestFixture.Instant.AddDays(3), IngestFixture.Instant);

        Assert.True(skewed.Within > TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromHours(1), skewed.Within);
    }

    [Fact]
    public void ThePlanIsTheSameWhateverOrderTheWaresArriveIn()
    {
        // So that a cost can be quoted, remembered, and then checked against what ran.
        var forward = Plan([new WareId(1602, Quality.High), new WareId(1601, Quality.Normal)]);
        var reversed = Plan([new WareId(1601, Quality.Normal), new WareId(1602, Quality.High)]);

        Assert.Equal(
            forward.Batches.Select(b => b.Address),
            reversed.Batches.Select(b => b.Address));
    }

    [Fact]
    public void ARefreshOfNothingIsRefusedRatherThanPlannedAsZeroRequests()
    {
        Assert.Throws<ArgumentException>(() => Plan([]));
    }

    private static FetchPlan Plan(IReadOnlyList<WareId> wares) =>
        FetchPlan.For(IngestFixture.World, wares, ListingWindow.Standard, HistoryWindow.Backfill);

    private static IReadOnlyList<WareId> Wares(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new WareId((uint)(2000 + i), Quality.Normal))];
}

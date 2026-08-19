using System.Text;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Ingest;
using EorzeanMarketMaster.Core.Store;
using EorzeanMarketMaster.Tests.Store;
using Xunit;

namespace EorzeanMarketMaster.Tests.Ingest;

/// <summary>
/// The ticket end to end: one Ware fetched from the aggregator, stored, and read back with its
/// Freshness - then the same thing again with nothing on the other end of the wire.
/// </summary>
public class AggregatorIngestTests
{
    private static readonly WareId FireShard = new(5, Quality.Normal);

    [Fact]
    public async Task OneWaresBoardAndSalesAreFetchedStoredAndReadBackWithTheirSourceAndAge()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var report = await Refresh(store, HistoryWindow.Backfill);

        Assert.Equal(RefreshGateState.Immediate, report.Verdict.State);
        Assert.Equal(2, report.RequestsMade);
        Assert.Equal(0, report.RequestsFailed);
        Assert.Equal(1, report.SnapshotsWritten);
        Assert.Equal(4, report.SalesWritten);

        var reading = Read(store, IngestFixture.Instant);

        Assert.True(reading.HasObservation);
        Assert.Equal(Source.Aggregator, reading.Source);
        Assert.Equal([43L, 44L, 45L], reading.Snapshot!.Listings.Select(l => l.UnitPrice.Gil));

        // Age from the Source's own last upload, which is what Freshness means - not from when
        // EMM happened to ask.
        //
        // To the second, and the missing 257 ms is a property of the store rather than a rounding
        // slip here: every instant it holds is Unix seconds, so the aggregator's millisecond
        // upload time truncates on the way in. That is deliberate and it is fine at this job -
        // upload ages are measured in hours, and a Freshness that claimed milliseconds would be
        // claiming a precision the Source does not have either.
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1_787_123_662),
            reading.Snapshot.UploadedAt);
        Assert.Equal(
            IngestFixture.Instant - DateTimeOffset.FromUnixTimeSeconds(1_787_123_662),
            reading.Age);
    }

    [Fact]
    public async Task AFreshInstallShowsAnAgeAndWithholdsAGradeUntilItHasSeenEnoughOfTheWorld()
    {
        // One refresh is one observation. Grading it would mean grading against a threshold EMM
        // invented, which is the thing the per-World calibration exists to prevent.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        await Refresh(store, HistoryWindow.Backfill);

        var reading = Read(store, IngestFixture.Instant);

        Assert.NotNull(reading.Age);
        Assert.Equal(FreshnessGrade.Uncalibrated, reading.Grade);
        Assert.False(reading.Calibration.IsCalibrated);
    }

    [Fact]
    public async Task WithNoNetworkEmmStillServesWhatItHoldsWithItsFreshnessAttached()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        await Refresh(store, HistoryWindow.Backfill);

        // The wire goes away. A day later the Player opens EMM and asks for a refresh.
        var later = IngestFixture.Instant.AddDays(1);
        var offline = RecordingTransport.Offline();

        var report = await AggregatorIngest.Run(
            Plan(HistoryWindow.Backfill), new SweepGate(), offline, new RecordingPacing(), store,
            later, TestContext.Current.CancellationToken);

        Assert.True(report.Ran);
        Assert.Equal(0, report.RequestsMade);
        Assert.Equal(2, report.RequestsFailed);
        Assert.Equal(0, report.SnapshotsWritten);
        Assert.All(report.Batches, batch => Assert.NotNull(batch.Failure));

        // And the figure is still there, a day older, and saying so.
        var reading = Read(store, later);

        Assert.True(reading.HasObservation);
        Assert.Equal([43L, 44L, 45L], reading.Snapshot!.Listings.Select(l => l.UnitPrice.Gil));
        Assert.True(reading.Age > TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task AFailedRequestDoesNotAbandonTheRestOfTheRefresh()
    {
        // One endpoint failing says nothing about the other. A partial refresh that reports which
        // half is missing is worth more than none at all.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var transport = new RecordingTransport(address =>
            address.AbsolutePath.Contains("/history/", StringComparison.Ordinal)
                ? TransportResult.Failed("503 from the aggregator")
                : Ok(IngestFixture.OneWareListings()));

        var report = await AggregatorIngest.Run(
            Plan(HistoryWindow.Backfill), new SweepGate(), transport, new RecordingPacing(),
            store, IngestFixture.Instant, TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.Asked.Count);
        Assert.Equal(1, report.RequestsMade);
        Assert.Equal(1, report.RequestsFailed);
        Assert.Equal(1, report.SnapshotsWritten);
        Assert.Equal(0, report.SalesWritten);
    }

    [Fact]
    public async Task AResponseThatCannotBeReadIsAFailedRequestRatherThanACrash()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var report = await AggregatorIngest.Run(
            Plan(HistoryWindow.Backfill), new SweepGate(),
            RecordingTransport.Returning("<html>we are having a moment</html>"),
            new RecordingPacing(), store, IngestFixture.Instant, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.RequestsFailed);
        Assert.Equal(0, report.SnapshotsWritten);

        // The body arrived, so the bytes were spent, and the report says so rather than pretending
        // the refresh was free.
        Assert.True(report.BytesReceived > 0);
    }

    [Fact]
    public async Task TheQuotedCostIsCheckableAgainstWhatActuallyArrived()
    {
        // An estimate nobody ever compares to an outcome is a number with no way of being wrong.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var plan = Plan(HistoryWindow.Backfill);
        var report = await AggregatorIngest.Run(
            plan, new SweepGate(), Transport(), new RecordingPacing(), store, IngestFixture.Instant,
            TestContext.Current.CancellationToken);

        var served = Encoding.UTF8.GetByteCount(IngestFixture.OneWareListings())
                   + Encoding.UTF8.GetByteCount(IngestFixture.OneWareHistory());

        Assert.Equal(served, report.BytesReceived);
        Assert.True(plan.Cost.EstimatedBytes > 0);
        Assert.Equal(plan.Cost.Requests, report.Batches.Count);
    }

    [Fact]
    public async Task TheSecondRefreshOfAWareAsksOnlyForSalesNewerThanTheOnesAlreadyHeld()
    {
        // The difference between a refresh and a re-download. The backfill is paid once.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        await Refresh(store, HistoryWindow.Backfill);

        var newest = store.NewestSaleAt(FireShard, IngestFixture.World);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_787_100_057), newest);

        var incremental = HistoryWindow.Since(newest!.Value, IngestFixture.Instant.AddHours(6));
        var address = FetchPlan
            .For(IngestFixture.World, [FireShard], ListingWindow.Standard, incremental)
            .Batches.Single(b => b.Endpoint == AggregatorEndpoint.History)
            .Address;

        Assert.True(incremental.Within < HistoryWindow.Backfill.Within);
        Assert.Contains("entriesWithin=", address.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("entriesWithin=15552000", address.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefetchingAnUnchangedBoardStoresItOnceAndAddsNoSales()
    {
        // The ordinary case, not an error case: an incremental refresh re-delivers rows already
        // held, and History only means anything if that is idempotent.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        await Refresh(store, HistoryWindow.Backfill);
        var second = await Refresh(store, HistoryWindow.Backfill);

        Assert.Equal(1, second.SnapshotsWritten);
        Assert.Equal(0, second.SalesWritten);
        Assert.Single(store.ReadSnapshots(
            FireShard, IngestFixture.World,
            IngestFixture.Instant.AddDays(-1), IngestFixture.Instant.AddDays(1)));
    }

    [Fact]
    public void TheNewestObservationIsTheOneServedAndTheOlderOnesStayWhereTheyAre()
    {
        // The surface asks for this once a second, so it asks for one Snapshot rather than a
        // month of them - but it still has to be the newest one, and the older ones have to
        // survive the asking.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        foreach (var hours in new[] { -50, -3, -20 })
        {
            store.Write(new Snapshot(
                FireShard, IngestFixture.World, IngestFixture.Instant.AddHours(hours),
                Source.Aggregator, IngestFixture.Instant.AddHours(hours - 1),
                [new Listing(new UnitPrice(100 - hours), 1, "Alderleaf", null)]));
        }

        var newest = store.LatestSnapshot(FireShard, IngestFixture.World, IngestFixture.Instant.AddDays(-30));

        Assert.NotNull(newest);
        Assert.Equal(IngestFixture.Instant.AddHours(-3), newest.ObservedAt);
        Assert.Equal(3, store.ReadSnapshots(
            FireShard, IngestFixture.World,
            IngestFixture.Instant.AddDays(-30), IngestFixture.Instant).Count);
    }

    [Fact]
    public void AWareWithNothingInTheWindowReadsAsNothingRatherThanAsAnOlderObservation()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.Write(new Snapshot(
            FireShard, IngestFixture.World, IngestFixture.Instant.AddDays(-40),
            Source.Aggregator, null, []));

        Assert.Null(store.LatestSnapshot(FireShard, IngestFixture.World, IngestFixture.Instant.AddDays(-30)));
    }

    private static TransportResult Ok(string body) =>
        TransportResult.Ok(body, Encoding.UTF8.GetByteCount(body));

    private static RecordingTransport Transport() =>
        RecordingTransport.Returning(IngestFixture.OneWareListings(), IngestFixture.OneWareHistory());

    private static FetchPlan Plan(HistoryWindow history) =>
        FetchPlan.For(IngestFixture.World, [FireShard], ListingWindow.Standard, history);

    private static Task<IngestReport> Refresh(MarketStore store, HistoryWindow history) =>
        AggregatorIngest.Run(
            Plan(history), new SweepGate(), Transport(), new RecordingPacing(), store,
            IngestFixture.Instant, TestContext.Current.CancellationToken);

    private static MarketReading Read(MarketStore store, DateTimeOffset now) =>
        StoredMarket.Latest(
            store,
            FireShard,
            IngestFixture.World,
            StoredMarket.Calibrate(store, IngestFixture.World, now),
            now,
            TimeSpan.FromDays(30));
}

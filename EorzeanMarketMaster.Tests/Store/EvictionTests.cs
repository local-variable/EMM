using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EorzeanMarketMaster.Tests.Store;

/// <summary>
/// The eviction ladder: what it removes, what it folds first, and what it refuses to touch.
///
/// The design this asserts runs opposite to a plain "drop the oldest" rule, and deliberately. The
/// replaceable data is the bulk and can be fetched again; the irreplaceable data is small and
/// cannot be fetched at all. Global oldest-first would delete exactly the second kind while
/// sparing the first.
/// </summary>
public class EvictionTests
{
    private static readonly EvictionPolicy Generous = EvictionPolicy.Default with
    {
        ByteCap = 1L * 1024 * 1024 * 1024,
    };

    [Fact]
    public void RawSnapshotsPastTheWindowFoldIntoDailyAggregatesAndTheirPartitionsAreDropped()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var now = StoreFixture.Instant;
        var old = now.AddDays(-60);
        var recent = now.AddDays(-1);

        store.Write(SnapshotAt(old, 1_000, 4));
        store.Write(SnapshotAt(old.AddHours(6), 900, 2));
        store.Write(SnapshotAt(recent, 1_500, 7));

        var report = store.Evict(Generous, now);

        // The old week is gone as a table; the recent one is untouched.
        Assert.Equal([StoreWeek.Of(old)], report.PartitionsDropped);
        Assert.Equal([StoreWeek.Of(recent)], store.Partitions());
        Assert.Equal(2, report.SnapshotRowsFolded);

        // Read straight out of the rollup. It has no reader of its own yet - the series builder
        // that will consume it is a later ticket - and the alternative to asserting the SQL here
        // is dropping two partitions on the strength of a fold nobody has ever looked at.
        var daily = DailySnapshots(temp.Path, store);
        var folded = Assert.Single(daily);

        Assert.Equal(2, folded.Observations);
        Assert.Equal(900, folded.MinUnitPrice);
        Assert.Equal(1_000, folded.MaxUnitPrice);

        // From the LAST observation of the day, not an average across it: Days of Supply is a
        // statement about how much is on the board, and a mean board depth describes no moment
        // that ever existed.
        Assert.Equal(2, folded.UnitsLast);
        Assert.Equal(1, folded.ListingsLast);
    }

    [Fact]
    public void AWeekStraddlingTheWindowBoundaryKeepsEveryRowRatherThanLosingItsOlderHalf()
    {
        // The partition is the unit that can be dropped, so the window rounds to a week edge. The
        // alternative - deleting the rows inside a live partition that happen to be too old - is
        // the multi-second write lock the partitioning exists to avoid.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var now = StoreFixture.Instant;
        var week = StoreWeek.Of(now.AddDays(-30));

        // One observation on the first day of the straddling week, well past the 30-day window,
        // and one on its last day, inside the window.
        store.Write(SnapshotAt(week.Start.AddHours(1), 1_000, 1));
        store.Write(SnapshotAt(week.EndExclusive.AddHours(-1), 1_100, 1));

        var report = store.Evict(EvictionPolicy.Default with { ByteCap = Generous.ByteCap }, now);

        Assert.Empty(report.PartitionsDropped);
        Assert.Equal(2, store.ReadSnapshots(
            StoreFixture.Ware, StoreFixture.World, week.Start, week.EndExclusive).Count);
    }

    [Fact]
    public void RawSalesPastTheHorizonFoldIntoDailyAggregatesAndAreRemoved()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var now = StoreFixture.Instant;
        var old = now.AddDays(-90);

        store.Write([
            SaleAt(old, unitPrice: 100, stack: 1),
            SaleAt(old.AddHours(2), unitPrice: 200, stack: 99),
            SaleAt(now.AddDays(-1), unitPrice: 500, stack: 3),
        ]);

        var report = store.Evict(Generous, now);

        Assert.Equal(2, report.SaleRowsFolded);
        Assert.Equal(1, RemainingSales(temp.Path, store));

        var daily = Assert.Single(DailySales(temp.Path, store));

        Assert.Equal(2, daily.Sales);

        // Units, and gil summed across units rather than rows. A 1-unit Sale and a 99-unit Sale
        // are not one observation each, and the band is fitted on units - so a fold that averaged
        // prices per row would silently reweight every estimate downstream of it.
        Assert.Equal(100, daily.Units);
        Assert.Equal((100 * 1) + (200 * 99), daily.Gil);
        Assert.Equal(100, daily.MinUnitPrice);
        Assert.Equal(200, daily.MaxUnitPrice);
    }

    [Fact]
    public void TheNeverEvictFloorKeepsEveryRowEvenWhenTheCapCannotBeMet()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var now = StoreFixture.Instant;

        store.Write([
            SaleAt(now.AddDays(-90), unitPrice: 100, stack: 1),
            SaleAt(now.AddDays(-91), unitPrice: 150, stack: 2),
        ]);

        // First pass at a generous cap folds the raw Sales into the rollup, which is itself
        // never-evict: once the raw rows are gone it is the only surviving record of them.
        store.Evict(Generous, now);

        var beforeCounts = store.ProtectedRowCounts();

        Assert.True(beforeCounts["market_sale_daily"] > 0);

        // Now a cap nothing could satisfy. Every rung has already run and what remains is the
        // floor, so there is nothing left the ladder is allowed to take.
        var report = store.Evict(Generous with { ByteCap = 1 }, now);

        Assert.Equal(EvictionOutcome.CapExceeded, report.Outcome);
        Assert.True(report.BytesAfterReclaim > report.ByteCap);

        // The assertion the acceptance criterion is actually about: EMM reported the breach and
        // did not delete its way out of it.
        Assert.Equal(beforeCounts, report.ProtectedRowCounts);
        Assert.Equal(beforeCounts, store.ProtectedRowCounts());
    }

    [Fact]
    public void TheProtectedTablesAreTheOnesTheDesignNames()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        // Own Sales because there is no sale event to replay; Lots because a Cost Basis cannot be
        // recovered after acquisition; the Proposal Ledger because it is the audit trail the
        // guardrails require; Levy readings because a past rate is never republished; calibration
        // because a re-measured threshold is judged against its own history; and both rollups
        // because they are what is left once the raw rows they came from have gone.
        Assert.Equal(
            ["calibration", "levy_reading", "lot", "market_sale_daily", "own_sale", "proposal", "snapshot_daily"],
            store.ProtectedRowCounts().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AStoreComfortablyInsideItsCapSaysSoRatherThanClaimingCredit()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var report = store.Evict(Generous, StoreFixture.Instant);

        Assert.Equal(EvictionOutcome.AlreadyWithinCap, report.Outcome);
        Assert.Empty(report.PartitionsDropped);
        Assert.Equal(0, report.SnapshotRowsFolded);
        Assert.Equal(0, report.SaleRowsFolded);
    }

    [Fact]
    public void ASnapshotArrivingLateForAWeekAlreadyFoldedAddsToTheAggregateRatherThanReplacingIt()
    {
        // The case that stops the conflict clause in the fold being dead code. A day never spans
        // two ISO weeks, so ordinarily each partition contributes days no other one has - but a
        // late observation recreates a dropped partition, and the next eviction folds that day a
        // second time.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var now = StoreFixture.Instant;
        var old = now.AddDays(-60);

        store.Write(SnapshotAt(old, 1_000, 4));
        store.Evict(Generous, now);

        store.Write(SnapshotAt(old.AddHours(3), 700, 9));
        store.Evict(Generous, now);

        var folded = Assert.Single(DailySnapshots(temp.Path, store));

        Assert.Equal(2, folded.Observations);
        Assert.Equal(700, folded.MinUnitPrice);
        Assert.Equal(1_000, folded.MaxUnitPrice);
        Assert.Equal(9, folded.UnitsLast);
    }

    private static Snapshot SnapshotAt(DateTimeOffset at, long unitPrice, int stack) =>
        new(StoreFixture.Ware, StoreFixture.World, at, Source.Aggregator, null,
            [new Listing(new UnitPrice(unitPrice), stack, "Coriander", null)]);

    private static MarketSale SaleAt(DateTimeOffset at, long unitPrice, int stack) =>
        new(StoreFixture.Ware, StoreFixture.World, at, new UnitPrice(unitPrice), stack, Source.Aggregator);

    private static IReadOnlyList<DailySnapshotRow> DailySnapshots(string path, MarketStore store)
    {
        var rows = new List<DailySnapshotRow>();

        foreach (var row in Query(
                     path,
                     store,
                     "SELECT observations, min_unit_price, max_unit_price, listings_last, units_last FROM snapshot_daily"))
        {
            rows.Add(new DailySnapshotRow(
                (int)(long)row[0], row[1] as long?, row[2] as long?, (int)(long)row[3], (int)(long)row[4]));
        }

        return rows;
    }

    private static IReadOnlyList<DailySaleRow> DailySales(string path, MarketStore store)
    {
        var rows = new List<DailySaleRow>();

        foreach (var row in Query(
                     path,
                     store,
                     "SELECT sales, units, gil, min_unit_price, max_unit_price FROM market_sale_daily"))
        {
            rows.Add(new DailySaleRow(
                (int)(long)row[0], (long)row[1], (long)row[2], (long)row[3], (long)row[4]));
        }

        return rows;
    }

    private static int RemainingSales(string path, MarketStore store) =>
        (int)(long)Query(path, store, "SELECT COUNT(*) FROM market_sale")[0][0];

    private static IReadOnlyList<object[]> Query(string path, MarketStore store, string sql) =>
        StoreFixture.Read(store, path, sql);

    private sealed record DailySnapshotRow(
        int Observations, long? MinUnitPrice, long? MaxUnitPrice, int ListingsLast, int UnitsLast);

    private sealed record DailySaleRow(int Sales, long Units, long Gil, long MinUnitPrice, long MaxUnitPrice);
}

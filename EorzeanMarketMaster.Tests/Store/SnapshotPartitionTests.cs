using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EorzeanMarketMaster.Tests.Store;

/// <summary>
/// Raw Snapshots: the round trip, the weekly partitioning, and the view that keeps readers from
/// having to know which weeks exist.
/// </summary>
public class SnapshotPartitionTests
{
    [Fact]
    public void OneWareRoundTripsAndItsPartitionIsThenDropped()
    {
        // The ticket's headline case, end to end and headless: written, read back, partition gone.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var observed = StoreFixture.Instant;
        var written = new Snapshot(
            StoreFixture.Ware,
            StoreFixture.World,
            observed,
            Source.OpenedBoard,
            observed.AddMinutes(-12),
            [
                new Listing(new UnitPrice(1_250), 3, "Coriander", observed.AddHours(-5)),
                new Listing(new UnitPrice(1_310), 1, "Marjoram", null),
            ]);

        store.Write(written);

        var readBack = Assert.Single(store.ReadSnapshots(
            StoreFixture.Ware, StoreFixture.World, observed.AddDays(-1), observed.AddDays(1)));

        Assert.Equal(written.Ware, readBack.Ware);
        Assert.Equal(written.World, readBack.World);
        Assert.Equal(written.ObservedAt, readBack.ObservedAt);
        Assert.Equal(written.Source, readBack.Source);
        Assert.Equal(written.UploadedAt, readBack.UploadedAt);
        Assert.Equal(written.Listings, readBack.Listings);

        var week = StoreWeek.Of(observed);

        Assert.Equal([week], store.Partitions());
        Assert.True(store.DropPartition(week));
        Assert.Empty(store.Partitions());
        Assert.Empty(store.ReadSnapshots(
            StoreFixture.Ware, StoreFixture.World, observed.AddDays(-1), observed.AddDays(1)));
    }

    [Fact]
    public void APartitionIsRemovedByDroppingTheTableRatherThanByDeletingItsRows()
    {
        using var temp = StoreFixture.NewStorePath();
        var week = StoreWeek.Of(StoreFixture.Instant);

        using (var store = MarketStore.OpenOrCreate(temp.Path))
        {
            store.Write(OneSnapshot(StoreFixture.Instant));

            Assert.Contains(week.TableName, TableNames(store, temp.Path));

            store.DropPartition(week);

            // The measurement, not a description of one. A DELETE leaves the table behind holding
            // zero rows and takes 8-17 s doing it at scale; a DROP takes 0.03 s and the table is
            // not there afterwards. Only the second of those can be told apart from the outside,
            // and it is the one this asserts.
            Assert.DoesNotContain(week.TableName, TableNames(store, temp.Path));
        }
    }

    [Fact]
    public void ObservationsInDifferentWeeksLandInDifferentPartitionsAndDroppingOneLeavesTheOther()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var thisWeek = StoreFixture.Instant;
        var lastWeek = StoreFixture.Instant.AddDays(-7);

        store.Write(OneSnapshot(lastWeek));
        store.Write(OneSnapshot(thisWeek));

        Assert.Equal([StoreWeek.Of(lastWeek), StoreWeek.Of(thisWeek)], store.Partitions());

        store.DropPartition(StoreWeek.Of(lastWeek));

        var remaining = store.ReadSnapshots(
            StoreFixture.Ware, StoreFixture.World, lastWeek.AddDays(-1), thisWeek.AddDays(1));

        Assert.Equal([StoreWeek.Of(thisWeek)], store.Partitions());
        Assert.Equal(thisWeek, Assert.Single(remaining).ObservedAt);
    }

    [Fact]
    public void ReadersGoThroughOneViewWhateverPartitionsExist()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.Write(OneSnapshot(StoreFixture.Instant.AddDays(-7)));
        store.Write(OneSnapshot(StoreFixture.Instant));

        // Queried by name rather than through the store, because the point of the view is that
        // something other than MarketStore can read raw Snapshots without knowing about weeks.
        using var connection = new SqliteConnection($"Data Source={temp.Path};Mode=ReadOnly;Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM snapshot";

        Assert.Equal(2L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void AFreshStoreAnswersAQueryForSnapshotsWithNothingRatherThanWithAMissingTable()
    {
        // A store on a first run has no partitions at all. The view still has to exist, or every
        // reader in EMM has to special-case the day it was installed.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        Assert.Empty(store.Partitions());
        Assert.Empty(store.ReadSnapshots(
            StoreFixture.Ware, StoreFixture.World, StoreFixture.Instant.AddYears(-1), StoreFixture.Instant));
    }

    [Fact]
    public void AnObservationThatFoundNothingListedIsStoredAsSuchRatherThanAsNoObservation()
    {
        // "Nobody is selling this here" is a fact about the Market and a different one from "EMM
        // never looked" - and it is the ordinary case for 42% of the catalogue.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.Write(new Snapshot(
            StoreFixture.Ware, StoreFixture.World, StoreFixture.Instant, Source.Aggregator, null, []));

        var readBack = Assert.Single(store.ReadSnapshots(
            StoreFixture.Ware, StoreFixture.World,
            StoreFixture.Instant.AddDays(-1), StoreFixture.Instant.AddDays(1)));

        Assert.Equal(StoreFixture.Instant, readBack.ObservedAt);
        Assert.Empty(readBack.Listings);
    }

    [Fact]
    public void ListingsComeBackCheapestFirstWhateverOrderTheSourceGaveThem()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.Write(new Snapshot(
            StoreFixture.Ware, StoreFixture.World, StoreFixture.Instant, Source.Aggregator, null,
            [
                new Listing(new UnitPrice(900), 1, null, null),
                new Listing(new UnitPrice(100), 5, null, null),
                new Listing(new UnitPrice(400), 2, null, null),
            ]));

        var readBack = Assert.Single(store.ReadSnapshots(
            StoreFixture.Ware, StoreFixture.World,
            StoreFixture.Instant.AddDays(-1), StoreFixture.Instant.AddDays(1)));

        // Ascending, because the lowest is what every Undercut and Reference Price reads and
        // making the caller sort it is one more place to get it wrong.
        Assert.Equal([100L, 400L, 900L], readBack.Listings.Select(l => l.UnitPrice.Gil));
    }

    [Fact]
    public void WritingTheSameObservationTwiceStoresItOnce()
    {
        // Refetching an unchanged board is the normal case, not an error case.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var snapshot = OneSnapshot(StoreFixture.Instant);

        store.Write(snapshot);
        store.Write(snapshot);

        var readBack = Assert.Single(store.ReadSnapshots(
            StoreFixture.Ware, StoreFixture.World,
            StoreFixture.Instant.AddDays(-1), StoreFixture.Instant.AddDays(1)));

        Assert.Single(readBack.Listings);
    }

    [Fact]
    public void DroppingAPartitionThatWasNeverThereReportsSoRatherThanThrowing()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        Assert.False(store.DropPartition(StoreWeek.Of(StoreFixture.Instant)));
    }

    [Fact]
    public void PartitionsSurviveTheStoreBeingClosedAndOpenedAgain()
    {
        using var temp = StoreFixture.NewStorePath();
        var week = StoreWeek.Of(StoreFixture.Instant);

        using (var first = MarketStore.OpenOrCreate(temp.Path))
        {
            first.Write(OneSnapshot(StoreFixture.Instant));
        }

        using var second = MarketStore.OpenOrCreate(temp.Path);

        // The partition set is rebuilt from the database rather than remembered in memory, which
        // is what makes it survive a restart - the whole reason there is a store at all.
        Assert.Equal([week], second.Partitions());
        Assert.True(second.DropPartition(week));
    }

    private static Snapshot OneSnapshot(DateTimeOffset at) =>
        new(StoreFixture.Ware, StoreFixture.World, at, Source.Aggregator, null,
            [new Listing(new UnitPrice(1_000), 3, "Coriander", null)]);

    /// <summary>Checkpoints the store first, so a read-only connection sees committed state.</summary>
    private static IReadOnlyList<string> TableNames(MarketStore store, string path)
    {
        _ = store.SizeInBytes;

        return StoreFixture.TableNames(path);
    }
}

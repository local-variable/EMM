using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EorzeanMarketMaster.Tests.Store;

/// <summary>
/// What has to be true at the moment a store is created, and what happens when it was not.
///
/// This is the one part of the schema with a one-way door in it. <c>auto_vacuum</c> can only be
/// set before the first table exists; afterwards the pragma runs, reports nothing, and reads back
/// 0. So "we can turn it on later" is not an option that exists, and a store that missed it can
/// never reclaim space.
/// </summary>
public class StoreCreationTests
{
    [Fact]
    public void ANewStoreIsCreatedWithIncrementalVacuumAndWriteAheadLogging()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        Assert.Equal(2, Pragma(temp.Path, "auto_vacuum"));
        Assert.Equal("wal", PragmaText(temp.Path, "journal_mode"));
    }

    [Fact]
    public void AStoreCreatedWithoutIncrementalVacuumIsRefusedAndLeftExactlyAsItWasFound()
    {
        using var temp = StoreFixture.NewStorePath();

        CreateStoreWithoutIncrementalVacuum(temp.Path);

        var thrown = Assert.Throws<StoreUnusableException>(() => MarketStore.OpenOrCreate(temp.Path));

        Assert.Equal(temp.Path, thrown.Path);
        Assert.Contains("auto_vacuum", thrown.Message, StringComparison.Ordinal);

        // The half of this that matters. Refusing is easy; refusing without having quietly tried
        // to fix it first is the actual requirement, because the only fix is a whole-database
        // VACUUM that takes an exclusive lock on a file which may be gigabytes, inside a running
        // game. A repair attempt that half-succeeded would be worse than the refusal.
        Assert.Equal(0, Pragma(temp.Path, "auto_vacuum"));
        Assert.Equal(1, Pragma(temp.Path, "user_version"));
    }

    [Fact]
    public void TheIncrementalVacuumSettingCannotBeAddedAfterTablesExist()
    {
        // The measurement the refusal above rests on, re-run here rather than cited. If SQLite
        // ever started honouring a late auto_vacuum, this test goes red and the refusal becomes
        // unnecessary strictness - which is exactly when somebody should be told.
        using var temp = StoreFixture.NewStorePath();

        CreateStoreWithoutIncrementalVacuum(temp.Path);

        using var connection = new SqliteConnection($"Data Source={temp.Path};Pooling=False");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA auto_vacuum=INCREMENTAL";
            pragma.ExecuteNonQuery();
        }

        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA auto_vacuum";

        Assert.Equal(0L, Convert.ToInt64(read.ExecuteScalar()));
    }

    [Fact]
    public void EveryStableTableExistsAndTheStoreIsStampedAtTheCurrentSchemaVersion()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var tables = TableNames(temp.Path);

        Assert.Equal(1, store.SchemaVersion);
        Assert.Equal(store.SchemaVersion, Pragma(temp.Path, "user_version"));

        foreach (var expected in new[]
                 {
                     "market_sale", "market_sale_daily", "snapshot_daily",
                     "own_sale", "lot", "proposal", "levy_reading", "calibration",
                 })
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public void ReopeningAnExistingStoreDoesNotRunItsMigrationsAgain()
    {
        using var temp = StoreFixture.NewStorePath();

        using (var first = MarketStore.OpenOrCreate(temp.Path))
        {
            first.Write(OneSnapshot(StoreFixture.Instant));
        }

        // A migration re-run would throw on CREATE TABLE, so surviving the open is most of the
        // assertion. The written row surviving is the other half: a store that was recreated
        // rather than reopened would be empty and would still pass a version check.
        using var second = MarketStore.OpenOrCreate(temp.Path);

        Assert.Equal(1, second.SchemaVersion);
        Assert.Single(second.ReadSnapshots(
            StoreFixture.Ware, StoreFixture.World, StoreFixture.Instant.AddDays(-1), StoreFixture.Instant.AddDays(1)));
    }

    [Fact]
    public void AStoreWrittenByANewerEmmIsRefusedRatherThanDowngraded()
    {
        using var temp = StoreFixture.NewStorePath();

        using (var store = MarketStore.OpenOrCreate(temp.Path))
        {
            // Nothing to do; the store exists at the current version.
        }

        using (var connection = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            connection.Open();
            using var stamp = connection.CreateCommand();
            stamp.CommandText = "PRAGMA user_version = 99";
            stamp.ExecuteNonQuery();
        }

        var thrown = Assert.Throws<StoreUnusableException>(() => MarketStore.OpenOrCreate(temp.Path));

        Assert.Contains("99", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(99, Pragma(temp.Path, "user_version"));
    }

    /// <summary>A store made the ordinary way, and therefore without the one pragma that matters.</summary>
    private static void CreateStoreWithoutIncrementalVacuum(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE something (a INTEGER PRIMARY KEY); PRAGMA user_version = 1";
        create.ExecuteNonQuery();
    }

    private static Snapshot OneSnapshot(DateTimeOffset at) =>
        new(StoreFixture.Ware, StoreFixture.World, at, Source.Aggregator, null,
            [new Listing(new UnitPrice(1_000), 3, "Coriander", null)]);

    private static long Pragma(string path, string name) => StoreFixture.Pragma(path, name);

    private static string PragmaText(string path, string name) => StoreFixture.PragmaText(path, name);

    private static IReadOnlyList<string> TableNames(string path) => StoreFixture.TableNames(path);
}

using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Store;
using Xunit;

namespace EorzeanMarketMaster.Tests.Store;

/// <summary>
/// Reading Sales back out as a series - the path the graph is drawn from.
/// </summary>
public class SalesReadTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SalesComeBackAsASeriesForTheWareAndMarketThatWereAskedFor()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var other = new WareId(StoreFixture.Ware.ItemId, Quality.Normal);
        var elsewhere = new WorldId(80);

        store.Write(
        [
            Sale(StoreFixture.Ware, StoreFixture.World, Midnight, 100, 1),
            Sale(StoreFixture.Ware, StoreFixture.World, Midnight.AddHours(6), 120, 3),
            Sale(other, StoreFixture.World, Midnight, 40, 1),
            Sale(StoreFixture.Ware, elsewhere, Midnight, 900, 1),
        ]);

        var series = store.ReadSales(
            StoreFixture.Ware, StoreFixture.World, Midnight, Midnight.AddDays(1));

        Assert.Equal(2, series.Sales.Count);
        Assert.Equal(4, series.Units);
        Assert.Equal([100L, 120L], series.Sales.Select(sale => sale.UnitPrice.Gil));
    }

    [Fact]
    public void TheRangeIsInclusiveOfItsStartAndExclusiveOfItsEnd()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.Write(
        [
            Sale(StoreFixture.Ware, StoreFixture.World, Midnight.AddDays(-1), 90, 1),
            Sale(StoreFixture.Ware, StoreFixture.World, Midnight, 100, 1),
            Sale(StoreFixture.Ware, StoreFixture.World, Midnight.AddDays(1), 110, 1),
        ]);

        var series = store.ReadSales(
            StoreFixture.Ware, StoreFixture.World, Midnight, Midnight.AddDays(1));

        Assert.Equal(100L, Assert.Single(series.Sales).UnitPrice.Gil);
    }

    [Fact]
    public void TheSourceSurvivesTheRoundTripSoAnImportedRowStaysIdentifiable()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.Write(
        [
            Sale(StoreFixture.Ware, StoreFixture.World, Midnight, 100, 1, Source.ImportedStore),
            Sale(StoreFixture.Ware, StoreFixture.World, Midnight.AddHours(1), 100, 1, Source.OpenedBoard),
        ]);

        var series = store.ReadSales(
            StoreFixture.Ware, StoreFixture.World, Midnight, Midnight.AddDays(1));

        Assert.Equal(
            [Source.ImportedStore, Source.OpenedBoard],
            series.Sales.Select(sale => sale.Source));
    }

    [Fact]
    public void AWareTheStoreHoldsNothingForReadsBackAsAnEmptySeriesRatherThanAsNothing()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var series = store.ReadSales(
            StoreFixture.Ware, StoreFixture.World, Midnight, Midnight.AddDays(1));

        Assert.Empty(series.Sales);
        Assert.Equal(StoreFixture.Ware, series.Ware);
        Assert.Equal(StoreFixture.World, series.World);
        Assert.Null(store.OldestSaleAt(StoreFixture.Ware, StoreFixture.World));
    }

    [Fact]
    public void TheOldestSaleIsWhatBoundsAllOfHistory()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.Write(
        [
            Sale(StoreFixture.Ware, StoreFixture.World, Midnight.AddDays(-180), 90, 1),
            Sale(StoreFixture.Ware, StoreFixture.World, Midnight, 100, 1),
        ]);

        Assert.Equal(Midnight.AddDays(-180), store.OldestSaleAt(StoreFixture.Ware, StoreFixture.World));
        Assert.Equal(Midnight, store.NewestSaleAt(StoreFixture.Ware, StoreFixture.World));
    }

    private static MarketSale Sale(
        WareId ware,
        WorldId world,
        DateTimeOffset at,
        long gil,
        int stack,
        Source source = Source.Aggregator) =>
        new(ware, world, at, new UnitPrice(gil), stack, source);
}

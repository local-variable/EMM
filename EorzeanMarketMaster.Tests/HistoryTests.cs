using EorzeanMarketMaster.Core;
using Xunit;

namespace EorzeanMarketMaster.Tests;

/// <summary>
/// The series builder: Sales in, Rollups out, and nothing requested from anybody.
/// </summary>
public class HistoryTests
{
    private static readonly WareId Ware = new(5057, Quality.High);
    private static readonly WorldId World = new(79);

    /// <summary>Midnight UTC, so a slice boundary is exactly where it looks.</summary>
    private static readonly DateTimeOffset Midnight = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARollupIsFoldedFromTheIndividualSalesInItsSlice()
    {
        // The ticket's first acceptance criterion, and it is a claim about arithmetic: no Source
        // hands back a rollup, so every figure below is computed here or it does not exist.
        var history = Series(
            (Midnight.AddHours(1), 100, 1),
            (Midnight.AddHours(5), 300, 2),
            (Midnight.AddHours(9), 200, 1));

        var rollups = history.Rollups(RollupWidth.Day, Midnight, Midnight.AddDays(1));

        var only = Assert.Single(rollups);

        Assert.Equal(Midnight, only.Start);
        Assert.Equal(3, only.SaleCount);
        Assert.Equal(4, only.Units);
        Assert.Equal(100 + (300 * 2) + 200, only.Gil);
        Assert.Equal(100, only.Lowest.Gil);
        Assert.Equal(300, only.Highest.Gil);
    }

    [Fact]
    public void TheMeanIsWeightedByUnitsAndNotByRows()
    {
        // A one-unit Sale and a ninety-nine-unit Sale are not one observation each. Row-weighted
        // this reads 150 gil, which is a price nobody paid and half the market's own answer.
        var history = Series(
            (Midnight.AddHours(1), 100, 1),
            (Midnight.AddHours(2), 200, 99));

        var only = Assert.Single(history.Rollups(RollupWidth.Day, Midnight, Midnight.AddDays(1)));

        Assert.Equal(199.0, only.MeanUnitPrice, 3);
        Assert.NotEqual(150.0, only.MeanUnitPrice, 3);
    }

    [Fact]
    public void ASliceWithNoSalesProducesNoRollupAtAll()
    {
        // THE GAP CASE. A fold that emitted an empty slice would put a price of nothing into the
        // series; one that carried the previous slice forward would invent Sales that never
        // happened. Absence is the only honest answer, and it is what BrokenLine draws as a hole.
        var history = Series(
            (Midnight.AddHours(2), 100, 1),
            (Midnight.AddDays(4).AddHours(2), 140, 1));

        var rollups = history.Rollups(RollupWidth.Day, Midnight, Midnight.AddDays(6));

        Assert.Equal(2, rollups.Count);
        Assert.Equal(Midnight, rollups[0].Start);
        Assert.Equal(Midnight.AddDays(4), rollups[1].Start);
    }

    [Fact]
    public void ARollupCarriesTheSlicesOwnFiguresAndNotItsNeighboursAcrossAGap()
    {
        // The other half of the gap case: the slice after a hole must not have absorbed anything
        // from the slice before it. Folding into one running accumulator that is never reset is
        // the obvious way to get this wrong, and it reads as a plausible rising series.
        var history = Series(
            (Midnight.AddHours(2), 100, 5),
            (Midnight.AddDays(4).AddHours(2), 140, 3));

        var rollups = history.Rollups(RollupWidth.Day, Midnight, Midnight.AddDays(6));

        Assert.Equal(5, rollups[0].Units);
        Assert.Equal(3, rollups[1].Units);
        Assert.Equal(140.0, rollups[1].MeanUnitPrice, 3);
    }

    [Fact]
    public void TheFoldDoesNotDependOnTheOrderTheSalesArrivedIn()
    {
        // Sales reach EMM from a store scan, an aggregator page and an import, in three different
        // orders. A fold that only worked on sorted input would be right in the suite and wrong on
        // the second Source.
        var forwards = Series(
            (Midnight.AddHours(1), 100, 1),
            (Midnight.AddDays(1).AddHours(1), 200, 1),
            (Midnight.AddDays(2).AddHours(1), 300, 1));

        var backwards = Series(
            (Midnight.AddDays(2).AddHours(1), 300, 1),
            (Midnight.AddHours(1), 100, 1),
            (Midnight.AddDays(1).AddHours(1), 200, 1));

        Assert.Equal(
            forwards.Rollups(RollupWidth.Day, Midnight, Midnight.AddDays(3)),
            backwards.Rollups(RollupWidth.Day, Midnight, Midnight.AddDays(3)));
    }

    [Fact]
    public void SalesOutsideTheWindowAreNotFolded()
    {
        var history = Series(
            (Midnight.AddDays(-1), 100, 1),
            (Midnight.AddHours(2), 200, 1),
            (Midnight.AddDays(1), 300, 1));

        var only = Assert.Single(history.Rollups(RollupWidth.Day, Midnight, Midnight.AddDays(1)));

        Assert.Equal(1, only.SaleCount);
        Assert.Equal(200, only.Lowest.Gil);
    }

    [Fact]
    public void ASeriesHoldsItsSalesOldestFirstWhateverOrderTheyWentIn()
    {
        var history = Series(
            (Midnight.AddDays(2), 300, 1),
            (Midnight, 100, 1),
            (Midnight.AddDays(1), 200, 1));

        Assert.Equal(Midnight, history.FirstSaleAt);
        Assert.Equal(Midnight.AddDays(2), history.LastSaleAt);
        Assert.Equal([100L, 200L, 300L], history.Sales.Select(sale => sale.UnitPrice.Gil));
    }

    [Fact]
    public void ASeriesRefusesASaleFromAnotherMarket()
    {
        // A series quietly holding another World's Sales draws perfectly and is wrong, which is
        // the worst combination available. Refused rather than filtered: silently dropping rows
        // would hide the bug that put them there.
        var elsewhere = new MarketSale(Ware, new WorldId(80), Midnight, new UnitPrice(100), 1, Source.Aggregator);

        Assert.Throws<ArgumentException>(() => new History(Ware, World, [elsewhere]));
    }

    [Fact]
    public void ASeriesRefusesASaleOfAnotherWare()
    {
        var otherQuality = new WareId(Ware.ItemId, Quality.Normal);
        var wrong = new MarketSale(otherQuality, World, Midnight, new UnitPrice(100), 1, Source.Aggregator);

        Assert.Throws<ArgumentException>(() => new History(Ware, World, [wrong]));
    }

    [Fact]
    public void AWareWithNoSalesIsAnEmptySeriesRatherThanNothing()
    {
        // "EMM holds no Sales for this Ware" is an answer, and one a Player needs to be able to
        // see. Represented as null it would be indistinguishable from "EMM has not looked".
        var empty = History.Empty(Ware, World);

        Assert.Empty(empty.Sales);
        Assert.Null(empty.FirstSaleAt);
        Assert.Null(empty.LastSaleAt);
        Assert.Empty(empty.Rollups(RollupWidth.Day, Midnight, Midnight.AddDays(30)));
    }

    [Fact]
    public void UnitsAreSummedAcrossTheSeriesRatherThanCounted()
    {
        var history = Series(
            (Midnight, 100, 1),
            (Midnight.AddHours(1), 100, 40));

        Assert.Equal(41, history.Units);
    }

    [Fact]
    public void ARollupCannotDescribeNoSalesAtAll()
    {
        // The guard behind "an empty slice produces no Rollup": with it, an empty one cannot even
        // be constructed, so the rule holds for every fold anyone writes later rather than only
        // for the one in this file.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Rollup(Midnight, TimeSpan.FromDays(1), 0, 0, 0, new UnitPrice(1), new UnitPrice(1)));
    }

    [Fact]
    public void ARollupsRangeCannotBeInsideOut()
    {
        // The pair are the ends of a range and a candle gets drawn from them the moment band
        // modelling arrives. A range drawn inside out is a mark that renders and means nothing.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Rollup(Midnight, TimeSpan.FromDays(1), 1, 1, 100, new UnitPrice(900), new UnitPrice(100)));

        // The boundary from the other side: equal ends are a slice where every Sale was the same
        // price, which is ordinary rather than an error.
        var flat = new Rollup(Midnight, TimeSpan.FromDays(1), 1, 1, 100, new UnitPrice(100), new UnitPrice(100));

        Assert.Equal(100, flat.Lowest.Gil);
    }

    private static History Series(params (DateTimeOffset At, long Gil, int Stack)[] sales) =>
        new(Ware, World,
            [.. sales.Select(s => new MarketSale(Ware, World, s.At, new UnitPrice(s.Gil), s.Stack, Source.Aggregator))]);
}

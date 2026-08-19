using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Holdings;
using Xunit;

namespace EorzeanMarketMaster.Tests.Holdings;

/// <summary>
/// A reading is the complete contents of one place, and it normalises itself into a canonical form
/// on the way in.
///
/// The canonical form is not tidiness. Two readings of an unchanged Retainer have to be equal, or
/// a write of one is not idempotent and a comparison against the last one reports a change that
/// did not happen.
/// </summary>
public class HoldingsReadingTests
{
    [Fact]
    public void AStackSplitAcrossTwoSlotsIsOneQuantityOfOneWare()
    {
        var bags = HoldingsFixture.Bags(
            HoldingsFixture.Noon,
            HoldingsFixture.InBag(HoldingsFixture.Tincture, 40),
            HoldingsFixture.InBag(HoldingsFixture.Tincture, 59),
            HoldingsFixture.InBag(HoldingsFixture.Fleece, 3));

        Assert.Equal(2, bags.Held.Count);
        Assert.Equal(99, bags.Held.Single(l => l.Ware == HoldingsFixture.Tincture).Units);
        Assert.Equal(3, bags.Held.Single(l => l.Ware == HoldingsFixture.Fleece).Units);
    }

    [Fact]
    public void TwoListingsOfOneWareAtOneEvenPriceStayTwoListings()
    {
        // The case a key of (place, item, quality) would collapse. Two slots are two Listings, a
        // buyer takes one whole, and folding them into one row would undercount by exactly the
        // units in the second.
        var reading = HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 10, 500),
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 10, 500));

        Assert.Equal(2, reading.Held.Count);
        Assert.Equal(2, reading.Listed);
        Assert.Equal([0, 1], reading.Held.Select(line => line.Ordinal));
        Assert.Equal(20, reading.Holdings().Sum(h => h.Units));
    }

    [Fact]
    public void TheSameContentsInADifferentSlotOrderAreTheSameReading()
    {
        var walkedOneWay = HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 1, 900),
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 1, 100),
            HoldingsFixture.Stock(HoldingsFixture.Fleece, 4));

        var walkedTheOther = HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Fleece, 4),
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 1, 100),
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 1, 900));

        Assert.Equal(walkedOneWay.Held, walkedTheOther.Held);
        Assert.Equal([100L, 900L], walkedOneWay.Held
            .Where(l => l.Place == HoldingPlace.Listed)
            .Select(l => l.AskingPrice!.Value.Gil));
    }

    [Fact]
    public void ARetainerReadingHoldsNoBagLinesAndABagsReadingHoldsNothingElse()
    {
        Assert.Throws<ArgumentException>(() => HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.InBag(HoldingsFixture.Tincture, 1)));

        Assert.Throws<ArgumentException>(() => HoldingsFixture.Bags(
            HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Tincture, 1)));
    }

    [Fact]
    public void ARetainerCannotBeReadUnderACharacterThatDoesNotOwnIt()
    {
        Assert.Throws<ArgumentException>(() => new HoldingsReading(
            "Someone Else",
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Noon,
            Source.OpenedBoard,
            []));
    }

    [Fact]
    public void LookingAndFindingNothingIsAReading()
    {
        // A sold-out Retainer. Storable as itself rather than as an absence of rows, which is the
        // only way it can be told apart from a Retainer nobody has opened.
        var soldOut = HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon);

        Assert.Empty(soldOut.Held);
        Assert.Equal(0, soldOut.Listed);
        Assert.Equal(new HoldingsPlaceKey(HoldingsFixture.Character, HoldingsFixture.Coriander), soldOut.Place);
    }

    [Fact]
    public void FlattenedRowsCarryTheReadingsOwnerInstantsAndSource()
    {
        var reading = HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 2, 750));

        var row = Assert.Single(reading.Holdings());

        Assert.Equal(HoldingsFixture.Character, row.Character);
        Assert.Equal(HoldingsFixture.Coriander, row.Retainer);
        Assert.Equal(HoldingsFixture.Noon, row.ObservedAt);
        Assert.Equal(HoldingsFixture.Noon, row.TrueAsOf);
        Assert.Equal(Source.OpenedBoard, row.Source);
        Assert.Equal(750L, row.AskingPrice!.Value.Gil);
    }
}

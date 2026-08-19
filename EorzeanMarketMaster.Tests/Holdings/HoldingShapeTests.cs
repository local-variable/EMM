using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Holdings;
using Xunit;

namespace EorzeanMarketMaster.Tests.Holdings;

/// <summary>
/// The invariants a Holding carries in its constructor.
///
/// Every one of these is a wrong-number bug rather than a crash if it is let through, which is why
/// they are checked where a row is made rather than where one is used. A bag row carrying a
/// Retainer allocates somebody else's stock; a Retainer row carrying the wrong Character does the
/// same across Characters; a Listing with no price is a slot the allocator would call free.
/// </summary>
public class HoldingShapeTests
{
    [Fact]
    public void ABagBelongsToACharacterAndNeverToARetainer()
    {
        Assert.Throws<ArgumentException>(() => new Holding(
            HoldingsFixture.Tincture,
            HoldingPlace.Bag,
            HoldingsFixture.Character,
            HoldingsFixture.Coriander,
            1,
            null,
            HoldingsFixture.Noon,
            HoldingsFixture.Noon,
            Source.OpenedBoard));
    }

    [Theory]
    [InlineData(HoldingPlace.Stock)]
    [InlineData(HoldingPlace.Listed)]
    public void StockAndListingsBelongToANamedRetainer(HoldingPlace place)
    {
        Assert.Throws<ArgumentException>(() => new Holding(
            HoldingsFixture.Tincture,
            place,
            HoldingsFixture.Character,
            null,
            1,
            place == HoldingPlace.Listed ? new UnitPrice(10) : null,
            HoldingsFixture.Noon,
            HoldingsFixture.Noon,
            Source.OpenedBoard));
    }

    [Fact]
    public void ARetainerCannotBeFiledUnderACharacterThatDoesNotOwnIt()
    {
        Assert.Throws<ArgumentException>(() => new Holding(
            HoldingsFixture.Tincture,
            HoldingPlace.Stock,
            "Someone Else",
            HoldingsFixture.Coriander,
            1,
            null,
            HoldingsFixture.Noon,
            HoldingsFixture.Noon,
            Source.OpenedBoard));
    }

    [Fact]
    public void AListingWithoutAnAskingPriceIsNotAListing()
    {
        Assert.Throws<ArgumentException>(() =>
            new HeldWare(HoldingsFixture.Tincture, HoldingPlace.Listed, 3, null));
    }

    [Theory]
    [InlineData(HoldingPlace.Bag)]
    [InlineData(HoldingPlace.Stock)]
    public void OnlyAListingHasAnAskingPrice(HoldingPlace place)
    {
        Assert.Throws<ArgumentException>(() =>
            new HeldWare(HoldingsFixture.Tincture, place, 3, new UnitPrice(10)));
    }

    [Fact]
    public void AHoldingOfNothingIsNotAHolding()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HeldWare(HoldingsFixture.Tincture, HoldingPlace.Stock, 0, null));
    }

    [Fact]
    public void AnUndatedRowReportsAnUnknownAgeRatherThanAFreshOne()
    {
        // The companion plugin's shape. It answers instantly with whatever it is holding and does
        // not say when it last looked, so the age of what it reports is genuinely unknown - and an
        // unknown age that renders as "read just now" is the dishonesty this field exists to stop.
        var dated = HoldingsFixture
            .Read(HoldingsFixture.Coriander, HoldingsFixture.Noon.AddHours(-3),
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 2))
            .Holdings();

        var undated = HoldingsFixture
            .Undated(HoldingsFixture.Saffron, HoldingsFixture.Noon,
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 2))
            .Holdings();

        Assert.Equal(TimeSpan.FromHours(3), Assert.Single(dated).Age(HoldingsFixture.Noon));
        Assert.Null(Assert.Single(undated).Age(HoldingsFixture.Noon));
    }
}

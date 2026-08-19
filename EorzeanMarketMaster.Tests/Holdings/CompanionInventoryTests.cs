using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Holdings;
using Xunit;

namespace EorzeanMarketMaster.Tests.Holdings;

/// <summary>
/// Reading the companion plugin's rows.
///
/// It reports places by number in its own positional layout, and the numbers are the game's
/// inventory enum - so the interesting cases are all about what happens when EMM does not
/// recognise one. Getting that wrong does not throw: it produces a Player who quietly owns less
/// than they do, or a Retainer confidently reported as empty.
/// </summary>
public class CompanionInventoryTests
{
    // The game's own inventory ids. The plugin builds this map from the enum, where the compiler
    // checks it; here they are literals so the decoder can be exercised with no client running.
    private const ulong Bag = 0;
    private const ulong RetainerPage = 10_000;
    private const ulong RetainerMarket = 12_002;
    private const ulong ArmouryChest = 3_200;
    private const ulong Equipped = 1_000;

    private static readonly Dictionary<ulong, HoldingPlace> Containers = new()
    {
        [Bag] = HoldingPlace.Bag,
        [RetainerPage] = HoldingPlace.Stock,
        [RetainerMarket] = HoldingPlace.Listed,
    };

    [Fact]
    public void ARowBecomesAWareAtAQualityWithItsQuantity()
    {
        var decoded = CompanionInventory.Decode(
            HoldingsFixture.Character,
            null,
            [Row(Bag, HoldingsFixture.Tincture.ItemId, 12, highQuality: true)],
            Containers,
            HoldingsFixture.Noon);

        var line = Assert.Single(decoded.Reading!.Held);

        Assert.Equal(HoldingsFixture.Tincture, line.Ware);
        Assert.Equal(HoldingPlace.Bag, line.Place);
        Assert.Equal(12, line.Units);
        Assert.Equal(0, decoded.Unplaced);
    }

    [Fact]
    public void WhatComesBackCarriesNoAgeAtAll()
    {
        // The companion answers instantly with whatever it is holding and never says when it last
        // looked. Stamping the moment EMM asked into the age would present a Retainer nobody has
        // opened in a week as read a second ago.
        var decoded = CompanionInventory.Decode(
            HoldingsFixture.Character,
            HoldingsFixture.Coriander,
            [Row(RetainerPage, HoldingsFixture.Fleece.ItemId, 4)],
            Containers,
            HoldingsFixture.Noon);

        Assert.Null(decoded.Reading!.TrueAsOf);
        Assert.Equal(HoldingsFixture.Noon, decoded.Reading.ObservedAt);
        Assert.Equal(Source.ImportedStore, decoded.Reading.Source);
    }

    [Fact]
    public void ARowInAContainerEmmDoesNotReadIsDroppedAndCounted()
    {
        var decoded = CompanionInventory.Decode(
            HoldingsFixture.Character,
            null,
            [
                Row(Bag, HoldingsFixture.TinctureNq.ItemId, 1),
                Row(ArmouryChest, HoldingsFixture.Fleece.ItemId, 1),
            ],
            Containers,
            HoldingsFixture.Noon);

        Assert.Equal(1, decoded.Placed);
        Assert.Equal(1, decoded.Unplaced);
        Assert.Equal(HoldingsFixture.TinctureNq, Assert.Single(decoded.Reading!.Held).Ware);
    }

    [Fact]
    public void APlaceWhoseEveryRowIsUnrecognisedIsRefusedRatherThanReportedEmpty()
    {
        // The damaging case. A reading is the complete contents of its place, so one built out of
        // rows EMM failed to recognise would be a Retainer stated to be holding nothing - and the
        // Player would see a sold-out Retainer that is in fact full.
        var decoded = CompanionInventory.Decode(
            HoldingsFixture.Character,
            HoldingsFixture.Coriander,
            [Row(ArmouryChest, HoldingsFixture.Fleece.ItemId, 1)],
            Containers,
            HoldingsFixture.Noon);

        Assert.Null(decoded.Reading);
        Assert.Equal(1, decoded.Unplaced);
    }

    [Fact]
    public void APlaceTheCompanionSaysNothingAboutIsRefusedToo()
    {
        // This interface cannot tell "that Retainer is empty" from "I have never cached it", so
        // the companion is never allowed to assert an absence - only to add coverage.
        var decoded = CompanionInventory.Decode(
            HoldingsFixture.Character, HoldingsFixture.Coriander, [], Containers, HoldingsFixture.Noon);

        Assert.Null(decoded.Reading);
        Assert.Equal(0, decoded.Placed);
    }

    [Fact]
    public void AListingWithNoPriceIsNotTakenAsAListing()
    {
        // The companion holds the price array beside the items and reports a zero for a slot it has
        // not seen priced. A Listing EMM cannot state the ask for is not one it can act on.
        var decoded = CompanionInventory.Decode(
            HoldingsFixture.Character,
            HoldingsFixture.Coriander,
            [
                Row(RetainerMarket, HoldingsFixture.Tincture.ItemId, 3, price: 0),
                Row(RetainerMarket, HoldingsFixture.Fleece.ItemId, 1, price: 640),
            ],
            Containers,
            HoldingsFixture.Noon);

        var line = Assert.Single(decoded.Reading!.Held);

        Assert.Equal(HoldingsFixture.Fleece, line.Ware);
        Assert.Equal(640L, line.AskingPrice!.Value.Gil);
        Assert.Equal(1, decoded.Unplaced);
    }

    [Fact]
    public void ABagRowInARetainersSetIsDroppedRatherThanThrowing()
    {
        // A mapping mistake, and it has to be survivable: the reading itself refuses to hold a bag
        // line under a Retainer, so letting one through would take out the whole refresh instead of
        // one row.
        var decoded = CompanionInventory.Decode(
            HoldingsFixture.Character,
            HoldingsFixture.Coriander,
            [
                Row(Bag, HoldingsFixture.Tincture.ItemId, 1),
                Row(RetainerPage, HoldingsFixture.Fleece.ItemId, 2),
            ],
            Containers,
            HoldingsFixture.Noon);

        Assert.Equal(HoldingPlace.Stock, Assert.Single(decoded.Reading!.Held).Place);
        Assert.Equal(1, decoded.Unplaced);
    }

    [Theory]
    [InlineData(0u, 1ul)]
    [InlineData(5057u, 0ul)]
    public void AnEmptySlotIsNotAHolding(uint itemId, ulong quantity)
    {
        var decoded = CompanionInventory.Decode(
            HoldingsFixture.Character,
            null,
            [Row(Bag, itemId, quantity)],
            Containers,
            HoldingsFixture.Noon);

        Assert.Null(decoded.Reading);
        Assert.Equal(0, decoded.Unplaced);
    }

    [Fact]
    public void ARowTooShortToBeTheExpectedLayoutIsIgnored()
    {
        var decoded = CompanionInventory.Decode(
            HoldingsFixture.Character, null, [new ulong[] { Bag, 0, 5057, 1 }], Containers, HoldingsFixture.Noon);

        Assert.Null(decoded.Reading);
        Assert.Equal(0, decoded.Unplaced);
    }

    [Fact]
    public void DroppedRowsAreCountedPerContainerRatherThanTotalled()
    {
        var decoded = CompanionInventory.Decode(
            HoldingsFixture.Character,
            null,
            [
                Row(Bag, HoldingsFixture.TinctureNq.ItemId, 1),
                Row(ArmouryChest, HoldingsFixture.Fleece.ItemId, 1),
                Row(ArmouryChest, HoldingsFixture.Tincture.ItemId, 1),
                Row(Equipped, HoldingsFixture.Fleece.ItemId, 1),
            ],
            Containers,
            HoldingsFixture.Noon);

        Assert.Equal(3, decoded.Unplaced);
        Assert.Equal(2, decoded.UnreadContainers[ArmouryChest]);
        Assert.Equal(1, decoded.UnreadContainers[Equipped]);
    }

    [Fact]
    public void ContainersEmmKnowinglySkipsAreNotWorthReporting()
    {
        // The whole point of the split, and it was learned from a live run: a companion asked for a
        // Character's inventory returns the armoury and the gear being worn, and asked for a
        // Retainer returns the gear that Retainer is wearing. EMM drops all of it on purpose. An
        // earlier version warned on the total, so it warned on every single refresh - hundreds of
        // rows for the bags and a dozen per Retainer - and a warning that is always on is one that
        // hides the day it means something.
        var dropped = new Dictionary<ulong, int> { [ArmouryChest] = 300, [Equipped] = 13 };
        var ignored = new HashSet<ulong> { ArmouryChest, Equipped };

        Assert.Empty(CompanionInventory.Unexpected(dropped, ignored));
    }

    [Fact]
    public void AContainerEmmHasNeverHeardOfIsWorthReporting()
    {
        // The case the noise was hiding: a container that is neither read nor knowingly skipped is
        // the evidence that the numbering has moved underneath EMM.
        const ulong Unheard = 98_765;

        var dropped = new Dictionary<ulong, int> { [ArmouryChest] = 300, [Unheard] = 4 };
        var ignored = new HashSet<ulong> { ArmouryChest, Equipped };

        Assert.Equal([Unheard], CompanionInventory.Unexpected(dropped, ignored));
    }

    /// <summary>
    /// One row in the companion's positional layout. Only the five positions EMM reads are filled;
    /// the rest are materia, stains and sort keys EMM has no use for.
    /// </summary>
    private static ulong[] Row(
        ulong container, uint itemId, ulong quantity, bool highQuality = false, ulong price = 0)
    {
        var row = new ulong[25];

        row[0] = container;
        row[2] = itemId;
        row[3] = quantity;
        row[6] = highQuality ? 1ul : 0ul;
        row[24] = price;

        return row;
    }
}

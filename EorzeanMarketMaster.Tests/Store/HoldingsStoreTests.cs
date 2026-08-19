using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Holdings;
using EorzeanMarketMaster.Core.Store;
using EorzeanMarketMaster.Tests.Holdings;
using Xunit;

namespace EorzeanMarketMaster.Tests.Store;

/// <summary>
/// Holdings on disk: what survives a restart, and what a second reading of a place does to the
/// first one.
///
/// These run against real SQLite on a real file for the same reason the rest of the store tests
/// do. The claims here are claims about the file - that a replace removes what has gone, that an
/// empty reading is stored as itself, and that the ladder cannot take any of it.
/// </summary>
public class HoldingsStoreTests
{
    [Fact]
    public void AReadingSurvivesARestartWithItsInstantsAndSourceIntact()
    {
        using var temp = StoreFixture.NewStorePath();

        var written = HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Fleece, 40),
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 3, 12_500));

        using (var store = MarketStore.OpenOrCreate(temp.Path))
        {
            store.WriteHoldings(written);
        }

        using var reopened = MarketStore.OpenOrCreate(temp.Path);

        var read = Assert.Single(reopened.ReadHoldings());

        // Field by field and then the lines. A record holding a list compares that list by
        // reference, so asserting the whole record would compare two objects that can never be
        // equal and would pass no judgement at all on what came back.
        Assert.Equal(written.Character, read.Character);
        Assert.Equal(written.Retainer, read.Retainer);
        Assert.Equal(written.ObservedAt, read.ObservedAt);
        Assert.Equal(written.TrueAsOf, read.TrueAsOf);
        Assert.Equal(written.Source, read.Source);
        Assert.Equal(written.Held, read.Held);
    }

    [Fact]
    public void ACharactersBagsComeBackAsBagsRatherThanAsARetainerCalledNothing()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.WriteHoldings(HoldingsFixture.Bags(
            HoldingsFixture.Noon, HoldingsFixture.InBag(HoldingsFixture.Tincture, 6)));

        var read = Assert.Single(store.ReadHoldings());

        Assert.Null(read.Retainer);
        Assert.Equal(HoldingsFixture.Character, read.Character);
        Assert.Equal(HoldingPlace.Bag, Assert.Single(read.Held).Place);
    }

    [Fact]
    public void LookingAndFindingNothingIsStoredAsItselfRatherThanAsNoRows()
    {
        // A sold-out Retainer. Stored as an absence of rows it would be indistinguishable from a
        // Retainer nobody has ever opened, and those two want opposite things from the Player.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.WriteHoldings(HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon));

        var read = Assert.Single(store.ReadHoldings());

        Assert.Empty(read.Held);
        Assert.Equal(HoldingsFixture.Coriander, read.Retainer);
    }

    [Fact]
    public void ASecondReadingOfAPlaceRemovesWhatIsNoLongerThere()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.WriteHoldings(HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon.AddHours(-1),
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 5, 400),
            HoldingsFixture.Listed(HoldingsFixture.Fleece, 2, 90)));

        store.WriteHoldings(HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Listed(HoldingsFixture.Fleece, 2, 90)));

        var line = Assert.Single(Assert.Single(store.ReadHoldings()).Held);

        Assert.Equal(HoldingsFixture.Fleece, line.Ware);

        // And through the file rather than through the object: a replace that left the row behind
        // and merely stopped returning it would pass every assertion above.
        Assert.Single(StoreFixture.Read(store, temp.Path, "SELECT * FROM holding"));
    }

    [Fact]
    public void ReplacingOneRetainerLeavesEveryOtherPlaceAlone()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.WriteHoldings(HoldingsFixture.Read(
            HoldingsFixture.Saffron, HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Tincture, 3)));
        store.WriteHoldings(HoldingsFixture.Bags(
            HoldingsFixture.Noon, HoldingsFixture.InBag(HoldingsFixture.Fleece, 1)));
        store.WriteHoldings(HoldingsFixture.Read(
            HoldingsFixture.Coriander, HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Fleece, 2)));

        store.WriteHoldings(HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon.AddHours(1)));

        var readings = store.ReadHoldings();

        Assert.Equal(3, readings.Count);
        Assert.Equal(4, readings.Sum(r => r.Held.Sum(line => line.Units)));
    }

    [Fact]
    public void TwoListingsOfOneWareAtOneEvenPriceBothSurviveTheRoundTrip()
    {
        // The case a key of (place, item, quality) would collapse on the way in. It would not
        // throw; it would silently store one row and undercount the Player's stock forever.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.WriteHoldings(HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 10, 500),
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 10, 500)));

        var read = Assert.Single(store.ReadHoldings());

        Assert.Equal(2, read.Held.Count);
        Assert.Equal(20, read.Held.Sum(line => line.Units));
    }

    [Fact]
    public void AnUndatedReadingKeepsItsUnknownAgeOnDisk()
    {
        // The companion's answer. Written as a real instant it would come back looking like ground
        // truth, and the surface would report an age nobody ever measured.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.WriteHoldings(HoldingsFixture.Undated(
            HoldingsFixture.Saffron, HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Fleece, 12)));

        var read = Assert.Single(store.ReadHoldings());

        Assert.Null(read.TrueAsOf);
        Assert.Equal(HoldingsFixture.Noon, read.ObservedAt);
        Assert.Equal(Source.ImportedStore, read.Source);
    }

    [Fact]
    public void TheLadderCannotTakeHoldingsEvenWhenTheCapIsImpossible()
    {
        // Not a refetch away. No Source republishes a Retainer's contents - only the Player,
        // standing at a bell, opening it - so evicting these would cost the record itself.
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        store.WriteHoldings(HoldingsFixture.Read(
            HoldingsFixture.Coriander, HoldingsFixture.Noon,
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 5, 400)));

        var report = store.Evict(EvictionPolicy.Default with { ByteCap = 1 }, HoldingsFixture.Noon);

        Assert.Equal(EvictionOutcome.CapExceeded, report.Outcome);
        Assert.Single(store.ReadHoldings());
        Assert.Equal(1, report.ProtectedRowCounts["holding"]);
        Assert.Equal(1, report.ProtectedRowCounts["holding_reading"]);
    }
}

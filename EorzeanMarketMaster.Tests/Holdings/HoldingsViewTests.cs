using EorzeanMarketMaster.Core.Holdings;
using Xunit;

namespace EorzeanMarketMaster.Tests.Holdings;

/// <summary>
/// The rollup the Holdings surface draws, and the one comparison EMM is permitted to make between
/// what the bell counts and what it last saw.
///
/// That permission is narrow on purpose. The Listing count was measured lagging its own Retainer's
/// container by several seconds while that Retainer was open, so a mismatch there says nothing at
/// all - and it nets a Sale against a relist, so even where it is trustworthy it can never say
/// which of the two happened.
/// </summary>
public class HoldingsViewTests
{
    [Fact]
    public void OneWareIsOneLineAcrossEveryPlaceItSitsIn()
    {
        var ledger = HoldingsLedger.From(
        [
            HoldingsFixture.Bags(HoldingsFixture.Noon,
                HoldingsFixture.InBag(HoldingsFixture.Tincture, 8)),
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon,
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 20),
                HoldingsFixture.Listed(HoldingsFixture.Tincture, 12, 400)),
        ]);

        var line = Assert.Single(HoldingsView.Build(ledger, null, null, HoldingsFixture.Noon).Wares);

        Assert.Equal(HoldingsFixture.Tincture, line.Ware);
        Assert.Equal(8, line.InBags);
        Assert.Equal(20, line.InStock);
        Assert.Equal(12, line.Listed);
        Assert.Equal(40, line.Units);
    }

    [Fact]
    public void HighAndNormalQualityAreTwoLinesBecauseTheyAreTwoPrices()
    {
        var ledger = HoldingsLedger.From(
        [
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon,
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 3),
                HoldingsFixture.Stock(HoldingsFixture.TinctureNq, 4)),
        ]);

        var view = HoldingsView.Build(ledger, null, null, HoldingsFixture.Noon);

        Assert.Equal(2, view.DistinctWares);
        Assert.Equal([4, 3], view.Wares.Select(w => w.Units));
    }

    [Fact]
    public void ALinesAgeIsItsStalestContributorRatherThanAnAverage()
    {
        var ledger = HoldingsLedger.From(
        [
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon.AddHours(-1),
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 1)),
            HoldingsFixture.Read(HoldingsFixture.Saffron, HoldingsFixture.Noon.AddHours(-9),
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 1)),
        ]);

        var line = Assert.Single(HoldingsView.Build(ledger, null, null, HoldingsFixture.Noon).Wares);

        Assert.False(line.AgeUnknown);
        Assert.Equal(TimeSpan.FromHours(9), line.Age(HoldingsFixture.Noon));
    }

    [Fact]
    public void OneUndatedContributorMakesTheWholeLinesAgeUnknown()
    {
        // Rather than quietly reporting the dated half's age for the total. A figure part of which
        // has no age has no age, and saying "one hour" over a Retainer nobody has opened in a week
        // is the one reading a Player must not be given.
        var ledger = HoldingsLedger.From(
        [
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon.AddHours(-1),
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 1)),
            HoldingsFixture.Undated(HoldingsFixture.Saffron, HoldingsFixture.Noon,
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 1)),
        ]);

        var line = Assert.Single(HoldingsView.Build(ledger, null, null, HoldingsFixture.Noon).Wares);

        Assert.True(line.AgeUnknown);
        Assert.Null(line.Age(HoldingsFixture.Noon));
    }

    [Fact]
    public void WithoutARosterNoRetainersAreClaimed()
    {
        // Deliberately not derived from the readings held. A Retainer the Player has since
        // dismissed would otherwise live on in that list forever.
        var ledger = HoldingsLedger.From(
        [
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon,
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 1)),
        ]);

        var view = HoldingsView.Build(ledger, null, null, HoldingsFixture.Noon);

        Assert.Empty(view.Retainers);
        Assert.Null(view.RosterReadAt);
    }

    [Fact]
    public void ARetainerEmmHasNeverOpenedIsReportedAsUnseenRatherThanAsEmpty()
    {
        var view = HoldingsView.Build(new HoldingsLedger(), Roster(), null, HoldingsFixture.Noon);
        var standing = view.Retainers.Single(r => r.Summary.Retainer == HoldingsFixture.Coriander);

        Assert.Equal(ContentsStanding.NeverSeen, standing.Standing);
        Assert.Null(standing.ListedKnown);
        Assert.Null(standing.TrueAsOf);
    }

    [Fact]
    public void MatchingCountsAgreeAndDifferingCountsSayOnlyThatSomethingMoved()
    {
        var ledger = HoldingsLedger.From(
        [
            // Coriander: two Listings, and the bell counts two.
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon.AddHours(-2),
                HoldingsFixture.Listed(HoldingsFixture.Tincture, 1, 100),
                HoldingsFixture.Listed(HoldingsFixture.Fleece, 1, 200)),

            // Saffron: three Listings, and the bell counts one.
            HoldingsFixture.Read(HoldingsFixture.Saffron, HoldingsFixture.Noon.AddHours(-2),
                HoldingsFixture.Listed(HoldingsFixture.Tincture, 1, 100),
                HoldingsFixture.Listed(HoldingsFixture.TinctureNq, 1, 50),
                HoldingsFixture.Listed(HoldingsFixture.Fleece, 1, 200)),
        ]);

        var view = HoldingsView.Build(ledger, Roster(), null, HoldingsFixture.Noon);

        var agrees = view.Retainers.Single(r => r.Summary.Retainer == HoldingsFixture.Coriander);
        var moved = view.Retainers.Single(r => r.Summary.Retainer == HoldingsFixture.Saffron);

        Assert.Equal(ContentsStanding.Agrees, agrees.Standing);
        Assert.Equal(2, agrees.ListedKnown);

        Assert.Equal(ContentsStanding.MayHaveMoved, moved.Standing);
        Assert.Equal(3, moved.ListedKnown);
        Assert.Equal(1, moved.Summary.MarketItemCount);
    }

    [Fact]
    public void TheRetainerBeingReadIsNotJudgedAgainstItsOwnCount()
    {
        // Measured on a live client: the count lags the container by seconds while a Retainer is
        // open, so within a visit a mismatch is the field catching up and nothing else. It may
        // never be read as a Sale, a delist or a reconciliation failure.
        var ledger = HoldingsLedger.From(
        [
            HoldingsFixture.Read(HoldingsFixture.Saffron, HoldingsFixture.Noon,
                HoldingsFixture.Listed(HoldingsFixture.Tincture, 1, 100),
                HoldingsFixture.Listed(HoldingsFixture.Fleece, 1, 200)),
        ]);

        var view = HoldingsView.Build(ledger, Roster(), HoldingsFixture.Saffron, HoldingsFixture.Noon);

        Assert.Equal(
            ContentsStanding.BeingRead,
            view.Retainers.Single(r => r.Summary.Retainer == HoldingsFixture.Saffron).Standing);
    }

    [Fact]
    public void ALapsedMarketMakesTheCountUntrustworthyRatherThanDamning()
    {
        var lapsed = new RetainerRoster(HoldingsFixture.Character, HoldingsFixture.Noon,
        [
            HoldingsFixture.Summary(HoldingsFixture.Coriander, 0, HoldingsFixture.Noon.AddDays(-1)),
        ]);

        var ledger = HoldingsLedger.From(
        [
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon.AddDays(-8),
                HoldingsFixture.Listed(HoldingsFixture.Tincture, 1, 100)),
        ]);

        var standing = Assert.Single(
            HoldingsView.Build(ledger, lapsed, null, HoldingsFixture.Noon).Retainers);

        Assert.Equal(ContentsStanding.MarketLapsed, standing.Standing);
        Assert.Equal(1, standing.ListedKnown);
    }

    [Fact]
    public void NarrowingToOneRetainerRecomputesEveryFigureRatherThanHidingRows()
    {
        // The whole point of Where. A surface showing one Retainer has to say what THAT Retainer
        // holds - a filter that hid lines while the units, the split and the age still described
        // everything would be the quietly-wrong figure this ticket is written against.
        var ledger = HoldingsLedger.From(
        [
            HoldingsFixture.Bags(HoldingsFixture.Noon,
                HoldingsFixture.InBag(HoldingsFixture.Tincture, 8)),
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon.AddHours(-1),
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 20),
                HoldingsFixture.Listed(HoldingsFixture.Tincture, 12, 400)),
            HoldingsFixture.Read(HoldingsFixture.Saffron, HoldingsFixture.Noon.AddHours(-9),
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 5),
                HoldingsFixture.Stock(HoldingsFixture.Fleece, 3)),
        ]);

        var everywhere = HoldingsView.Build(ledger, Roster(), null, HoldingsFixture.Noon);
        var coriander = everywhere.Where(row => row.Retainer == HoldingsFixture.Coriander);

        var line = Assert.Single(coriander.Wares);

        Assert.Equal(HoldingsFixture.Tincture, line.Ware);
        Assert.Equal(32, line.Units);
        Assert.Equal(0, line.InBags);
        Assert.Equal(20, line.InStock);
        Assert.Equal(12, line.Listed);

        // And the age is the stalest of what SURVIVED, not of what was there before - Saffron's
        // nine-hour reading is out of this line entirely.
        Assert.Equal(TimeSpan.FromHours(1), line.Age(HoldingsFixture.Noon));

        // Unfiltered totals for comparison, so the case fails if Where quietly did nothing.
        Assert.Equal(2, everywhere.DistinctWares);
        Assert.Equal(45, everywhere.Wares.Single(w => w.Ware == HoldingsFixture.Tincture).Units);
    }

    [Fact]
    public void NarrowingLeavesTheRetainerStandingsAlone()
    {
        // They answer "how far can what EMM holds be relied on", which is a question about coverage
        // rather than about what is on screen. A Player narrowing to one Retainer still needs to
        // know the others exist and how stale they are.
        var ledger = HoldingsLedger.From(
        [
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon,
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 1)),
        ]);

        var narrowed = HoldingsView
            .Build(ledger, Roster(), null, HoldingsFixture.Noon)
            .Where(row => row.Place == HoldingPlace.Bag);

        Assert.Empty(narrowed.Wares);
        Assert.Equal(2, narrowed.Retainers.Count);
        Assert.Equal(HoldingsFixture.Noon, narrowed.RosterReadAt);
    }

    [Fact]
    public void TheTotalsSplitWhatIsEarningFromWhatIsNot()
    {
        var ledger = HoldingsLedger.From(
        [
            HoldingsFixture.Bags(HoldingsFixture.Noon,
                HoldingsFixture.InBag(HoldingsFixture.Fleece, 5)),
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon,
                HoldingsFixture.Stock(HoldingsFixture.Tincture, 7),
                HoldingsFixture.Listed(HoldingsFixture.Tincture, 2, 400)),
        ]);

        var view = HoldingsView.Build(ledger, null, null, HoldingsFixture.Noon);

        Assert.Equal(2, view.UnitsListed);
        Assert.Equal(12, view.UnitsUnlisted);
    }

    private static RetainerRoster Roster() =>
        new(HoldingsFixture.Character, HoldingsFixture.Noon,
        [
            HoldingsFixture.Summary(HoldingsFixture.Coriander, 2, HoldingsFixture.Noon.AddDays(5)),
            HoldingsFixture.Summary(HoldingsFixture.Saffron, 1, HoldingsFixture.Noon.AddDays(5)),
        ]);
}

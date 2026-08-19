using EorzeanMarketMaster.Core.Holdings;
using Xunit;

namespace EorzeanMarketMaster.Tests.Holdings;

/// <summary>
/// Which reading of a place EMM keeps.
///
/// The rule has two halves and the second is the one that earns its keep: readings replace rather
/// than merge, and a Source that cannot date what it reports never displaces one that can. The
/// second half is the whole standing of the companion plugin - an additional Source whose absence
/// changes coverage and nothing else.
/// </summary>
public class HoldingsLedgerTests
{
    [Fact]
    public void ANewerReadingReplacesTheOlderOneWholesale()
    {
        // The defect this class exists to make impossible: a Ware that has since sold lingering
        // because the two readings were merged row by row.
        var ledger = new HoldingsLedger();

        ledger.Record(HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon.AddHours(-1),
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 5, 400),
            HoldingsFixture.Listed(HoldingsFixture.Fleece, 2, 90)));

        ledger.Record(HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Listed(HoldingsFixture.Fleece, 2, 90)));

        var held = ledger.Holdings();

        Assert.Equal(HoldingsFixture.Fleece, Assert.Single(held).Ware);
    }

    [Fact]
    public void AnOlderReadingOfAPlaceAlreadyKnownIsRefused()
    {
        var ledger = new HoldingsLedger();

        Assert.True(ledger.Record(HoldingsFixture.Read(
            HoldingsFixture.Coriander, HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Tincture, 9))));

        Assert.False(ledger.Record(HoldingsFixture.Read(
            HoldingsFixture.Coriander, HoldingsFixture.Noon.AddMinutes(-30),
            HoldingsFixture.Stock(HoldingsFixture.Tincture, 1))));

        Assert.Equal(9, Assert.Single(ledger.Holdings()).Units);
    }

    [Fact]
    public void AnUndatedReadingCoversAPlaceEmmHasNeverLookedAt()
    {
        // The companion earning its place. EMM has never opened Saffron, so an answer of unknown
        // age is the only answer there is, and coverage beats no coverage.
        var ledger = new HoldingsLedger();

        Assert.True(ledger.Record(HoldingsFixture.Undated(
            HoldingsFixture.Saffron, HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Fleece, 12))));

        var row = Assert.Single(ledger.Holdings());

        Assert.Equal(12, row.Units);
        Assert.Null(row.TrueAsOf);
    }

    [Fact]
    public void AnUndatedReadingNeverDisplacesOneEmmCanDate()
    {
        // Asked for LATER by the wall clock and still refused. Its ObservedAt says only when EMM
        // asked, which is a fact about EMM - ordering on it would let a cache of unknown age
        // overwrite ground truth every time the surface refreshed.
        var ledger = new HoldingsLedger();

        ledger.Record(HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon.AddDays(-2),
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 5, 400)));

        Assert.False(ledger.Record(HoldingsFixture.Undated(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Listed(HoldingsFixture.Tincture, 1, 999))));

        var row = Assert.Single(ledger.Holdings());

        Assert.Equal(5, row.Units);
        Assert.Equal(HoldingsFixture.Noon.AddDays(-2), row.TrueAsOf);
    }

    [Fact]
    public void ADatedReadingDisplacesAnUndatedOneEvenWhenItWasAskedForFirst()
    {
        // The other direction, and it has to hold whatever the clock says: an age EMM knows is
        // worth more than an age it does not, however long ago it was taken.
        var ledger = new HoldingsLedger();

        ledger.Record(HoldingsFixture.Undated(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Fleece, 12)));

        Assert.True(ledger.Record(HoldingsFixture.Read(
            HoldingsFixture.Coriander,
            HoldingsFixture.Noon.AddDays(-2),
            HoldingsFixture.Stock(HoldingsFixture.Tincture, 1))));

        var row = Assert.Single(ledger.Holdings());

        Assert.Equal(HoldingsFixture.Tincture, row.Ware);
        Assert.Equal(HoldingsFixture.Noon.AddDays(-2), row.TrueAsOf);
    }

    [Fact]
    public void BetweenTwoUndatedReadingsTheLaterAskWins()
    {
        // Not a claim that it is newer - only that re-asking the same Source is worth more than
        // holding its previous answer, which is what makes refreshing the companion do anything.
        var ledger = new HoldingsLedger();

        ledger.Record(HoldingsFixture.Undated(
            HoldingsFixture.Saffron, HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Fleece, 12)));

        Assert.True(ledger.Record(HoldingsFixture.Undated(
            HoldingsFixture.Saffron, HoldingsFixture.Noon.AddMinutes(5),
            HoldingsFixture.Stock(HoldingsFixture.Fleece, 7))));

        Assert.Equal(7, Assert.Single(ledger.Holdings()).Units);
    }

    [Fact]
    public void OneRetainerNeverStandsInForAnother()
    {
        var ledger = new HoldingsLedger();

        ledger.Record(HoldingsFixture.Read(
            HoldingsFixture.Coriander, HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Tincture, 1)));
        ledger.Record(HoldingsFixture.Read(
            HoldingsFixture.Saffron, HoldingsFixture.Noon,
            HoldingsFixture.Stock(HoldingsFixture.Tincture, 2)));
        ledger.Record(HoldingsFixture.Bags(
            HoldingsFixture.Noon,
            HoldingsFixture.InBag(HoldingsFixture.Tincture, 4)));

        Assert.NotNull(ledger.Of(new HoldingsPlaceKey(HoldingsFixture.Character, null)));
        Assert.NotNull(ledger.Of(new HoldingsPlaceKey(HoldingsFixture.Character, HoldingsFixture.Coriander)));
        Assert.NotNull(ledger.Of(new HoldingsPlaceKey(HoldingsFixture.Character, HoldingsFixture.Saffron)));
        Assert.Equal(7, ledger.Holdings().Sum(h => h.Units));
    }

    [Fact]
    public void TheSameReadingsInAnyOrderProduceTheSameLedger()
    {
        // A state handed to the decision seam that varied with insertion order would make a
        // reproducible decision impossible to reproduce.
        var readings = new[]
        {
            HoldingsFixture.Read(HoldingsFixture.Saffron, HoldingsFixture.Noon,
                HoldingsFixture.Stock(HoldingsFixture.Fleece, 2)),
            HoldingsFixture.Bags(HoldingsFixture.Noon,
                HoldingsFixture.InBag(HoldingsFixture.Tincture, 3)),
            HoldingsFixture.Read(HoldingsFixture.Coriander, HoldingsFixture.Noon,
                HoldingsFixture.Listed(HoldingsFixture.TinctureNq, 1, 20)),
        };

        var forwards = HoldingsLedger.From(readings).Holdings();
        var backwards = HoldingsLedger.From(readings.Reverse()).Holdings();

        Assert.Equal(forwards, backwards);
    }

    [Fact]
    public void APlaceNeverReadHasNoReading()
    {
        var ledger = new HoldingsLedger();

        Assert.Null(ledger.Of(new HoldingsPlaceKey(HoldingsFixture.Character, HoldingsFixture.Coriander)));
        Assert.Null(ledger.Of(new HoldingsPlaceKey(HoldingsFixture.Character, null)));
        Assert.Empty(ledger.Holdings());
    }
}

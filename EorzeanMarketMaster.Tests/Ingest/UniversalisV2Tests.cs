using System.Text.Json;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Ingest;
using Xunit;

namespace EorzeanMarketMaster.Tests.Ingest;

/// <summary>
/// Reading the aggregator's own responses, asserted against bodies it really sent.
/// </summary>
public class UniversalisV2Tests
{
    private static readonly WareId FireShard = new(5, Quality.Normal);

    [Fact]
    public void OneWaresBoardIsReadOutOfTheSingleItemShape()
    {
        // The point refresh, which is this ticket's headline case, and the shape it comes back in:
        // asking for one Item returns the view flat at the root with no items map at all.
        var reading = UniversalisV2.ReadListings(
            IngestFixture.OneWareListings(), [FireShard], IngestFixture.Instant);

        var snapshot = Assert.Single(reading.Snapshots);

        Assert.Equal(FireShard, snapshot.Ware);
        Assert.Equal(IngestFixture.World, snapshot.World);
        Assert.Equal(IngestFixture.Instant, snapshot.ObservedAt);
        Assert.Equal(Source.Aggregator, snapshot.Source);

        // Freshness comes from when the Source last learned anything, not from when EMM asked.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_787_123_662_257), snapshot.UploadedAt);

        Assert.Equal([43L, 44L, 45L], snapshot.Listings.Select(l => l.UnitPrice.Gil));
        Assert.Equal([1_000, 1_013, 1_000], snapshot.Listings.Select(l => l.Stack));
        Assert.Equal("Alderleaf", snapshot.Listings[0].Retainer);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_787_123_658), snapshot.Listings[0].LastReviewedAt);
    }

    [Fact]
    public void OneWaresSalesAreReadOutOfTheSingleItemShapeAndCarryTheirStacks()
    {
        var reading = UniversalisV2.ReadHistory(IngestFixture.OneWareHistory(), [FireShard]);

        Assert.Equal(4, reading.Sales.Count);
        Assert.All(reading.Sales, sale => Assert.Equal(FireShard, sale.Ware));
        Assert.All(reading.Sales, sale => Assert.Equal(Source.Aggregator, sale.Source));

        // Four rows, 2,300 units. The distinction is not decoration: velocity is measured in units
        // and a row-weighted reading of this response would under-count it by a factor of 575.
        Assert.Equal(2_300, reading.Sales.Sum(sale => sale.Stack));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_787_100_057), reading.Sales[0].SoldAt);
        Assert.Equal(40L, reading.Sales[0].UnitPrice.Gil);
    }

    [Fact]
    public void OneItemsTwoWaresAreSplitApartBecauseAPriceAttachesToAWare()
    {
        // Both arrive in one response and they are two different things to price. Item 1602 came
        // back with two NQ Listings and one HQ.
        var reading = UniversalisV2.ReadListings(
            IngestFixture.Listings(),
            [new WareId(1602, Quality.Normal), new WareId(1602, Quality.High)],
            IngestFixture.Instant);

        var nq = SnapshotOf(reading, 1602, Quality.Normal);
        var hq = SnapshotOf(reading, 1602, Quality.High);

        Assert.Equal([4_999L, 1_000_000L], nq.Listings.Select(l => l.UnitPrice.Gil));
        Assert.Equal([25_000L], hq.Listings.Select(l => l.UnitPrice.Gil));
    }

    [Fact]
    public void AQualityWithNothingListedOnAnItemThatHasDataIsARealObservation()
    {
        // Item 1603 has two HQ Listings and no NQ ones. "Nobody is selling the NQ here" is a fact
        // about the board, not a gap - and it is the ordinary case for a quarter of the catalogue.
        var reading = UniversalisV2.ReadListings(
            IngestFixture.Listings(), [new WareId(1603, Quality.Normal)], IngestFixture.Instant);

        var nq = SnapshotOf(reading, 1603, Quality.Normal);

        Assert.Empty(nq.Listings);
        Assert.NotNull(nq.UploadedAt);
        Assert.Empty(reading.WithoutData);
    }

    [Fact]
    public void AWareTheSourceHoldsNothingForIsNotStoredAsAnEmptyBoard()
    {
        // THE FINDING THIS TICKET PAID FOR. An Item with nothing listed and an Item nobody has
        // ever uploaded come back identically - hasData false, lastUploadTime zero, empty listings
        // - and across 2,000 cached records the two fields were perfectly collinear, so there is
        // no third field to tell them apart with.
        //
        // Writing an empty Snapshot here would put "EMM looked and the board was bare" in the
        // store on the Source's behalf, where Days of Supply and every Undercut downstream would
        // read it as observed. So it is reported, not stored.
        var requested = new WareId(IngestFixture.NoDataItem, Quality.Normal);

        var reading = UniversalisV2.ReadListings(
            IngestFixture.Listings(), [requested], IngestFixture.Instant);

        Assert.Equal([requested], reading.WithoutData);
        Assert.DoesNotContain(reading.Snapshots, snapshot => snapshot.Ware == requested);
    }

    [Fact]
    public void OnAHistoryResponseTheUploadTimeIsTheOnlySignalThereIs()
    {
        // History responses carry no hasData field at all - checked against real ones - so the
        // "nothing here" test on that endpoint rests entirely on the upload time being zero. Left
        // untested, the two guards look like belt and braces while one of them is the only belt.
        var reading = UniversalisV2.ReadHistory(
            """{"itemID":5,"worldID":79,"lastUploadTime":0,"entries":[]}""",
            [FireShard]);

        Assert.Equal([FireShard], reading.WithoutData);
        Assert.Empty(reading.Sales);
    }

    [Fact]
    public void AListingsResponseWithNoUploadTimeIsNothingHeldEvenWhereHasDataIsAbsent()
    {
        var reading = UniversalisV2.ReadListings(
            """{"itemID":5,"worldID":79,"lastUploadTime":0,"listings":[]}""",
            [FireShard],
            IngestFixture.Instant);

        Assert.Equal([FireShard], reading.WithoutData);
        Assert.Empty(reading.Snapshots);
    }

    [Fact]
    public void TheOtherQualityIsKeptWhenTheResponseCarriesItAnyway()
    {
        // It arrived in a response already paid for. Throwing it away would mean fetching it again
        // later at full price.
        var reading = UniversalisV2.ReadListings(
            IngestFixture.Listings(), [new WareId(1602, Quality.Normal)], IngestFixture.Instant);

        Assert.Contains(reading.Snapshots, s => s.Ware == new WareId(1602, Quality.High));
        Assert.Contains(reading.Snapshots, s => s.Ware == new WareId(1604, Quality.High));
    }

    [Fact]
    public void SalesOfBothQualitiesAreSeparatedByWare()
    {
        var reading = UniversalisV2.ReadHistory(
            IngestFixture.History(), [new WareId(1602, Quality.Normal)]);

        // Item 1602's recorded history is 28 Sales, six of them HQ.
        Assert.Equal(22, reading.Sales.Count(s => s.Ware == new WareId(1602, Quality.Normal)));
        Assert.Equal(6, reading.Sales.Count(s => s.Ware == new WareId(1602, Quality.High)));
        Assert.Equal(22, reading.Sales.Count(s => s.Ware == new WareId(1601, Quality.Normal)));
    }

    [Fact]
    public void ItemIdsTheSourceDidNotRecogniseAreReportedRatherThanDropped()
    {
        var reading = UniversalisV2.ReadListings(
            """
            {"itemIDs":[5,999999],"items":{},"worldID":79,"unresolvedItems":[999999]}
            """,
            [FireShard],
            IngestFixture.Instant);

        Assert.Equal([999_999u], reading.Unresolved);
    }

    [Fact]
    public void ARowThatCannotBeBelievedIsCountedRatherThanQuietlySkipped()
    {
        // A Listing of zero units is not a smaller Listing, it is one that does not exist. Dropping
        // it silently would turn a Source defect into a thin Sample with no explanation.
        var reading = UniversalisV2.ReadListings(
            """
            {"itemID":5,"worldID":79,"hasData":true,"lastUploadTime":1787123662257,
             "listings":[{"hq":false,"pricePerUnit":43,"quantity":0,"retainerName":"Alderleaf"},
                         {"hq":false,"pricePerUnit":44,"quantity":7,"retainerName":"Nettlewick"}]}
            """,
            [FireShard],
            IngestFixture.Instant);

        Assert.Equal(1, reading.DiscardedRows);
        Assert.Equal([44L], Assert.Single(reading.Snapshots).Listings.Select(l => l.UnitPrice.Gil));
    }

    [Fact]
    public void ASaleAtNoMomentIsDiscardedRatherThanFiledIn1970()
    {
        var reading = UniversalisV2.ReadHistory(
            """
            {"itemID":5,"worldID":79,"lastUploadTime":1787123662245,
             "entries":[{"hq":false,"pricePerUnit":40,"quantity":800,"timestamp":0},
                        {"hq":false,"pricePerUnit":41,"quantity":10,"timestamp":1787100057}]}
            """,
            [FireShard]);

        Assert.Equal(1, reading.DiscardedRows);
        Assert.Equal(10, Assert.Single(reading.Sales).Stack);
    }

    [Fact]
    public void ABodyThatIsNeitherShapeIsRefusedRatherThanReadAsAnEmptySuccess()
    {
        // The failure that matters: a body EMM cannot recognise must not look like "the Source has
        // nothing", because that is indistinguishable from a real answer once it is in the store.
        Assert.Throws<JsonException>(() => UniversalisV2.ReadListings(
            """{"error":"something else entirely"}""", [FireShard], IngestFixture.Instant));
    }

    private static Snapshot SnapshotOf(ListingsReading reading, uint itemId, Quality quality) =>
        Assert.Single(reading.Snapshots, s => s.Ware == new WareId(itemId, quality));
}

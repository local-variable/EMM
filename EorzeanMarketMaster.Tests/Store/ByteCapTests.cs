using System.Diagnostics;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Store;
using Xunit;

namespace EorzeanMarketMaster.Tests.Store;

/// <summary>
/// The measurement the whole cap design rests on, re-run as a test rather than cited as a number.
///
/// Deleting half of two million rows was measured leaving the database file byte-identical: freed
/// pages go to the freelist and SQLite never returns them to the file system on its own. So a
/// "do not exceed X GB" rule implemented as a DELETE would never once reduce the file, and would
/// keep deleting, and would keep failing, and would destroy data doing it.
///
/// This is also the only test in the suite that is deliberately slow. It is at two million rows
/// because that is the scale the finding was made at, and a scaled-down version would pass just as
/// happily against an implementation that had quietly started calling VACUUM per delete.
/// </summary>
public class ByteCapTests(ITestOutputHelper output)
{
    private const int Rows = 2_000_000;

    /// <summary>Days the Sales are spread over. Divides Rows exactly, so the count is the count.</summary>
    private const int Days = 25;

    [Fact]
    public void RemovingRowsDoesNotShrinkTheFileAndTheIncrementalVacuumIsWhatEnforcesTheCap()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var now = StoreFixture.Instant;
        var written = Stopwatch.StartNew();

        // Every Sale sits well outside the 60-day horizon, so one pass of the ladder folds and
        // removes all of them.
        store.Write(SalesEndingAt(now.AddDays(-120)));
        written.Stop();

        var report = store.Evict(EvictionPolicy.Default with { ByteCap = 1 }, now);

        output.WriteLine(
            $"{Rows:N0} rows written in {written.Elapsed.TotalSeconds:F1}s; " +
            $"before {report.BytesBefore:N0} B, after removal {report.BytesAfterRemoval:N0} B, " +
            $"after reclaim {report.BytesAfterReclaim:N0} B " +
            $"({report.BytesReclaimed * 100.0 / report.BytesAfterRemoval:F1}% returned)");

        Assert.Equal(Rows, report.SaleRowsFolded);

        // The finding. Removing two million rows did not give a single byte back. Asserted as
        // "did not shrink" rather than as byte-equality because the ladder folds before it
        // removes, and a rollup write is entitled to take a page - though in practice it does not,
        // and the figures logged above have come back identical to the byte.
        Assert.True(
            report.BytesAfterRemoval >= report.BytesBefore,
            $"removing {Rows:N0} rows shrank the file from {report.BytesBefore:N0} to " +
            $"{report.BytesAfterRemoval:N0} bytes, which SQLite does not do - if this is now true, the " +
            "reason this store enforces its cap by vacuum rather than by DELETE has gone away.");

        // And the consequence: the vacuum is what actually enforces a byte cap.
        Assert.True(
            report.BytesReclaimed > 0,
            $"the incremental vacuum returned nothing: {report.BytesAfterRemoval:N0} bytes before, " +
            $"{report.BytesAfterReclaim:N0} after. A cap that cannot reclaim is not a cap.");

        // Half the file, which is the order of magnitude the design was costed against - a
        // vacuum that freed a page or two would satisfy "greater than zero" while proving that
        // the pragma had not been stepped to completion.
        Assert.True(
            report.BytesReclaimed > report.BytesAfterRemoval / 2,
            $"only {report.BytesReclaimed:N0} of {report.BytesAfterRemoval:N0} bytes came back, well under " +
            "the ~52% a full reclaim returns. The incremental vacuum emits one row per page freed, so a " +
            "driver that stopped stepping it early would look exactly like this.");
    }

    /// <summary>
    /// Two million Sales of one Ware, in primary-key order.
    ///
    /// Streamed rather than listed: two million records held at once to feed one insert loop would
    /// cost more memory than the database they end up in. Generated in key order because
    /// time-clustered inserts were measured at 106,876 rows/s against 62,855 scattered, and this
    /// test is slow enough already.
    /// </summary>
    private static IEnumerable<MarketSale> SalesEndingAt(DateTimeOffset last)
    {
        var perDay = Rows / Days;
        var firstDay = last.AddDays(-Days);

        for (var day = 0; day < Days; day++)
        {
            var start = firstDay.AddDays(day);

            for (var i = 0; i < perDay; i++)
            {
                yield return new MarketSale(
                    StoreFixture.Ware,
                    StoreFixture.World,
                    start.AddSeconds(i),
                    new UnitPrice(100 + (i % 9_973)),
                    1 + (i % 7),
                    Source.Aggregator);
            }
        }
    }
}

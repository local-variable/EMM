using System.Text;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Ingest;

namespace EorzeanMarketMaster.Tests.Ingest;

/// <summary>
/// What the ingest tests need: recorded responses, and doubles for the only two things below the
/// seam that reach outside the process.
///
/// The recorded responses are real. They were fetched from the aggregator on 2026-08-17 into the
/// cached corpus, and the slices here keep every structural quirk that came with them - the empty
/// <c>hq</c> blocks, the null ids, the no-data records, the field order. What was changed is every
/// retainer, creator and buyer name and every opaque id, replaced with invented ones: the shape is
/// what the fixture is for, the names are other players', and this repository is public.
/// </summary>
internal static class IngestFixture
{
    /// <summary>Cactuar, the World the corpus was collected on.</summary>
    internal static readonly WorldId World = new(79);

    /// <summary>A fixed instant, so nothing under test ever reads a clock.</summary>
    internal static readonly DateTimeOffset Instant = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An Item whose recorded response carries both Qualities.</summary>
    internal const uint MixedQualityItem = 1602;

    /// <summary>An Item the aggregator holds nothing for: hasData false, lastUploadTime zero.</summary>
    internal const uint NoDataItem = 1605;

    /// <summary>The recorded listings response, covering Items 1602, 1604 and 1605.</summary>
    internal static string Listings() => Read("listings-cactuar.json");

    /// <summary>The recorded history response, covering Items 1601 and 1602.</summary>
    internal static string History() => Read("history-cactuar.json");

    /// <summary>
    /// A recorded listings response for a single Item, which comes back in a different shape: the
    /// view flat at the root, with no <c>items</c> map. That is the shape a point refresh of one
    /// Ware always gets, so it is the ordinary case rather than an edge one - and the cached corpus
    /// was collected in batches of a hundred and holds no example of it, which is why this one was
    /// fetched on 2026-08-19 specifically to record the shape.
    /// </summary>
    internal static string OneWareListings() => Read("listings-one-ware.json");

    /// <summary>The same, for history. Four Sales carrying stacks of 800, 999, 500 and 1.</summary>
    internal static string OneWareHistory() => Read("history-one-ware.json");

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ingest", "Fixtures", name));
}

/// <summary>
/// A transport that answers from a table and remembers what it was asked.
///
/// It also counts how many requests are in flight at once. That figure is the connection cap made
/// measurable: the ingest issues its requests one at a time, and the only way to know that is
/// still true after somebody edits it is to watch from the other side of the seam.
/// </summary>
internal sealed class RecordingTransport : IAggregatorTransport
{
    private readonly Func<Uri, TransportResult> answer;
    private int inFlight;

    internal RecordingTransport(Func<Uri, TransportResult> answer) => this.answer = answer;

    /// <summary>A transport with no network behind it at all. A new one each time: xunit runs
    /// classes in parallel and a shared recorder would report another test's requests.</summary>
    /// <returns>The transport.</returns>
    internal static RecordingTransport Offline() =>
        new(_ => TransportResult.Failed("no such host is known"));

    /// <summary>Every address asked for, in order.</summary>
    internal List<Uri> Asked { get; } = [];

    /// <summary>The most requests that were ever in flight at the same time.</summary>
    internal int MaxConcurrent { get; private set; }

    /// <summary>A transport that returns one body for every address.</summary>
    /// <param name="body">The body to return.</param>
    /// <returns>The transport.</returns>
    internal static RecordingTransport Returning(string body) =>
        new(_ => TransportResult.Ok(body, Encoding.UTF8.GetByteCount(body)));

    /// <summary>A transport that answers listings and history requests differently.</summary>
    /// <param name="listings">The listings body.</param>
    /// <param name="history">The history body.</param>
    /// <returns>The transport.</returns>
    internal static RecordingTransport Returning(string listings, string history) =>
        new(address =>
        {
            var body = address.AbsolutePath.Contains("/history/", StringComparison.Ordinal) ? history : listings;

            return TransportResult.Ok(body, Encoding.UTF8.GetByteCount(body));
        });

    /// <inheritdoc/>
    public async Task<TransportResult> Get(Uri address, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref inFlight);

        try
        {
            MaxConcurrent = Math.Max(MaxConcurrent, current);
            Asked.Add(address);

            // A real suspension point. Without one a caller that fired every request without
            // awaiting would still be observed running them one at a time, and the concurrency
            // figure above would be measuring nothing.
            await Task.Yield();

            return answer(address);
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }
}

/// <summary>
/// Pacing that records what it was asked to wait for and returns at once.
///
/// This is what makes the sustained rate assertable without a test that takes as long as the
/// sweep it is checking.
/// </summary>
internal sealed class RecordingPacing : IPacing
{
    /// <summary>Every wait asked for, in order.</summary>
    internal List<TimeSpan> Waits { get; } = [];

    /// <summary>The total time the ingest asked to spend waiting.</summary>
    internal TimeSpan Total => Waits.Aggregate(TimeSpan.Zero, (sum, wait) => sum + wait);

    /// <inheritdoc/>
    public Task Wait(TimeSpan duration, CancellationToken cancellationToken)
    {
        Waits.Add(duration);

        return Task.CompletedTask;
    }
}

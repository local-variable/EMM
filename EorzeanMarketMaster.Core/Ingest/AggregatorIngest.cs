using EorzeanMarketMaster.Core.Store;

namespace EorzeanMarketMaster.Core.Ingest;

/// <summary>
/// What came back from one request.
/// </summary>
/// <param name="Body">The response body, or null where the request did not succeed.</param>
/// <param name="Bytes">
/// What it weighed on the wire. Counted by whoever read it rather than taken from the string's
/// length: the body is decoded text and its character count is not its byte count, and the figure
/// this feeds is the one a Player is shown as the cost of a refresh.
/// </param>
/// <param name="Failure">
/// Why it did not, for the log. Null on success. A diagnostic rather than anything a Player reads
/// - the surface wraps its own sentence around it.
/// </param>
public sealed record TransportResult(string? Body, long Bytes, string? Failure)
{
    /// <summary>A response.</summary>
    /// <param name="body">The body.</param>
    /// <param name="bytes">What it weighed.</param>
    /// <returns>A successful result.</returns>
    public static TransportResult Ok(string body, long bytes) =>
        new(Guard.NotNull(body, nameof(body)), bytes, null);

    /// <summary>No response.</summary>
    /// <param name="reason">What went wrong.</param>
    /// <returns>A failed result.</returns>
    public static TransportResult Failed(string reason) =>
        new(null, 0, Guard.NotBlank(reason, nameof(reason), "A failure has to say what failed."));

    /// <summary>Whether there is a body to read.</summary>
    public bool Succeeded => Body is not null;
}

/// <summary>
/// The one thing below this seam that touches a network. Implemented on the plugin side, doubled
/// in the suite, and deliberately given nothing to decide: it is handed an address and returns
/// text or a reason.
/// </summary>
public interface IAggregatorTransport
{
    /// <summary>Fetches one address.</summary>
    /// <param name="address">Where to ask.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The body, or why there is none.</returns>
    Task<TransportResult> Get(Uri address, CancellationToken cancellationToken);
}

/// <summary>
/// The one thing below this seam that lets time pass.
///
/// A port rather than a call to <c>Task.Delay</c> so that pacing stays enforced here, in the layer
/// accountable for it, while a test can still watch it happen without waiting. The suite's double
/// records what it was asked to wait for and returns at once, which is how "the sustained rate was
/// honoured" becomes an assertion over a measurement rather than over the shape of the code.
/// </summary>
public interface IPacing
{
    /// <summary>Waits.</summary>
    /// <param name="duration">How long.</param>
    /// <param name="cancellationToken">Cuts the wait short.</param>
    /// <returns>A task that completes when the wait is over.</returns>
    Task Wait(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>
/// What one request did.
/// </summary>
/// <param name="Batch">The request.</param>
/// <param name="Failure">Why it failed, or null if it did not.</param>
/// <param name="Bytes">How many bytes came back. Zero on failure.</param>
public sealed record BatchOutcome(FetchBatch Batch, string? Failure, long Bytes)
{
    /// <summary>Whether the request succeeded.</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>
/// What a refresh did, in the same currency the plan quoted it in.
/// </summary>
/// <param name="Plan">What was planned.</param>
/// <param name="Verdict">What the gate said, including the cost that was quoted first.</param>
/// <param name="Batches">One entry per request attempted. Empty where the gate held the refresh back.</param>
/// <param name="SnapshotsWritten">Snapshots stored.</param>
/// <param name="SalesWritten">Market Sales the store did not already hold.</param>
/// <param name="WithoutData">Wares the Source holds nothing for. Not stored as empty boards.</param>
/// <param name="Unresolved">Item ids the Source did not recognise.</param>
/// <param name="DiscardedRows">Rows the parser refused to believe.</param>
public sealed record IngestReport(
    FetchPlan Plan,
    RefreshVerdict Verdict,
    IReadOnlyList<BatchOutcome> Batches,
    int SnapshotsWritten,
    int SalesWritten,
    IReadOnlyList<WareId> WithoutData,
    IReadOnlyList<uint> Unresolved,
    int DiscardedRows)
{
    /// <summary>The requests attempted.</summary>
    public IReadOnlyList<BatchOutcome> Batches { get; } = Guard.CopyOf(Batches, nameof(Batches));

    /// <summary>Whether anything was sent at all.</summary>
    public bool Ran => Batches.Count > 0;

    /// <summary>Requests that came back.</summary>
    public int RequestsMade => Batches.Count(batch => batch.Succeeded);

    /// <summary>Requests that did not.</summary>
    public int RequestsFailed => Batches.Count(batch => !batch.Succeeded);

    /// <summary>
    /// Bytes actually received, against the <see cref="RefreshCost.EstimatedBytes"/> the plan
    /// quoted. Reported so that the estimate can be checked rather than merely believed - an
    /// estimate nobody ever compares to an outcome is a number with no way of being wrong.
    /// </summary>
    public long BytesReceived => Batches.Sum(batch => batch.Bytes);
}

/// <summary>
/// Fetches a planned refresh, stores what comes back, and reports what it cost.
///
/// <b>Requests go one at a time.</b> Not because two would be unsafe - the ceiling allows two -
/// but because at one request per second there is nothing for a second connection to do, and a
/// sequential loop is a thing whose concurrency a test can measure. The suite asserts the
/// transport never sees two requests in flight; the plugin's HTTP handler is capped at
/// <see cref="Citizenship.MaxConnections"/> as well, so the ceiling holds even for a caller that
/// is not this one.
///
/// <b>A failed request does not abandon the sweep.</b> One Ware's response failing tells you
/// nothing about the next Ware's, and a partial refresh that says which parts are missing is worth
/// more than none. With no network at all every request fails, nothing is written, and the report
/// says so - which is the whole of EMM's offline behaviour on the write side. The read side needs
/// no offline mode because it never had an online one: see <see cref="StoredMarket"/>.
/// </summary>
public static class AggregatorIngest
{
    /// <summary>
    /// Runs a planned refresh.
    /// </summary>
    /// <param name="plan">What to fetch.</param>
    /// <param name="gate">The sweep floor. Consulted first and told when a sweep begins.</param>
    /// <param name="transport">Where requests go.</param>
    /// <param name="pacing">What holds the rate down.</param>
    /// <param name="store">Where observations land.</param>
    /// <param name="now">The instant the refresh is taken to happen at.</param>
    /// <param name="cancellationToken">Stops the refresh between requests.</param>
    /// <returns>What it did, and what it cost.</returns>
    public static async Task<IngestReport> Run(
        FetchPlan plan,
        SweepGate gate,
        IAggregatorTransport transport,
        IPacing pacing,
        MarketStore store,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(pacing);
        ArgumentNullException.ThrowIfNull(store);

        var verdict = gate.Assess(plan, now);

        if (!verdict.MayStartNow)
        {
            // Queued, not refused. Nothing is sent and the caller is handed the countdown and the
            // cost, which is what a Player is owed before a ring-wide refresh rather than after.
            return new IngestReport(plan, verdict, [], 0, 0, [], [], 0);
        }

        gate.Started(plan, now);

        var outcomes = new List<BatchOutcome>();
        var withoutData = new List<WareId>();
        var unresolved = new List<uint>();
        var snapshots = 0;
        var sales = 0;
        var discarded = 0;

        for (var index = 0; index < plan.Batches.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The first request goes at once; every one after it waits out the interval. Pacing a
            // single point refresh would spend a second of the Player's time achieving nothing.
            if (index > 0)
            {
                await pacing.Wait(Citizenship.MinimumInterval, cancellationToken).ConfigureAwait(false);
            }

            var batch = plan.Batches[index];
            var result = await transport.Get(batch.Address, cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                outcomes.Add(new BatchOutcome(batch, result.Failure ?? "the request did not complete", 0));
                continue;
            }

            var body = result.Body!;
            IReadOnlyList<Snapshot> parsedSnapshots;
            IReadOnlyList<MarketSale> parsedSales;

            // Parsing is what is allowed to fail here, and it is fenced off from the writing on
            // purpose. A response EMM cannot read is a failed request and the sweep carries on;
            // a store that will not accept a row is a different kind of wrong, and swallowing it
            // under the same message would report a broken database as a malformed response.
            try
            {
                if (batch.Endpoint == AggregatorEndpoint.Listings)
                {
                    var reading = UniversalisV2.ReadListings(body, batch.Wares, now);

                    parsedSnapshots = reading.Snapshots;
                    parsedSales = [];
                    withoutData.AddRange(reading.WithoutData);
                    unresolved.AddRange(reading.Unresolved);
                    discarded += reading.DiscardedRows;
                }
                else
                {
                    var reading = UniversalisV2.ReadHistory(body, batch.Wares);

                    parsedSnapshots = [];
                    parsedSales = reading.Sales;
                    withoutData.AddRange(reading.WithoutData);
                    unresolved.AddRange(reading.Unresolved);
                    discarded += reading.DiscardedRows;
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or ArgumentException)
            {
                // The body arrived, so the bytes were spent and are counted; nothing from it was
                // stored.
                outcomes.Add(new BatchOutcome(batch, $"the response could not be read: {ex.Message}", result.Bytes));
                continue;
            }

            foreach (var snapshot in parsedSnapshots)
            {
                store.Write(snapshot);
                snapshots++;
            }

            sales += store.Write(parsedSales);

            outcomes.Add(new BatchOutcome(batch, null, result.Bytes));
        }

        return new IngestReport(
            plan,
            verdict,
            outcomes,
            snapshots,
            sales,
            [.. withoutData.Distinct()],
            [.. unresolved.Distinct()],
            discarded);
    }
}

namespace EorzeanMarketMaster.Core.Ingest;

/// <summary>
/// What EMM will not do to the service it reads.
///
/// A static class with constants rather than a record with a <c>Default</c>, and that is the whole
/// design. The safety posture put these under the heading "ceilings configuration cannot remove",
/// and the cheapest way to make that true is to leave nowhere for a setting to live: there is no
/// instance to construct, no property to assign, and no constructor a later ticket can reach for
/// when a user asks to go faster. A number that can be raised is a setting whatever the
/// documentation calls it.
///
/// Every figure here is either published by the service or measured. None of them is a guess:
///
/// <list type="bullet">
/// <item><description>
/// <b>25 req/s, 50 burst, 8 connections per IP</b> is what the aggregator publishes. EMM takes
/// one request per second and two connections, which is 4% of the rate and a quarter of the
/// connections. That is not timidity - a full-catalogue sweep of every marketable item was
/// measured at 169 requests, so even hourly it comes to 0.38% of the published ceiling. The rate
/// limit was never the binding constraint; payload and storage were.
/// </description></item>
/// <item><description>
/// <b>The per-IP part is why the margin is this wide.</b> These limits are counted per address,
/// and a household or a shared connection can host several Players running this plugin. EMM
/// budgets as though it is one of several rather than the only one.
/// </description></item>
/// <item><description>
/// <b>The fifteen-minute sweep floor</b> comes from the safety posture and applies to sweeps
/// alone. A point lookup of one Ware is not a sweep and was ruled immediate; see
/// <see cref="SweepGate"/>, which is also where that distinction is made unbypassable.
/// </description></item>
/// </list>
/// </summary>
public static class Citizenship
{
    /// <summary>
    /// The sustained rate the aggregator publishes, in requests per second. Here to be divided
    /// into <see cref="SustainedRequestsPerSecond"/> and shown, never to be approached.
    /// </summary>
    public const double PublishedRequestsPerSecond = 25.0;

    /// <summary>The burst the aggregator publishes, in requests per second.</summary>
    public const double PublishedBurstRequestsPerSecond = 50.0;

    /// <summary>The simultaneous connections per IP the aggregator publishes.</summary>
    public const int PublishedMaxConnections = 8;

    /// <summary>
    /// What EMM allows itself, in requests per second. One, sustained, with no burst allowance -
    /// EMM never spends a burst budget it might be sharing with another Player behind the same
    /// address.
    /// </summary>
    public const double SustainedRequestsPerSecond = 1.0;

    /// <summary>
    /// The most connections EMM will hold open. Two is the ceiling; the ingest actually issues its
    /// requests one at a time, so one is what is ever observed - see <see cref="AggregatorIngest"/>.
    /// </summary>
    public const int MaxConnections = 2;

    /// <summary>The shortest gap EMM leaves between two requests.</summary>
    public static TimeSpan MinimumInterval { get; } =
        TimeSpan.FromSeconds(1.0 / SustainedRequestsPerSecond);

    /// <summary>
    /// The shortest gap between two sweeps. Not between two requests, and not between two point
    /// refreshes: this bounds how often EMM may walk a whole population.
    /// </summary>
    public static TimeSpan SweepFloor { get; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// EMM's sustained rate as a fraction of the published one. 0.04 today, and computed rather
    /// than written down so that raising one of the two constants cannot leave the displayed
    /// fraction claiming something that stopped being true.
    /// </summary>
    public static double ShareOfPublishedRate => SustainedRequestsPerSecond / PublishedRequestsPerSecond;

    /// <summary>
    /// EMM's connection cap as a fraction of the published one. 0.25, computed for the same reason.
    /// </summary>
    public static double ShareOfPublishedConnections => (double)MaxConnections / PublishedMaxConnections;
}

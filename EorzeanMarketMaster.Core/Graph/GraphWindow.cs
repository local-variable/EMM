namespace EorzeanMarketMaster.Core.Graph;

/// <summary>
/// How much of History the graph is showing: one of the offered presets, or everything EMM holds.
///
/// A type rather than a bare number of days because "all of History" is not a number of days and
/// writing it as one - zero, or int.MaxValue, or 3650 - is how a sentinel ends up being arithmetic
/// somewhere. Here the absence is the absence: <see cref="Days"/> is null and <see cref="From"/>
/// hands back nothing to clip against.
/// </summary>
public readonly record struct GraphWindow
{
    private GraphWindow(int? days) => Days = days;

    /// <summary>
    /// The window that opens when nobody has chosen one.
    ///
    /// Thirty days, and it is a placeholder with a stated end date rather than a preference. The
    /// graph decision fixed the default as <i>the window the Estimate was priced from</i>, so that
    /// the picture and the number cannot disagree - but there is no Estimate yet (#31 mints it and
    /// #32 gives ware types their windows), so there is nothing to follow. Thirty days is the
    /// window that decision named for most ware types. <b>The surface states which window is open
    /// rather than leaving the reader to infer it</b>, which is the part that has to be true now:
    /// when the Estimate arrives this default follows it, and a reader who was never told what
    /// they were looking at would not notice the change.
    /// </summary>
    public static GraphWindow Default => OfDays(30);

    /// <summary>Everything EMM has stored. Bounded by the store, not by a span.</summary>
    public static GraphWindow All { get; } = new(null);

    /// <summary>
    /// The presets offered, shortest first, ending in <see cref="All"/>.
    ///
    /// From a week to all of History, which is the range the ticket asks for. Seven, fourteen,
    /// thirty, sixty, ninety and a hundred and eighty days are the graph decision's own list.
    /// </summary>
    public static IReadOnlyList<GraphWindow> Presets { get; } =
        [OfDays(7), OfDays(14), OfDays(30), OfDays(60), OfDays(90), OfDays(180), All];

    /// <summary>How many days the window covers, or null where it covers everything held.</summary>
    public int? Days { get; }

    /// <summary>How long the window is, or null where it covers everything held.</summary>
    public TimeSpan? Span => Days is { } days ? TimeSpan.FromDays(days) : null;

    /// <summary>A window of a fixed number of days.</summary>
    /// <param name="days">How many. At least one.</param>
    /// <returns>The window.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is not positive.</exception>
    public static GraphWindow OfDays(int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);

        return new GraphWindow(days);
    }

    /// <summary>
    /// Where the window starts, given the moment it ends at.
    /// </summary>
    /// <param name="toExclusive">The moment the window runs up to.</param>
    /// <param name="earliestHeld">
    /// The oldest Sale EMM holds, used only by <see cref="All"/>. Null where EMM holds none, which
    /// leaves an "all of History" window with nothing to span and no reason to pretend otherwise.
    /// </param>
    /// <returns>The window's inclusive lower bound.</returns>
    public DateTimeOffset From(DateTimeOffset toExclusive, DateTimeOffset? earliestHeld) =>
        Span is { } span
            ? toExclusive - span
            : earliestHeld ?? toExclusive - TimeSpan.FromDays(Default.Days!.Value);
}

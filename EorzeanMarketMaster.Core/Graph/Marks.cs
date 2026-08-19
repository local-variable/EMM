namespace EorzeanMarketMaster.Core.Graph;

/// <summary>
/// The span of gil the picture has to cover to show everything on it.
///
/// Carried rather than left to the plotter to work out, and that is a correctness fix rather than
/// a cosmetic one. Only the Rollup line goes through a plotting primitive; the Sale dots and the
/// Listing lines are drawn by hand, so an automatic fit sees the slice means and not the Sales
/// they were computed from. A Sale dearer than every slice mean - which is what an outlier IS -
/// would then sit outside the axis and be clipped, and a Sale the picture silently does not draw
/// is the one thing this graph may not do.
/// </summary>
/// <param name="Lowest">The cheapest gil figure drawn anywhere.</param>
/// <param name="Highest">The dearest gil figure drawn anywhere.</param>
public readonly record struct DrawnRange(double Lowest, double Highest)
{
    /// <summary>
    /// The same span with room around it, so a mark at either extreme is not drawn on the axis.
    ///
    /// A range of one price gets its padding from the price rather than from the span, because a
    /// Ware that traded at exactly 300 gil all month has a span of zero and would otherwise be
    /// drawn as a line with no room at all.
    /// </summary>
    /// <param name="fraction">How much room, as a share of the span.</param>
    /// <returns>The padded range.</returns>
    public DrawnRange Padded(double fraction)
    {
        var span = Highest - Lowest;
        var room = span > 0 ? span * fraction : Math.Max(Math.Abs(Highest) * fraction, 1);

        // Never below zero. Gil does not go negative, and an axis that starts at -40 says it might.
        return new DrawnRange(Math.Max(Lowest - room, 0), Highest + room);
    }

    /// <summary>The range covering both this and another.</summary>
    /// <param name="other">The other range.</param>
    /// <returns>The union.</returns>
    public DrawnRange Covering(DrawnRange other) =>
        new(Math.Min(Lowest, other.Lowest), Math.Max(Highest, other.Highest));

    /// <summary>The range spanning a set of gil figures, or null where there are none.</summary>
    /// <param name="values">The figures.</param>
    /// <returns>The range, or null.</returns>
    public static DrawnRange? Over(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        DrawnRange? range = null;

        foreach (var value in values)
        {
            var one = new DrawnRange(value, value);

            range = range is { } seen ? seen.Covering(one) : one;
        }

        return range;
    }
}

/// <summary>
/// One Sale, drawn: a dot at the moment it happened, at the price it happened at, sized by the
/// Stack that changed hands.
///
/// A dot per Sale and never a smoothed line, because three Sales in a week must look like three
/// Sales in a week. Nothing here aggregates, so nothing here can make thin data look thick.
/// </summary>
/// <param name="At">When it sold, in seconds since the Unix epoch.</param>
/// <param name="UnitPrice">Gil for one unit.</param>
/// <param name="Stack">Units that changed hands.</param>
public readonly record struct SaleMark(double At, double UnitPrice, int Stack)
{
    /// <summary>Units that changed hands. At least one - the same rule the Sale itself carries.</summary>
    public int Stack { get; } =
        Guard.Positive(Stack, nameof(Stack), "A Sale moves at least one unit, so its dot has a size.");

    /// <summary>The smallest dot drawn, in pixels. A one-unit Sale.</summary>
    public const float SmallestRadius = 2.5f;

    /// <summary>The largest dot drawn, in pixels. Reached at a Stack of sixteen and held there.</summary>
    public const float LargestRadius = 10f;

    /// <summary>
    /// How big the dot is, in pixels.
    ///
    /// <b>Radius grows with the square root of the Stack, so it is the dot's <i>area</i> that
    /// tracks the units.</b> Radius proportional to Stack would draw a 99-unit Sale with ten
    /// thousand times the ink of a single unit, which reads as a hundred times the trade it was.
    /// Sub-linear growth is the standard fix and is why the mapping lives here rather than in a
    /// renderer: it is a claim about what the picture says, and a claim is testable.
    ///
    /// Absolute rather than scaled to whatever is on screen. A dot that changed size when the time
    /// preset changed would be comparing Sales to their neighbours instead of to a fixed unit, and
    /// the same Sale would look busier on a quiet week than on a busy one.
    ///
    /// Clamped at both ends: below the floor a dot is a smudge nobody can see or hover, and above
    /// the ceiling one bulk Sale swallows its neighbours.
    /// </summary>
    public float Radius => Math.Clamp(
        SmallestRadius * MathF.Sqrt(Stack), SmallestRadius, LargestRadius);
}

/// <summary>
/// One Listing, drawn: a line at the price it is asking, running from when it was put up to when
/// EMM last saw it still sitting there.
///
/// <b>Never the same mark as a Sale, and that is the whole reason this is a separate type.</b> A
/// Sale is what a buyer paid; a Listing is what a seller hopes for, and most of them never become
/// the former. Drawn as dots on the same axis the two would read as one population, and every
/// median, band and Undercut a reader took off the picture would be wrong in the direction that
/// costs gil.
/// </summary>
/// <param name="From">When the Listing was put up, in seconds since the Unix epoch.</param>
/// <param name="To">When EMM last observed it on the board, in the same units.</param>
/// <param name="UnitPrice">Gil for one unit.</param>
/// <param name="Stack">Units in the Listing. A Listing is bought whole.</param>
/// <param name="Traced">
/// Whether <paramref name="From"/> is the Listing's own review time or merely the moment EMM
/// observed it.
///
/// Carried rather than smoothed over because the two are different claims. Where the Source
/// reports a review time the line is a statement about how long the Listing has been sitting
/// there; where it does not, the line starts at the observation and says only "it was on the board
/// when EMM looked", which is a much weaker thing and should not be drawn as though it were the
/// stronger one.
/// </param>
public readonly record struct ListingLine(double From, double To, double UnitPrice, int Stack, bool Traced)
{
    /// <summary>Units in the Listing. At least one - the same rule the Listing itself carries.</summary>
    public int Stack { get; } =
        Guard.Positive(Stack, nameof(Stack), "A Listing carries at least one unit.");

    /// <summary>How long the line runs, in seconds. Zero where the Listing was seen exactly once.</summary>
    public double Seconds => Math.Max(To - From, 0);
}

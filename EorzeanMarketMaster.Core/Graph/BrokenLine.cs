namespace EorzeanMarketMaster.Core.Graph;

/// <summary>
/// A line across the time axis with a hole wherever there is nothing to draw.
///
/// <b>This type exists so that "no interpolation across gaps" is a property of the data rather
/// than a habit of the renderer.</b> The Rollups a series produces are only the occupied slices,
/// so handing them straight to a plotter would draw one unbroken segment from the Sale before a
/// quiet fortnight to the Sale after it - a line through fourteen days in which nothing was
/// observed, drawn exactly like a line through fourteen days of steady trade. That is the lie the
/// graph decision named: the model may interpolate, the rendering may not.
///
/// So the line runs over <i>every</i> slice in the window, occupied or not, and an unoccupied one
/// carries <see cref="double.NaN"/>. Every plotting library worth using breaks its line at a NaN,
/// which turns the honesty rule into the default behaviour rather than into something a call site
/// has to remember. A reader who wants the occupied slices alone still has the Rollups.
/// </summary>
public sealed class BrokenLine
{
    private BrokenLine(double[] at, double[] value, int breaks)
    {
        At = at;
        Value = value;
        Breaks = breaks;
    }

    /// <summary>The x of every slice in the window, in seconds since the Unix epoch, oldest first.</summary>
    public IReadOnlyList<double> At { get; }

    /// <summary>The y of every slice, NaN wherever the slice held nothing.</summary>
    public IReadOnlyList<double> Value { get; }

    /// <summary>How many slices held nothing. The size of the hole, stated rather than counted by the reader.</summary>
    public int Breaks { get; }

    /// <summary>How many slices the line spans, holes included.</summary>
    public int Count => At.Count;

    /// <summary>Whether there is anything at all to draw.</summary>
    public bool IsEmpty => Count == 0 || Breaks == Count;

    /// <summary>A line over no slices at all.</summary>
    public static BrokenLine Nothing { get; } = new([], [], 0);

    /// <summary>
    /// Lays Rollups out across every slice of a window.
    ///
    /// The slice grid comes from the width and the window, not from the Rollups, which is what
    /// makes a hole appear at all - a grid derived from the data cannot represent the absence of
    /// data.
    /// </summary>
    /// <param name="rollups">The occupied slices, oldest first.</param>
    /// <param name="width">How wide one slice is.</param>
    /// <param name="from">The window's inclusive lower bound.</param>
    /// <param name="toExclusive">The window's exclusive upper bound.</param>
    /// <returns>The line, holes included.</returns>
    public static BrokenLine Over(
        IReadOnlyList<Rollup> rollups,
        RollupWidth width,
        DateTimeOffset from,
        DateTimeOffset toExclusive)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        if (toExclusive <= from)
        {
            return Nothing;
        }

        var occupied = rollups.ToDictionary(rollup => rollup.Start, rollup => rollup.MeanUnitPrice);

        var at = new List<double>();
        var value = new List<double>();
        var breaks = 0;

        for (var slice = width.StartOf(from); slice < toExclusive; slice += width.Span)
        {
            // The middle of the slice, matching where a Rollup says its mark belongs: a figure
            // covering a week drawn at the moment the week began reads as a week old.
            at.Add((slice + (width.Span / 2)).ToUnixTimeSeconds());

            if (occupied.TryGetValue(slice, out var mean))
            {
                value.Add(mean);
            }
            else
            {
                value.Add(double.NaN);
                breaks++;
            }
        }

        return new BrokenLine([.. at], [.. value], breaks);
    }
}

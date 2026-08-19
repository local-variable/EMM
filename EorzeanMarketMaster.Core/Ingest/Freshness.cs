namespace EorzeanMarketMaster.Core.Ingest;

/// <summary>
/// How a figure's age reads against the World it came from. Never against a constant - see
/// <see cref="WorldFreshness"/>.
/// </summary>
public enum FreshnessGrade
{
    /// <summary>
    /// EMM has not yet seen enough of this World to say. The age is still shown; the judgement is
    /// withheld, because the only way to grade without a calibration is to invent a threshold, and
    /// inventing one is the exact thing this type exists to avoid.
    /// </summary>
    Uncalibrated,

    /// <summary>Younger than this World's own typical upload age.</summary>
    Fresh,

    /// <summary>Older than typical for this World, but inside what it ordinarily reaches.</summary>
    Aging,

    /// <summary>Older than this World's data ordinarily gets.</summary>
    Stale,
}

/// <summary>
/// One World's own idea of what "old" means, built from what EMM has actually observed there.
///
/// <b>Why this is not a number in a settings file.</b> The aggregator's data is uploaded by
/// players opening boards, so its age is a property of how busy a World is, and busy and quiet
/// Worlds differ by two orders of magnitude. Measured over the 200 most-recently-traded Items:
/// median upload age 1.8 hours on a busy World against 130 hours on a quiet one, with the quiet
/// World's 90th percentile past a month and a quarter of those Items never uploaded at all. A
/// single global threshold - any global threshold - is therefore wrong nearly everywhere: set it
/// at a day and a quiet World is permanently red; set it at a week and a busy World's genuinely
/// stale figures read as fine.
///
/// So the threshold is the World's own distribution. A figure is Fresh while it is younger than
/// what this World typically manages, Stale once it is older than this World ordinarily reaches,
/// and Aging in between.
///
/// <b>And where there is not enough to calibrate with, it says so.</b> Below
/// <see cref="MinimumSample"/> observations the grade is <see cref="FreshnessGrade.Uncalibrated"/>
/// and the raw age is all EMM claims. Falling back to a global default in that case would quietly
/// reintroduce the very thing being avoided, on exactly the Worlds EMM knows least about.
/// </summary>
/// <param name="World">The World this describes.</param>
/// <param name="Sample">How many observations it was built from.</param>
/// <param name="Median">This World's median observed upload age.</param>
/// <param name="Ninetieth">This World's 90th-percentile observed upload age.</param>
public sealed record WorldFreshness(WorldId World, int Sample, TimeSpan Median, TimeSpan Ninetieth)
{
    /// <summary>
    /// The fewest observations a calibration is built from.
    ///
    /// Eight, which is a judgement rather than a measurement and is written here so it can be
    /// argued with. It is set where a median and a 90th percentile stop being one observation
    /// wearing two hats, and low enough that a Player who has looked at a handful of Wares once
    /// gets a grade rather than a shrug.
    /// </summary>
    public const int MinimumSample = 8;

    /// <summary>Whether there was enough to say anything.</summary>
    public bool IsCalibrated => Sample >= MinimumSample;

    /// <summary>
    /// Builds a World's calibration from upload ages EMM has observed there - for each stored
    /// observation, how old the Source's data already was when EMM read it.
    /// </summary>
    /// <param name="world">The World.</param>
    /// <param name="observedAges">
    /// The ages. Order does not matter; negatives are dropped, since an upload timestamped after
    /// the moment it was read is a clock disagreement rather than a fresh figure.
    /// </param>
    /// <returns>The calibration, which may be uncalibrated.</returns>
    public static WorldFreshness From(WorldId world, IReadOnlyList<TimeSpan> observedAges)
    {
        ArgumentNullException.ThrowIfNull(observedAges);

        var sorted = observedAges.Where(age => age >= TimeSpan.Zero).OrderBy(age => age).ToList();

        return sorted.Count == 0
            ? new WorldFreshness(world, 0, TimeSpan.Zero, TimeSpan.Zero)
            : new WorldFreshness(world, sorted.Count, Percentile(sorted, 0.50), Percentile(sorted, 0.90));
    }

    /// <summary>
    /// Grades an age against this World.
    /// </summary>
    /// <param name="age">How old the figure is.</param>
    /// <returns>The grade.</returns>
    public FreshnessGrade Grade(TimeSpan age)
    {
        if (!IsCalibrated)
        {
            return FreshnessGrade.Uncalibrated;
        }

        if (age <= Median)
        {
            return FreshnessGrade.Fresh;
        }

        return age <= Ninetieth ? FreshnessGrade.Aging : FreshnessGrade.Stale;
    }

    /// <summary>Nearest-rank, so every reported percentile is an age that was actually observed.</summary>
    private static TimeSpan Percentile(IReadOnlyList<TimeSpan> sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Count);

        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}

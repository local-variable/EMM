namespace EorzeanMarketMaster.Core.Ingest;

/// <summary>Whether a planned refresh may start, and if not, when.</summary>
public enum RefreshGateState
{
    /// <summary>
    /// A point refresh. Exempt from the sweep floor by ruling, not by oversight: the floor was
    /// written against walking a population, and one Ware is not one.
    /// </summary>
    Immediate,

    /// <summary>A sweep, and long enough has passed since the last one.</summary>
    Ready,

    /// <summary>A sweep inside the floor. It waits; it is not refused and it is not sped up.</summary>
    Queued,
}

/// <summary>
/// The gate's answer about one plan at one instant.
/// </summary>
/// <param name="State">Whether it may start.</param>
/// <param name="ReadyAt">
/// The instant it may start. Equal to the instant asked about where the state is
/// <see cref="RefreshGateState.Immediate"/> or <see cref="RefreshGateState.Ready"/>.
/// </param>
/// <param name="Countdown">How long until then. Zero where it may start now.</param>
/// <param name="Cost">
/// What it will cost when it does run. Carried on the verdict rather than fetched separately so
/// that the thing shown to the Player and the thing that governs whether it may start cannot
/// drift apart.
/// </param>
public sealed record RefreshVerdict(
    RefreshGateState State,
    DateTimeOffset ReadyAt,
    TimeSpan Countdown,
    RefreshCost Cost)
{
    /// <summary>Whether the refresh may start at the instant it was assessed.</summary>
    public bool MayStartNow => State != RefreshGateState.Queued;
}

/// <summary>
/// The fifteen-minute floor on sweeps.
///
/// It holds one instant - when the last sweep began - and answers questions about plans. It does
/// not hold a timer, does not read a clock, and cannot be asked to hurry: every answer is a
/// function of the instant it is handed, which is what lets the floor be asserted in a test that
/// takes no time to run.
///
/// <b>What it deliberately does not do is refuse.</b> A Player who asks for a ring-wide refresh
/// inside the floor gets a countdown and a stated cost, not an error - forcing a refresh was ruled
/// to change scope and priority, never rate.
///
/// <b>Known limit, stated rather than hidden.</b> The last-sweep instant is handed in at
/// construction so the plugin can carry it across a restart. Nothing here can stop somebody
/// editing that value in their own configuration file; what it can do is make the honest path the
/// default one and show the Player what the floor is.
/// </summary>
public sealed class SweepGate
{
    private DateTimeOffset? lastSweepStartedAt;

    /// <summary>
    /// A gate that has never seen a sweep, or one restored from a remembered instant.
    /// </summary>
    /// <param name="lastSweepStartedAt">When the last sweep began, or null if none has.</param>
    public SweepGate(DateTimeOffset? lastSweepStartedAt = null) =>
        this.lastSweepStartedAt = lastSweepStartedAt;

    /// <summary>When the last sweep began, for the plugin to persist and for the UI to show.</summary>
    public DateTimeOffset? LastSweepStartedAt => lastSweepStartedAt;

    /// <summary>
    /// Answers whether a plan may start now.
    /// </summary>
    /// <param name="plan">The planned refresh.</param>
    /// <param name="now">The instant to assess at.</param>
    /// <returns>The verdict, carrying the plan's cost either way.</returns>
    public RefreshVerdict Assess(FetchPlan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Kind == RefreshKind.Point)
        {
            return new RefreshVerdict(RefreshGateState.Immediate, now, TimeSpan.Zero, plan.Cost);
        }

        if (lastSweepStartedAt is not { } last)
        {
            return new RefreshVerdict(RefreshGateState.Ready, now, TimeSpan.Zero, plan.Cost);
        }

        var readyAt = last + Citizenship.SweepFloor;

        return readyAt <= now
            ? new RefreshVerdict(RefreshGateState.Ready, now, TimeSpan.Zero, plan.Cost)
            : new RefreshVerdict(RefreshGateState.Queued, readyAt, readyAt - now, plan.Cost);
    }

    /// <summary>
    /// Records that a refresh has begun. A point refresh does not move the floor - it never had to
    /// clear it, and letting it reset the clock would make the floor punish the cheap operation.
    /// </summary>
    /// <param name="plan">The plan that started.</param>
    /// <param name="now">When it started.</param>
    public void Started(FetchPlan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Kind == RefreshKind.Sweep)
        {
            lastSweepStartedAt = now;
        }
    }
}

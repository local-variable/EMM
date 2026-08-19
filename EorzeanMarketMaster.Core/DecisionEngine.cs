namespace EorzeanMarketMaster.Core;

/// <summary>
/// The single decision boundary. Everything the map's rulings converge on happens behind
/// <see cref="Evaluate"/>, and everything on the game side is an adapter that either fills a
/// <see cref="WorldState"/> or applies an <see cref="Outcome"/>.
///
/// The constraint that makes this worth having: no game, no network, no clock, no ImGui. The
/// instant comes in on the state rather than off <c>DateTimeOffset.Now</c>, market data comes in
/// as Snapshots rather than off an HTTP client, and nothing here draws. That is what lets the
/// honesty rules be executable assertions rather than review comments.
///
/// It is an instance method rather than a static one because the ports below the seam - a market
/// Source, the store, the game surface, the write path - are constructor-injected as they arrive.
/// Today there are none, and the engine is a pure function in all but signature.
/// </summary>
public sealed class DecisionEngine
{
    /// <summary>
    /// Turn observed state into decisions.
    /// </summary>
    /// <param name="state">Everything the engine is allowed to see.</param>
    /// <returns>
    /// What it decided: ordered Proposals, Holds carrying a reason and a review time, Notices,
    /// and the Estimates the decisions rest on. Always an Outcome - the engine having nothing to
    /// say is an empty Outcome, never a null and never silence.
    /// </returns>
    public Outcome Evaluate(WorldState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Empty by construction until the tickets below this one fill it in. The shape is the
        // deliverable here; the decisions arrive with #31 (the Estimate), #34 (Strategy
        // resolution), #36 (the Slot Yield allocator) and #41 (the Notice rules).
        return Outcome.Empty;
    }
}

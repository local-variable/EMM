using EorzeanMarketMaster.Core;
using Xunit;

namespace EorzeanMarketMaster.Tests;

/// <summary>
/// The seam: <c>Evaluate(WorldState) -> Outcome</c>, headless. No game, no network, no clock,
/// no ImGui.
/// </summary>
public class DecisionSeamTests
{
    [Fact]
    public void EvaluateReturnsAnOutcomeWithEveryCollectionPresentAndEmpty()
    {
        var outcome = new DecisionEngine().Evaluate(WorldState.Empty);

        Assert.Empty(outcome.Proposals);
        Assert.Empty(outcome.Holds);
        Assert.Empty(outcome.Notices);
        Assert.Empty(outcome.Estimates);
    }
}

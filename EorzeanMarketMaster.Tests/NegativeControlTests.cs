using EorzeanMarketMaster.Core;
using Xunit;
using Xunit.Sdk;
using static EorzeanMarketMaster.Tests.TestData;

namespace EorzeanMarketMaster.Tests;

/// <summary>
/// Proof that this suite's assertions can go red.
///
/// The obligation comes from issue #13, where a first harness rendered seven real failures as
/// ticks, and the in-game self-test has carried a negative control ever since. This suite starts
/// life in the worst possible position for that failure mode: <c>Evaluate</c> returns
/// <see cref="Outcome.Empty"/> by construction, so every "is it empty" assertion in
/// <see cref="DecisionSeamTests"/> passes without exercising anything at all. Left alone they
/// would be indistinguishable from decoration until some ticket months from now made the engine
/// return something and found out the hard way.
///
/// So: feed the same assertions a known-populated Outcome and require them to fail. If a case
/// here ever passes vacuously, the emptiness checks next door are worthless.
/// </summary>
public class NegativeControlTests
{
    /// <summary>An Outcome carrying one of everything. Nothing here is empty.</summary>
    private static Outcome Populated() => new(
        Proposals: [new Proposal(ProposalKind.Reprice, Retainer, Ware, Price, 3, "undercut the lowest competing Listing")],
        Holds: [new Hold(Retainer, Ware, "a competitor dumped stock; clearance expected first", ReviewAt)],
        Notices: [new Notice(Retainer, [NoticeReason.NothingListed])],
        Estimates:
        [
            new Estimate(Ware, Scope.World, new UnitPrice(1_300), new Sample(42, 11, 27),
                new PrecisionInterval(new UnitPrice(1_180), new UnitPrice(1_420)), EstimateGrade.Fitted),
        ]);

    [Fact]
    public void AssertEmptyGoesRedOnEveryOutcomeCollection()
    {
        var populated = Populated();

        // Record.Exception rather than Assert.Throws<T>: what matters is that the assertion
        // fails, not which xunit exception type it happens to fail with.
        AssertGoesRed(() => Assert.Empty(populated.Proposals), nameof(Outcome.Proposals));
        AssertGoesRed(() => Assert.Empty(populated.Holds), nameof(Outcome.Holds));
        AssertGoesRed(() => Assert.Empty(populated.Notices), nameof(Outcome.Notices));
        AssertGoesRed(() => Assert.Empty(populated.Estimates), nameof(Outcome.Estimates));
    }

    [Fact]
    public void TheControlItselfDistinguishesEmptyFromPopulated()
    {
        // The guard on the guard. AssertGoesRed would be satisfied by an assertion that always
        // threw, so prove it also reports the known-good case as green: the same Assert.Empty
        // against Outcome.Empty must NOT throw.
        Assert.Null(Record.Exception(() => Assert.Empty(Outcome.Empty.Proposals)));
        Assert.Null(Record.Exception(() => Assert.Empty(Outcome.Empty.Holds)));
        Assert.Null(Record.Exception(() => Assert.Empty(Outcome.Empty.Notices)));
        Assert.Null(Record.Exception(() => Assert.Empty(Outcome.Empty.Estimates)));
    }

    private static void AssertGoesRed(Action assertion, string collection)
    {
        var failure = Record.Exception(assertion);

        Assert.True(failure is not null,
            $"Assert.Empty passed on a populated {collection}; this suite's emptiness checks are decoration.");
        Assert.IsAssignableFrom<XunitException>(failure);
    }
}

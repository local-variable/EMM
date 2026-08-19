using EorzeanMarketMaster.Core;
using Xunit;
using static EorzeanMarketMaster.Tests.TestData;

namespace EorzeanMarketMaster.Tests;

/// <summary>
/// The honesty rules that can be made structural, made structural.
///
/// The spec states these as things the engine must do. Stated that way they are enforced once
/// per call site, by whoever remembers - and #41, #36 and #34 all add call sites. Enforced in
/// the constructor they are true of every Hold and every Notice that has ever existed, including
/// the ones written by a ticket that never read this file.
/// </summary>
public class OutcomeHonestyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AHoldCannotBeMadeWithoutAReason(string reason)
    {
        // "Hold returns a reason and a review time; it never returns silence."
        Assert.Throws<ArgumentException>(() => new Hold(Retainer, Ware, reason, ReviewAt));
    }

    [Fact]
    public void AHoldCannotBeMadeWithoutAReviewTime()
    {
        // The half of the rule that is easiest to drop, because default(DateTimeOffset) is a
        // perfectly ordinary value that renders as a date and reviews on the twelfth of never.
        Assert.Throws<ArgumentException>(() => new Hold(Retainer, Ware, "a competitor dumped stock", default));
    }

    [Fact]
    public void AHoldWithBothIsFine()
    {
        var hold = new Hold(Retainer, Ware, "a competitor dumped stock", ReviewAt);

        Assert.Equal("a competitor dumped stock", hold.Reason);
        Assert.Equal(ReviewAt, hold.ReviewAt);
    }

    [Fact]
    public void ANoticeCannotBeMadeWithoutAReason()
    {
        // "One per Retainer, carrying its reasons." A Notice with no reason is an alert, which
        // is exactly the thing the glossary says a Notice is not.
        Assert.Throws<ArgumentException>(() => new Notice(Retainer, []));
    }

    [Fact]
    public void ANoticeCarriesEveryReasonAtOnce()
    {
        // A Retainer with three problems is one interruption, not three.
        var notice = new Notice(Retainer,
            [NoticeReason.NothingListed, NoticeReason.GilNearRetainerCap, NoticeReason.ListingsLapsed]);

        Assert.Equal(3, notice.Reasons.Count);
    }

    [Fact]
    public void AnEstimateCannotBeMadeWithoutItsSampleOrItsInterval()
    {
        // "No Estimate rendered without its Sample, its interval and its grade." The grade is an
        // enum and cannot be absent; the other two are references and can be, so they are checked
        // the same way a Hold's reason is. An Estimate that reached the UI carrying a null Sample
        // would be a number with nothing behind it, which is the one thing an Estimate may not be.
        Assert.Throws<ArgumentNullException>(
            () => new Estimate(Ware, Scope.World, new UnitPrice(1_300), null!,
                new PrecisionInterval(new UnitPrice(1_180), new UnitPrice(1_420)), EstimateGrade.Fitted));

        Assert.Throws<ArgumentNullException>(
            () => new Estimate(Ware, Scope.World, new UnitPrice(1_300), new Sample(42, 11, 27), null!, EstimateGrade.Fitted));
    }

    [Fact]
    public void AnOutcomeCannotCarryANullCollection()
    {
        // "An engine that had nothing to say says so with an empty list, never with a null."
        Assert.Throws<ArgumentNullException>(() => new Outcome(null!, [], [], []));
        Assert.Throws<ArgumentNullException>(() => new Outcome([], null!, [], []));
        Assert.Throws<ArgumentNullException>(() => new Outcome([], [], null!, []));
        Assert.Throws<ArgumentNullException>(() => new Outcome([], [], [], null!));
    }

    [Fact]
    public void AnOutcomeCannotBeChangedByWhoeverBuiltIt()
    {
        // The Proposals list is ordered and the order is the allocation order, so an Outcome that
        // shares a mutable list with its builder can be silently reordered or emptied after the
        // engine has returned it. Guard.NotEmpty already copies for exactly this reason; the four
        // Outcome collections are the ones it actually matters for.
        var holds = new List<Hold> { new(Retainer, Ware, "a competitor dumped stock", ReviewAt) };
        var outcome = new Outcome([], holds, [], []);

        holds.Clear();

        Assert.Single(outcome.Holds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AProposalCannotBeMadeWithoutItsReasoning(string reasoning)
    {
        // "Every act carried out under a Mandate recorded as a Proposal with the reasoning that
        // produced it, so that unattended running leaves an audit trail." An audit trail of
        // blank strings is not one.
        Assert.Throws<ArgumentException>(
            () => new Proposal(ProposalKind.Reprice, Retainer, Ware, Price, 3, reasoning));
    }
}

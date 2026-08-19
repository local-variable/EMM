using System.Reflection;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Ingest;
using EorzeanMarketMaster.Core.Store;
using EorzeanMarketMaster.Tests.Store;
using Xunit;

namespace EorzeanMarketMaster.Tests.Ingest;

/// <summary>
/// The ceilings EMM holds itself to, asserted as behaviour rather than as documentation.
///
/// Each of these exists because the corresponding failure is invisible from inside EMM: a plugin
/// that quietly sends four requests a second still works, still looks right, and is only a problem
/// for a service that cannot tell it apart from an attack.
/// </summary>
public class CitizenshipTests
{
    [Fact]
    public void TheSustainedRateSitsFarBelowThePublishedCeilingAndTheConnectionCapWellInsideIt()
    {
        // Not a restatement of the constants - a relationship between them, which is what breaks
        // if somebody raises one and leaves the other. The published figures are the service's;
        // EMM's are a small fraction of them, and this fails the moment that stops being true.
        Assert.True(Citizenship.ShareOfPublishedRate <= 0.10,
            $"EMM's sustained rate is {Citizenship.ShareOfPublishedRate:P1} of the published ceiling");

        Assert.True(Citizenship.ShareOfPublishedConnections <= 0.50,
            $"EMM opens {Citizenship.MaxConnections} of the published {Citizenship.PublishedMaxConnections} connections");

        // And EMM claims no burst allowance at all, because the limits are per address and it may
        // not be the only plugin behind one.
        Assert.True(Citizenship.SustainedRequestsPerSecond < Citizenship.PublishedBurstRequestsPerSecond);
    }

    [Fact]
    public void TheCeilingsHaveNowhereForASettingToLive()
    {
        // "Enforced in code and shown as facts, not as settings" is a claim about shape, so this
        // asserts the shape: a static class with no constructor, no instance state and nothing
        // writable. A later ticket that adds a knob here fails this before it ships one.
        Assert.True(Writable(typeof(Citizenship)).Count == 0,
            $"Citizenship exposes writable members: {string.Join(", ", Writable(typeof(Citizenship)))}");

        Assert.True(typeof(Citizenship) is { IsAbstract: true, IsSealed: true });
        Assert.Empty(typeof(Citizenship).GetConstructors());
    }

    [Fact]
    public void TheSettingDetectorCanTellAKnobFromAConstant()
    {
        // NEGATIVE CONTROL. The case above passes today and would pass just as happily if Writable
        // always returned nothing. Require it to find a knob that is really there.
        Assert.Equal(["Rate"], Writable(typeof(ConfigurableCeilingWouldLookLikeThis)));
    }

    [Fact]
    public async Task EveryRequestAfterTheFirstWaitsOutTheIntervalAndTheFirstDoesNot()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var plan = Plan(itemCount: 120);
        var transport = RecordingTransport.Returning(IngestFixture.Listings(), IngestFixture.History());
        var pacing = new RecordingPacing();

        var report = await AggregatorIngest.Run(
            plan, new SweepGate(), transport, pacing, store, IngestFixture.Instant,
            TestContext.Current.CancellationToken);

        // 120 Items is 2 listings requests at 100 and 6 history requests at 20.
        Assert.Equal(8, report.Batches.Count);
        Assert.Equal(7, pacing.Waits.Count);
        Assert.All(pacing.Waits, wait => Assert.Equal(Citizenship.MinimumInterval, wait));

        // Which is the pacing floor the plan quoted before any of it was sent.
        Assert.Equal(plan.Cost.PacingFloor, pacing.Total);
    }

    [Fact]
    public async Task RequestsGoOneAtATimeSoTheConnectionCapIsNeverApproached()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var transport = RecordingTransport.Returning(IngestFixture.Listings(), IngestFixture.History());

        await AggregatorIngest.Run(
            Plan(itemCount: 60), new SweepGate(), transport, new RecordingPacing(), store,
            IngestFixture.Instant, TestContext.Current.CancellationToken);

        Assert.Equal(1, transport.MaxConcurrent);
        Assert.True(transport.MaxConcurrent <= Citizenship.MaxConnections);
    }

    [Fact]
    public async Task TheConcurrencyDetectorCanSeeTwoRequestsAtOnce()
    {
        // NEGATIVE CONTROL for the case above, which would pass against a transport that never
        // counted anything. Two genuinely overlapping calls have to read as two.
        var transport = RecordingTransport.Returning("""{"itemID":1,"hasData":false}""");
        var address = new Uri("https://example.invalid/one");
        var token = TestContext.Current.CancellationToken;

        await Task.WhenAll(transport.Get(address, token), transport.Get(address, token));

        Assert.Equal(2, transport.MaxConcurrent);
    }

    [Fact]
    public async Task ASweepInsideTheFloorIsQueuedWithACountdownAndACostAndSendsNothing()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        var gate = new SweepGate(IngestFixture.Instant.AddMinutes(-4));
        var plan = Plan(itemCount: 40);
        var transport = RecordingTransport.Returning(IngestFixture.Listings(), IngestFixture.History());

        var report = await AggregatorIngest.Run(
            plan, gate, transport, new RecordingPacing(), store, IngestFixture.Instant,
            TestContext.Current.CancellationToken);

        Assert.Equal(RefreshGateState.Queued, report.Verdict.State);
        Assert.Equal(TimeSpan.FromMinutes(11), report.Verdict.Countdown);
        Assert.Equal(IngestFixture.Instant.AddMinutes(11), report.Verdict.ReadyAt);

        // Queued, not refused: the Player is told what it will cost and when, rather than being
        // told no.
        Assert.Equal(plan.Cost.Requests, report.Verdict.Cost.Requests);
        Assert.True(report.Verdict.Cost.EstimatedBytes > 0);

        Assert.False(report.Ran);
        Assert.Empty(transport.Asked);
        Assert.Equal(0, report.SnapshotsWritten);
    }

    [Fact]
    public async Task APointRefreshIsImmediateHoweverRecentlyASweepRan()
    {
        using var temp = StoreFixture.NewStorePath();
        using var store = MarketStore.OpenOrCreate(temp.Path);

        // A sweep started one second ago, so the floor has fourteen minutes and change to run.
        var gate = new SweepGate(IngestFixture.Instant.AddSeconds(-1));
        var point = FetchPlan.For(
            IngestFixture.World,
            [new WareId(IngestFixture.MixedQualityItem, Quality.Normal)],
            ListingWindow.Standard,
            HistoryWindow.Backfill);

        var transport = RecordingTransport.Returning(IngestFixture.Listings(), IngestFixture.History());

        var report = await AggregatorIngest.Run(
            point, gate, transport, new RecordingPacing(), store, IngestFixture.Instant,
            TestContext.Current.CancellationToken);

        Assert.Equal(RefreshGateState.Immediate, report.Verdict.State);
        Assert.True(report.Ran);
        Assert.Equal(2, transport.Asked.Count);
    }

    [Fact]
    public void APointRefreshDoesNotResetTheSweepFloor()
    {
        // Otherwise the cheap operation would push the expensive one back, and a Player checking
        // one Ware every ten minutes could never sweep at all.
        var gate = new SweepGate(IngestFixture.Instant.AddMinutes(-14));
        var point = FetchPlan.For(
            IngestFixture.World,
            [new WareId(IngestFixture.MixedQualityItem, Quality.Normal)],
            ListingWindow.Standard,
            HistoryWindow.Backfill);

        gate.Started(point, IngestFixture.Instant);

        Assert.Equal(IngestFixture.Instant.AddMinutes(-14), gate.LastSweepStartedAt);
    }

    [Fact]
    public void ASweepCannotCallItselfAPointRefreshToSkipTheFloor()
    {
        // The exemption is derived from what the plan covers, never declared by whoever built it.
        // Both Wares of one Item are one request and stay a point refresh; two Items are not.
        var oneItemBothQualities = FetchPlan.For(
            IngestFixture.World,
            [new WareId(1602, Quality.Normal), new WareId(1602, Quality.High)],
            ListingWindow.Standard,
            HistoryWindow.Backfill);

        var twoItems = FetchPlan.For(
            IngestFixture.World,
            [new WareId(1602, Quality.Normal), new WareId(1604, Quality.Normal)],
            ListingWindow.Standard,
            HistoryWindow.Backfill);

        Assert.Equal(RefreshKind.Point, oneItemBothQualities.Kind);
        Assert.Equal(RefreshKind.Sweep, twoItems.Kind);
    }

    [Fact]
    public void TheFloorIsCountedFromWhenTheLastSweepBeganAndClearsExactlyAtIt()
    {
        var lastSweep = IngestFixture.Instant;
        var gate = new SweepGate(lastSweep);
        var plan = Plan(itemCount: 5);

        // Asserted from both sides of the boundary, because an off-by-one here is a floor that is
        // either fifteen minutes or not a floor at all.
        var justBefore = gate.Assess(plan, lastSweep + Citizenship.SweepFloor - TimeSpan.FromSeconds(1));
        var exactly = gate.Assess(plan, lastSweep + Citizenship.SweepFloor);

        Assert.Equal(RefreshGateState.Queued, justBefore.State);
        Assert.Equal(TimeSpan.FromSeconds(1), justBefore.Countdown);
        Assert.Equal(RefreshGateState.Ready, exactly.State);
        Assert.Equal(TimeSpan.Zero, exactly.Countdown);
    }

    [Fact]
    public void AGateThatHasNeverSeenASweepLetsTheFirstOneThrough()
    {
        Assert.Equal(
            RefreshGateState.Ready,
            new SweepGate().Assess(Plan(itemCount: 5), IngestFixture.Instant).State);
    }

    private static FetchPlan Plan(int itemCount) =>
        FetchPlan.For(
            IngestFixture.World,
            [.. Enumerable.Range(0, itemCount).Select(i => new WareId((uint)(1600 + i), Quality.Normal))],
            ListingWindow.Standard,
            HistoryWindow.Backfill);

    /// <summary>Public members of a type that something outside it could assign to.</summary>
    private static IReadOnlyList<string> Writable(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(property => property.CanWrite)
                .Select(property => property.Name)
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                    .Where(field => field is { IsInitOnly: false, IsLiteral: false })
                    .Select(field => field.Name))
                .OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>What a ceiling that had been turned into a setting would look like.</summary>
    private sealed class ConfigurableCeilingWouldLookLikeThis
    {
        public double Rate { get; set; }
    }
}

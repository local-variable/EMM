using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Graph;
using Xunit;

namespace EorzeanMarketMaster.Tests.Graph;

/// <summary>
/// The rule that a hole in the data is drawn as a hole.
/// </summary>
public class BrokenLineTests
{
    private static readonly WareId Ware = new(5057, Quality.High);
    private static readonly WorldId World = new(79);
    private static readonly DateTimeOffset Midnight = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AQuietSliceIsAHoleAndNotASegmentDrawnStraightAcrossIt()
    {
        // THE ACCEPTANCE CRITERION, made arithmetic. Handed only the occupied slices, a plotter
        // draws one unbroken segment from the Sale before a quiet stretch to the Sale after it -
        // a line through days in which nothing was observed, drawn exactly like a line through
        // days of steady trade. The NaN is what stops that, and it is here rather than in the
        // renderer so that it can be asserted with no client running.
        var line = Line(
            (Midnight.AddHours(2), 100),
            (Midnight.AddDays(4).AddHours(2), 400));

        Assert.Equal(5, line.Count);
        Assert.Equal(3, line.Breaks);

        Assert.Equal(100, line.Value[0]);
        Assert.True(double.IsNaN(line.Value[1]));
        Assert.True(double.IsNaN(line.Value[2]));
        Assert.True(double.IsNaN(line.Value[3]));
        Assert.Equal(400, line.Value[4]);
    }

    [Fact]
    public void AnUnbrokenStretchCarriesNoHolesAtAll()
    {
        // The negative control for the case above. A line that reported holes everywhere would
        // pass the gap test and be useless, so the detector has to be shown telling the two apart.
        var line = Line(
            (Midnight.AddHours(2), 100),
            (Midnight.AddDays(1).AddHours(2), 200),
            (Midnight.AddDays(2).AddHours(2), 300),
            (Midnight.AddDays(3).AddHours(2), 400),
            (Midnight.AddDays(4).AddHours(2), 500));

        Assert.Equal(5, line.Count);
        Assert.Equal(0, line.Breaks);
        Assert.DoesNotContain(line.Value, double.IsNaN);
    }

    [Fact]
    public void TheLineRunsOverEverySliceInTheWindowRatherThanOverTheOccupiedOnes()
    {
        // The grid comes from the window, not from the data. A grid derived from the Rollups
        // cannot represent the absence of a Rollup, which is the whole thing being drawn here.
        var line = Line((Midnight.AddHours(2), 100));

        Assert.Equal(5, line.Count);
        Assert.Equal(4, line.Breaks);
    }

    [Fact]
    public void AWindowWithNothingInItIsEmptyRatherThanFlat()
    {
        var line = BrokenLine.Over([], RollupWidth.Day, Midnight, Midnight.AddDays(5));

        Assert.Equal(5, line.Count);
        Assert.True(line.IsEmpty);
        Assert.All(line.Value, value => Assert.True(double.IsNaN(value)));
    }

    [Fact]
    public void AMarkSitsInTheMiddleOfItsSliceRatherThanAtItsStart()
    {
        // A figure covering a whole day drawn at the moment the day began reads as a day old, and
        // on a six-day slice it is nearly a week out. The Rollup says the same thing about itself,
        // so the two cannot disagree.
        var line = Line((Midnight.AddHours(2), 100));

        Assert.Equal(Midnight.AddHours(12).ToUnixTimeSeconds(), line.At[0]);
    }

    [Fact]
    public void AWindowThatEndsBeforeItBeginsDrawsNothing()
    {
        var line = BrokenLine.Over([], RollupWidth.Day, Midnight, Midnight.AddDays(-1));

        Assert.Equal(0, line.Count);
        Assert.True(line.IsEmpty);
    }

    private static BrokenLine Line(params (DateTimeOffset At, long Gil)[] sales)
    {
        var history = new History(Ware, World,
            [.. sales.Select(s => new MarketSale(Ware, World, s.At, new UnitPrice(s.Gil), 1, Source.Aggregator))]);

        var to = Midnight.AddDays(5);
        var rollups = history.Rollups(RollupWidth.Day, Midnight, to);

        return BrokenLine.Over(rollups, RollupWidth.Day, Midnight, to);
    }
}

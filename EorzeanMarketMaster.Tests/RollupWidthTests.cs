using EorzeanMarketMaster.Core;
using Xunit;

namespace EorzeanMarketMaster.Tests;

/// <summary>
/// The slice grid: where a slice starts, and which width a window gets.
/// </summary>
public class RollupWidthTests
{
    [Fact]
    public void ASliceIsAnchoredToTheEpochAndNotToWhenTheReaderLooked()
    {
        // THE PROPERTY THAT MAKES A ROLLUP REPRODUCIBLE. Anchored at "now", the same Sale would
        // fall in a different slice depending on when the graph was opened, and two readers
        // comparing figures would be comparing different arithmetic over the same Sales.
        var noon = new DateTimeOffset(2026, 8, 19, 12, 34, 56, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero), RollupWidth.Day.StartOf(noon));
        Assert.Equal(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero), RollupWidth.SixHours.StartOf(noon));
    }

    [Fact]
    public void AnInstantExactlyOnABoundaryStartsTheSliceRatherThanEndingThePreviousOne()
    {
        var boundary = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(boundary, RollupWidth.Day.StartOf(boundary));
        Assert.Equal(
            boundary.AddDays(-1),
            RollupWidth.Day.StartOf(boundary.AddTicks(-1)));
    }

    [Fact]
    public void SliceBoundariesAreExactAtInstantsADoubleCannotHold()
    {
        // Ticks since the epoch passed 2^53 in 1998, so the obvious spelling of a floor - divide
        // as doubles, then Math.Floor - cannot represent this year to the second and lands Sales
        // in the neighbouring slice at the boundaries. Asserted on the two instants either side of
        // a real boundary, which is exactly where that error shows and nowhere else.
        var boundary = new DateTimeOffset(2026, 8, 19, 6, 0, 0, TimeSpan.Zero);

        Assert.Equal(boundary, RollupWidth.SixHours.StartOf(boundary));
        Assert.Equal(boundary, RollupWidth.SixHours.StartOf(boundary.AddTicks(1)));
        Assert.Equal(boundary.AddHours(-6), RollupWidth.SixHours.StartOf(boundary.AddTicks(-1)));
    }

    [Theory]
    [InlineData(7, 6)]
    [InlineData(14, 6)]
    [InlineData(30, 24)]
    [InlineData(60, 24)]
    [InlineData(90, 72)]
    [InlineData(180, 144)]
    public void EveryOfferedPresetGetsAStatedSliceWidthRatherThanOneGuessedAtACallSite(int days, int hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), RollupWidth.For(TimeSpan.FromDays(days)).Span);
    }

    [Fact]
    public void TheWidthsRunCoarserAsTheWindowGrows()
    {
        // A narrower window with a coarser slice would draw a week as a single mark. Asserted as a
        // monotone property rather than case by case, so a later edit to one threshold cannot pass
        // by moving one case and breaking the shape.
        var windows = new[] { 7, 14, 30, 60, 90, 180, 365 }
            .Select(days => RollupWidth.For(TimeSpan.FromDays(days)).Span)
            .ToList();

        Assert.Equal(windows.OrderBy(span => span), windows);
    }

    [Fact]
    public void ASliceOfNoTimeIsRefused()
    {
        // A zero width divides the whole axis into infinitely many slices, which is a hang rather
        // than a wrong number - the loop that walks them never reaches the end.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollupWidth(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollupWidth(TimeSpan.FromHours(-1)));
    }
}

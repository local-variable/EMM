using EorzeanMarketMaster.Core;
using Xunit;

namespace EorzeanMarketMaster.Tests;

/// <summary>
/// <see cref="UnitPrice"/> exists so that a per-unit figure and a Stack total cannot be assigned
/// to one another, which a <c>long</c> on both sides permits and the compiler cannot see.
/// </summary>
public class UnitPriceTests
{
    [Fact]
    public void AUnitPriceCannotBeNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnitPrice(-1));
    }

    [Fact]
    public void ZeroIsALegalUnitPrice()
    {
        // Not the same question as the one above. Zero is a real figure - a vendor-worthless Ware
        // has one - so rejecting it would be inventing a rule nobody asked for.
        Assert.Equal(0, new UnitPrice(0).Gil);
    }

    [Fact]
    public void TwoUnitPricesOfTheSameGilAreEqual()
    {
        // Value semantics, so that an assertion over a computed price compares figures rather
        // than references.
        Assert.Equal(new UnitPrice(1_250), new UnitPrice(1_250));
        Assert.NotEqual(new UnitPrice(1_250), new UnitPrice(1_251));
    }
}

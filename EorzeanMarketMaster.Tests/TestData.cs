using EorzeanMarketMaster.Core;

namespace EorzeanMarketMaster.Tests;

/// <summary>
/// Fixtures shared across the suite. Invented names and a real Item id, so that a failure message
/// reads like something from the game rather than like "retainer1".
/// </summary>
internal static class TestData
{
    /// <summary>A Retainer to hang decisions on.</summary>
    internal static readonly RetainerId Retainer = new("Aeryn Vale", "Coriander");

    /// <summary>One Ware. HQ, so the Quality half of the identity is never the default.</summary>
    internal static readonly WareId Ware = new(5057, Quality.High);

    /// <summary>
    /// A fixed instant. Never <c>DateTimeOffset.Now</c>: the seam takes its instant on the state
    /// precisely so that nothing under test reads a clock, and a fixture that did would be the
    /// first thing to break that.
    /// </summary>
    internal static readonly DateTimeOffset ReviewAt = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A plausible per-unit price.</summary>
    internal static readonly UnitPrice Price = new(1_250);
}

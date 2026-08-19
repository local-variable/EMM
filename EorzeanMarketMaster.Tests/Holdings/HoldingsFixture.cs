using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Holdings;

namespace EorzeanMarketMaster.Tests.Holdings;

/// <summary>
/// Names and instants the Holdings suite shares. Invented Character and Retainer names and real
/// Item ids, so a failure message reads like something from the game.
/// </summary>
internal static class HoldingsFixture
{
    /// <summary>The Character everything here belongs to.</summary>
    internal const string Character = "Aeryn Vale";

    /// <summary>One of that Character's Retainers.</summary>
    internal static readonly RetainerId Coriander = new(Character, "Coriander");

    /// <summary>A second Retainer, for the cases about one not standing in for another.</summary>
    internal static readonly RetainerId Saffron = new(Character, "Saffron");

    /// <summary>An HQ Ware. Quality is part of the identity, so it is never the default here.</summary>
    internal static readonly WareId Tincture = new(5057, Quality.High);

    /// <summary>The same Item at Normal Quality - a different Ware, and a different price.</summary>
    internal static readonly WareId TinctureNq = new(5057, Quality.Normal);

    /// <summary>A second Item.</summary>
    internal static readonly WareId Fleece = new(5325, Quality.Normal);

    /// <summary>A fixed instant. Nothing under test reads a clock.</summary>
    internal static readonly DateTimeOffset Noon = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A Retainer reading EMM took itself, so its contents are dated.</summary>
    internal static HoldingsReading Read(
        RetainerId retainer, DateTimeOffset at, params HeldWare[] held) =>
        new(Character, retainer, at, at, Source.OpenedBoard, held);

    /// <summary>
    /// A reading from a Source that cannot say when it last looked - the companion plugin's shape.
    /// </summary>
    internal static HoldingsReading Undated(
        RetainerId retainer, DateTimeOffset asked, params HeldWare[] held) =>
        new(Character, retainer, asked, null, Source.ImportedStore, held);

    /// <summary>A Character's bags.</summary>
    internal static HoldingsReading Bags(DateTimeOffset at, params HeldWare[] held) =>
        new(Character, null, at, at, Source.OpenedBoard, held);

    /// <summary>A line of stock: held by the Retainer, not offered to anyone.</summary>
    internal static HeldWare Stock(WareId ware, int units) =>
        new(ware, HoldingPlace.Stock, units, null);

    /// <summary>A line on the board.</summary>
    internal static HeldWare Listed(WareId ware, int units, long gil) =>
        new(ware, HoldingPlace.Listed, units, new UnitPrice(gil));

    /// <summary>A line in a Character's bag.</summary>
    internal static HeldWare InBag(WareId ware, int units) =>
        new(ware, HoldingPlace.Bag, units, null);

    /// <summary>One Retainer as the bell reports it.</summary>
    internal static RetainerSummary Summary(
        RetainerId retainer,
        int marketItems,
        DateTimeOffset? expires = null,
        int items = 0,
        long gil = 0) =>
        new(retainer, items, marketItems, gil, expires, Available: true);
}

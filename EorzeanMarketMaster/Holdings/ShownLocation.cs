using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Holdings;

namespace EorzeanMarketMaster.Holdings;

/// <summary>
/// One entry in the Holdings list's location filter: every Character's bags, or one Retainer.
///
/// It carries its own predicate and its own label so the surface does not have to hold a switch
/// over what kind of filter is selected. "Everywhere" is not a value here - it is the absence of
/// one, spelled <c>null</c>, which is what stops a third case existing that every caller has to
/// remember to handle.
///
/// Named for what it is on screen rather than <c>Place</c>, because <see cref="HoldingPlace"/> is
/// the Core enum for bag-versus-stock-versus-listed and two types a letter apart would be read
/// wrong eventually.
/// </summary>
internal readonly record struct ShownLocation
{
    private ShownLocation(RetainerId? retainer) => Retainer = retainer;

    /// <summary>Every Character's own bags, and no Retainer.</summary>
    internal static ShownLocation Bags { get; } = new(null);

    /// <summary>The Retainer this narrows to, or null where it narrows to bags.</summary>
    internal RetainerId? Retainer { get; }

    /// <summary>Narrows to one Retainer.</summary>
    /// <param name="retainer">The Retainer.</param>
    /// <returns>The filter.</returns>
    internal static ShownLocation Of(RetainerId retainer) => new(retainer);

    /// <summary>What to call it in the control.</summary>
    internal string Label => Retainer?.Retainer ?? "Bags";

    /// <summary>Whether a row belongs to this location.</summary>
    /// <param name="holding">The row.</param>
    /// <returns>Whether to count it.</returns>
    internal bool Keeps(Holding holding) =>
        Retainer is { } only ? holding.Retainer == only : holding.Place == HoldingPlace.Bag;
}

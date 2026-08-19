namespace EorzeanMarketMaster.Core.Holdings;

/// <summary>
/// Where a quantity of a Ware is sitting.
///
/// The three the glossary names, and no more: Holdings are "character bags, Retainer stock, and
/// units currently listed". They are kept apart rather than summed because they answer different
/// questions - what is listed is earning, what is in stock is waiting for a slot, and what is in a
/// bag is not with the Retainer that would sell it.
/// </summary>
public enum HoldingPlace
{
    /// <summary>A Character's own inventory.</summary>
    Bag,

    /// <summary>A Retainer's inventory. Held by the Retainer, not offered to anyone.</summary>
    Stock,

    /// <summary>A Retainer's live Listing. On the board at an asking price.</summary>
    Listed,
}

/// <summary>
/// Units of one Ware the Player owns in one place, carrying how old that statement is.
///
/// <b>The age is two figures, not one, for the reason a Snapshot's is.</b>
/// <see cref="ObservedAt"/> is when EMM read this; <see cref="TrueAsOf"/> is when the Source it
/// came from last actually looked at the container. For EMM's own reader they are the same
/// instant. For a companion plugin read through its interface they are not: the companion answers
/// immediately with whatever it is holding, and it does not report when it last saw the Retainer -
/// so a reading taken a second ago can describe a Retainer nobody has opened in a week. Stamping
/// one instant on both would present that as fresh, which is the exact dishonesty
/// <see cref="Snapshot.UploadedAt"/> exists to prevent on the market side.
///
/// <b>Retainer stock is only ground truth while that Retainer is open.</b> Everything here is
/// therefore last-seen state and is labelled as such; nothing in EMM may treat a Holding as a
/// statement about the present.
/// </summary>
/// <param name="Ware">What is owned. A price attaches to a Ware, so HQ and NQ are never summed.</param>
/// <param name="Place">Whether it is in a bag, in Retainer stock, or listed.</param>
/// <param name="Character">The owning Character.</param>
/// <param name="Retainer">
/// The holding Retainer, or null for a Character's own bags. Where present its Character must be
/// the same one - a Retainer belongs to exactly one Character, and a row saying otherwise is a
/// wrong-owner bug that would allocate one Character's stock to another's sell space.
/// </param>
/// <param name="Units">How many units. At least one.</param>
/// <param name="AskingPrice">
/// What it is listed at, per unit. Present exactly when <paramref name="Place"/> is
/// <see cref="HoldingPlace.Listed"/> - stock has no price, and a Listing without one is not a
/// Listing.
/// </param>
/// <param name="ObservedAt">When EMM read this.</param>
/// <param name="TrueAsOf">
/// When the Source last knew this to be true of the game, or null where it cannot say. Freshness
/// is measured from here, and a null is shown as an unknown age rather than as a fresh one.
/// </param>
/// <param name="Source">Where the reading came from.</param>
public sealed record Holding(
    WareId Ware,
    HoldingPlace Place,
    string Character,
    RetainerId? Retainer,
    int Units,
    UnitPrice? AskingPrice,
    DateTimeOffset ObservedAt,
    DateTimeOffset? TrueAsOf,
    Source Source)
{
    /// <summary>The owning Character. Never blank.</summary>
    public string Character { get; } =
        Guard.NotBlank(Character, nameof(Character), "A Holding belongs to a named Character.");

    /// <summary>How many units. A Holding of nothing is not a Holding.</summary>
    public int Units { get; } =
        Guard.Positive(Units, nameof(Units), "A Holding carries at least one unit.");

    /// <summary>When EMM read this. Never <c>default</c>, for the reason a Snapshot's is not.</summary>
    public DateTimeOffset ObservedAt { get; } = Guard.NotDefault(
        ObservedAt, nameof(ObservedAt), "A Holding must carry the time it was read.");

    /// <summary>The holding Retainer, or null for bags.</summary>
    public RetainerId? Retainer { get; } = Check(Place, Character, Retainer, AskingPrice);

    /// <summary>
    /// How old this statement is, or null where the Source could not say when it last looked.
    /// </summary>
    /// <param name="now">The instant to measure against.</param>
    /// <returns>The age, or null where it is unknown.</returns>
    public TimeSpan? Age(DateTimeOffset now) => TrueAsOf is { } seen ? now - seen : null;

    /// <summary>
    /// The three invariants that make a row's place and its fields agree, checked once here rather
    /// than at every call site.
    ///
    /// Each of them is a wrong-number bug rather than a crash if it is allowed through: a bag row
    /// carrying a Retainer allocates stock to somebody who does not hold it, a Retainer row
    /// carrying the wrong Character does the same across Characters, and a Listed row without a
    /// price is a slot the allocator would think is free.
    /// </summary>
    private static RetainerId? Check(
        HoldingPlace place, string character, RetainerId? retainer, UnitPrice? askingPrice)
    {
        switch (place)
        {
            case HoldingPlace.Bag when retainer is not null:
                throw new ArgumentException(
                    "A bag Holding belongs to a Character, not to a Retainer.", nameof(retainer));

            case HoldingPlace.Stock or HoldingPlace.Listed when retainer is null:
                throw new ArgumentException(
                    "Retainer stock and Listings belong to a named Retainer.", nameof(retainer));
        }

        if (retainer is { } held && !string.Equals(held.Character, character, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Retainer {held.Retainer} belongs to {held.Character}, not to {character}.",
                nameof(retainer));
        }

        if (place == HoldingPlace.Listed && askingPrice is null)
        {
            throw new ArgumentException(
                "A Listing is a Stack at an asking price; one without a price is not a Listing.",
                nameof(askingPrice));
        }

        if (place != HoldingPlace.Listed && askingPrice is not null)
        {
            throw new ArgumentException(
                "Only a Listing has an asking price. Stock is held, not offered.", nameof(askingPrice));
        }

        return retainer;
    }
}

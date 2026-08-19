namespace EorzeanMarketMaster.Core.Holdings;

/// <summary>
/// One line of a reading: a Ware, where it sat, and how much of it there was.
///
/// Deliberately carries no instant, no Character and no Source. Those are facts about the reading
/// that produced it and are identical for every line in that reading, so putting them here would
/// create a class of bug that cannot otherwise exist - rows disagreeing with the header they were
/// read under. <see cref="HoldingsReading.Holdings"/> stamps them on the way out.
/// </summary>
/// <param name="Ware">What was there.</param>
/// <param name="Place">Whether it was in a bag, in stock, or listed.</param>
/// <param name="Units">How many units.</param>
/// <param name="AskingPrice">What it was listed at, per unit; null unless it was listed.</param>
public sealed record HeldWare(WareId Ware, HoldingPlace Place, int Units, UnitPrice? AskingPrice)
{
    /// <summary>How many units. At least one.</summary>
    public int Units { get; } =
        Guard.Positive(Units, nameof(Units), "A Holding carries at least one unit.");

    /// <summary>The asking price, checked against the place it was read from.</summary>
    public UnitPrice? AskingPrice { get; } = Priced(Place, AskingPrice);

    /// <summary>
    /// Which of several Listings of one Ware this is.
    ///
    /// <b>Assigned by <see cref="HoldingsReading"/>, never by a caller</b>, and it exists because
    /// a Retainer can hold two Listings of the same Ware at the same price in two slots - so the
    /// Ware and the place together do not identify a line, and anything keyed on them alone would
    /// silently collapse the two into one and undercount what the Player owns.
    ///
    /// It is not the game's slot index. A reading orders its Listings by price and assigns these
    /// from that order, so re-reading an unchanged Retainer produces the identical reading however
    /// the slots happen to be arranged - which is what makes a write of it idempotent. Bag and
    /// stock lines are summed per Ware and are all ordinal 0.
    /// </summary>
    public int Ordinal { get; init; }

    private static UnitPrice? Priced(HoldingPlace place, UnitPrice? askingPrice) => place switch
    {
        HoldingPlace.Listed when askingPrice is null => throw new ArgumentException(
            "A Listing is a Stack at an asking price; one without a price is not a Listing.",
            nameof(askingPrice)),

        not HoldingPlace.Listed when askingPrice is not null => throw new ArgumentException(
            "Only a Listing has an asking price. Stock is held, not offered.", nameof(askingPrice)),

        _ => askingPrice,
    };
}

/// <summary>
/// Everything EMM saw in one place at one moment: a Character's bags, or one Retainer's stock and
/// Listings together.
///
/// <b>A reading is complete for its place, and that is what makes it usable.</b> It is not a list
/// of what was found - it is the whole contents, so a Ware absent from it was absent from the
/// place. Anything weaker cannot express a Retainer that has sold out, and a sold-out Retainer
/// whose last known Listings simply linger is the failure this type exists to prevent. It is the
/// same rule the Snapshot table already carries: "EMM looked and there was nothing here" is a real
/// observation and is stored as one, never as an absence of rows.
///
/// <b>It normalises its own lines.</b> Bag and stock lines are summed per Ware, since a stack
/// split across two slots is still one quantity of one Ware; Listings are kept apart, since two
/// slots are two Listings and may carry two prices. Callers hand over whatever the game gave them
/// and get a canonical reading back, so two readings of an unchanged place are equal rather than
/// merely equivalent.
/// </summary>
/// <param name="Character">The owning Character.</param>
/// <param name="Retainer">The Retainer read, or null where this reading is the Character's bags.</param>
/// <param name="ObservedAt">When EMM took the reading.</param>
/// <param name="TrueAsOf">
/// When the Source last knew this to be true of the game, or null where it cannot say. Equal to
/// <paramref name="ObservedAt"/> for EMM's own reader, which reads the game directly; null for a
/// companion plugin, which answers from a cache of unknown age.
/// </param>
/// <param name="Source">Where the reading came from.</param>
/// <param name="Held">The complete contents. Empty means the place was empty, not unread.</param>
public sealed record HoldingsReading(
    string Character,
    RetainerId? Retainer,
    DateTimeOffset ObservedAt,
    DateTimeOffset? TrueAsOf,
    Source Source,
    IReadOnlyList<HeldWare> Held)
{
    /// <summary>The owning Character. Never blank.</summary>
    public string Character { get; } =
        Guard.NotBlank(Character, nameof(Character), "A reading belongs to a named Character.");

    /// <summary>The Retainer read, or null for the Character's bags.</summary>
    public RetainerId? Retainer { get; } = Owned(Character, Retainer);

    /// <summary>The contents, canonical. Never null; empty means EMM looked and found nothing.</summary>
    public IReadOnlyList<HeldWare> Held { get; } = Normalise(Retainer, Held);

    /// <summary>When EMM took the reading. Never <c>default</c>.</summary>
    public DateTimeOffset ObservedAt { get; } = Guard.NotDefault(
        ObservedAt, nameof(ObservedAt), "A reading must carry the time it was taken.");

    /// <summary>
    /// Which place this reading covers, and the unit a later reading replaces wholesale.
    ///
    /// A Retainer's stock and its Listings are one place because they are read in one pass off one
    /// open Retainer - splitting them would let EMM hold a Retainer's stock from today beside its
    /// Listings from last week, which is a picture that never existed.
    /// </summary>
    public HoldingsPlaceKey Place => new(Character, Retainer);

    /// <summary>How many Listings were on the board when this was read.</summary>
    public int Listed => Held.Count(line => line.Place == HoldingPlace.Listed);

    /// <summary>
    /// The reading flattened into the rows the decision seam sees, each stamped with this
    /// reading's owner, instants and Source.
    /// </summary>
    /// <returns>One Holding per line held, in the reading's own order.</returns>
    public IReadOnlyList<Holding> Holdings() =>
    [
        .. Held.Select(line => new Holding(
            line.Ware,
            line.Place,
            Character,
            Retainer,
            line.Units,
            line.AskingPrice,
            ObservedAt,
            TrueAsOf,
            Source)),
    ];

    /// <summary>A Retainer belongs to exactly one Character, and a reading may not say otherwise.</summary>
    private static RetainerId? Owned(string character, RetainerId? retainer) =>
        retainer is { } held && !string.Equals(held.Character, character, StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"Retainer {held.Retainer} belongs to {held.Character}, not to {character}.",
                nameof(retainer))
            : retainer;

    /// <summary>
    /// Canonical form: bags and stock summed per Ware, Listings ordered and numbered.
    ///
    /// The place check comes first because it is the one that would corrupt rather than merely
    /// confuse - a row naming a Retainer while claiming to be in a bag would be replaced by every
    /// later reading of that Retainer, quietly deleting units the Character actually holds.
    /// </summary>
    private static IReadOnlyList<HeldWare> Normalise(RetainerId? retainer, IReadOnlyList<HeldWare>? held)
    {
        var lines = Guard.CopyOf(held, nameof(held));
        var wantsBags = retainer is null;

        if (lines.Any(line => (line.Place == HoldingPlace.Bag) != wantsBags))
        {
            throw new ArgumentException(
                wantsBags
                    ? "A reading of a Character's bags holds only bag lines."
                    : "A reading of a Retainer holds its stock and its Listings, never bag lines.",
                nameof(held));
        }

        var summed = lines
            .Where(line => line.Place != HoldingPlace.Listed)
            .GroupBy(line => (line.Ware, line.Place))
            .Select(group => new HeldWare(
                group.Key.Ware, group.Key.Place, group.Sum(line => line.Units), null));

        // Ordered before numbering, and by price first, so the ordinals describe the board rather
        // than the order the slots happened to be walked in.
        var listings = lines
            .Where(line => line.Place == HoldingPlace.Listed)
            .OrderBy(line => line.Ware.ItemId)
            .ThenBy(line => line.Ware.Quality)
            .ThenBy(line => line.AskingPrice!.Value.Gil)
            .ThenBy(line => line.Units)
            .Select((line, index) => line with { Ordinal = index });

        return
        [
            .. summed.Concat(listings)
                .OrderBy(line => line.Place)
                .ThenBy(line => line.Ware.ItemId)
                .ThenBy(line => line.Ware.Quality)
                .ThenBy(line => line.Ordinal),
        ];
    }
}

/// <summary>
/// The place a reading covers: a Character's bags, or one of that Character's Retainers.
///
/// Named a place rather than a scope on purpose - <see cref="Core.Scope"/> is the glossary's word
/// for the breadth a price is measured over (World, Data Centre, Region) and means something else
/// entirely.
/// </summary>
/// <param name="Character">The owning Character.</param>
/// <param name="Retainer">The Retainer, or null for that Character's own bags.</param>
public readonly record struct HoldingsPlaceKey(string Character, RetainerId? Retainer);

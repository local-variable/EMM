namespace EorzeanMarketMaster.Core;

/// <summary>
/// The Listings for a Ware in a Market as observed at a single moment, carrying the time it was
/// observed. Never "current" - by the time it is read it is already a record of the past.
///
/// A Snapshot with no Listings is a real observation and not a missing one: 42% of the catalogue
/// has nothing listed on a given World at any moment, and "nobody is selling this here" is one of
/// the more useful things EMM can know. It is stored, not skipped.
/// </summary>
/// <param name="Ware">What was being observed.</param>
/// <param name="World">Whose board. A Market is one World's board, so this is never optional.</param>
/// <param name="ObservedAt">When EMM took the observation.</param>
/// <param name="Source">Where it came from, which is what its Freshness and trustworthiness hang off.</param>
/// <param name="UploadedAt">
/// When the Source itself last learned anything about this Market, where it says. Freshness is
/// measured from here rather than from <paramref name="ObservedAt"/>: an aggregator answering
/// instantly with data nobody has refreshed in five days is a stale observation taken at a fresh
/// moment, and median upload age was measured ranging from 1.8 to 130 hours between Worlds.
/// Null where the Source does not report one.
/// </param>
/// <param name="Listings">
/// What was on the board, in ascending Unit Price. The store returns them in that order whatever
/// order they went in, because the lowest is the one every Undercut and Reference Price reads.
/// </param>
public sealed record Snapshot(
    WareId Ware,
    WorldId World,
    DateTimeOffset ObservedAt,
    Source Source,
    DateTimeOffset? UploadedAt,
    IReadOnlyList<Listing> Listings)
{
    /// <summary>What was on the board. Never null; empty means nothing was listed.</summary>
    public IReadOnlyList<Listing> Listings { get; } = Guard.CopyOf(Listings, nameof(Listings));

    /// <summary>
    /// When the observation was taken. Never <c>default</c>, which is not null, renders as an
    /// ordinary date, and would silently file the observation in a partition for the year 1.
    /// </summary>
    public DateTimeOffset ObservedAt { get; } =
        Guard.NotDefault(ObservedAt, nameof(ObservedAt), "A Snapshot must carry the time it was observed.");
}

/// <summary>
/// One live Listing on a board: a Stack of a Ware at a Unit Price, put up by a Retainer.
///
/// The Retainer is a bare name rather than a <see cref="RetainerId"/> because most of these belong
/// to other Players and a Source reports whatever name it has. It is kept at all because a
/// Competing Listing is defined as one that is not the Player's own, and telling those apart needs
/// something to tell them apart by.
/// </summary>
/// <param name="UnitPrice">Gil for one unit.</param>
/// <param name="Stack">Units in the Listing. A Listing is bought whole, so this sets what a buyer must commit.</param>
/// <param name="Retainer">The selling Retainer's name as the Source gave it, or null where it gave none.</param>
/// <param name="LastReviewedAt">
/// When the Listing was last touched, where the Source reports it. A lower bound on the Listing's
/// age rather than a series, which is why Days of Supply is built from stored Snapshots instead.
/// </param>
public sealed record Listing(
    UnitPrice UnitPrice,
    int Stack,
    string? Retainer,
    DateTimeOffset? LastReviewedAt)
{
    /// <summary>Units in the Listing. At least one - a Listing of nothing is not a Listing.</summary>
    public int Stack { get; } = Guard.Positive(Stack, nameof(Stack), "A Listing carries at least one unit.");
}

/// <summary>
/// One completed transaction observed through a Source: a Stack of a Ware that changed hands at a
/// Unit Price. Sampled, never complete, and carrying no Cost Basis - which is what separates it
/// from an Own Sale and why Profit can never be computed from one.
/// </summary>
/// <param name="Ware">What changed hands.</param>
/// <param name="World">Where. A Sale on another World is a fact about a Market the Player is not in.</param>
/// <param name="SoldAt">When, as the Source reports it.</param>
/// <param name="UnitPrice">Gil for one unit.</param>
/// <param name="Stack">Units in the Sale. Summed rather than counted wherever velocity is measured.</param>
/// <param name="Source">Where the observation came from.</param>
public sealed record MarketSale(
    WareId Ware,
    WorldId World,
    DateTimeOffset SoldAt,
    UnitPrice UnitPrice,
    int Stack,
    Source Source)
{
    /// <summary>Units in the Sale. At least one.</summary>
    public int Stack { get; } = Guard.Positive(Stack, nameof(Stack), "A Sale moves at least one unit.");

    /// <summary>When it sold. Never <c>default</c>, for the same reason a Snapshot's instant is not.</summary>
    public DateTimeOffset SoldAt { get; } =
        Guard.NotDefault(SoldAt, nameof(SoldAt), "A Sale must carry the time it was observed to have happened.");
}

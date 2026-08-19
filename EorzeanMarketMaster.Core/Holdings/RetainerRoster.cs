namespace EorzeanMarketMaster.Core.Holdings;

/// <summary>
/// What the summoning bell says about one Retainer without it being opened.
///
/// <b>Counts, not contents.</b> Everything here is readable for every Retainer the moment the bell
/// list is up, and none of it names a single Ware. That asymmetry is the whole shape of the
/// Holdings surface: the roster can be refreshed across every Retainer in one press, and the
/// contents behind those counts cannot be refreshed at all without opening each one.
/// </summary>
/// <param name="Retainer">Which Retainer.</param>
/// <param name="ItemCount">How many inventory slots it is holding.</param>
/// <param name="MarketItemCount">
/// How many Listings it has. A cheap trigger and a coverage hint, and nothing more: it counts, it
/// nets a sale against a relist, and it was measured lagging the Retainer's own container by
/// several seconds while that Retainer was open. It may never be read as evidence that a Sale, a
/// delist or a reconciliation failure happened.
/// </param>
/// <param name="Gil">Gil the Retainer is holding.</param>
/// <param name="MarketExpiresAt">
/// When its market lapses, or null where the game reports none. Beyond this instant the Listing
/// count stops being trustworthy, so nothing is concluded from it - the same guard the prior art
/// applies, adopted rather than reinvented.
/// </param>
/// <param name="Available">
/// The game's own availability flag, carried rather than interpreted. What it means precisely is
/// not settled here, so no surface says anything about it - a figure shown to a Player has to be
/// one EMM can explain, and this one is not yet.
/// </param>
public sealed record RetainerSummary(
    RetainerId Retainer,
    int ItemCount,
    int MarketItemCount,
    long Gil,
    DateTimeOffset? MarketExpiresAt,
    bool Available);

/// <summary>
/// Every Retainer of one Character as the bell reports them, at one instant.
/// </summary>
/// <param name="Character">The owning Character.</param>
/// <param name="ReadAt">When the roster was read.</param>
/// <param name="Retainers">The Retainers, in the game's own sorted order.</param>
public sealed record RetainerRoster(
    string Character,
    DateTimeOffset ReadAt,
    IReadOnlyList<RetainerSummary> Retainers)
{
    /// <summary>The owning Character. Never blank.</summary>
    public string Character { get; } =
        Guard.NotBlank(Character, nameof(Character), "A roster belongs to a named Character.");

    /// <summary>The Retainers. Never null; empty is a Character with none.</summary>
    public IReadOnlyList<RetainerSummary> Retainers { get; } = Guard.CopyOf(Retainers, nameof(Retainers));

    /// <summary>When the roster was read. Never <c>default</c>.</summary>
    public DateTimeOffset ReadAt { get; } = Guard.NotDefault(
        ReadAt, nameof(ReadAt), "A roster must carry the time it was read.");
}

/// <summary>
/// How much EMM's last-seen contents for a Retainer can be relied on, judged against the count the
/// bell reports.
///
/// This is the only comparison EMM is permitted to make between the two, and the permission is
/// narrow: the Listing count lags its own container by seconds while a Retainer is open, so a
/// mismatch there says nothing at all. Used as it is here - across the Retainers EMM is <i>not</i>
/// looking at - it answers the one question the bell can answer, which is whether what EMM holds
/// is worth acting on or worth a visit.
/// </summary>
public enum ContentsStanding
{
    /// <summary>EMM has never read this Retainer. It knows the counts and none of the contents.</summary>
    NeverSeen,

    /// <summary>
    /// This Retainer is open now. The count is known to lag the container while it is, so nothing
    /// is concluded from a disagreement.
    /// </summary>
    BeingRead,

    /// <summary>
    /// Its market has lapsed, so the Listing count is not trustworthy and is not read against
    /// anything.
    /// </summary>
    MarketLapsed,

    /// <summary>The bell's Listing count matches what EMM last saw listed.</summary>
    Agrees,

    /// <summary>
    /// The counts differ, so something has moved since EMM last looked. Deliberately not called a
    /// Sale: the count nets, so a Sale and a relist between visits reads as no change at all, and
    /// one number cannot say which of the two happened.
    /// </summary>
    MayHaveMoved,
}

/// <summary>
/// One Retainer as the Holdings surface shows it: what the bell says, what EMM last saw, and
/// whether those two agree.
/// </summary>
/// <param name="Summary">What the bell reports.</param>
/// <param name="Standing">How far EMM's own reading can be relied on.</param>
/// <param name="ListedKnown">How many Listings EMM last saw, or null where it has never looked.</param>
/// <param name="TrueAsOf">
/// When EMM's reading of this Retainer was last true, or null where it has never read it or the
/// Source could not say.
/// </param>
public sealed record RetainerStanding(
    RetainerSummary Summary,
    ContentsStanding Standing,
    int? ListedKnown,
    DateTimeOffset? TrueAsOf);

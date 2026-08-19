namespace EorzeanMarketMaster.Core.Holdings;

/// <summary>
/// One Ware the Player owns, everywhere it is, as one line.
///
/// <b>The three places are carried separately and the total is derived.</b> "I own forty" is the
/// answer to the ticket's question; "twelve of them are listed, twenty are with a Retainer that
/// cannot sell them until a slot frees, and eight are in a bag on the wrong Character" is what the
/// Player actually does something about.
/// </summary>
/// <param name="Ware">The Ware. Never an Item: HQ and NQ are two lines, because they are two prices.</param>
/// <param name="InBags">Units in Character bags.</param>
/// <param name="InStock">Units held by Retainers and not listed.</param>
/// <param name="Listed">Units on a board.</param>
/// <param name="Places">
/// Every row behind the line, so the surface can name the Retainers rather than only counting.
/// Ordered as the ledger orders them.
/// </param>
/// <param name="OldestTrueAsOf">
/// The oldest moment any contributing row was known to be true, or null where at least one of them
/// has no age at all. The weakest link rather than an average: a total is only as current as the
/// stalest thing in it, and averaging two ages describes neither.
/// </param>
/// <param name="AgeUnknown">
/// Whether some contributing row came from a Source that cannot say when it last looked. Kept
/// apart from <paramref name="OldestTrueAsOf"/> so "partly of unknown age" never renders as an age.
/// </param>
public sealed record OwnedWare(
    WareId Ware,
    int InBags,
    int InStock,
    int Listed,
    IReadOnlyList<Holding> Places,
    DateTimeOffset? OldestTrueAsOf,
    bool AgeUnknown)
{
    /// <summary>Every unit of this Ware the Player is known to own, wherever it sits.</summary>
    public int Units => InBags + InStock + Listed;

    /// <summary>The rows behind the line. Never null.</summary>
    public IReadOnlyList<Holding> Places { get; } = Guard.CopyOf(Places, nameof(Places));

    /// <summary>How old the oldest contributing row is, or null where any of them has no age.</summary>
    /// <param name="now">The instant to measure against.</param>
    /// <returns>The age, or null where it is unknown.</returns>
    public TimeSpan? Age(DateTimeOffset now) =>
        AgeUnknown || OldestTrueAsOf is not { } oldest ? null : now - oldest;
}

/// <summary>
/// What the Holdings surface draws: what the Player owns, and how far each Retainer's share of it
/// can be relied on.
///
/// <b>Built from the ledger and the roster together, and neither is optional in the same way.</b>
/// The ledger is what EMM has read and is the only thing that knows contents. The roster is what
/// the bell reports and is the only thing that is current. A view built from the ledger alone
/// would present week-old Listings with no hint they had moved; one built from the roster alone
/// would be a column of numbers naming nothing.
/// </summary>
/// <param name="Wares">Every Ware owned, most units first.</param>
/// <param name="Retainers">
/// Every Retainer the roster reported, with how far EMM's reading of it can be relied on. Empty
/// where no roster has been read - EMM does not invent Retainers out of the readings it happens to
/// hold, because a Retainer the Player has since dismissed would live on forever in that list.
/// </param>
/// <param name="RosterReadAt">When the roster behind this was read, or null where none has been.</param>
public sealed record HoldingsView(
    IReadOnlyList<OwnedWare> Wares,
    IReadOnlyList<RetainerStanding> Retainers,
    DateTimeOffset? RosterReadAt)
{
    /// <summary>Nothing looked at yet.</summary>
    public static HoldingsView Empty { get; } = new([], [], null);

    /// <summary>Every Ware owned. Never null.</summary>
    public IReadOnlyList<OwnedWare> Wares { get; } = Guard.CopyOf(Wares, nameof(Wares));

    /// <summary>Every Retainer the roster reported. Never null.</summary>
    public IReadOnlyList<RetainerStanding> Retainers { get; } = Guard.CopyOf(Retainers, nameof(Retainers));

    /// <summary>How many distinct Wares are owned.</summary>
    public int DistinctWares => Wares.Count;

    /// <summary>How many units are on a board across every Retainer.</summary>
    public int UnitsListed => Wares.Sum(w => w.Listed);

    /// <summary>How many units are owned and not listed - stock and bags together.</summary>
    public int UnitsUnlisted => Wares.Sum(w => w.InBags + w.InStock);

    /// <summary>
    /// The same view over a subset of the rows - one Category, one Retainer, the bags alone.
    ///
    /// <b>The totals are recomputed rather than filtered.</b> A surface showing one Retainer has to
    /// say what that Retainer holds, so every figure on the line - the units, the split across
    /// places, the age of the stalest contributor - is rebuilt from the rows that survived. Hiding
    /// lines while leaving the numbers describing all of them is the kind of quietly wrong figure
    /// this whole ticket is written against.
    ///
    /// The Retainer standings are deliberately NOT filtered. They answer "how far can what EMM
    /// holds be relied on", which is a question about coverage rather than about what is on screen,
    /// and a Player narrowing the list to one Retainer still needs to know the others exist.
    /// </summary>
    /// <param name="keeps">Which rows to count.</param>
    /// <returns>A view over the rows that survived, with every figure rebuilt.</returns>
    public HoldingsView Where(Func<Holding, bool> keeps)
    {
        ArgumentNullException.ThrowIfNull(keeps);

        return new HoldingsView(Roll(Wares.SelectMany(w => w.Places).Where(keeps)), Retainers, RosterReadAt);
    }

    /// <summary>
    /// Rolls the ledger up by Ware and grades every Retainer's coverage against the roster.
    /// </summary>
    /// <param name="ledger">What EMM has read.</param>
    /// <param name="roster">What the bell last reported, or null where it has never been read.</param>
    /// <param name="beingRead">
    /// The Retainer currently open, or null where none is. Its Listing count is known to lag its
    /// own container while it is open, so it is excluded from the comparison rather than reported
    /// as disagreeing every time a Listing changes.
    /// </param>
    /// <param name="now">The instant lapsed markets are judged against.</param>
    /// <returns>The view.</returns>
    public static HoldingsView Build(
        HoldingsLedger ledger,
        RetainerRoster? roster,
        RetainerId? beingRead,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        var wares = Roll(ledger.Holdings());

        var retainers = roster is null
            ? []
            : roster.Retainers
                .Select(summary => Grade(ledger, summary, roster.Character, beingRead, now))
                .ToList();

        return new HoldingsView(wares, retainers, roster?.ReadAt);
    }

    /// <summary>
    /// One line per Ware, most units first.
    ///
    /// The order is what the surface is read in, and the Ware breaks the tie so it is stable rather
    /// than whatever the grouping happened to produce - a list that reshuffled itself between two
    /// frames holding the same Holdings would be unusable.
    /// </summary>
    private static List<OwnedWare> Roll(IEnumerable<Holding> holdings) =>
    [
        .. holdings
            .GroupBy(h => h.Ware)
            .Select(group => new OwnedWare(
                group.Key,
                InBags: UnitsIn(group, HoldingPlace.Bag),
                InStock: UnitsIn(group, HoldingPlace.Stock),
                Listed: UnitsIn(group, HoldingPlace.Listed),
                Places: [.. group],
                OldestTrueAsOf: group.Min(h => h.TrueAsOf),
                AgeUnknown: group.Any(h => h.TrueAsOf is null)))
            .OrderByDescending(w => w.Units)
            .ThenBy(w => w.Ware.ItemId)
            .ThenBy(w => w.Ware.Quality),
    ];

    private static int UnitsIn(IEnumerable<Holding> group, HoldingPlace place) =>
        group.Where(h => h.Place == place).Sum(h => h.Units);

    /// <summary>
    /// One Retainer's standing. The order of the tests is the order of the excuses: a Retainer EMM
    /// has never read has no reading to grade, a Retainer being read now cannot be graded, and a
    /// lapsed market makes the count itself untrustworthy. Only what survives all three is
    /// compared.
    /// </summary>
    private static RetainerStanding Grade(
        HoldingsLedger ledger,
        RetainerSummary summary,
        string character,
        RetainerId? beingRead,
        DateTimeOffset now)
    {
        var reading = ledger.Of(new HoldingsPlaceKey(character, summary.Retainer));

        if (reading is null)
        {
            return new RetainerStanding(summary, ContentsStanding.NeverSeen, null, null);
        }

        var listed = reading.Listed;

        var standing =
            summary.Retainer == beingRead ? ContentsStanding.BeingRead
            : summary.MarketExpiresAt is { } expiry && now > expiry ? ContentsStanding.MarketLapsed
            : listed == summary.MarketItemCount ? ContentsStanding.Agrees
            : ContentsStanding.MayHaveMoved;

        return new RetainerStanding(summary, standing, listed, reading.TrueAsOf);
    }
}

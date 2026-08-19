namespace EorzeanMarketMaster.Core;

/// <summary>
/// Everything the decision engine is allowed to see. Nothing below this line reaches the game,
/// the network, the clock or ImGui: an adapter on the plugin side reads those and fills this in,
/// which is what lets the engine be exercised with no client running.
///
/// The slot list is fixed by the spec. The element types are not all settled yet - several are
/// whole tickets of their own - so where a type below is a bare placeholder, its documentation
/// names the ticket that owns filling it in. Adding fields to these later is an easy change;
/// discovering halfway through #36 that the engine was never given sell space is not, and that
/// asymmetry is the entire reason this ticket exists.
/// </summary>
/// <param name="Instant">
/// When this state was taken. Injected rather than read from the clock, so a fit is reproducible
/// and a test can sit at a chosen moment.
/// </param>
/// <param name="Tier">What EMM can do unaided in this session.</param>
/// <param name="SellSpace">Listing slots per Retainer, and how many are already spoken for. Owned by #35.</param>
/// <param name="Holdings">Every unit of every Ware the Player owns anywhere. Owned by #26.</param>
/// <param name="Snapshots">Observed Listings, each carrying when it was observed. Shape in Observations.cs.</param>
/// <param name="History">Accumulated Sales per Ware per Scope. Owned by #25.</param>
/// <param name="Strategies">Which Strategy applies where, Ware over Group over Retainer. Owned by #34.</param>
/// <param name="Mandates">What EMM may do unattended, and the state of the seven guardrails. Owned by #38.</param>
/// <param name="Levies">Seller Tax per city and the flat Buyer Fee, read live rather than assumed. Owned by #30.</param>
/// <param name="Models">The fitted model state the Estimates are drawn from. Owned by #31 and #33.</param>
public sealed record WorldState(
    DateTimeOffset Instant,
    CapabilityTier Tier,
    IReadOnlyDictionary<RetainerId, SellSpace> SellSpace,
    IReadOnlyList<Holding> Holdings,
    IReadOnlyList<Snapshot> Snapshots,
    IReadOnlyList<History> History,
    StrategyAssignments Strategies,
    MandateState Mandates,
    IReadOnlyList<LevyReading> Levies,
    ModelState Models)
{
    /// <summary>
    /// A world in which EMM can see nothing: no Retainers, no Holdings, no observations, no
    /// permissions. A legitimate state rather than an error - it is what a fresh install at the
    /// title screen looks like, and the engine must return an Outcome for it like any other.
    /// </summary>
    public static WorldState Empty { get; } = new(
        Instant: DateTimeOffset.UnixEpoch,
        Tier: CapabilityTier.Solo,
        SellSpace: new Dictionary<RetainerId, SellSpace>(),
        Holdings: [],
        Snapshots: [],
        History: [],
        Strategies: new StrategyAssignments(),
        Mandates: new MandateState(),
        Levies: [],
        Models: new ModelState());
}

/// <summary>
/// A Retainer's Listing slots. What the Slot Yield allocator is allocating: the scarce thing is
/// the posting, not the gil, so "what deserves this slot" is the question and this is the budget.
/// </summary>
/// <param name="Capacity">Listing slots the Retainer has.</param>
/// <param name="Listed">Slots already holding a live Listing.</param>
public sealed record SellSpace(int Capacity, int Listed)
{
    /// <summary>Slots the allocator has left to fill.</summary>
    public int Free => Capacity - Listed;
}

/// <summary>
/// Units of one Ware the Player owns in one place.
///
/// Placeholder: shape owned by #26 (Holdings) and #27 (Cost Basis and FIFO Lots). Cost Basis in
/// particular is deliberately absent here rather than guessed at - it is recorded per Lot at
/// acquisition, and inventing a single per-Ware figure now would bake in the exact averaging
/// that #27 exists to avoid.
/// </summary>
public sealed record Holding;

/// <summary>
/// The accumulated series of Sales for a Ware at a Scope. EMM builds it: no Source supplies
/// rollups, so every average, band and candle is computed from individual Sales.
///
/// Placeholder: shape owned by #25 (the History series builder).
/// </summary>
public sealed record History;

/// <summary>
/// Which Strategy applies to what. Resolution is Ware over Group over Retainer, with an
/// untouched Group inheriting rather than overriding.
///
/// Placeholder: shape owned by #34 (Strategy and Group with ware-level precedence).
/// </summary>
public sealed record StrategyAssignments;

/// <summary>
/// What EMM has been permitted to do unattended, and the state of the seven guardrails that
/// cannot be configured away. Every Mandate is off until granted.
///
/// Placeholder: shape owned by #38 (Mandates and the seven guardrails).
/// </summary>
public sealed record MandateState;

/// <summary>
/// One Levy rate as read, with the expiry that makes it a reading rather than a constant.
///
/// Which Levy it is has to be carried in the data, not inferred: Seller Tax is deducted from what
/// a seller banks and varies by the city the Retainer is stationed in, Buyer Fee is added on top
/// of the asking price and is flat everywhere, and one transaction carries both. The glossary
/// permits the category word here precisely because this type is the one place that does not yet
/// need to know which - and forbids it everywhere downstream that does.
///
/// Placeholder: shape owned by #30 (Seller Tax and Buyer Fee - two levies, read live).
/// </summary>
public sealed record LevyReading;

/// <summary>
/// The fitted model state every Estimate is drawn from, refitted daily per Ware type on the
/// Player's own World.
///
/// Placeholder: shape owned by #31 (the Estimate) and #33 (band modelling).
/// </summary>
public sealed record ModelState;

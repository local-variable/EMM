namespace EorzeanMarketMaster.Core;

/// <summary>
/// Everything one turn of the decision engine decided. The four collections are the whole of
/// what the seam returns, and each is always present: an engine that had nothing to say says so
/// with an empty list, never with a null.
/// </summary>
/// <param name="Proposals">
/// Ordered. The order is the allocation order - the engine ranks candidates globally rather than
/// per Retainer, so the position of a Proposal in this list carries meaning and must be preserved.
/// </param>
/// <param name="Holds">Where the engine decided to do nothing, and why, and when it will look again.</param>
/// <param name="Notices">Conditions EMM cannot clear unaided. At most one per Retainer.</param>
/// <param name="Estimates">The fitted Unit Prices the decisions above were reached with.</param>
public sealed record Outcome(
    IReadOnlyList<Proposal> Proposals,
    IReadOnlyList<Hold> Holds,
    IReadOnlyList<Notice> Notices,
    IReadOnlyList<Estimate> Estimates)
{
    /// <summary>Ordered. The order is the allocation order and is part of the answer.</summary>
    public IReadOnlyList<Proposal> Proposals { get; } = Guard.CopyOf(Proposals, nameof(Proposals));

    /// <summary>Where the engine decided to do nothing, and why, and when it will look again.</summary>
    public IReadOnlyList<Hold> Holds { get; } = Guard.CopyOf(Holds, nameof(Holds));

    /// <summary>Conditions EMM cannot clear unaided. At most one per Retainer.</summary>
    public IReadOnlyList<Notice> Notices { get; } = Guard.CopyOf(Notices, nameof(Notices));

    /// <summary>The fitted Unit Prices the decisions above were reached with.</summary>
    public IReadOnlyList<Estimate> Estimates { get; } = Guard.CopyOf(Estimates, nameof(Estimates));

    /// <summary>An Outcome that decided nothing. Not an error and not a refusal.</summary>
    public static Outcome Empty { get; } = new([], [], [], []);
}

/// <summary>What a Proposal would do to a Market.</summary>
public enum ProposalKind
{
    /// <summary>Put units of a Ware up for sale for the first time.</summary>
    List,

    /// <summary>Change the Unit Price of an existing Listing.</summary>
    Reprice,

    /// <summary>Put a withdrawn Ware back up for sale.</summary>
    Relist,

    /// <summary>Withdraw a Listing, returning the units to the Retainer.</summary>
    Delist,

    /// <summary>Acquire units of a Ware.</summary>
    Buy,
}

/// <summary>
/// A change EMM has computed but not yet made - the unit of preview, dry run and undo. Where no
/// Mandate covers it the Player sees it before it becomes an act; where a Mandate does, it is
/// still recorded, with the reasoning that produced it, and remains reversible after.
/// </summary>
/// <param name="Kind">Which act this would be.</param>
/// <param name="Retainer">The Retainer that would carry it out.</param>
/// <param name="Ware">The Ware it concerns.</param>
/// <param name="UnitPrice">Gil for one unit.</param>
/// <param name="Stack">Units in the Listing this would create or change.</param>
/// <param name="Reasoning">Why the engine reached this. Recorded whether or not a human ever reads it.</param>
public sealed record Proposal(
    ProposalKind Kind,
    RetainerId Retainer,
    WareId Ware,
    UnitPrice UnitPrice,
    int Stack,
    string Reasoning)
{
    /// <summary>
    /// Why the engine reached this. Never blank: the audit trail an unattended run leaves behind
    /// is only worth having if every row in it says something.
    /// </summary>
    public string Reasoning { get; } =
        Guard.NotBlank(Reasoning, nameof(Reasoning), "A Proposal must carry the reasoning that produced it.");
}

/// <summary>
/// "Do nothing, and here is why, and here is when I will look again." A first-class answer the
/// engine returns rather than an absence it leaves behind.
/// </summary>
/// <param name="Retainer">The Retainer the decision was being made for.</param>
/// <param name="Ware">The Ware the decision was about.</param>
/// <param name="Reason">Why acting was declined. Never blank.</param>
/// <param name="ReviewAt">When the engine will reconsider. Never absent.</param>
public sealed record Hold(
    RetainerId Retainer,
    WareId Ware,
    string Reason,
    DateTimeOffset ReviewAt)
{
    /// <summary>Why acting was declined. Never blank.</summary>
    public string Reason { get; } =
        Guard.NotBlank(Reason, nameof(Reason), "A Hold must carry a reason; it never returns silence.");

    /// <summary>
    /// When the engine will reconsider. Never <c>default</c> - that value is not null, renders as
    /// an ordinary date, and means the review never comes.
    /// </summary>
    public DateTimeOffset ReviewAt { get; } =
        Guard.NotDefault(ReviewAt, nameof(ReviewAt), "A Hold must carry a review time; it never returns silence.");
}

/// <summary>
/// A condition that will persist until the Player attends to it in person. Raised only where no
/// Mandate and no scheduled visit will clear it, which is what separates a Notice from a status.
/// </summary>
public enum NoticeReason
{
    /// <summary>
    /// The Retainer has nothing left to sell. Distinct from <see cref="NothingListed"/>: this one
    /// says the stock ran out, and only the Player can restock it.
    /// </summary>
    SellSpaceOutOfStock,

    /// <summary>The Retainer has no live Listings.</summary>
    NothingListed,

    /// <summary>Gil held by the Retainer is approaching the cap, above which Sales are lost.</summary>
    GilNearRetainerCap,

    /// <summary>Listings have expired, and only a visit revives them.</summary>
    ListingsLapsed,

    /// <summary>A mark could not be re-anchored, so EMM refused to act rather than act on a guess.</summary>
    MarkNotReAnchored,

    /// <summary>An act suspended itself after repeated failure.</summary>
    ActSelfSuspended,

    /// <summary>A Proposal that can only be applied inside a window, with no Mandate to apply it.</summary>
    ProposalNeedsWindow,
}

/// <summary>
/// A statement that EMM cannot proceed unaided and needs the Player at a summoning bell. One per
/// Retainer, carrying every reason at once: a Retainer with three problems is one interruption,
/// not three.
/// </summary>
/// <param name="Retainer">The Retainer that needs attention.</param>
/// <param name="Reasons">Why. Never empty - a Notice with no reason is an alert, which is the thing this is not.</param>
public sealed record Notice(
    RetainerId Retainer,
    IReadOnlyList<NoticeReason> Reasons)
{
    /// <summary>
    /// Why the Player is needed. Never empty, and copied on the way in so the caller cannot empty
    /// it afterwards.
    /// </summary>
    public IReadOnlyList<NoticeReason> Reasons { get; } =
        Guard.NotEmpty(Reasons, nameof(Reasons), "A Notice must carry at least one reason.");
}

/// <summary>
/// The standing acknowledgement that History is a sample of Sales and never a census: the client
/// transmits only a small window of recent sales per visit, and Sales missed between visits can
/// be neither counted nor detected. Every Estimate carries the depth it actually has.
/// </summary>
/// <param name="Rows">Observed Sales behind the Estimate.</param>
/// <param name="DistinctSaleDays">Distinct days those Sales fall on.</param>
/// <param name="DistinctBuyers">Distinct buyers behind them.</param>
public sealed record Sample(
    int Rows,
    int DistinctSaleDays,
    int DistinctBuyers);

/// <summary>
/// How well an Estimate is pinned down - and nothing else. It is not a forecast: it says where
/// the fitted number sits, never where the next Sale will land.
/// </summary>
/// <param name="Low">Lower bound.</param>
/// <param name="High">Upper bound.</param>
public sealed record PrecisionInterval(UnitPrice Low, UnitPrice High);

/// <summary>
/// How an Estimate was reached.
///
/// The spec grades every Estimate on a six-rung severity ladder, and the six rungs are not yet
/// named - that is the pricing ticket's work, not this one's. What is already settled, and what
/// this enum carries until then, is the distinction the honesty rules actually turn on: an
/// Estimate the normal machinery produced, versus a provisional one produced because it could
/// not. A provisional Estimate that becomes a Listing raises an interrupt; a fitted one does not.
/// </summary>
public enum EstimateGrade
{
    /// <summary>Produced by the normal machinery, on a Sample that supported it.</summary>
    Fitted,

    /// <summary>
    /// Produced because the normal machinery could not - a vendor-price or component-derived
    /// fallback. Always says how it was reached, so it is never mistaken for a market price.
    /// </summary>
    Provisional,
}

/// <summary>
/// The pricing engine's fitted Unit Price for a Ware - computed, not observed, which is what
/// separates it from a Reference Price. Never rendered without its Sample, its interval and its
/// grade, so those three are constructor parameters rather than things a caller may omit.
/// </summary>
/// <param name="Ware">What is being priced.</param>
/// <param name="Scope">The breadth it was measured over.</param>
/// <param name="UnitPrice">The fitted figure.</param>
/// <param name="Sample">The depth behind it.</param>
/// <param name="Interval">How well it is pinned down.</param>
/// <param name="Grade">How it was reached.</param>
public sealed record Estimate(
    WareId Ware,
    Scope Scope,
    UnitPrice UnitPrice,
    Sample Sample,
    PrecisionInterval Interval,
    EstimateGrade Grade)
{
    /// <summary>The depth behind the figure. Never absent - a number with nothing behind it is not an Estimate.</summary>
    public Sample Sample { get; } = Guard.NotNull(Sample, nameof(Sample));

    /// <summary>How well the figure is pinned down. Never absent, for the same reason.</summary>
    public PrecisionInterval Interval { get; } = Guard.NotNull(Interval, nameof(Interval));
}

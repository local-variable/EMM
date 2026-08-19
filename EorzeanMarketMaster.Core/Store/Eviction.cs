namespace EorzeanMarketMaster.Core.Store;

/// <summary>
/// How much raw data the store keeps, and how large it may grow.
///
/// The window is what actually bounds the store; the cap is a backstop against pathological
/// growth. At a Ring 1 of 4,000 Wares the 30-day raw window plus daily rollup settles at about
/// 1.05 GB, roughly a tenth of the default cap - so in ordinary use the cap is never reached and
/// the downsample is doing all the work.
/// </summary>
/// <param name="RawSnapshotWindow">
/// How long raw Snapshots are kept before being folded into daily aggregates. Whole weekly
/// partitions are dropped, so the effective window rounds up to the end of the oldest week still
/// inside it.
/// </param>
/// <param name="RawSaleWindow">
/// How long raw Market Sales are kept before being folded. Sixty days because that is the longest
/// active horizon any Ware type uses - gear - and folding inside a horizon would remove rows the
/// estimator is still fitted on.
/// </param>
/// <param name="ByteCap">
/// The most the store may occupy on disk, counting the database and its write-ahead log. Raisable
/// by configuration, deliberately, so that a Player who wants to hoard data can.
/// </param>
public sealed record EvictionPolicy(
    TimeSpan RawSnapshotWindow,
    TimeSpan RawSaleWindow,
    long ByteCap)
{
    /// <summary>The shipped defaults: 30 days of raw Snapshots, 60 of raw Sales, a 10 GB cap.</summary>
    public static EvictionPolicy Default { get; } = new(
        RawSnapshotWindow: TimeSpan.FromDays(30),
        RawSaleWindow: TimeSpan.FromDays(60),
        ByteCap: 10L * 1024 * 1024 * 1024);
}

/// <summary>
/// Where a store stands against its cap once the ladder has run.
/// </summary>
public enum EvictionOutcome
{
    /// <summary>Under the cap before anything was removed. The ladder still runs its window.</summary>
    AlreadyWithinCap,

    /// <summary>Over the cap, and the ladder brought it back under.</summary>
    BroughtWithinCap,

    /// <summary>
    /// Over the cap, and still over it with every rung run.
    ///
    /// EMM stops here rather than reaching into the never-evict floor. What remains is Own Sales,
    /// Cost Basis Lots, the Proposal Ledger, Levy readings, calibration history and the rollups -
    /// none of which any Source can supply again. Deleting them to satisfy a number the Player
    /// chose would be the store quietly destroying the only copy of its own bookkeeping, so it is
    /// reported instead. <see cref="EvictionReport.ProtectedRowCounts"/> says what is holding the
    /// space.
    /// </summary>
    CapExceeded,
}

/// <summary>
/// What one run of the eviction ladder did, measured rather than described.
///
/// The three byte figures are the point of the record and are taken in that order deliberately.
/// A byte cap cannot be enforced by removing rows: deleting half of two million rows was measured
/// leaving the file byte-identical, because freed pages go to the freelist and the file never
/// shrinks on its own. So <see cref="BytesAfterRemoval"/> is expected to equal
/// <see cref="BytesBefore"/>, and it is <see cref="BytesAfterReclaim"/> - the incremental vacuum -
/// that does the enforcing. Reporting all three means a caller can see that, rather than having
/// to know it.
/// </summary>
/// <param name="Outcome">Where the store stands against its cap.</param>
/// <param name="BytesBefore">Size on disk before the ladder ran, with the write-ahead log checkpointed.</param>
/// <param name="BytesAfterRemoval">Size after every rung ran and before any space was reclaimed.</param>
/// <param name="BytesAfterReclaim">Size after the incremental vacuum. The figure the cap is judged against.</param>
/// <param name="ByteCap">The cap in force, carried so the report reads on its own.</param>
/// <param name="PartitionsDropped">Weekly Snapshot partitions removed, by DROP rather than by DELETE.</param>
/// <param name="SnapshotRowsFolded">Raw Snapshot rows folded into daily aggregates before their partitions went.</param>
/// <param name="SaleRowsFolded">Raw Market Sale rows folded into daily aggregates and then removed.</param>
/// <param name="ProtectedRowCounts">
/// Rows remaining in each never-evict table. Present on every report, not only on a breach, so
/// that "the floor never lost a row" is something a caller can check rather than trust.
/// </param>
public sealed record EvictionReport(
    EvictionOutcome Outcome,
    long BytesBefore,
    long BytesAfterRemoval,
    long BytesAfterReclaim,
    long ByteCap,
    IReadOnlyList<StoreWeek> PartitionsDropped,
    int SnapshotRowsFolded,
    int SaleRowsFolded,
    IReadOnlyDictionary<string, long> ProtectedRowCounts)
{
    /// <summary>Weekly partitions removed. Copied on the way in so the caller cannot empty it after.</summary>
    public IReadOnlyList<StoreWeek> PartitionsDropped { get; } =
        Guard.CopyOf(PartitionsDropped, nameof(PartitionsDropped));

    /// <summary>Bytes the incremental vacuum returned to the file system.</summary>
    public long BytesReclaimed => BytesAfterRemoval - BytesAfterReclaim;
}

namespace EorzeanMarketMaster.Core.Store;

/// <summary>
/// Whether losing a table's contents costs a refetch or costs the record itself.
///
/// This is the distinction the whole eviction design turns on, and it runs the opposite way to
/// what a plain "drop the oldest" rule assumes. The replaceable data is the bulk - Market Sales
/// and raw Snapshots, refetchable at rate-limit cost. The irreplaceable data is small, single-digit
/// megabytes: EMM's own bookkeeping, for which there is no event to replay, plus rows read out of
/// another plugin's store that overwrites itself. Evicting globally oldest-first would therefore
/// delete precisely what cannot be recovered while sparing the bulk.
/// </summary>
internal enum RetentionClass
{
    /// <summary>A Source can supply it again. The ladder acts here and nowhere else.</summary>
    Replaceable,

    /// <summary>
    /// Nothing can supply it again. Never evicted; where the cap would still be breached EMM
    /// reports that rather than deleting, per the standing "decline, with a reason" posture.
    /// </summary>
    Irreplaceable,
}

/// <summary>
/// The tables, what class each belongs to, and the ordered migrations that create them.
///
/// Migrations are gated on <c>PRAGMA user_version</c>. Deliberately not EF Core: the tables here
/// are WITHOUT ROWID with the primary key doing the clustering, the weekly partitions and the view
/// over them are created and dropped at runtime, and the whole partition lifecycle is raw SQL by
/// design - so an ORM would have carried the migration numbering and nothing else, in exchange for
/// fourteen extra assemblies and about 5.8 MB inside the game process.
/// </summary>
internal static class StoreSchema
{
    /// <summary>
    /// The schema this build expects. A store below it is migrated forward on open; a store above
    /// it was written by a newer EMM and is refused rather than guessed at.
    /// </summary>
    internal const int Version = 1;

    /// <summary>The view every reader of raw Snapshots goes through, whatever partitions exist.</summary>
    internal const string SnapshotView = "snapshot";

    /// <summary>Daily aggregates of raw Snapshots. The destination of the first eviction rung.</summary>
    internal const string SnapshotDaily = "snapshot_daily";

    /// <summary>Market Sales as observed. Replaceable, and the second rung's subject.</summary>
    internal const string MarketSale = "market_sale";

    /// <summary>Daily aggregates of Market Sales. The destination of the second rung.</summary>
    internal const string MarketSaleDaily = "market_sale_daily";

    /// <summary>
    /// The columns of a raw Snapshot row, in the order every partition and the view over them
    /// declare. One list, because a view whose column order disagrees with its partitions is a
    /// wrong-number bug that no compiler and no test of a single partition would catch.
    /// </summary>
    internal const string SnapshotColumns =
        "item_id, quality, world_id, observed_at, ordinal, unit_price, stack, retainer_name, " +
        "last_reviewed_at, source, uploaded_at";

    /// <summary>
    /// Every table the store holds, and what losing it would cost.
    ///
    /// Weekly Snapshot partitions are not listed: they are created at runtime and are all
    /// Replaceable by construction. Everything here is created by a migration.
    /// </summary>
    internal static IReadOnlyDictionary<string, RetentionClass> Tables { get; } =
        new Dictionary<string, RetentionClass>(StringComparer.Ordinal)
        {
            // Refetchable from the aggregator, and the bulk of the store.
            [MarketSale] = RetentionClass.Replaceable,

            // Both rollups are Irreplaceable, which reads oddly next to the raw tables they are
            // computed from and is nonetheless the truth: once the raw rows are gone the rollup is
            // the only surviving record, and no Source republishes a historical board state or an
            // old Sale window. They are also tiny - a Ring 1 year of raw hourly Snapshots is
            // ~2.96 GB, and ~20.4 MB once rolled up - so protecting them costs nothing.
            [SnapshotDaily] = RetentionClass.Irreplaceable,
            [MarketSaleDaily] = RetentionClass.Irreplaceable,

            // The never-evict floor proper. EMM's own bookkeeping: there is no sale event to
            // replay, a Cost Basis is unrecoverable after acquisition, the Proposal Ledger is the
            // audit trail the guardrails require, a Levy reading is a reading with an expiry and
            // the past rate is never republished, and the calibration history is what a re-measured
            // threshold is judged against.
            ["own_sale"] = RetentionClass.Irreplaceable,
            ["lot"] = RetentionClass.Irreplaceable,
            ["proposal"] = RetentionClass.Irreplaceable,
            ["levy_reading"] = RetentionClass.Irreplaceable,
            ["calibration"] = RetentionClass.Irreplaceable,
        };

    /// <summary>Tables whose rows the eviction ladder may never remove.</summary>
    internal static IReadOnlyList<string> ProtectedTables { get; } =
        [.. Tables.Where(t => t.Value == RetentionClass.Irreplaceable).Select(t => t.Key).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The migrations, in order. Each runs in one transaction and then stamps
    /// <c>user_version</c>, so a store is never left half-migrated.
    ///
    /// Adding a step is how a later ticket extends the schema; steps are append-only and are never
    /// edited once shipped, because a store in the wild has already run them.
    /// </summary>
    internal static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(1,
        [
            // Market Sales as observed.
            //
            // The primary key is the whole row on purpose. History arrives as a union of
            // overlapping windows - the client only ever transmits its most recent 20 sales - so
            // the same Sale is fetched again and again, and a natural key that collapses duplicates
            // is what makes an incremental refresh idempotent. The cost is that two genuinely
            // separate Sales of the same Ware, at the same price, of the same Stack, in the same
            // second, are stored once. That is rare, and undercounting a duplicate is a smaller
            // error than counting a refetch as a new Sale every hour.
            //
            // WITHOUT ROWID with the key leading on the Ware: time-clustered inserts were measured
            // at 106,876 rows/s against 62,855 scattered, and the graph reads exactly this order.
            $"""
             CREATE TABLE {MarketSale} (
                 item_id    INTEGER NOT NULL,
                 quality    INTEGER NOT NULL,
                 world_id   INTEGER NOT NULL,
                 sold_at    INTEGER NOT NULL,
                 unit_price INTEGER NOT NULL,
                 stack      INTEGER NOT NULL,
                 source     INTEGER NOT NULL,
                 PRIMARY KEY (item_id, quality, world_id, sold_at, unit_price, stack)
             ) WITHOUT ROWID
             """,

            // Daily rollup of Market Sales.
            //
            // gil is the sum of unit_price x stack rather than a mean, so that a unit-weighted
            // average survives the fold. A row-weighted one would not: a 1-unit Sale and a 99-unit
            // Sale are not one observation each, and everything downstream measures velocity in
            // units.
            $"""
             CREATE TABLE {MarketSaleDaily} (
                 item_id        INTEGER NOT NULL,
                 quality        INTEGER NOT NULL,
                 world_id       INTEGER NOT NULL,
                 day            INTEGER NOT NULL,
                 sales          INTEGER NOT NULL,
                 units          INTEGER NOT NULL,
                 gil            INTEGER NOT NULL,
                 min_unit_price INTEGER NOT NULL,
                 max_unit_price INTEGER NOT NULL,
                 PRIMARY KEY (item_id, quality, world_id, day)
             ) WITHOUT ROWID
             """,

            // Daily rollup of raw Snapshots.
            //
            // units_last and listings_last come from the last Snapshot of the day rather than from
            // an average across it, because Days of Supply is a statement about how much is on the
            // board, and averaging a board depth across a day describes no moment that existed.
            $"""
             CREATE TABLE {SnapshotDaily} (
                 item_id          INTEGER NOT NULL,
                 quality          INTEGER NOT NULL,
                 world_id         INTEGER NOT NULL,
                 day              INTEGER NOT NULL,
                 observations     INTEGER NOT NULL,
                 min_unit_price   INTEGER,
                 max_unit_price   INTEGER,
                 listings_last    INTEGER NOT NULL,
                 units_last       INTEGER NOT NULL,
                 last_observed_at INTEGER NOT NULL,
                 PRIMARY KEY (item_id, quality, world_id, day)
             ) WITHOUT ROWID
             """,

            // Own Sales. EMM's own detection, and the only kind of Sale that can carry a Cost Basis.
            //
            // sold_at is nullable and that is a finding rather than an oversight: a live session
            // proved the game replays nothing on login, so a Sale made while the Player was logged
            // out has a detection time and no sale time, and writing the detection time into both
            // would manufacture a timestamp.
            //
            // A rowid table, not WITHOUT ROWID: this is an append-only ledger read by id, not a
            // series clustered by Ware and time.
            """
            CREATE TABLE own_sale (
                id             INTEGER PRIMARY KEY,
                character_name TEXT    NOT NULL,
                retainer_name  TEXT    NOT NULL,
                item_id        INTEGER NOT NULL,
                quality        INTEGER NOT NULL,
                world_id       INTEGER NOT NULL,
                unit_price     INTEGER NOT NULL,
                stack          INTEGER NOT NULL,
                detected_at    INTEGER NOT NULL,
                sold_at        INTEGER
            )
            """,

            // Cost Basis, held as acquisition lots so a declared FIFO policy has something to
            // consume. Recorded at acquisition because it cannot be recovered afterwards.
            """
            CREATE TABLE lot (
                id              INTEGER PRIMARY KEY,
                item_id         INTEGER NOT NULL,
                quality         INTEGER NOT NULL,
                units           INTEGER NOT NULL,
                units_remaining INTEGER NOT NULL,
                unit_cost       INTEGER NOT NULL,
                acquired_at     INTEGER NOT NULL
            )
            """,

            // The Proposal Ledger. Mirrors the Proposal record at the decision seam, including its
            // reasoning, which is NOT NULL for the same reason it is guarded there: an audit trail
            // an unattended run leaves behind is only worth having if every row in it says something.
            """
            CREATE TABLE proposal (
                id             INTEGER PRIMARY KEY,
                kind           INTEGER NOT NULL,
                character_name TEXT    NOT NULL,
                retainer_name  TEXT    NOT NULL,
                item_id        INTEGER NOT NULL,
                quality        INTEGER NOT NULL,
                unit_price     INTEGER NOT NULL,
                stack          INTEGER NOT NULL,
                reasoning      TEXT    NOT NULL,
                computed_at    INTEGER NOT NULL,
                applied_at     INTEGER
            )
            """,

            // Levy readings.
            //
            // levy says which of the two this is, and it is stored rather than inferred: the two
            // differ in payer, direction, rate structure and city dependence, and a figure that
            // picks the wrong one is wrong silently.
            //
            // city is NULL for a Levy that is not city-dependent, never 0. Nothing here uses a
            // sentinel inside a numeric range, because the rate itself already taught that lesson:
            // 0 is a legal Seller Tax rate and is also what the aggregator returns when it has no
            // data, and the two cannot be told apart.
            //
            // rate_basis_points, not a percentage as a REAL. Levies are applied to gil, and a
            // binary float is the wrong container for anything that ends up in a Player's purse.
            """
            CREATE TABLE levy_reading (
                id                INTEGER PRIMARY KEY,
                levy              INTEGER NOT NULL,
                city              INTEGER,
                rate_basis_points INTEGER NOT NULL,
                read_at           INTEGER NOT NULL,
                valid_until       INTEGER
            )
            """,

            // Calibration history. The fit itself is rendered by the ticket that owns fitting;
            // what this ticket owns is that it survives eviction, because a threshold that is
            // re-measured rather than typed in needs its own past to be judged against.
            """
            CREATE TABLE calibration (
                id         INTEGER PRIMARY KEY,
                fitted_at  INTEGER NOT NULL,
                ware_type  INTEGER NOT NULL,
                world_id   INTEGER NOT NULL,
                parameters TEXT    NOT NULL
            )
            """,
        ]),
    ];

    /// <summary>
    /// The DDL for one weekly Snapshot partition.
    ///
    /// Not a migration. Partitions are created when an observation for a new week first arrives
    /// and dropped when the week falls out of the raw window, so their lifetime is a runtime
    /// concern and they would be invisible to anyone reading the migrations for the schema. That
    /// is the reason this constant sits next to the migrations rather than somewhere else.
    ///
    /// unit_price and stack are nullable, and that carries meaning rather than laxity. "EMM looked
    /// and nobody was selling this" is a real observation - 42% of the catalogue has nothing
    /// listed on a given World - and it is a different fact from "EMM never looked". An empty
    /// Snapshot is therefore stored as a single row at ordinal 0 whose price and stack are NULL.
    /// NULL rather than 0 because 0 is a gil figure: the Levy work already paid for the lesson
    /// that a sentinel inside a value's own range cannot be told apart from the value.
    /// </summary>
    /// <param name="week">The week the partition holds.</param>
    /// <returns>A CREATE TABLE IF NOT EXISTS statement.</returns>
    internal static string CreatePartition(StoreWeek week) =>
        $"""
         CREATE TABLE IF NOT EXISTS {week.TableName} (
             item_id          INTEGER NOT NULL,
             quality          INTEGER NOT NULL,
             world_id         INTEGER NOT NULL,
             observed_at      INTEGER NOT NULL,
             ordinal          INTEGER NOT NULL,
             unit_price       INTEGER,
             stack            INTEGER,
             retainer_name    TEXT,
             last_reviewed_at INTEGER,
             source           INTEGER NOT NULL,
             uploaded_at      INTEGER,
             PRIMARY KEY (item_id, quality, world_id, observed_at, ordinal)
         ) WITHOUT ROWID
         """;

    /// <summary>
    /// The view over every live partition.
    ///
    /// Readers never name a partition. That is what lets eviction drop one without every query in
    /// EMM having to know which weeks currently exist.
    /// </summary>
    /// <param name="partitions">The live partitions, oldest first.</param>
    /// <returns>A CREATE VIEW statement.</returns>
    internal static string CreateSnapshotView(IReadOnlyList<StoreWeek> partitions)
    {
        if (partitions.Count == 0)
        {
            // A store with no partitions still has to answer a query. Typed NULLs give the view
            // the right column names and affinities while returning nothing, so a reader on a
            // fresh install gets an empty result rather than "no such table".
            var empty = string.Join(", ", SnapshotColumns.Split(", ").Select(c => $"NULL AS {c}"));

            return $"CREATE VIEW {SnapshotView} AS SELECT {empty} WHERE 0";
        }

        var union = string.Join(
            " UNION ALL ",
            partitions.Select(p => $"SELECT {SnapshotColumns} FROM {p.TableName}"));

        return $"CREATE VIEW {SnapshotView} AS {union}";
    }
}

/// <summary>
/// One step of the schema, and the version it leaves the store at.
/// </summary>
/// <param name="Version">The <c>user_version</c> this step stamps once its statements have run.</param>
/// <param name="Statements">The statements, run in order inside one transaction.</param>
internal sealed record MigrationStep(int Version, IReadOnlyList<string> Statements);

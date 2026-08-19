using System.Globalization;
using EorzeanMarketMaster.Core.Holdings;
using Microsoft.Data.Sqlite;

namespace EorzeanMarketMaster.Core.Store;

/// <summary>
/// Where EMM keeps what it observes: a SQLite database that survives a restart, bounds its own
/// growth, and can be asserted against with no game running.
///
/// Three things about it are decisions rather than details, and each is here because the
/// alternative fails quietly.
///
/// <b>Incremental vacuum is set at creation.</b> A byte cap cannot be enforced by deleting rows -
/// deleting half of two million was measured leaving the file byte-identical - so the cap is
/// enforced by <c>PRAGMA incremental_vacuum</c>, and that only works if <c>auto_vacuum</c> was
/// INCREMENTAL before the first table existed. Setting it afterwards is silently ignored. A store
/// without it is refused, not repaired.
///
/// <b>Raw Snapshots are partitioned by week behind a view.</b> They are the bulk of the store and
/// the most disposable part of it. Dropping a week's table was measured at 0.03 s against 8-17 s
/// for the equivalent DELETE, and the DELETE holds a write lock inside a running game. Readers go
/// through the view and never name a partition, which is what lets one be dropped without every
/// query learning which weeks exist.
///
/// <b>The engine is loaded by full path.</b> See <see cref="SqliteEngine"/>; the store simply
/// requires that it has been.
/// </summary>
public sealed class MarketStore : IDisposable
{
    private const long SecondsPerDay = 86_400;

    private readonly SqliteConnection connection;
    private readonly HashSet<StoreWeek> partitions;

    private MarketStore(string path, SqliteConnection connection, int schemaVersion, HashSet<StoreWeek> partitions)
    {
        Path = path;
        this.connection = connection;
        this.partitions = partitions;
        SchemaVersion = schemaVersion;
    }

    /// <summary>The store file.</summary>
    public string Path { get; }

    /// <summary>The schema version the store is at, which after a successful open is the current one.</summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// What the store occupies on disk: the database plus its write-ahead log and shared-memory
    /// file, after checkpointing.
    ///
    /// The checkpoint is not optional. A measurement pass during the design of this store was
    /// discarded for sizing files without one - pages committed but not yet folded back sit in the
    /// WAL, so an uncheckpointed reading describes neither the old file nor the new one.
    /// </summary>
    public long SizeInBytes
    {
        get
        {
            Checkpoint();
            return OnDiskBytes();
        }
    }

    /// <summary>
    /// Opens the store, creating and migrating it as needed.
    /// </summary>
    /// <param name="path">Full path to the store file. Its directory must exist.</param>
    /// <returns>An open store at the current schema version.</returns>
    /// <exception cref="StoreUnusableException">
    /// The store was created without incremental vacuum, or was written by a newer EMM than this
    /// one. Neither is repaired: the first cannot be, and the second would mean guessing at a
    /// schema this build has never seen.
    /// </exception>
    public static MarketStore OpenOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // A zero-length file counts as new. SQLite creates the file before it writes a header, so
        // a run that died between the two would otherwise leave a store that looks existing,
        // fails the auto_vacuum check, and could never be opened again.
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // No pooling. A pooled connection outlives Dispose, and on Windows that keeps a handle
            // on the file after the plugin has been unloaded - which is what makes an in-place
            // plugin update fail while the game is running.
            Pooling = false,
        }.ToString());

        connection.Open();

        try
        {
            if (isNew)
            {
                // Before any table exists, and on this connection. This is the one-way door.
                Execute(connection, "PRAGMA auto_vacuum=INCREMENTAL");
            }

            Execute(connection, "PRAGMA journal_mode=WAL");
            Execute(connection, "PRAGMA foreign_keys=ON");

            var autoVacuum = ScalarLong(connection, "PRAGMA auto_vacuum");

            if (autoVacuum != 2)
            {
                throw new StoreUnusableException(
                    path,
                    $"auto_vacuum reads {autoVacuum} where INCREMENTAL (2) is required. It can only be set " +
                    "before the first table is created, so this store can never reclaim space and cannot " +
                    "enforce a byte cap. Nothing has been changed; move or delete the file to start again.");
            }

            var version = Migrate(connection, path);
            var live = ReadPartitions(connection);

            RebuildSnapshotView(connection, live);

            return new MarketStore(path, connection, version, live);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>The weekly Snapshot partitions that currently exist, oldest first.</summary>
    /// <returns>The live partitions.</returns>
    public IReadOnlyList<StoreWeek> Partitions() =>
        [.. partitions.OrderBy(w => w.Year).ThenBy(w => w.Week)];

    /// <summary>
    /// Records one Snapshot, creating its week's partition if this is the first observation to
    /// land in that week.
    ///
    /// Writing the same observation twice is a no-op rather than a duplicate. Listings are stored
    /// in ascending Unit Price and their ordinal is assigned from that order, so a refetch of an
    /// unchanged board produces byte-for-byte the same rows however the Source happened to order
    /// them.
    /// </summary>
    /// <param name="snapshot">What was observed.</param>
    public void Write(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var week = StoreWeek.Of(snapshot.ObservedAt);

        EnsurePartition(week);

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText =
            $"""
             INSERT OR REPLACE INTO {week.TableName}
                 ({StoreSchema.SnapshotColumns})
             VALUES
                 ($item, $quality, $world, $observed, $ordinal, $price, $stack, $retainer,
                  $reviewed, $source, $uploaded)
             """;

        Parameter(command, "$item", (long)snapshot.Ware.ItemId);
        Parameter(command, "$quality", (long)snapshot.Ware.Quality);
        Parameter(command, "$world", (long)snapshot.World.Id);
        Parameter(command, "$observed", snapshot.ObservedAt.ToUnixTimeSeconds());
        Parameter(command, "$source", (long)snapshot.Source);
        Parameter(command, "$uploaded", StoredMoment(snapshot.UploadedAt));

        var ordinal = Parameter(command, "$ordinal", 0L);
        var price = Parameter(command, "$price", DBNull.Value);
        var stack = Parameter(command, "$stack", DBNull.Value);
        var retainer = Parameter(command, "$retainer", DBNull.Value);
        var reviewed = Parameter(command, "$reviewed", DBNull.Value);

        if (snapshot.Listings.Count == 0)
        {
            // One row, no Listing. "EMM looked and nobody was selling this" is an observation
            // worth keeping, and a Snapshot stored as zero rows would be indistinguishable from
            // one never taken.
            command.ExecuteNonQuery();
        }
        else
        {
            var ordered = snapshot.Listings
                .OrderBy(l => l.UnitPrice.Gil)
                .ThenBy(l => l.Stack)
                .ThenBy(l => l.Retainer ?? string.Empty, StringComparer.Ordinal)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var listing = ordered[i];

                ordinal.Value = (long)i;
                price.Value = listing.UnitPrice.Gil;
                stack.Value = (long)listing.Stack;
                retainer.Value = (object?)listing.Retainer ?? DBNull.Value;
                reviewed.Value = StoredMoment(listing.LastReviewedAt);

                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Records Market Sales, ignoring any already held.
    ///
    /// Duplicates are expected rather than exceptional: the client only ever transmits its most
    /// recent sales, so every refresh re-delivers rows already stored, and an incremental refresh
    /// has to be idempotent for the History to mean anything.
    /// </summary>
    /// <param name="sales">
    /// The Sales observed. Enumerated once, so a backfill can stream a sweep straight in rather
    /// than having to hold it all in memory first.
    /// </param>
    /// <returns>How many rows the store did not already hold.</returns>
    public int Write(IEnumerable<MarketSale> sales)
    {
        ArgumentNullException.ThrowIfNull(sales);

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText =
            $"""
             INSERT OR IGNORE INTO {StoreSchema.MarketSale}
                 (item_id, quality, world_id, sold_at, unit_price, stack, source)
             VALUES
                 ($item, $quality, $world, $sold, $price, $stack, $source)
             """;

        var item = Parameter(command, "$item", 0L);
        var quality = Parameter(command, "$quality", 0L);
        var world = Parameter(command, "$world", 0L);
        var sold = Parameter(command, "$sold", 0L);
        var price = Parameter(command, "$price", 0L);
        var stack = Parameter(command, "$stack", 0L);
        var source = Parameter(command, "$source", 0L);

        var written = 0;

        foreach (var sale in sales)
        {
            item.Value = (long)sale.Ware.ItemId;
            quality.Value = (long)sale.Ware.Quality;
            world.Value = (long)sale.World.Id;
            sold.Value = sale.SoldAt.ToUnixTimeSeconds();
            price.Value = sale.UnitPrice.Gil;
            stack.Value = (long)sale.Stack;
            source.Value = (long)sale.Source;

            written += command.ExecuteNonQuery();
        }

        transaction.Commit();

        return written;
    }

    /// <summary>
    /// Every Snapshot of one Ware in one Market inside a time range, oldest first.
    ///
    /// Read through the view, so which partitions happen to exist is not the caller's problem.
    /// </summary>
    /// <param name="ware">The Ware.</param>
    /// <param name="world">The World whose board was observed.</param>
    /// <param name="from">Inclusive lower bound on the observation time.</param>
    /// <param name="toExclusive">Exclusive upper bound.</param>
    /// <returns>The Snapshots, each with its Listings in ascending Unit Price.</returns>
    public IReadOnlyList<Snapshot> ReadSnapshots(
        WareId ware,
        WorldId world,
        DateTimeOffset from,
        DateTimeOffset toExclusive)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            $"""
             SELECT observed_at, unit_price, stack, retainer_name, last_reviewed_at, source, uploaded_at
             FROM {StoreSchema.SnapshotView}
             WHERE item_id = $item AND quality = $quality AND world_id = $world
               AND observed_at >= $from AND observed_at < $to
             ORDER BY observed_at, ordinal
             """;

        Parameter(command, "$item", (long)ware.ItemId);
        Parameter(command, "$quality", (long)ware.Quality);
        Parameter(command, "$world", (long)world.Id);
        Parameter(command, "$from", from.ToUnixTimeSeconds());
        Parameter(command, "$to", toExclusive.ToUnixTimeSeconds());

        var snapshots = new List<Snapshot>();
        var listings = new List<Listing>();

        long? currentObserved = null;
        var currentSource = Source.Aggregator;
        DateTimeOffset? currentUploaded = null;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var observedAt = reader.GetInt64(0);

            if (currentObserved is { } previous && previous != observedAt)
            {
                snapshots.Add(BuildSnapshot(ware, world, previous, currentSource, currentUploaded, listings));
                listings = [];
            }

            currentObserved = observedAt;
            currentSource = (Source)reader.GetInt64(5);
            currentUploaded = reader.IsDBNull(6)
                ? null
                : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6));

            // A NULL price is the marker for an observation that found nothing listed, so it
            // contributes no Listing rather than a Listing worth nothing.
            if (!reader.IsDBNull(1))
            {
                listings.Add(new Listing(
                    new UnitPrice(reader.GetInt64(1)),
                    (int)reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4))));
            }
        }

        if (currentObserved is { } last)
        {
            snapshots.Add(BuildSnapshot(ware, world, last, currentSource, currentUploaded, listings));
        }

        return snapshots;
    }

    /// <summary>
    /// The most recent Snapshot of one Ware in one Market, or null where there is none.
    ///
    /// Separate from <see cref="ReadSnapshots"/> rather than "read the range and take the last"
    /// because a surface asks this once a second: at an hourly cadence a thirty-day range is
    /// several hundred Snapshots and their Listings, all of them materialised to keep one. This
    /// finds the instant first and reads only that.
    /// </summary>
    /// <param name="ware">The Ware.</param>
    /// <param name="world">The World whose board was observed.</param>
    /// <param name="notBefore">Ignore observations older than this.</param>
    /// <returns>The newest Snapshot in range, or null.</returns>
    public Snapshot? LatestSnapshot(WareId ware, WorldId world, DateTimeOffset notBefore)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            $"""
             SELECT MAX(observed_at) FROM {StoreSchema.SnapshotView}
             WHERE item_id = $item AND quality = $quality AND world_id = $world
               AND observed_at >= $from
             """;

        Parameter(command, "$item", (long)ware.ItemId);
        Parameter(command, "$quality", (long)ware.Quality);
        Parameter(command, "$world", (long)world.Id);
        Parameter(command, "$from", notBefore.ToUnixTimeSeconds());

        var value = command.ExecuteScalar();

        if (value is null or DBNull)
        {
            return null;
        }

        var observedAt = DateTimeOffset.FromUnixTimeSeconds(
            Convert.ToInt64(value, CultureInfo.InvariantCulture));

        // Inclusive of that instant and exclusive one second later, so exactly one observation
        // comes back rather than the whole range.
        var snapshots = ReadSnapshots(ware, world, observedAt, observedAt.AddSeconds(1));

        return snapshots.Count == 0 ? null : snapshots[^1];
    }

    /// <summary>
    /// How old the Source's data already was, each time EMM read this World.
    ///
    /// The raw material for calibrating Freshness against a World rather than against a constant.
    /// It is derived from rows the store already carries - every Snapshot records both when EMM
    /// observed it and when the Source last learned anything - so the calibration costs no schema
    /// and no extra fetch, and it describes the Worlds this Player actually reads rather than a
    /// table shipped with the plugin.
    ///
    /// Observations whose Source reported no upload time contribute nothing and are left out
    /// rather than counted as age zero.
    /// </summary>
    /// <param name="world">The World.</param>
    /// <param name="from">Inclusive lower bound on the observation time.</param>
    /// <param name="toExclusive">Exclusive upper bound.</param>
    /// <returns>One age per observation, unordered.</returns>
    public IReadOnlyList<TimeSpan> ReadUploadAges(
        WorldId world,
        DateTimeOffset from,
        DateTimeOffset toExclusive)
    {
        using var command = connection.CreateCommand();

        // DISTINCT over the observation rather than the row: a Snapshot is stored one row per
        // Listing, and counting a board with forty Listings as forty observations would let the
        // busiest Wares set the whole World's idea of what fresh means.
        command.CommandText =
            $"""
             SELECT DISTINCT item_id, quality, observed_at, uploaded_at
             FROM {StoreSchema.SnapshotView}
             WHERE world_id = $world AND uploaded_at IS NOT NULL
               AND observed_at >= $from AND observed_at < $to
             """;

        Parameter(command, "$world", (long)world.Id);
        Parameter(command, "$from", from.ToUnixTimeSeconds());
        Parameter(command, "$to", toExclusive.ToUnixTimeSeconds());

        var ages = new List<TimeSpan>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            ages.Add(TimeSpan.FromSeconds(reader.GetInt64(2) - reader.GetInt64(3)));
        }

        return ages;
    }

    /// <summary>
    /// Every Ware the store holds a Snapshot of on one World, raw or rolled up.
    ///
    /// What a sweep sweeps, until a later ticket seeds a tracked population of its own. Defined
    /// over Snapshots rather than over Sales because a Snapshot is what a sweep refreshes, and
    /// because the Sale table is the large one - answering this question from it would mean
    /// scanning the bulk of the store to decide what to fetch.
    /// </summary>
    /// <param name="world">The World.</param>
    /// <returns>The Wares, ordered by Item then Quality.</returns>
    public IReadOnlyList<WareId> TrackedWares(WorldId world)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            $"""
             SELECT item_id, quality FROM {StoreSchema.SnapshotView} WHERE world_id = $world
             UNION
             SELECT item_id, quality FROM {StoreSchema.SnapshotDaily} WHERE world_id = $world
             ORDER BY item_id, quality
             """;

        Parameter(command, "$world", (long)world.Id);

        var wares = new List<WareId>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            wares.Add(new WareId((uint)reader.GetInt64(0), (Quality)reader.GetInt64(1)));
        }

        return wares;
    }

    /// <summary>
    /// When the newest Sale the store holds for one Ware in one Market happened.
    ///
    /// What makes a history refresh incremental rather than a re-download: the window starts just
    /// before this instead of at the beginning of the series, which is the difference between a
    /// few kilobytes and the whole backfill every time.
    /// </summary>
    /// <param name="ware">The Ware.</param>
    /// <param name="world">The World.</param>
    /// <returns>The newest Sale's instant, or null where the store holds none.</returns>
    public DateTimeOffset? NewestSaleAt(WareId ware, WorldId world)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            $"""
             SELECT MAX(sold_at) FROM {StoreSchema.MarketSale}
             WHERE item_id = $item AND quality = $quality AND world_id = $world
             """;

        Parameter(command, "$item", (long)ware.ItemId);
        Parameter(command, "$quality", (long)ware.Quality);
        Parameter(command, "$world", (long)world.Id);

        var value = command.ExecuteScalar();

        return value is null or DBNull
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Every Sale of one Ware in one Market inside a time range, as one series.
    ///
    /// Returns a <see cref="History"/> rather than a bare list because the series is what callers
    /// want and building it from a list is the step somebody eventually forgets - and the History
    /// is what carries the rule that a Sale of another Ware or another Market cannot be in it.
    ///
    /// The rows come back in the order the table already clusters them, which is the order the
    /// graph reads: the primary key leads on the Ware and then the instant, so this is a range
    /// scan rather than a sort.
    /// </summary>
    /// <param name="ware">The Ware.</param>
    /// <param name="world">The World whose Market the Sales happened in.</param>
    /// <param name="from">Inclusive lower bound on the sale time.</param>
    /// <param name="toExclusive">Exclusive upper bound.</param>
    /// <returns>The series, empty where the store holds nothing in range.</returns>
    public History ReadSales(
        WareId ware,
        WorldId world,
        DateTimeOffset from,
        DateTimeOffset toExclusive)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            $"""
             SELECT sold_at, unit_price, stack, source
             FROM {StoreSchema.MarketSale}
             WHERE item_id = $item AND quality = $quality AND world_id = $world
               AND sold_at >= $from AND sold_at < $to
             ORDER BY sold_at
             """;

        Parameter(command, "$item", (long)ware.ItemId);
        Parameter(command, "$quality", (long)ware.Quality);
        Parameter(command, "$world", (long)world.Id);
        Parameter(command, "$from", from.ToUnixTimeSeconds());
        Parameter(command, "$to", toExclusive.ToUnixTimeSeconds());

        var sales = new List<MarketSale>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            sales.Add(new MarketSale(
                ware,
                world,
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                new UnitPrice(reader.GetInt64(1)),
                (int)reader.GetInt64(2),
                (Source)reader.GetInt64(3)));
        }

        return new History(ware, world, sales);
    }

    /// <summary>
    /// When the oldest Sale the store holds for one Ware in one Market happened.
    ///
    /// What bounds an "all of History" window. Asked separately rather than read off a full range
    /// scan because the answer is one index seek and the scan it would replace is the entire
    /// series - which for a liquid Ware is thousands of rows fetched to look at the first.
    /// </summary>
    /// <param name="ware">The Ware.</param>
    /// <param name="world">The World.</param>
    /// <returns>The oldest Sale's instant, or null where the store holds none.</returns>
    public DateTimeOffset? OldestSaleAt(WareId ware, WorldId world)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            $"""
             SELECT MIN(sold_at) FROM {StoreSchema.MarketSale}
             WHERE item_id = $item AND quality = $quality AND world_id = $world
             """;

        Parameter(command, "$item", (long)ware.ItemId);
        Parameter(command, "$quality", (long)ware.Quality);
        Parameter(command, "$world", (long)world.Id);

        var value = command.ExecuteScalar();

        return value is null or DBNull
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Records what EMM saw in one place, replacing whatever it last saw there.
    ///
    /// <b>A replace, never a merge, and the transaction is what makes that true.</b> A reading is
    /// the complete contents of its place, so the previous rows go before the new ones arrive - a
    /// Ware that has since sold has to disappear, and an insert-or-update would leave it behind
    /// forever. The delete and the insert are one transaction because a crash between them would
    /// leave the Player's own Retainer looking empty.
    /// </summary>
    /// <param name="reading">What was seen. Empty contents are recorded as such, not skipped.</param>
    public void WriteHoldings(HoldingsReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var character = reading.Character;
        var retainer = reading.Retainer?.Retainer ?? string.Empty;

        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText =
                $"""
                 DELETE FROM {StoreSchema.Holding}
                 WHERE character_name = $character AND retainer_name = $retainer
                 """;

            Parameter(clear, "$character", character);
            Parameter(clear, "$retainer", retainer);
            clear.ExecuteNonQuery();
        }

        using (var header = connection.CreateCommand())
        {
            // An upsert rather than INSERT OR REPLACE. REPLACE deletes the row before reinserting
            // it, and the rows above hang off it by a cascading foreign key - so the tidy-looking
            // spelling would delete the contents this statement is being written to keep.
            header.Transaction = transaction;
            header.CommandText =
                $"""
                 INSERT INTO {StoreSchema.HoldingReading}
                     (character_name, retainer_name, observed_at, true_as_of, source)
                 VALUES ($character, $retainer, $observed, $true, $source)
                 ON CONFLICT (character_name, retainer_name) DO UPDATE SET
                     observed_at = excluded.observed_at,
                     true_as_of = excluded.true_as_of,
                     source = excluded.source
                 """;

            Parameter(header, "$character", character);
            Parameter(header, "$retainer", retainer);
            Parameter(header, "$observed", reading.ObservedAt.ToUnixTimeSeconds());
            Parameter(header, "$true", StoredMoment(reading.TrueAsOf));
            Parameter(header, "$source", (long)reading.Source);
            header.ExecuteNonQuery();
        }

        if (reading.Held.Count > 0)
        {
            using var insert = connection.CreateCommand();

            insert.Transaction = transaction;
            insert.CommandText =
                $"""
                 INSERT INTO {StoreSchema.Holding}
                     (character_name, retainer_name, place, item_id, quality, ordinal, units, unit_price)
                 VALUES ($character, $retainer, $place, $item, $quality, $ordinal, $units, $price)
                 """;

            Parameter(insert, "$character", character);
            Parameter(insert, "$retainer", retainer);

            var place = Parameter(insert, "$place", 0L);
            var item = Parameter(insert, "$item", 0L);
            var quality = Parameter(insert, "$quality", 0L);
            var ordinal = Parameter(insert, "$ordinal", 0L);
            var units = Parameter(insert, "$units", 0L);
            var price = Parameter(insert, "$price", DBNull.Value);

            foreach (var line in reading.Held)
            {
                place.Value = (long)line.Place;
                item.Value = (long)line.Ware.ItemId;
                quality.Value = (long)line.Ware.Quality;
                ordinal.Value = (long)line.Ordinal;
                units.Value = (long)line.Units;
                price.Value = line.AskingPrice is { } asking ? asking.Gil : DBNull.Value;

                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Every place EMM has read Holdings in, with what it saw there.
    ///
    /// Read whole rather than by Character: the surface answers "what do I own" across every
    /// Character, the whole table is bounded by the number of Retainers a Player has, and a query
    /// per place would be thirty round trips to answer one question.
    /// </summary>
    /// <returns>The readings, ordered by Character then Retainer.</returns>
    public IReadOnlyList<HoldingsReading> ReadHoldings()
    {
        var lines = new Dictionary<(string Character, string Retainer), List<HeldWare>>();

        using (var contents = connection.CreateCommand())
        {
            contents.CommandText =
                $"""
                 SELECT character_name, retainer_name, place, item_id, quality, ordinal, units, unit_price
                 FROM {StoreSchema.Holding}
                 ORDER BY character_name, retainer_name, place, item_id, quality, ordinal
                 """;

            using var reader = contents.ExecuteReader();

            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));

                if (!lines.TryGetValue(key, out var held))
                {
                    held = [];
                    lines[key] = held;
                }

                held.Add(new HeldWare(
                    new WareId((uint)reader.GetInt64(3), (Quality)reader.GetInt64(4)),
                    (HoldingPlace)reader.GetInt64(2),
                    (int)reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : new UnitPrice(reader.GetInt64(7)))
                {
                    Ordinal = (int)reader.GetInt64(5),
                });
            }
        }

        var readings = new List<HoldingsReading>();

        using (var headers = connection.CreateCommand())
        {
            headers.CommandText =
                $"""
                 SELECT character_name, retainer_name, observed_at, true_as_of, source
                 FROM {StoreSchema.HoldingReading}
                 ORDER BY character_name, retainer_name
                 """;

            using var reader = headers.ExecuteReader();

            while (reader.Read())
            {
                var character = reader.GetString(0);
                var retainer = reader.GetString(1);

                readings.Add(new HoldingsReading(
                    character,

                    // The empty string is the Character's own bags. It cannot be a Retainer name,
                    // which is what lets one column carry both without a second flag.
                    retainer.Length == 0 ? null : new RetainerId(character, retainer),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                    reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                    (Source)reader.GetInt64(4),
                    lines.TryGetValue((character, retainer), out var held) ? held : []));
            }
        }

        return readings;
    }

    /// <summary>
    /// Removes one week of raw Snapshots by dropping its table, and rebuilds the view over what is
    /// left.
    ///
    /// A DROP, never a DELETE. That is the whole reason the partitions exist: the same removal as
    /// a DELETE was measured at 8-17 s holding a write lock, against 0.03 s here, and this runs
    /// while the Player is in a dungeon.
    /// </summary>
    /// <param name="week">The week to remove.</param>
    /// <returns>Whether a partition for that week existed.</returns>
    public bool DropPartition(StoreWeek week)
    {
        if (!partitions.Contains(week))
        {
            return false;
        }

        Execute(connection, $"DROP TABLE {week.TableName}");
        partitions.Remove(week);
        RebuildSnapshotView(connection, partitions);

        return true;
    }

    /// <summary>
    /// Returns free pages to the file system.
    ///
    /// This is what enforces the byte cap. Removing rows does not: freed pages go to the freelist
    /// and the file keeps its size, which is why every figure in <see cref="EvictionReport"/> is
    /// taken either side of this call.
    /// </summary>
    /// <returns>Bytes the file gave back.</returns>
    public long ReclaimFreeSpace()
    {
        Checkpoint();
        var before = OnDiskBytes();

        // One statement, run to completion. Incremental vacuum emits a row per page freed, and a
        // driver that stopped reading after the first row would free exactly one page and look
        // like it had worked. ExecuteNonQuery steps it fully - measured, not assumed, because the
        // driver this design was prototyped against did not.
        Execute(connection, "PRAGMA incremental_vacuum");
        Checkpoint();

        return before - OnDiskBytes();
    }

    /// <summary>
    /// Runs the eviction ladder and reports what it did.
    ///
    /// The ladder is class-aware and only ever descends the replaceable side. Raw Snapshots past
    /// the window fold into daily aggregates and their partitions are dropped; raw Market Sales
    /// past the horizon fold and are removed. Below that is the never-evict floor - Own Sales,
    /// Cost Basis Lots, the Proposal Ledger, Levy readings, calibration history, and the rollups
    /// which are the only surviving record of everything already folded. If the cap is still
    /// breached with those left, EMM says so rather than deleting them.
    /// </summary>
    /// <param name="policy">The windows and the cap.</param>
    /// <param name="now">The moment the windows are measured back from. Injected, never a clock read.</param>
    /// <returns>What was removed, and where the store stands against its cap.</returns>
    public EvictionReport Evict(EvictionPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);

        Checkpoint();

        var before = OnDiskBytes();
        var wasWithinCap = before <= policy.ByteCap;

        var dropped = new List<StoreWeek>();
        var snapshotRows = 0;

        // Rung 1. A partition goes only when its whole week is outside the window: weeks are the
        // unit that can be dropped, so a week straddling the boundary keeps all of its rows rather
        // than losing the older half to a DELETE.
        var snapshotCutoff = now - policy.RawSnapshotWindow;

        foreach (var week in Partitions().Where(w => w.EndExclusive <= snapshotCutoff))
        {
            snapshotRows += FoldPartitionIntoDaily(week);
            DropPartition(week);
            dropped.Add(week);
        }

        // Rung 2. A single table rather than partitions, because the whole catalogue's Sale
        // history for 180 days is around 105 MB - small enough that the partition machinery would
        // cost more than it saved.
        var saleRows = FoldSalesBefore(now - policy.RawSaleWindow);

        Checkpoint();

        var afterRemoval = OnDiskBytes();

        ReclaimFreeSpace();

        var afterReclaim = OnDiskBytes();

        var outcome = afterReclaim <= policy.ByteCap
            ? wasWithinCap ? EvictionOutcome.AlreadyWithinCap : EvictionOutcome.BroughtWithinCap
            : EvictionOutcome.CapExceeded;

        return new EvictionReport(
            outcome,
            before,
            afterRemoval,
            afterReclaim,
            policy.ByteCap,
            dropped,
            snapshotRows,
            saleRows,
            ProtectedRowCounts());
    }

    /// <summary>
    /// Rows held in each table the ladder may never touch.
    /// </summary>
    /// <returns>Table name to row count, for every never-evict table.</returns>
    public IReadOnlyDictionary<string, long> ProtectedRowCounts()
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var table in StoreSchema.ProtectedTables)
        {
            // The table names come from StoreSchema's own compile-time list, never from a caller.
            counts[table] = ScalarLong(connection, $"SELECT COUNT(*) FROM {table}");
        }

        return counts;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Checkpoint();
        connection.Dispose();
    }

    private static Snapshot BuildSnapshot(
        WareId ware,
        WorldId world,
        long observedAt,
        Source source,
        DateTimeOffset? uploadedAt,
        IReadOnlyList<Listing> listings) =>
        new(ware, world, DateTimeOffset.FromUnixTimeSeconds(observedAt), source, uploadedAt, listings);

    private void EnsurePartition(StoreWeek week)
    {
        if (partitions.Contains(week))
        {
            return;
        }

        Execute(connection, StoreSchema.CreatePartition(week));
        partitions.Add(week);
        RebuildSnapshotView(connection, partitions);
    }

    /// <summary>
    /// Folds one partition's rows into <c>snapshot_daily</c>.
    ///
    /// A day never spans two ISO weeks, so in the ordinary run of things each partition contributes
    /// days no other partition has. The conflict clause is not therefore dead: a Snapshot can
    /// arrive late for a week already folded and dropped, which recreates the partition, and the
    /// next eviction folds those days a second time. It has to add rather than overwrite.
    /// </summary>
    private int FoldPartitionIntoDaily(StoreWeek week)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            $"""
             WITH per_observation AS (
                 SELECT item_id, quality, world_id,
                        (observed_at / {SecondsPerDay}) * {SecondsPerDay} AS day,
                        observed_at,
                        COUNT(unit_price) AS listings,
                        COALESCE(SUM(stack), 0) AS units,
                        MIN(unit_price) AS min_price,
                        MAX(unit_price) AS max_price
                 FROM {week.TableName}
                 GROUP BY item_id, quality, world_id, observed_at
             ),
             ranked AS (
                 SELECT *, ROW_NUMBER() OVER (
                            PARTITION BY item_id, quality, world_id, day
                            ORDER BY observed_at DESC) AS recency
                 FROM per_observation
             ),
             per_day AS (
                 SELECT item_id, quality, world_id, day,
                        COUNT(*) AS observations,
                        MIN(min_price) AS min_unit_price,
                        MAX(max_price) AS max_unit_price,
                        MAX(observed_at) AS last_observed_at
                 FROM per_observation
                 GROUP BY item_id, quality, world_id, day
             )
             INSERT INTO {StoreSchema.SnapshotDaily}
                 (item_id, quality, world_id, day, observations, min_unit_price, max_unit_price,
                  listings_last, units_last, last_observed_at)
             SELECT d.item_id, d.quality, d.world_id, d.day, d.observations,
                    d.min_unit_price, d.max_unit_price,
                    r.listings, r.units, d.last_observed_at
             FROM per_day d
             JOIN ranked r
               ON r.item_id = d.item_id AND r.quality = d.quality
              AND r.world_id = d.world_id AND r.day = d.day AND r.recency = 1
             WHERE TRUE
             ON CONFLICT (item_id, quality, world_id, day) DO UPDATE SET
                 observations = {StoreSchema.SnapshotDaily}.observations + excluded.observations,
                 min_unit_price = MIN(
                     IFNULL({StoreSchema.SnapshotDaily}.min_unit_price, excluded.min_unit_price),
                     IFNULL(excluded.min_unit_price, {StoreSchema.SnapshotDaily}.min_unit_price)),
                 max_unit_price = MAX(
                     IFNULL({StoreSchema.SnapshotDaily}.max_unit_price, excluded.max_unit_price),
                     IFNULL(excluded.max_unit_price, {StoreSchema.SnapshotDaily}.max_unit_price)),
                 listings_last = CASE
                     WHEN excluded.last_observed_at >= {StoreSchema.SnapshotDaily}.last_observed_at
                     THEN excluded.listings_last ELSE {StoreSchema.SnapshotDaily}.listings_last END,
                 units_last = CASE
                     WHEN excluded.last_observed_at >= {StoreSchema.SnapshotDaily}.last_observed_at
                     THEN excluded.units_last ELSE {StoreSchema.SnapshotDaily}.units_last END,
                 last_observed_at = MAX(
                     {StoreSchema.SnapshotDaily}.last_observed_at, excluded.last_observed_at)
             """;

        var rows = ScalarLong(connection, $"SELECT COUNT(*) FROM {week.TableName}");

        command.ExecuteNonQuery();

        return (int)rows;
    }

    private int FoldSalesBefore(DateTimeOffset cutoff)
    {
        var seconds = cutoff.ToUnixTimeSeconds();
        var rows = (int)ScalarLong(
            connection,
            $"SELECT COUNT(*) FROM {StoreSchema.MarketSale} WHERE sold_at < {seconds.ToString(CultureInfo.InvariantCulture)}");

        if (rows == 0)
        {
            return 0;
        }

        using var transaction = connection.BeginTransaction();

        using (var fold = connection.CreateCommand())
        {
            fold.Transaction = transaction;
            fold.CommandText =
                $"""
                 INSERT INTO {StoreSchema.MarketSaleDaily}
                     (item_id, quality, world_id, day, sales, units, gil, min_unit_price, max_unit_price)
                 SELECT item_id, quality, world_id,
                        (sold_at / {SecondsPerDay}) * {SecondsPerDay} AS day,
                        COUNT(*), SUM(stack), SUM(unit_price * stack),
                        MIN(unit_price), MAX(unit_price)
                 FROM {StoreSchema.MarketSale}
                 WHERE sold_at < $cutoff
                 GROUP BY item_id, quality, world_id, day
                 ON CONFLICT (item_id, quality, world_id, day) DO UPDATE SET
                     sales = {StoreSchema.MarketSaleDaily}.sales + excluded.sales,
                     units = {StoreSchema.MarketSaleDaily}.units + excluded.units,
                     gil = {StoreSchema.MarketSaleDaily}.gil + excluded.gil,
                     min_unit_price = MIN({StoreSchema.MarketSaleDaily}.min_unit_price, excluded.min_unit_price),
                     max_unit_price = MAX({StoreSchema.MarketSaleDaily}.max_unit_price, excluded.max_unit_price)
                 """;

            Parameter(fold, "$cutoff", seconds);
            fold.ExecuteNonQuery();
        }

        using (var purge = connection.CreateCommand())
        {
            purge.Transaction = transaction;
            purge.CommandText = $"DELETE FROM {StoreSchema.MarketSale} WHERE sold_at < $cutoff";
            Parameter(purge, "$cutoff", seconds);
            purge.ExecuteNonQuery();
        }

        transaction.Commit();

        return rows;
    }

    private static int Migrate(SqliteConnection connection, string path)
    {
        var current = (int)ScalarLong(connection, "PRAGMA user_version");

        if (current > StoreSchema.Version)
        {
            throw new StoreUnusableException(
                path,
                $"its schema is version {current} and this build of EMM knows version " +
                $"{StoreSchema.Version}. A newer EMM wrote it; downgrading would mean guessing at a " +
                "schema this build has never seen.");
        }

        foreach (var step in StoreSchema.Steps.Where(s => s.Version > current).OrderBy(s => s.Version))
        {
            using var transaction = connection.BeginTransaction();

            foreach (var statement in step.Statements)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            // Stamped inside the same transaction as the statements it describes, so the store is
            // never left carrying tables it does not admit to or a version it does not have.
            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = $"PRAGMA user_version = {step.Version.ToString(CultureInfo.InvariantCulture)}";
                stamp.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return (int)ScalarLong(connection, "PRAGMA user_version");
    }

    private static HashSet<StoreWeek> ReadPartitions(SqliteConnection connection)
    {
        var found = new HashSet<StoreWeek>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'snapshot_%'";

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            // TryParse is what separates a partition from snapshot_daily, rather than a second
            // LIKE clause that would have to be kept in step with every table added later.
            if (StoreWeek.TryParse(reader.GetString(0), out var week))
            {
                found.Add(week);
            }
        }

        return found;
    }

    private static void RebuildSnapshotView(SqliteConnection connection, IEnumerable<StoreWeek> live)
    {
        var ordered = live.OrderBy(w => w.Year).ThenBy(w => w.Week).ToList();

        Execute(connection, $"DROP VIEW IF EXISTS {StoreSchema.SnapshotView}");
        Execute(connection, StoreSchema.CreateSnapshotView(ordered));
    }

    private void Checkpoint() => Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");

    private long OnDiskBytes()
    {
        long total = 0;

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = new FileInfo(Path + suffix);

            if (file.Exists)
            {
                total += file.Length;
            }
        }

        return total;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return command.ExecuteScalar() switch
        {
            long value => value,
            int value => value,
            null or DBNull => 0,
            var other => Convert.ToInt64(other, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// A moment as SQLite stores it, or DBNull where there is none.
    ///
    /// Written out rather than inlined because the conditional form has to be typed as object at
    /// every call site or the compiler picks a common type between a long and a DBNull and finds
    /// none - and the version that compiles by accident is the one that writes "0" for "unknown".
    /// </summary>
    private static object StoredMoment(DateTimeOffset? instant) =>
        instant is { } value ? value.ToUnixTimeSeconds() : DBNull.Value;

    private static SqliteParameter Parameter(SqliteCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);

        return parameter;
    }
}

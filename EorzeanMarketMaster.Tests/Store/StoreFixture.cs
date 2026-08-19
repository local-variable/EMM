using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Store;
using Microsoft.Data.Sqlite;

namespace EorzeanMarketMaster.Tests.Store;

/// <summary>
/// What the store tests need before they can assert anything: a real native engine, and a fresh
/// file to put a database in.
///
/// The store is tested against real SQLite on a real file rather than against an abstraction of
/// it, because every measurement this ticket rests on is a fact about the file - that DELETE does
/// not shrink it, that incremental vacuum does, that dropping a partition costs milliseconds
/// where deleting its rows costs seconds. A fake store would assert none of that.
/// </summary>
internal static class StoreFixture
{
    /// <summary>
    /// The engine the tests load, in the layout the packaged plugin also uses: beside the
    /// assembly, not under runtimes/. Pinned there by RuntimeIdentifier in both project files, so
    /// a test passing here is evidence about the shipped arrangement rather than about a
    /// developer-only one.
    /// </summary>
    internal static string NativeLibraryPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");

    /// <summary>
    /// Loads the engine once per test process. The provider is process-wide and xunit runs
    /// classes in parallel, so this has to be safe to call from anywhere at any time.
    /// </summary>
    internal static void EnsureEngineLoaded() => SqliteEngine.LoadFrom(NativeLibraryPath);

    /// <summary>
    /// A path in a fresh temp directory where no file exists yet.
    /// </summary>
    /// <returns>The store path, and the directory to delete afterwards.</returns>
    internal static TempStore NewStorePath()
    {
        EnsureEngineLoaded();

        var directory = Path.Combine(Path.GetTempPath(), $"emm-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        return new TempStore(directory, Path.Combine(directory, "market.db"));
    }

    /// <summary>A fixed instant, so nothing under test ever reads a clock.</summary>
    internal static readonly DateTimeOffset Instant = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Reads rows back on a separate read-only connection.
    ///
    /// Separate on purpose: what these tests need to know is what survived a commit, and a query
    /// on the store's own connection would happily report uncommitted work as though it had.
    /// </summary>
    /// <param name="path">The store file.</param>
    /// <param name="sql">The query.</param>
    /// <returns>The rows, as raw column values.</returns>
    internal static IReadOnlyList<object[]> Read(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var rows = new List<object[]>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }

        return rows;
    }

    /// <summary>
    /// The same read, after checkpointing an open store so the write-ahead log is folded back into
    /// the file the read-only connection will see.
    /// </summary>
    /// <param name="store">The open store, checkpointed before the read.</param>
    /// <param name="path">The store file.</param>
    /// <param name="sql">The query.</param>
    /// <returns>The rows, as raw column values.</returns>
    internal static IReadOnlyList<object[]> Read(MarketStore store, string path, string sql)
    {
        _ = store.SizeInBytes;

        return Read(path, sql);
    }

    /// <summary>One pragma, as a number.</summary>
    /// <param name="path">The store file.</param>
    /// <param name="name">The pragma.</param>
    /// <returns>Its value, or 0 where it has none.</returns>
    internal static long Pragma(string path, string name)
    {
        var rows = Read(path, $"PRAGMA {name}");

        return rows.Count == 0 ? 0 : Convert.ToInt64(rows[0][0]);
    }

    /// <summary>One pragma, as text.</summary>
    /// <param name="path">The store file.</param>
    /// <param name="name">The pragma.</param>
    /// <returns>Its value, or the empty string where it has none.</returns>
    internal static string PragmaText(string path, string name)
    {
        var rows = Read(path, $"PRAGMA {name}");

        return rows.Count == 0 ? string.Empty : rows[0][0]?.ToString() ?? string.Empty;
    }

    /// <summary>Every table the store file holds, partitions included.</summary>
    /// <param name="path">The store file.</param>
    /// <returns>The table names.</returns>
    internal static IReadOnlyList<string> TableNames(string path) =>
        [.. Read(path, "SELECT name FROM sqlite_master WHERE type = 'table'").Select(row => (string)row[0])];

    /// <summary>The Ware the round-trip is asserted on.</summary>
    internal static readonly WareId Ware = new(5057, Quality.High);

    /// <summary>One World. Cactuar's id, so a failure message names something real.</summary>
    internal static readonly WorldId World = new(79);
}

/// <summary>
/// A store path in a directory this owns, removed on dispose along with any WAL and shared-memory
/// files SQLite left beside it.
/// </summary>
/// <param name="Directory">The temp directory holding the store.</param>
/// <param name="Path">The store file itself.</param>
internal sealed record TempStore(string Directory, string Path) : IDisposable
{
    /// <summary>The store file's size in bytes, or 0 where it does not exist yet.</summary>
    internal long SizeInBytes => File.Exists(Path) ? new FileInfo(Path).Length : 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}

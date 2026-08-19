using System;
using System.IO;
using EorzeanMarketMaster.Core.Store;

namespace EorzeanMarketMaster.Store;

/// <summary>
/// The plugin side of the store: where the engine is, where the database goes, and what happens
/// when either of those is wrong.
///
/// Core holds the store itself and knows nothing about Dalamud. What lives here is the two paths
/// only the plugin can supply - the native library beside this assembly, and the per-plugin
/// configuration directory the launcher hands out - and the decision that neither being usable is
/// allowed to take EMM down with it.
///
/// That last part is deliberate. EMM without a store is badly hobbled but not useless: it can
/// still read a board, still show what it sees, and still say why it is not remembering any of it.
/// A plugin that failed to load would instead present as broken, with the real reason buried in
/// the launcher's log.
/// </summary>
internal sealed class StoreHost : IDisposable
{
    /// <summary>
    /// The store file's name inside the plugin's configuration directory. Permanent: the file
    /// carries a Player's own bookkeeping, and renaming it later would orphan it.
    /// </summary>
    private const string StoreFileName = "market.db";

    private StoreHost(MarketStore? store, string status)
    {
        Store = store;
        Status = status;
    }

    /// <summary>The open store, or null where it could not be opened.</summary>
    internal MarketStore? Store { get; }

    /// <summary>
    /// One line for the log saying what happened. Not for the status strip: whether the engine
    /// loaded is a maintainer's concern, and the Player-facing wording for a hobbled EMM is copy
    /// nobody has approved.
    /// </summary>
    internal string Status { get; }

    /// <summary>
    /// Loads the engine and opens the store, reporting rather than throwing.
    /// </summary>
    /// <param name="assemblyDirectory">The directory this plugin's assemblies were laid down in.</param>
    /// <param name="configDirectory">The plugin's own configuration directory.</param>
    /// <returns>A host, with or without a store.</returns>
    internal static StoreHost Open(string assemblyDirectory, string configDirectory)
    {
        // Beside the plugin, by full path, and never by bare name. Dalamud carries its own
        // e_sqlite3 in this very process, and Windows resolves a native import by base name
        // against what is already loaded - so the bare-name form would bind EMM to the host's
        // copy silently and successfully, which is worse than failing.
        var enginePath = Path.Combine(assemblyDirectory, "e_sqlite3.dll");

        try
        {
            SqliteEngine.LoadFrom(enginePath);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EMM could not load its own SQLite engine from {Path}", enginePath);

            return new StoreHost(null, $"store unavailable - engine not loaded from {enginePath}");
        }

        var storePath = Path.Combine(configDirectory, StoreFileName);

        try
        {
            Directory.CreateDirectory(configDirectory);

            var store = MarketStore.OpenOrCreate(storePath);

            return new StoreHost(
                store,
                $"store open at {storePath} (schema v{store.SchemaVersion}, SQLite {SqliteEngine.Version()}, " +
                $"{store.Partitions().Count} snapshot partitions, {store.SizeInBytes:N0} bytes)");
        }
        catch (StoreUnusableException ex)
        {
            // The one failure that is a decision rather than an accident. A store created without
            // incremental vacuum cannot enforce a byte cap and cannot be repaired in place, so it
            // is refused and said so - it is not deleted, and it is not quietly rebuilt around.
            Plugin.Log.Error(ex, "EMM refused the store at {Path}", storePath);

            return new StoreHost(null, $"store unusable - {ex.Message}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EMM could not open the store at {Path}", storePath);

            return new StoreHost(null, $"store unavailable - {storePath} could not be opened");
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Store?.Dispose();
}

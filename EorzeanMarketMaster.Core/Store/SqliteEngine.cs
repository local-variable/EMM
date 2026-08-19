using SQLitePCL;
using Native = System.Runtime.InteropServices.NativeLibrary;

namespace EorzeanMarketMaster.Core.Store;

/// <summary>
/// Binds SQLite to one named file on disk, and to no other.
///
/// This exists because of where EMM runs. Dalamud ships its own <c>e_sqlite3</c> for four RIDs
/// and it is resident in the FFXIV process on every install, whatever EMM does. Windows resolves
/// a native import by base name against modules already loaded, so the ordinary way of getting
/// SQLite - reference <c>SQLitePCLRaw.bundle_e_sqlite3</c> and let it initialise itself - does not
/// load EMM's copy at all inside the game. It finds the host's, at whatever version the host
/// happens to carry, and it does so silently: there is no error, no warning, and no way to tell
/// from inside EMM which engine answered.
///
/// So the coupling is made explicit instead. EMM ships its own binary, names it by full path, and
/// hands the resulting function-pointer source to the dynamic provider. The cost is about 1.9 MB
/// in the package; what it buys is that a SQLite version change is something EMM chose.
///
/// The provider is process-wide state, which is why this is static and why it is frozen once set.
/// </summary>
public static class SqliteEngine
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// The absolute path of the native library in use, or <see langword="null"/> if
    /// <see cref="LoadFrom"/> has not run yet.
    ///
    /// Diagnostic only. It reports what EMM asked for; the loader is the authority on what was
    /// actually opened, and the test suite asserts against the process module table rather than
    /// against this.
    /// </summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>
    /// Loads the SQLite engine from one exact file and makes it the process's provider.
    ///
    /// Idempotent for the same path, so plugin load, a reload and a test all reach it safely.
    /// </summary>
    /// <param name="nativeLibraryPath">
    /// Full path to the native library. Never a bare name: a bare name is what binds EMM to the
    /// host's copy, which is the entire failure this method exists to prevent.
    /// </param>
    /// <exception cref="ArgumentException">The path is not fully qualified.</exception>
    /// <exception cref="FileNotFoundException">
    /// No file sits at that path. Deliberately fatal rather than falling back to a bare-name
    /// load: a fallback would turn a packaging mistake into an invisible version coupling.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A different library was already loaded. The provider is process-wide, so a second one
    /// cannot take effect - and pretending it did would leave a caller talking to an engine it
    /// did not choose.
    /// </exception>
    public static void LoadFrom(string nativeLibraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeLibraryPath);

        if (!Path.IsPathFullyQualified(nativeLibraryPath))
        {
            throw new ArgumentException(
                $"The SQLite engine must be named by full path, not by '{nativeLibraryPath}'. A relative " +
                "name resolves against the current directory and, inside the game, against libraries the " +
                "host has already loaded.",
                nameof(nativeLibraryPath));
        }

        // Checked before the already-loaded guard below, and the order matters. A caller naming a
        // file that is not there has made a packaging mistake, and saying so is more useful than
        // reporting which engine happens to have won the race - which is the answer they would get
        // if state were consulted first. It also keeps "an absent engine is fatal" true at every
        // moment in the process's life rather than only before the first successful load.
        if (!File.Exists(nativeLibraryPath))
        {
            throw new FileNotFoundException(
                "EMM ships its own SQLite engine and will not fall back to the host's copy.",
                nativeLibraryPath);
        }

        lock (Gate)
        {
            if (LoadedFrom is not null)
            {
                if (string.Equals(LoadedFrom, nativeLibraryPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"The SQLite engine is already loaded from '{LoadedFrom}' and cannot be replaced with " +
                    $"'{nativeLibraryPath}'. The provider is process-wide.");
            }

            var handle = Native.Load(nativeLibraryPath);

            SQLite3Provider_dynamic_cdecl.Setup("e_sqlite3", new ExportsOf(handle));
            raw.SetProvider(new SQLite3Provider_dynamic_cdecl());

            // Freezing turns "something else set the provider later" from a silent swap into a
            // throw. Nothing in EMM sets it twice; the point is that nothing outside EMM can either.
            raw.FreezeProvider();

            LoadedFrom = nativeLibraryPath;
        }
    }

    /// <summary>
    /// The version string the loaded engine reports.
    /// </summary>
    /// <returns>SQLite's own version, for logging beside the path it came from.</returns>
    /// <exception cref="InvalidOperationException">Nothing has been loaded yet.</exception>
    public static string Version()
    {
        if (LoadedFrom is null)
        {
            throw new InvalidOperationException(
                $"{nameof(SqliteEngine)}.{nameof(LoadFrom)} has not run, so there is no engine to ask.");
        }

        return raw.sqlite3_libversion().utf8_to_string();
    }

    /// <summary>
    /// Resolves SQLite's entry points out of one already-opened module.
    ///
    /// The single reason the dynamic provider is used at all: every symbol comes from the handle
    /// this was constructed with, so there is no name-based lookup left anywhere in the path.
    /// </summary>
    private sealed class ExportsOf(IntPtr handle) : IGetFunctionPointer
    {
        /// <summary>The address of one export, or zero where the engine does not carry it.</summary>
        public IntPtr GetFunctionPointer(string name)
            => Native.TryGetExport(handle, name, out var address) ? address : IntPtr.Zero;
    }
}

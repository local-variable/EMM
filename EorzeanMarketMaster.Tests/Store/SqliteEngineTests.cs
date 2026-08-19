using System.Diagnostics;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Store;
using Xunit;

namespace EorzeanMarketMaster.Tests.Store;

/// <summary>
/// The native-load half of #23, asserted over what the operating system's loader actually did
/// rather than over what the loading code says it did.
///
/// The hazard is specific and silent. Dalamud ships its own e_sqlite3 for four RIDs and it is
/// resident in the FFXIV process on every install; Windows resolves a native import by base name
/// against modules already loaded. So a bare-name load inside the game does not fail - it
/// succeeds, against a copy nobody chose, at a version nobody pinned. Nothing about that is
/// visible from inside EMM. The only way to know EMM got its own binary is to ask the loader
/// which file it opened.
/// </summary>
public class SqliteEngineTests
{
    [Fact]
    public void TheLoadedModuleIsTheExactFileTheEngineWasGiven()
    {
        var asked = StoreFixture.NativeLibraryPath;
        StoreFixture.EnsureEngineLoaded();

        var loaded = ModulesNamed("e_sqlite3");

        // Measured from the process module table, which is the loader's own record. A test that
        // asserted SqliteEngine.LoadedFrom would only be asking the code to repeat itself.
        Assert.Contains(asked, loaded, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheModuleDetectorTellsALoadedLibraryFromOneThatWasNeverAskedFor()
    {
        // NEGATIVE CONTROL. The case above passes today and would pass just as happily if
        // ModulesNamed returned every path it could find. Require it to distinguish.
        StoreFixture.EnsureEngineLoaded();

        Assert.NotEmpty(ModulesNamed("e_sqlite3"));
        Assert.Empty(ModulesNamed("e_sqlite3_a_library_that_does_not_exist"));
    }

    [Fact]
    public void TheEngineReportsTheAbsolutePathItLoaded()
    {
        StoreFixture.EnsureEngineLoaded();

        var reported = SqliteEngine.LoadedFrom;

        Assert.NotNull(reported);
        Assert.True(Path.IsPathFullyQualified(reported));
    }

    [Fact]
    public void ARelativePathIsRefusedRatherThanResolvedAgainstWhateverTheCurrentDirectoryIs()
    {
        // The whole point is naming one exact file. A relative path names a different file
        // depending on where the process happens to be standing, which is the ambiguity this
        // ticket exists to remove - so it is refused rather than helpfully resolved.
        var thrown = Assert.Throws<ArgumentException>(() => SqliteEngine.LoadFrom("e_sqlite3.dll"));

        Assert.Contains("full path", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAbsentFileIsRefusedRatherThanFallingBackToABareNameLoad()
    {
        // The dangerous failure mode: EMM's own binary is missing from the package, a fallback
        // quietly loads the host's, and everything appears to work. There is no fallback.
        var missing = Path.Combine(AppContext.BaseDirectory, "e_sqlite3_not_shipped.dll");

        Assert.Throws<FileNotFoundException>(() => SqliteEngine.LoadFrom(missing));
    }

    [Fact]
    public void LoadingTwiceFromTheSamePathIsAcceptedAndLoadingFromAnotherIsNot()
    {
        StoreFixture.EnsureEngineLoaded();

        // Idempotent: plugin load, a test, and a reload all reach the same call.
        SqliteEngine.LoadFrom(StoreFixture.NativeLibraryPath);

        // But a second, different binary is a real problem rather than a race to be tolerated:
        // the provider is process-wide, so the loser would be silently talking to the winner's
        // engine - exactly the ambiguity this ticket removes. A real, present, readable copy is
        // used rather than a missing path, so that what is being refused is unambiguously the
        // second load and not the absence of a file.
        var other = Path.Combine(Path.GetTempPath(), $"emm-second-engine-{Guid.NewGuid():N}.dll");
        File.Copy(StoreFixture.NativeLibraryPath, other);

        try
        {
            var thrown = Assert.Throws<InvalidOperationException>(() => SqliteEngine.LoadFrom(other));

            Assert.Contains("already loaded", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(other);
        }
    }

    [Fact]
    public void NoBareNameProviderIsShippedAlongsideTheDynamicOne()
    {
        // The reference-level guard. SQLitePCLRaw's batteries_v2 bundle and its static
        // e_sqlite3 provider both resolve the native library by bare name; if either one ever
        // arrives transitively, EMM regains the coupling it just paid to remove - and it would
        // arrive as a file in the output directory, not as a line anybody edited.
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var shipped = output.GetFiles("*.dll").Select(f => f.Name).ToArray();

        Assert.DoesNotContain("SQLitePCLRaw.batteries_v2.dll", shipped);
        Assert.DoesNotContain("SQLitePCLRaw.provider.e_sqlite3.dll", shipped);
        Assert.Contains("SQLitePCLRaw.provider.dynamic_cdecl.dll", shipped);
    }

    /// <summary>
    /// Every loaded module whose file name contains <paramref name="name"/>, as the process
    /// module table reports it.
    /// </summary>
    private static IReadOnlyList<string> ModulesNamed(string name)
    {
        var matches = new List<string>();

        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (module.ModuleName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(module.FileName ?? string.Empty);
            }
        }

        return matches;
    }
}

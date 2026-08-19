using System.Reflection;
using EorzeanMarketMaster.Core;
using Xunit;

namespace EorzeanMarketMaster.Tests;

/// <summary>
/// The boundary this ticket exists to draw, asserted rather than reviewed.
///
/// "Core has no reference to the game host" is the kind of rule that holds until the afternoon
/// somebody needs one convenient type from Dalamud and the build still goes green. A project
/// file comment does not stop that; a failing test does.
///
/// Known limit, stated rather than glossed: this reads the references the compiler actually
/// emitted, so it catches a game-host type being *used*, not a package being merely referenced
/// and left idle. Use is the thing that breaks headless testing, so that is the right place to
/// catch it - but an unused PackageReference would slip past, and the project file carries the
/// comment that explains why none should be added.
/// </summary>
public class CoreIsHostFreeTests
{
    /// <summary>
    /// Assemblies that only exist inside the game process. Dalamud and its ImGui bindings are the
    /// host and its UI; FFXIVClientStructs and Lumina are the client's memory and data; ECommons
    /// and AutoRetainerAPI are the vendored plugin-side libraries.
    /// </summary>
    private static readonly string[] HostAssemblies =
    [
        "Dalamud",
        "ImGui",
        "ImPlot",
        "ImGuizmo",
        "FFXIVClientStructs",
        "Lumina",
        "ECommons",
        "AutoRetainerAPI",
    ];

    [Fact]
    public void CoreReferencesNothingThatOnlyExistsInsideTheGame()
    {
        var core = typeof(DecisionEngine).Assembly;
        var referenced = core.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

        var offenders = HostReferencesIn(referenced);

        Assert.True(offenders.Count == 0,
            $"{core.GetName().Name} references the game host: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheDetectorTellsAHostReferenceFromAnInnocentOne()
    {
        // NEGATIVE CONTROL, for the same reason the in-game self-test carries one. The case
        // above passes today and would pass just as happily if HostReferencesIn always returned
        // nothing. Require it to tell a known-bad list from a known-good one.
        var onBad = HostReferencesIn(["System.Runtime", "Dalamud.Bindings.ImGui", "System.Linq"]);
        var onGood = HostReferencesIn(["System.Runtime", "System.Collections", "System.Linq"]);

        Assert.Equal(["Dalamud.Bindings.ImGui"], onBad);
        Assert.Empty(onGood);
    }

    private static IReadOnlyList<string> HostReferencesIn(IEnumerable<string> assemblyNames)
        => [.. assemblyNames.Where(name =>
            HostAssemblies.Any(host => name.Contains(host, StringComparison.OrdinalIgnoreCase)))];
}

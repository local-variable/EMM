using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// Drives the real UI and asserts over what it actually laid out.
///
/// Two rules this harness is built to obey, both learned the hard way on issue #13:
///
///   1. A test that cannot fail is worse than no test. Every case here asserts a measurement
///      taken from ImGui during layout — a hit rectangle, a vertex count, a scroll extent — never
///      a value the drawing code reported about itself.
///   2. A UI nobody scripts through has broken navigation nobody has found yet. The rail's rows
///      overlapped for an entire session and the section body still rendered, so it looked fine
///      and was not: clicks landed on the wrong entry or none. Case RAIL-OVERLAP exists for that
///      defect specifically and must never be deleted.
///
/// Run with /emm selftest. Results go to the plugin log; use /xllog to read them.
/// </summary>
internal sealed class SelfTest
{
    private const double BodyTextContrast = 4.5;
    private const double UiTextContrast = 3.0;
    private const float MinRowHeight = 24f;

    private readonly record struct Step(bool RailExpanded, int Section, Vector2? ForcedSize, string Phase);

    private readonly MainWindow main;
    private readonly Configuration configuration;
    private readonly List<(string Case, bool Pass, string Detail)> results = [];
    private readonly List<Step> plan = [];

    // `running` is deliberately separate from `step`. An earlier version derived IsRunning from
    // `step >= 0`, which is false at the moment Start() finishes — so Tick() bailed out on every
    // frame and the run announced itself, did nothing, and never reported. A harness that quietly
    // does nothing is the exact failure this class exists to prevent; keep the flag explicit.
    private bool running;
    private int step = -1;
    private bool awaitingFrame;
    private bool savedRailExpanded;
    private int savedSection;

    public SelfTest(MainWindow main, Configuration configuration)
    {
        this.main = main;
        this.configuration = configuration;
    }

    public bool IsRunning => running;

    public void Start()
    {
        if (IsRunning)
            return;

        results.Clear();
        plan.Clear();

        savedRailExpanded = configuration.RailExpanded;
        savedSection = main.ActiveSection;

        RunStaticChecks();

        // Every section, in both rail states: the rail must lay out correctly either way.
        foreach (var expanded in new[] { false, true })
            for (var s = 0; s < MainWindow.SectionCount; s++)
                plan.Add(new Step(expanded, s, null, expanded ? "rail-expanded" : "rail-collapsed"));

        // Narrow window: #13 found dense layouts overflowing when docked, where EMM gets under
        // 700px. The rail must survive the squeeze without the body scrolling sideways.
        plan.Add(new Step(false, 0, new Vector2(640, 400), "narrow"));
        plan.Add(new Step(false, MainWindow.SectionCount - 1, new Vector2(640, 400), "narrow"));

        main.IsOpen = true;
        step = -1;
        awaitingFrame = false;
        running = true;
        Plugin.Log.Information("[selftest] starting — {Static} static checks, {Frames} draw steps",
            results.Count, plan.Count);
    }

    /// <summary>
    /// Called once per frame before the window system draws. Evaluates the frame the previous
    /// step produced, then stages the next one.
    /// </summary>
    public void Tick()
    {
        if (!IsRunning)
            return;

        // The window must stay open or nothing draws and every probe reads empty, which would
        // report as a wall of failures rather than as "you closed the window".
        main.IsOpen = true;

        if (awaitingFrame)
            EvaluateFrame(plan[step]);

        step++;
        if (step >= plan.Count)
        {
            Finish();
            return;
        }

        var next = plan[step];
        configuration.RailExpanded = next.RailExpanded;
        main.ActiveSection = next.Section;
        if (next.ForcedSize is { } size)
            main.ForceSize(size);

        UiProbe.BeginFrame();
        UiProbe.Capturing = true;
        awaitingFrame = true;
    }

    private void EvaluateFrame(Step s)
    {
        var tag = $"{s.Phase}/{MainWindow.SectionList[s.Section].Label}";
        var rows = UiProbe.RailRows.ToList();

        Check($"RAIL-COUNT [{tag}]",
            rows.Count == MainWindow.SectionCount,
            $"drew {rows.Count} rail rows, expected {MainWindow.SectionCount}");

        Check($"RAIL-HEIGHT [{tag}]",
            rows.Count > 0 && rows.All(r => r.Max.Y - r.Min.Y >= MinRowHeight),
            rows.Count == 0
                ? "no rows to measure"
                : $"shortest row {rows.Min(r => r.Max.Y - r.Min.Y):F1}px, minimum {MinRowHeight}px");

        // THE REGRESSION GUARD. Overlapping hit rectangles are invisible — the icons still paint
        // and the body still renders — but clicks land on the wrong section or are swallowed.
        var overlaps = FindOverlaps(rows);

        Check($"RAIL-OVERLAP [{tag}]",
            overlaps.Count == 0,
            overlaps.Count == 0 ? "no rows overlap" : $"overlapping rows: {string.Join(", ", overlaps)}");

        Check($"RAIL-ORDER [{tag}]",
            rows.Count < 2 || rows.Zip(rows.Skip(1), (a, b) => b.Min.Y >= a.Max.Y - 0.5f).All(ok => ok),
            "rows run top to bottom without doubling back");

        Check($"BODY-DREW [{tag}]",
            UiProbe.ActiveSection == s.Section && UiProbe.BodyVertices > 0,
            $"section {UiProbe.ActiveSection} emitted {UiProbe.BodyVertices} vertices");

        if (s.Phase == "narrow")
        {
            Check($"NARROW-NO-HSCROLL [{tag}]",
                UiProbe.BodyScrollMaxX <= 0.5f,
                $"horizontal scroll extent {UiProbe.BodyScrollMaxX:F1}px (want 0)");
        }
    }

    private void RunStaticChecks()
    {
        var sections = MainWindow.SectionList;

        Check("DEF-SECTION-COUNT", sections.Count == 6, $"{sections.Count} sections (#13 settled on six)");
        Check("DEF-SECTION-LABELS",
            sections.All(x => !string.IsNullOrWhiteSpace(x.Label)) &&
            sections.Select(x => x.Label).Distinct().Count() == sections.Count,
            "section names are present and unique");
        Check("DEF-SECTION-ICONS",
            sections.Select(x => x.Icon).Distinct().Count() == sections.Count,
            "each section has its own glyph — two sections sharing one is unreadable in a collapsed rail");
        Check("DEF-SIZE-PRESETS",
            MainWindow.SizePresets.Length > 1 &&
            MainWindow.SizePresets.Zip(MainWindow.SizePresets.Skip(1), (a, b) => b.X > a.X && b.Y > a.Y).All(ok => ok),
            "size cycle is strictly increasing");

        // NEGATIVE CONTROL. An all-green suite is worthless unless the assertions can go red, and
        // #13's first harness proved that is not automatic — it rendered seven real failures as
        // ticks. Feed the overlap detector a pair that provably overlaps and a pair that provably
        // does not, and require it to tell them apart. If this case ever passes vacuously, the
        // twelve RAIL-OVERLAP cases below are decoration.
        var clean = new List<(int, Vector2, Vector2)>
        {
            (0, new Vector2(0, 0), new Vector2(10, 30)),
            (1, new Vector2(0, 32), new Vector2(10, 62)),
        };
        var broken = new List<(int, Vector2, Vector2)>
        {
            (0, new Vector2(0, 0), new Vector2(10, 30)),
            (1, new Vector2(0, 15), new Vector2(10, 45)),
        };

        var onBroken = FindOverlaps(broken).Count;
        var onClean = FindOverlaps(clean).Count;
        Check("HARNESS-DETECTS-OVERLAP",
            onBroken == 1 && onClean == 0,
            $"detector reported {onBroken} overlap on a known-bad pair and {onClean} on a known-good pair");

        // Contrast. #13's fourth round found black-on-dark throughout because native controls do
        // not inherit colour. Colour maths catches that class before a human squints at it.
        var windowBg = StyleColor(ImGuiCol.WindowBg);
        var text = StyleColor(ImGuiCol.Text);
        var railBg = Over(Palette.RailBackground, windowBg);
        var railActive = Over(Palette.RailActiveBackground, railBg);

        CheckContrast("CONTRAST-BODY-TEXT", text, windowBg, BodyTextContrast);
        CheckContrast("CONTRAST-RAIL-IDLE", Palette.RailIdle, railBg, UiTextContrast);
        CheckContrast("CONTRAST-RAIL-SELECTED", Palette.Gold, railActive, UiTextContrast);
        CheckContrast("CONTRAST-ACCENT-ON-BODY", Palette.Gold, windowBg, UiTextContrast);
        CheckContrast("CONTRAST-MUTED-ON-BODY", Palette.Muted, windowBg, UiTextContrast);
    }

    /// <summary>
    /// Vertical overlap between any two row rectangles. Two rows sharing screen rows means their
    /// hit targets fight, which is invisible to the eye and fatal to clicking.
    /// </summary>
    private static List<string> FindOverlaps(IReadOnlyList<(int Index, Vector2 Min, Vector2 Max)> rows)
    {
        var found = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = i + 1; j < rows.Count; j++)
            {
                var a = rows[i];
                var b = rows[j];
                if (a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y)
                    found.Add($"{a.Index}x{b.Index}");
            }
        }

        return found;
    }

    private void CheckContrast(string name, Vector4 fg, Vector4 bg, double minimum)
    {
        var ratio = Contrast(fg, bg);
        Check(name, ratio >= minimum, $"{ratio:F2}:1 against a {minimum:F1}:1 minimum");
    }

    private void Check(string name, bool pass, string detail) => results.Add((name, pass, detail));

    private void Finish()
    {
        UiProbe.Capturing = false;
        running = false;
        step = -1;
        awaitingFrame = false;

        configuration.RailExpanded = savedRailExpanded;
        main.ActiveSection = savedSection;

        var failed = results.Where(r => !r.Pass).ToList();
        foreach (var f in failed)
            Plugin.Log.Error("[selftest] FAIL {Case} — {Detail}", f.Case, f.Detail);

        if (failed.Count == 0)
            Plugin.Log.Information("[selftest] {Pass}/{Total} passed", results.Count, results.Count);
        else
            Plugin.Log.Error("[selftest] {Pass}/{Total} passed, {Failed} FAILED",
                results.Count - failed.Count, results.Count, failed.Count);

        Announce(failed);
    }

    /// <summary>
    /// Puts the verdict in chat as well as the log.
    ///
    /// The log is the record, but it costs a trip through /xllog to read, and a harness whose
    /// result is that far away invites "it ran, so it worked" — which is the exact reading that
    /// let an earlier version of this class announce itself, do nothing, and report nothing. A
    /// line in chat makes the result the first thing seen rather than something looked up.
    ///
    /// A failure names its cases rather than only counting them, because a bare "78/82" sends the
    /// reader to the log anyway and the whole point is not having to go. The list is capped: a
    /// broken rail fails a case per section per rail state, and thirty lines of chat would bury
    /// the count that matters.
    /// </summary>
    private void Announce(IReadOnlyList<(string Case, bool Pass, string Detail)> failed)
    {
        if (failed.Count == 0)
        {
            Plugin.ChatGui.Print($"[EMM selftest] {results.Count}/{results.Count} passed");
            return;
        }

        const int Listed = 5;

        var named = string.Join(", ", failed.Take(Listed).Select(f => f.Case));
        var rest = failed.Count > Listed ? $", and {failed.Count - Listed} more" : string.Empty;

        // PrintError, not Print: a failed self-test is the one thing here that must not read like
        // ordinary output scrolling past.
        Plugin.ChatGui.PrintError(
            $"[EMM selftest] {results.Count - failed.Count}/{results.Count} passed, {failed.Count} FAILED: " +
            $"{named}{rest} — see /xllog for details");
    }

    /// <summary>The binding hands back a pointer into ImGui's style, so it needs dereferencing.</summary>
    private static unsafe Vector4 StyleColor(ImGuiCol index) => *ImGui.GetStyleColorVec4(index);

    private static Vector4 Over(Vector4 fg, Vector4 bg)
        => new(
            (fg.X * fg.W) + (bg.X * (1f - fg.W)),
            (fg.Y * fg.W) + (bg.Y * (1f - fg.W)),
            (fg.Z * fg.W) + (bg.Z * (1f - fg.W)),
            1f);

    private static double Contrast(Vector4 fg, Vector4 bg)
    {
        var a = Luminance(fg);
        var b = Luminance(bg);
        return a > b ? (a + 0.05) / (b + 0.05) : (b + 0.05) / (a + 0.05);
    }

    /// <summary>WCAG 2.1 relative luminance.</summary>
    private static double Luminance(Vector4 c)
    {
        static double Channel(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return (0.2126 * Channel(c.X)) + (0.7152 * Channel(c.Y)) + (0.0722 * Channel(c.Z));
    }
}

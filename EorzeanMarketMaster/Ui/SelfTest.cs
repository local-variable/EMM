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

    /// <summary>
    /// One staged frame.
    /// </summary>
    /// <param name="RailExpanded">Which rail state to draw in.</param>
    /// <param name="Section">Which section to open.</param>
    /// <param name="ForcedSize">A window size to force, or null to leave it alone.</param>
    /// <param name="Phase">The label a failure is reported under.</param>
    /// <param name="Measure">
    /// Whether to assert over the frame this step produces.
    /// </param>
    private readonly record struct Step(
        bool RailExpanded,
        int Section,
        Vector2? ForcedSize,
        string Phase,
        bool Measure = true);

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
        //
        // EACH NARROW STEP IS DRAWN TWICE AND MEASURED ONCE, and the discarded frame is the whole
        // point. ImGui reports a child's scroll extent from the content size measured at the END of
        // the previous frame, so the first frame after a resize compares the OLD content width
        // against the NEW window width. With every section drawing one short scaffold string that
        // never mattered; the moment a section wraps its text to the content region, the frame the
        // window shrinks on reports a scroll extent that does not exist and is gone by the next
        // frame. Measuring the settled frame is what makes NARROW-NO-HSCROLL a statement about the
        // layout rather than about the resize.
        // EVERY SECTION WITH A BODY, plus the first as a scaffold control. This used to be the
        // first section and the last, which was right only while the last was the only one that
        // drew anything: the moment a second body existed, the squeeze it had never been through
        // was the one most likely to be wrong.
        foreach (var section in MainWindow.BuiltSections.Prepend(0).Distinct())
        {
            plan.Add(new Step(false, section, new Vector2(640, 400), "narrow", Measure: false));
            plan.Add(new Step(false, section, new Vector2(640, 400), "narrow"));
        }

        // THE SAME SQUEEZE WITH THE RAIL EXPANDED, which is the worst case for body width and was
        // the configuration this phase did not cover. A 640px window with a 200px rail leaves the
        // body around 400px, and a control laid out beside another one stops fitting - it is
        // submitted, clipped by the child, and invisible, which from the outside looks exactly
        // like a control that was never drawn. That is a whole in-game pass to diagnose, so the
        // configuration is now tested rather than assumed.
        foreach (var section in MainWindow.BuiltSections)
        {
            plan.Add(new Step(true, section, new Vector2(640, 400), "narrow-rail", Measure: false));
            plan.Add(new Step(true, section, new Vector2(640, 400), "narrow-rail"));
        }

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

        if (awaitingFrame && plan[step].Measure)
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

        // Keyed on the forced size rather than on the phase name, so a new squeezed configuration
        // gets the check by being squeezed rather than by being remembered.
        if (s.ForcedSize is not null)
        {
            var over = UiProbe.Rows
                .Where(row => row.ReachedX > UiProbe.RowRegionX + 0.5f)
                .OrderByDescending(row => row.ReachedX)
                .ToList();

            // THE ASSERTION THAT MEANS WHAT THIS PHASE IS FOR. A control laid out past the content
            // region is submitted, hit-testable and clipped - drawn and unseeable - which is the
            // defect the narrow phase exists to catch, and it is measured here directly. The scroll
            // extent below was only ever a proxy for it, and a proxy is what let a one-pixel
            // accounting artefact read as a layout fault for five rounds.
            //
            // It is a stronger claim than the extent, not a weaker one: it names the row, and it
            // caught the section separator overrunning a 400px region by 16px while every other
            // measure said the section was fine.
            Check($"NARROW-ROWS-FIT [{tag}]",
                over.Count == 0,
                over.Count == 0
                    ? $"every row inside {UiProbe.RowRegionX:F1}px"
                    : "past the content region: " +
                      string.Join(", ", over.Select(row => $"'{row.Row}' {row.ReachedX:F1}px")));

            var detail =
                $"horizontal scroll extent {UiProbe.BodyScrollMaxX:F1}px (want 0)\n" +
                $"region {UiProbe.RowRegionX:F1}  avail {UiProbe.BodyRegionAvailX:F1}  " +
                $"winW {UiProbe.BodyWindowSizeX:F1}  bar {UiProbe.ScrollbarSize:F1}\n" +
                string.Join("\n",
                    UiProbe.Rows
                        .OrderByDescending(row => row.ReachedX)
                        .Take(10)
                        .Select(row => $"  {row.ReachedX,7:F1}px  {row.Row}"));

            Check($"NARROW-NO-HSCROLL [{tag}]", UiProbe.BodyScrollMaxX <= 0.5f, detail);
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
        // A name in the built-sections list matching no section would silently drop that section
        // from the narrow phase - the coverage would go away without anything going red, which is
        // the failure mode the whole harness exists to refuse.
        Check("DEF-BUILT-SECTIONS",
            MainWindow.BuiltSections.Count == MainWindow.BuiltNamed,
            $"{MainWindow.BuiltSections.Count} of {MainWindow.BuiltNamed} named sections resolved");

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

        // The two Quality colours, which are marks on a plot rather than text but are read the
        // same way and fail the same way against a dark ground. Their separation FROM EACH OTHER
        // under colour-vision deficiency was checked all-pairs in the graph prototype, in both
        // light and dark; what is checked here is the thing that changes when a background does -
        // whether either one can be seen at all.
        CheckContrast("CONTRAST-QUALITY-NQ", Palette.QualityNormal, windowBg, UiTextContrast);
        CheckContrast("CONTRAST-QUALITY-HQ", Palette.QualityHigh, windowBg, UiTextContrast);
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

    /// <summary>
    /// The window-local x the body's content actually reached, derived from what ImGui reported.
    ///
    /// ImGui computes a window's horizontal scroll extent as
    /// <c>ContentSize + WindowPadding*2 - InnerWidth</c>, where <c>ContentSize</c> is measured from
    /// the content's start rather than from the window, and <c>InnerWidth</c> is the window less
    /// its scrollbar. Every term but the cursor is now captured, so this inverts the formula and
    /// hands back the one number the probe cannot read directly.
    ///
    /// It is what turns "1px from somewhere" into "something reached exactly here": if no tagged
    /// row is at this figure, the overshoot is inside a black box rather than in EMM's layout.
    /// </summary>
    private static float CursorMax()
    {
        var inner = UiProbe.BodyWindowSizeX - UiProbe.ScrollbarSize;
        var contentSize = UiProbe.BodyScrollMaxX - (UiProbe.WindowPadding * 2f) + inner;

        return contentSize + UiProbe.WindowPadding;
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

        // One line per line, rather than one line per case. A diagnostic that has to carry a
        // region, four style figures and ten row widths does not fit a log line, and a reader who
        // has to reassemble it from two screenshots is a reader who will skim it instead.
        foreach (var f in failed)
        {
            var lines = f.Detail.Split('\n');

            Plugin.Log.Error("[selftest] FAIL {Case} — {Detail}", f.Case, lines[0]);

            foreach (var line in lines.Skip(1))
                Plugin.Log.Error("[selftest]      {Line}", line);
        }

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

        // Case names only. The detail is several lines now and chat is the wrong place for it.
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

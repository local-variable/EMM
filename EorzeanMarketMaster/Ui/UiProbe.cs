using System.Collections.Generic;
using System.Numerics;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// Telemetry recorded by the real draw path so the self-test can assert over what was actually
/// laid out, rather than over what the drawing code claims it did.
///
/// This exists because #13 found that a harness which accepts a self-reported result renders real
/// failures as passes. Every field here is a measurement taken during layout — a screen rectangle,
/// a vertex count, a content extent — not a flag set by the code under test.
///
/// Capturing is off except while the self-test runs, so this costs nothing in normal use.
/// </summary>
internal static class UiProbe
{
    public static bool Capturing;

    /// <summary>Screen-space rectangle of every rail row drawn this frame, in draw order.</summary>
    public static readonly List<(int Index, Vector2 Min, Vector2 Max)> RailRows = [];

    /// <summary>ImGui vertices emitted by the section body. Zero means nothing was painted.</summary>
    public static int BodyVertices;

    /// <summary>Rightmost content extent reached by the body, and the width it had available.</summary>
    public static float BodyMaxX;
    public static float BodyAvailX;

    /// <summary>Non-zero means the body content did not fit its region horizontally.</summary>
    public static float BodyScrollMaxX;

    /// <summary>Which section the window believed was active while this frame was drawn.</summary>
    public static int ActiveSection = -1;

    /// <summary>
    /// The rightmost edge any named row reached, and which row reached it.
    ///
    /// <b>This exists because "the body overflowed by 3 pixels" does not say what overflowed.</b>
    /// A scroll extent is a fact about the whole section; on a section with a dozen rows it leaves
    /// a dozen candidates and no way to choose between them, which is exactly how a layout fault
    /// gets diagnosed by reasoning and gets diagnosed wrong. A row that calls
    /// <see cref="Widest"/> hands the harness the name to put in the failure message.
    /// </summary>
    public static readonly List<(string Row, float ReachedX)> Rows = [];

    /// <summary>The right edge of the region the rows had to fit inside.</summary>
    public static float RowRegionX;

    /// <summary>
    /// The style figures the layout arithmetic depends on, captured rather than assumed.
    ///
    /// Two rounds of this investigation were spent on theories about what the region loses and
    /// when. These are the actual numbers ImGui was working from, so the next reading can be
    /// checked against arithmetic instead of against a story.
    /// </summary>
    public static float ScrollbarSize;

    /// <summary>The window padding in force while the body drew.</summary>
    public static float WindowPadding;

    /// <summary>The body's own available width at the end of its draw.</summary>
    public static float BodyRegionAvailX;

    /// <summary>
    /// The body child's full width, scrollbar included.
    ///
    /// The one figure that closes the arithmetic. ImGui derives a window's horizontal scroll
    /// extent as <c>ContentSize + WindowPadding*2 - InnerWidth</c>, and with the window size known
    /// every other term is known - so a reported extent can be turned back into the exact cursor
    /// position that produced it, rather than argued about.
    /// </summary>
    public static float BodyWindowSizeX;

    /// <summary>
    /// Records the row just drawn.
    ///
    /// <b>Every row, not only the widest one seen so far.</b> Keeping just the maximum was enough
    /// to clear the plot of suspicion and no more: it named a row that turned out to be sixteen
    /// pixels INSIDE the region, which proved the overflow was somewhere else and left no way to
    /// say where. The whole list costs a few entries a frame while the self-test runs and turns
    /// the next failure into an answer rather than another elimination.
    ///
    /// Note for wrapped text: ImGui sizes a wrapped item to its widest laid-out line, so this
    /// reports what the text actually occupied rather than what it was allowed to.
    /// </summary>
    /// <param name="row">What to call it in a failure message.</param>
    public static void Widest(string row)
    {
        if (!Capturing)
        {
            return;
        }

        var left = Dalamud.Bindings.ImGui.ImGui.GetWindowPos().X;

        RowRegionX = Dalamud.Bindings.ImGui.ImGui.GetWindowContentRegionMax().X;
        Rows.Add((row, Dalamud.Bindings.ImGui.ImGui.GetItemRectMax().X - left));
    }

    public static void BeginFrame()
    {
        RailRows.Clear();
        BodyVertices = 0;
        BodyMaxX = 0f;
        BodyAvailX = 0f;
        BodyScrollMaxX = 0f;
        ActiveSection = -1;
        Rows.Clear();
        RowRegionX = 0f;
        ScrollbarSize = 0f;
        WindowPadding = 0f;
        BodyRegionAvailX = 0f;
        BodyWindowSizeX = 0f;
    }
}

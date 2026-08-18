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

    public static void BeginFrame()
    {
        RailRows.Clear();
        BodyVertices = 0;
        BodyMaxX = 0f;
        BodyAvailX = 0f;
        BodyScrollMaxX = 0f;
        ActiveSection = -1;
    }
}

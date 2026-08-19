using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// Measurements a surface takes before it commits to a layout.
/// </summary>
internal static class Layout
{
    /// <summary>
    /// Whether a control of this label still fits on the line the last one was drawn on.
    ///
    /// <b>Measured from the right edge of the item just drawn, not from the content region.</b>
    /// The obvious spelling - compare the new control's width against
    /// <c>GetContentRegionAvail().X</c> - is wrong, and wrong in a way that looks right: by the
    /// time a control has been submitted, ImGui has already moved the cursor to the start of the
    /// NEXT line, so the region it reports is the full body width rather than what is left beside
    /// the control. Every control then passes the test, every one gets a <c>SameLine</c>, and a
    /// row of seven buttons is laid out straight past the right edge.
    ///
    /// That is not merely ugly. A child window with no horizontal scrollbar clips what runs past
    /// its edge, so the control is submitted, is hit-testable, and cannot be seen - which from the
    /// outside is indistinguishable from a control that was never drawn, and cost a whole in-game
    /// pass to diagnose once already. The content size still grows, which is what the self-test's
    /// NARROW-NO-HSCROLL case measures and how this was caught the second time.
    /// </summary>
    /// <param name="label">The control's visible text, without any id suffix.</param>
    /// <returns>Whether to put it on the same line.</returns>
    internal static bool FitsBeside(string label)
    {
        var style = ImGui.GetStyle();
        var width = ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f);
        var next = ImGui.GetItemRectMax().X + style.ItemSpacing.X + width;
        var edge = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;

        return next <= edge;
    }

    /// <summary>
    /// Wrapped text that leaves room for a vertical scrollbar whether or not one is showing.
    ///
    /// <b>Plain <c>TextWrapped</c> wraps to whatever the content region is at that instant, and
    /// the content region loses <c>ScrollbarSize</c> the moment a vertical scrollbar appears.</b>
    /// A section that grows tall enough to scroll therefore lays its text out one frame too wide,
    /// and the frame after that it is a scrollbar's width over the edge.
    ///
    /// Measured rather than reasoned, and the measurement is why this exists: the self-test
    /// reported a body overflowing by 1.0px at a 400px region with the widest row at 401.0px - an
    /// exact match - and by 3.0px at a 542px region where the widest row was 541.0px, which is
    /// INSIDE it. Those two reconcile only one way. ImGui reports a child's scroll extent from the
    /// content measured at the end of the previous frame, so the second reading was the frame
    /// before, when the scrollbar did not yet exist and the text had wrapped to the wider region.
    /// One cause, two faces, and the sub-pixel one would never have been found by reading the code.
    ///
    /// Reserving the width unconditionally also kills the frame-lag: a wrap position that does not
    /// depend on scrollbar state produces the same layout on every frame, so the harness's settle
    /// frame has nothing left to settle.
    /// </summary>
    /// <param name="text">The text.</param>
    internal static void TextWrapped(string text)
    {
        var edge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize;

        ImGui.PushTextWrapPos(edge);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// The width a sized item may occupy without ever reaching the edge.
    ///
    /// The same arithmetic <see cref="TextWrapped"/> and <see cref="Separator"/> already use, named
    /// because a table cannot wrap. Text that overruns is clipped and looks wrong; a table that
    /// overruns takes the whole body's content size with it and the section starts scrolling
    /// sideways, which the self-test fails outright.
    /// </summary>
    /// <returns>The available width, less a scrollbar.</returns>
    internal static float BodyWidth() =>
        MathF.Max(ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize, 1f);

    /// <summary>
    /// A horizontal rule that stays inside the content region even when the body scrolls.
    ///
    /// <b><c>ImGui.Separator()</c> spans the work rect, and the work rect is wider than the
    /// content region by exactly <c>ScrollbarSize</c> once a vertical scrollbar exists.</b>
    /// Measured, not deduced: the self-test reported the section separator reaching 416.0px inside
    /// a 400.0px region with <c>ScrollbarSize</c> at 16.0px, while every other item in the body
    /// sat comfortably inside. It was the last thing to be suspected because it is not in the
    /// section that failed - it is the header rule the shell draws for every section, which is
    /// also why the Scan section never showed it: a body that does not scroll has no scrollbar for
    /// the two rects to disagree over.
    ///
    /// Drawn by hand rather than nudged, because the width is then EMM's own arithmetic instead of
    /// a rect whose definition varies with scrollbar state.
    /// </summary>
    internal static void Separator()
    {
        var style = ImGui.GetStyle();
        var width = MathF.Max(ImGui.GetContentRegionAvail().X - style.ScrollbarSize, 1f);
        var at = ImGui.GetCursorScreenPos();

        ImGui.GetWindowDrawList().AddLine(
            at, at with { X = at.X + width }, ImGui.GetColorU32(ImGuiCol.Separator));

        // An item of the same width, so the cursor advances and the content size counts what was
        // actually drawn. A bare AddLine paints without telling ImGui anything is there.
        ImGui.Dummy(new Vector2(width, 1f));
    }
}

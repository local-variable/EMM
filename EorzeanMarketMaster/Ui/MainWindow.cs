using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures.TextureWraps;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// The shell decided on issue #13: a collapsible icon rail down the left, six sections, a
/// resizable window and a status strip along the bottom.
///
/// The rail, the sections and the chrome are real. Scan and Pricing have bodies; the other four
/// are still stubs, and say so. Nothing here is written to the game.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    /// <summary>
    /// Size cycle from #13: the window must be resizable, not two fixed sizes.
    ///
    /// The smallest was raised from 880x520 once Holdings had a table in it. A section whose
    /// natural shape is columns needs room for them, and the old first preset put the window in
    /// its own compact mode by default - which is the fallback for a docked window, not the thing
    /// a Player should meet first. An install that has already saved a size keeps it: the window
    /// is sized on first use only, so this moves the default and nothing else.
    /// </summary>
    internal static readonly Vector2[] SizePresets =
    [
        new(1100, 660),
        new(1400, 840),
        new(1700, 1000),
    ];

    // Names only. The six sections come from issue #13 and are approved copy; describing what
    // each will hold is a UI ticket's call, not the scaffold's, so nothing else is asserted here.
    private static readonly (FontAwesomeIcon Icon, string Label)[] Sections =
    [
        (FontAwesomeIcon.ClipboardList, "Tasks"),
        (FontAwesomeIcon.ChartLine, "Pricing"),
        (FontAwesomeIcon.BorderAll, "Sell Space"),
        (FontAwesomeIcon.Boxes, "Holdings"),
        (FontAwesomeIcon.SlidersH, "Strategies"),
        (FontAwesomeIcon.Database, "Scan"),
    ];

    /// <summary>
    /// The sections that draw real content rather than the scaffold string.
    ///
    /// Named here so the self-test can squeeze exactly these. The narrow phase used to cover the
    /// first section and the last, which was right while the last was the only one with a body;
    /// the moment a second existed, the configuration that had never been drawn narrow was the one
    /// most likely to be broken. That is the harness gap the sweep control fell through, and it
    /// costs an in-game pass to find.
    /// </summary>
    private static readonly string[] Built = ["Pricing", "Holdings", "Scan"];

    private const float RailCollapsedWidth = 58f;
    private const float RailExpandedWidth = 200f;
    private const float RailRowHeight = 34f;
    private const float RailPad = 6f;

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly string iconPath;

    private int active;

    public MainWindow(Plugin plugin, string iconPath)
        : base("Eorzean Market Master###EmmMain")
    {
        this.plugin = plugin;
        this.iconPath = iconPath;
        configuration = plugin.Configuration;

        active = Math.Clamp(configuration.ActiveSection, 0, Sections.Length - 1);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 380),
            MaximumSize = new Vector2(4000, 3000),
        };

        Size = SizePresets[Math.Clamp(configuration.SizePreset, 0, SizePresets.Length - 1)];
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    /// <summary>Section count, for the self-test to assert the rail drew all of them.</summary>
    internal static int SectionCount => Sections.Length;

    internal static IReadOnlyList<(FontAwesomeIcon Icon, string Label)> SectionList => Sections;

    /// <summary>
    /// Which sections have a body, by index. What the self-test's narrow phase drives, and what it
    /// checks its own list against - a name in <see cref="Built"/> that matches no section would
    /// otherwise silently drop that section's coverage.
    /// </summary>
    internal static IReadOnlyList<int> BuiltSections { get; } =
    [
        .. Enumerable.Range(0, Sections.Length).Where(i => Built.Contains(Sections[i].Label)),
    ];

    /// <summary>How many sections <see cref="Built"/> names. Equal to BuiltSections where nothing has drifted.</summary>
    internal static int BuiltNamed => Built.Length;

    /// <summary>Driven by the self-test to walk every section. Clamped, so a bad step cannot throw.</summary>
    internal int ActiveSection
    {
        get => active;
        set => active = Math.Clamp(value, 0, Sections.Length - 1);
    }

    /// <summary>Forces a size for one frame. Used by the self-test's narrow-window phase.</summary>
    internal void ForceSize(Vector2 size)
    {
        Size = size;
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        var bodyHeight = ImGui.GetContentRegionAvail().Y - footerHeight;

        DrawRail(bodyHeight);
        ImGui.SameLine();
        DrawSection(bodyHeight);

        // ImGui's own separator, deliberately. The scrollbar-aware one exists for windows that
        // scroll, and this one rules off the main window, which does not - reserving a scrollbar
        // there would shorten the line by 16px to guard against something that cannot happen.
        ImGui.Separator();
        DrawFooter();
    }

    private void DrawRail(float height)
    {
        var width = configuration.RailExpanded ? RailExpandedWidth : RailCollapsedWidth;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Palette.RailBackground);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(RailPad, RailPad));

        if (ImGui.BeginChild("##emm-rail", new Vector2(width, height)))
        {
            DrawLogo(width);
            ImGui.Spacing();

            // ImGui's own, as above: the rail holds six rows and a toggle and never scrolls. It is
            // also only 46px wide with the rail collapsed, where reserving a scrollbar would take a
            // third of the line away.
            ImGui.Separator();
            ImGui.Spacing();

            for (var i = 0; i < Sections.Length; i++)
                DrawRailEntry(i, width);

            // Pin the toggle to the foot of the rail, but never on top of the last section.
            var toggleTop = ImGui.GetWindowHeight() - (RailRowHeight - 6f) - RailPad;
            if (toggleTop > ImGui.GetCursorPosY())
                ImGui.SetCursorPosY(toggleTop);

            ImGui.SetCursorPosX(RailPad);
            DrawRailToggle(width);
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// The logo is the approved icon in miniature. #13 recorded a defect where a logo drawn in a
    /// title bar read as a control and got clicked, so this is an image and nothing else — no
    /// button, no frame, no hover state.
    /// </summary>
    private void DrawLogo(float railWidth)
    {
        IDalamudTextureWrap? icon = null;
        try
        {
            icon = Plugin.TextureProvider.GetFromFile(iconPath).GetWrapOrDefault();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not load the plugin icon from {Path}", iconPath);
        }

        const float side = 32f;
        var indent = configuration.RailExpanded
            ? RailPad
            : MathF.Max((railWidth - (RailPad * 2f) - side) * 0.5f + RailPad, RailPad);

        if (icon is not null)
        {
            ImGui.SetCursorPosX(indent);
            ImGui.Image(icon.Handle, new Vector2(side, side));
        }
        else
        {
            // Never leave a blank where the identity should be.
            ImGui.SetCursorPosX(indent);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Palette.Gold, "EMM");
        }

        if (configuration.RailExpanded)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((side - ImGui.GetTextLineHeight()) * 0.5f));
            ImGui.TextColored(Palette.GoldHighlight, "EMM");
        }
    }

    private void DrawRailEntry(int index, float railWidth)
    {
        var (icon, label) = Sections[index];
        var selected = index == active;
        var rowWidth = railWidth - (RailPad * 2f);

        // The row is one hit target with the glyph and label painted back over it. Capture the
        // cursor either side of the Selectable and restore it afterwards: measuring from the
        // glyph's own rect instead collapses every row to text height.
        var origin = ImGui.GetCursorPos();

        ImGui.PushStyleColor(ImGuiCol.Header, Palette.RailActiveBackground);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Palette.RailHoverBackground);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Palette.RailActiveBackground);

        var clicked = ImGui.Selectable($"##emm-rail-{index}", selected, ImGuiSelectableFlags.None,
            new Vector2(rowWidth, RailRowHeight));
        var hovered = ImGui.IsItemHovered();

        ImGui.PopStyleColor(3);

        var next = ImGui.GetCursorPos();
        var tint = selected ? Palette.Gold : Palette.RailIdle;

        if (UiProbe.Capturing)
        {
            var min = ImGui.GetItemRectMin();
            UiProbe.RailRows.Add((index, min, ImGui.GetItemRectMax()));
        }

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var glyph = icon.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            var glyphX = configuration.RailExpanded ? 12f : (rowWidth - glyphSize.X) * 0.5f;
            ImGui.SetCursorPos(origin + new Vector2(glyphX, (RailRowHeight - glyphSize.Y) * 0.5f));
            ImGui.TextColored(tint, glyph);
        }

        if (configuration.RailExpanded)
        {
            var textSize = ImGui.CalcTextSize(label);
            ImGui.SetCursorPos(origin + new Vector2(42f, (RailRowHeight - textSize.Y) * 0.5f));
            ImGui.TextColored(tint, label);
        }
        else if (hovered)
        {
            ImGui.SetTooltip(label);
        }

        // A selected row gets a gold spine on its leading edge, so which section is open is
        // legible even where the tinted plate is subtle.
        if (selected)
        {
            var screen = ImGui.GetWindowPos() + origin - new Vector2(ImGui.GetScrollX(), ImGui.GetScrollY());
            ImGui.GetWindowDrawList().AddRectFilled(
                screen, screen + new Vector2(3f, RailRowHeight), ImGui.GetColorU32(Palette.Gold));
        }

        ImGui.SetCursorPos(next);

        if (clicked)
        {
            active = index;
            configuration.ActiveSection = index;
            configuration.Save();
        }
    }

    private void DrawRailToggle(float railWidth)
    {
        var glyph = configuration.RailExpanded ? FontAwesomeIcon.AngleLeft : FontAwesomeIcon.AngleRight;

        bool pressed;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            pressed = ImGui.Button($"{glyph.ToIconString()}##emm-rail-toggle",
                new Vector2(railWidth - (RailPad * 2f), RailRowHeight - 6f));

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(configuration.RailExpanded ? "Collapse the rail" : "Expand the rail");

        if (pressed)
        {
            configuration.RailExpanded = !configuration.RailExpanded;
            configuration.Save();
        }
    }

    private void DrawSection(float height)
    {
        // The scrollbar is always present rather than appearing when the content grows, and that
        // is a layout decision rather than a cosmetic one. Every rect ImGui reports inside a child
        // - the content region, the work rect, the available width - shrinks by ScrollbarSize the
        // moment a vertical scrollbar appears, and ImGui decides whether to show one from the
        // PREVIOUS frame's content. So a section that grows tall enough to scroll lays itself out
        // one frame too wide, every frame after a resize. Keeping the scrollbar makes every one of
        // those rects the same on every frame, which is what lets a section be laid out against a
        // width that is actually true.
        if (ImGui.BeginChild(
                "##emm-body",
                new Vector2(0, height),
                border: false,
                ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            var (icon, label) = Sections[active];
            var verticesBefore = UiProbe.Capturing ? ImGui.GetWindowDrawList().VtxBuffer.Size : 0;

            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                ImGui.TextColored(Palette.Gold, icon.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(Palette.Gold, label);
            UiProbe.Widest("section title");

            // Tagged because a Separator spans the whole work rect by construction, which makes it
            // the one item in the body guaranteed to touch the edge — and therefore the first
            // suspect once every other row has been measured and cleared.
            Layout.Separator();
            UiProbe.Widest("section separator");

            ImGui.Spacing();

            // Grouped so the harness can read the union of everything the section drew. That union
            // is the closest thing to ImGui's internal CursorMaxPos reachable from outside it, and
            // CursorMaxPos is what a scroll extent is actually computed from - so a section whose
            // every named row fits while this does not is one overflowing somewhere its own code
            // cannot see.
            ImGui.BeginGroup();

            // Three sections have bodies now. The rest are still the scaffold, and saying so is
            // better than a plausible-looking empty panel.
            switch (label)
            {
                case "Scan":
                    ScanTab.Draw(plugin.Scan);
                    break;
                case "Pricing":
                    PricingTab.Draw(plugin.Chart);
                    break;
                case "Holdings":
                    HoldingsTab.Draw(plugin.Owned);
                    break;
                default:
                    ImGui.TextColored(Palette.Muted, "Not built yet — this is the scaffold.");
                    break;
            }

            ImGui.EndGroup();
            UiProbe.Widest("whole section body");

            if (UiProbe.Capturing)
            {
                UiProbe.ActiveSection = active;
                UiProbe.BodyVertices = ImGui.GetWindowDrawList().VtxBuffer.Size - verticesBefore;
                UiProbe.BodyMaxX = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
                UiProbe.BodyAvailX = ImGui.GetWindowWidth();
                UiProbe.BodyScrollMaxX = ImGui.GetScrollMaxX();
                UiProbe.ScrollbarSize = ImGui.GetStyle().ScrollbarSize;
                UiProbe.WindowPadding = ImGui.GetStyle().WindowPadding.X;
                UiProbe.BodyRegionAvailX = ImGui.GetContentRegionAvail().X;
                UiProbe.BodyWindowSizeX = ImGui.GetWindowSize().X;
            }
        }

        ImGui.EndChild();
    }

    private void DrawFooter()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Palette.Muted, plugin.EnvironmentSummary);

        var buttonWidth = 108f;
        ImGui.SameLine(MathF.Max(ImGui.GetContentRegionAvail().X - buttonWidth, buttonWidth));
        if (ImGui.Button("Size##emm-size", new Vector2(buttonWidth - 8f, 0)))
        {
            configuration.SizePreset = (configuration.SizePreset + 1) % SizePresets.Length;
            configuration.Save();
            Size = SizePresets[configuration.SizePreset];
            SizeCondition = ImGuiCond.Always;
        }
        else
        {
            // Only force the size on the frame the button is pressed, or the window cannot be dragged.
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Cycle the window size. The window is also resizable by its corner.");
    }
}

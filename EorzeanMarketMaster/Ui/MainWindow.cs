using System;
using System.Collections.Generic;
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
/// SCAFFOLD. The rail, the sections and the chrome are real; every section body is a stub. No
/// market data is read, nothing is computed and nothing is written to the game.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    /// <summary>Size cycle from #13: the window must be resizable, not two fixed sizes.</summary>
    internal static readonly Vector2[] SizePresets =
    [
        new(880, 520),
        new(1180, 720),
        new(1480, 900),
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
        if (ImGui.BeginChild("##emm-body", new Vector2(0, height)))
        {
            var (icon, label) = Sections[active];
            var verticesBefore = UiProbe.Capturing ? ImGui.GetWindowDrawList().VtxBuffer.Size : 0;

            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                ImGui.TextColored(Palette.Gold, icon.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(Palette.Gold, label);
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(Palette.Muted, "Not built yet — this is the scaffold.");

            if (UiProbe.Capturing)
            {
                UiProbe.ActiveSection = active;
                UiProbe.BodyVertices = ImGui.GetWindowDrawList().VtxBuffer.Size - verticesBefore;
                UiProbe.BodyMaxX = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
                UiProbe.BodyAvailX = ImGui.GetWindowWidth();
                UiProbe.BodyScrollMaxX = ImGui.GetScrollMaxX();
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

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// Config opens anywhere and is moveable, and carries its own rail — the five pages settled on
/// issue #13. #13 also recorded a defect where three of these pages fell through to General; each
/// one here renders distinctly, and the scaffold keeps it that way.
///
/// SCAFFOLD. Page bodies are stubs. No Mandate can be granted and no guardrail is enforced,
/// because nothing yet acts.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    // Names only, for the same reason as the main window's sections: these five pages are #13's
    // approved copy, and what each will contain is not the scaffold's to assert.
    private static readonly (FontAwesomeIcon Icon, string Label)[] Pages =
    [
        (FontAwesomeIcon.SlidersH, "General"),
        (FontAwesomeIcon.BorderAll, "Sell space defaults"),
        (FontAwesomeIcon.Database, "Data & Sources"),
        (FontAwesomeIcon.LayerGroup, "Strategies & Groups"),
        (FontAwesomeIcon.Coins, "Mandates & guardrails"),
    ];

    private readonly Configuration configuration;
    private int active;

    public ConfigWindow(Plugin plugin)
        : base("Eorzean Market Master — Settings###EmmConfig")
    {
        configuration = plugin.Configuration;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 340),
            MaximumSize = new Vector2(2400, 1800),
        };

        Size = new Vector2(720, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.BeginChild("##emm-cfg-rail", new Vector2(210, 0)))
        {
            for (var i = 0; i < Pages.Length; i++)
            {
                var (icon, label) = Pages[i];
                var selected = i == active;

                ImGui.PushStyleColor(ImGuiCol.Text, selected ? Palette.Gold : Palette.RailIdle);
                using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                    ImGui.TextUnformatted(icon.ToIconString());
                ImGui.PopStyleColor();

                ImGui.SameLine();
                if (ImGui.Selectable($"{label}##emm-cfg-{i}", selected))
                    active = i;
            }
        }

        ImGui.EndChild();
        ImGui.SameLine();

        if (ImGui.BeginChild("##emm-cfg-body"))
        {
            var (_, label) = Pages[active];

            ImGui.TextColored(Palette.Gold, label);
            ImGui.Separator();
            ImGui.Spacing();

            // The one setting that exists, because the scaffold's own window uses it.
            var expanded = configuration.RailExpanded;
            if (ImGui.Checkbox("Show labels beside the rail icons", ref expanded))
            {
                configuration.RailExpanded = expanded;
                configuration.Save();
            }

            ImGui.Spacing();
            ImGui.TextColored(Palette.Muted, "Not built yet — this is the scaffold.");
        }

        ImGui.EndChild();
    }
}

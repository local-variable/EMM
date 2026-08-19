using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Graph;
using EorzeanMarketMaster.Graph;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// The Pricing section: what a Ware has actually done, drawn honestly, before any modelling is
/// layered on top.
///
/// <b>Every string in this file is unapproved copy.</b> What the picture is allowed to claim was
/// settled on the graph ticket; the words it says it in have not been.
///
/// <b>Nothing here reads the store directly.</b> The surface draws what <see cref="GraphHost.View"/>
/// last held, for the same reason the Scan surface does: the store is one connection and a frame
/// may not wait on it.
///
/// Laid out to wrap rather than to scroll - the in-game self-test drives this section at 640x400
/// with the rail expanded, which leaves the body around 400px, and fails it on any horizontal
/// scroll extent at all.
/// </summary>
internal static class PricingTab
{
    /// <summary>The tallest the plot is allowed to get, so the notes under it stay reachable.</summary>
    private const float MaximumPlotHeight = 460f;

    /// <summary>The shortest a plot may be and still be a picture rather than a stripe.</summary>
    private const float MinimumPlotHeight = 140f;

    internal static void Draw(GraphHost? host)
    {
        if (host is null)
        {
            Layout.TextWrapped(
                "The store could not be opened, so there is no History to draw. EMM is running " +
                "without it; see the log for why.");
            return;
        }

        var view = host.View;

        DrawWareSelector(host);
        ImGui.Spacing();
        UiProbe.Widest("after selector spacing");

        DrawControls(host, view);
        ImGui.Spacing();
        UiProbe.Widest("after controls spacing");

        if (view is null)
        {
            Layout.TextWrapped("Not logged in, so there is no World whose Market to draw.");
            return;
        }

        DrawWindowLine(view);
        ImGui.Spacing();
        UiProbe.Widest("after window-line spacing");

        if (view.IsEmpty)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
            Layout.TextWrapped(
                "Nothing stored for this Ware on this World yet. EMM draws what it has observed " +
                "and never fetches behind a draw, so this stays empty until the Scan section is " +
                "asked for a refresh.");
            ImGui.PopStyleColor();
            return;
        }

        DrawPlot(host, view);
        ImGui.Spacing();
        UiProbe.Widest("after plot spacing");

        DrawDiscarded(view);
        DrawWhatIsNotDrawn();
    }

    /// <summary>
    /// Which Item is on show. The Ware is shared with the Scan section, so picking one here moves
    /// both - a window showing one Item's Freshness beside another Item's graph would be worse
    /// than either alone.
    /// </summary>
    private static void DrawWareSelector(GraphHost host)
    {
        var itemId = (int)host.Item;

        ImGui.SetNextItemWidth(120f);

        if (ImGui.InputInt("Item##emm-graph-item", ref itemId) && itemId > 0)
        {
            host.Select((uint)itemId);
        }

        UiProbe.Widest("item selector");

        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
        Layout.TextWrapped(NameOf(host.Item));
        ImGui.PopStyleColor();
        UiProbe.Widest("item name");
    }

    /// <summary>The two controls: which Qualities are drawn, and how much of History.</summary>
    private static void DrawControls(GraphHost host, GraphView? view)
    {
        DrawOverlay(host);

        ImGui.Spacing();

        foreach (var preset in GraphWindow.Presets)
        {
            var label = Label(preset);

            if (!First(preset) && Layout.FitsBeside(label))
            {
                ImGui.SameLine();
            }

            var open = view is not null && view.Window == preset;

            if (open)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, Palette.RailActiveBackground);
                ImGui.PushStyleColor(ImGuiCol.Text, Palette.Gold);
            }

            if (ImGui.Button($"{label}##emm-graph-window-{label}"))
            {
                host.Show(preset);
            }

            if (open)
            {
                ImGui.PopStyleColor(2);
            }

            UiProbe.Widest($"window preset {label}");
        }
    }

    /// <summary>
    /// NQ, HQ, or both on one axis.
    ///
    /// Never side by side. Two Wares of one Item are two series with two of everything, but the
    /// question a seller asks is "what is the HQ premium, and is it moving?" - and that needs one
    /// axis. The colours belong to the Qualities rather than to the order they are drawn in, so
    /// toggling one does not recolour the other.
    /// </summary>
    private static void DrawOverlay(GraphHost host)
    {
        var choices = new[]
        {
            (QualityOverlay.Normal, "NQ", Palette.QualityNormal),
            (QualityOverlay.High, "HQ", Palette.QualityHigh),
            (QualityOverlay.Both, "Both", Palette.Gold),
        };

        for (var i = 0; i < choices.Length; i++)
        {
            var (overlay, label, colour) = choices[i];

            if (i > 0 && Layout.FitsBeside(label))
            {
                ImGui.SameLine();
            }

            var chosen = host.Overlay == overlay;

            if (chosen)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, Palette.RailActiveBackground);
                ImGui.PushStyleColor(ImGuiCol.Text, colour);
            }

            if (ImGui.Button($"{label}##emm-graph-overlay-{label}"))
            {
                host.Show(overlay);
            }

            if (chosen)
            {
                ImGui.PopStyleColor(2);
            }

            UiProbe.Widest($"overlay {label}");
        }
    }

    /// <summary>
    /// Which window is open, said in words.
    ///
    /// The ticket asks for the default to be stated, and this is where it is stated. It matters
    /// more than it looks: the graph decision fixed the default as the window the Estimate was
    /// priced from, there is no Estimate yet, and when there is one this default will move. A
    /// reader who was never told what they were looking at would not notice that it changed.
    /// </summary>
    private static void DrawWindowLine(GraphView view)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);

        var window = view.Window.Days is { } days
            ? $"the last {days} days"
            : "everything stored";

        var isDefault = view.Window == GraphWindow.Default
            ? ", which is what opens by default"
            : string.Empty;

        Layout.TextWrapped($"Showing {window}{isDefault}, folded into {Slice(view.Width)}.");
        ImGui.PopStyleColor();
        UiProbe.Widest("window line");
    }

    /// <summary>
    /// The plot, sized to whatever is left.
    ///
    /// <b>The width reserves room for a vertical scrollbar whether or not one is showing, and that
    /// is not superstition - it is the fix for a measured 15px overflow.</b> Everything else in
    /// this section is wrapped text, which re-wraps when the content region narrows; a plot is a
    /// fixed-width item and simply hangs over the edge. At 640x400 the body is tall enough to need
    /// a vertical scrollbar, and on the frame that scrollbar appears the region loses exactly
    /// <c>ScrollbarSize</c> - which the plot, already sized, has no way to give back. The
    /// self-test caught it at 15.0px identically at a 570px body and a 400px one, and it is the
    /// sameness of those two numbers that identified it: a width-dependent overflow would differ.
    ///
    /// Reserved unconditionally rather than only when <c>GetScrollMaxY</c> says so, because that
    /// answer is a frame behind - which is the same frame-lag trap the narrow phase already has a
    /// discarded settle frame for.
    ///
    /// Height is clamped at both ends: a plot given everything left over pushes the notes under it
    /// off the bottom, and one given a stripe is not a picture.
    /// </summary>
    private static void DrawPlot(GraphHost host, GraphView view)
    {
        var available = ImGui.GetContentRegionAvail();
        var height = Math.Clamp(available.Y - 90f, MinimumPlotHeight, MaximumPlotHeight);
        var width = available.X - ImGui.GetStyle().ScrollbarSize;

        // The unit, as a caption over the axis it belongs to. ImPlot's own Y label is drawn
        // rotated through a routine that assumes the bound texture is the font atlas, which under
        // Dalamud renders as noise - seen in game. Horizontal text says the same thing legibly.
        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
        ImGui.TextUnformatted("gil / unit");
        ImGui.PopStyleColor();
        UiProbe.Widest("axis caption");

        try
        {
            // Grouped purely to measure it. GetItemRectMax reports the LAST item submitted, but
            // the overshoot being hunted would be the widest of the several items ImPlot submits
            // internally - which that cannot see. A group's rect is the union of everything inside
            // it, so this makes the maximum visible without moving anything.
            ImGui.BeginGroup();
            MarketGraph.Draw(view, new Vector2(width, height), host.TakeAxisForce());
            ImGui.EndGroup();

            UiProbe.Widest("the plot");
        }
        catch (Exception ex)
        {
            // A plot that cannot be drawn is not a reason to take the window down with it. The
            // same posture the store takes: hobbled and saying so beats broken and silent.
            Plugin.Log.Error(ex, "EMM graph: ImPlot failed to draw");
            Layout.TextWrapped("The graph could not be drawn; see the log for why.");
        }
    }

    /// <summary>
    /// The Listings thrown away for being priced at nothing anybody would pay.
    ///
    /// Said out loud rather than left silent. Discarding them is the right call - one cap-priced
    /// Listing flattens every real mark on the picture - but a board holding four Listings and
    /// drawing three without a word is a picture telling a small lie about the market.
    /// </summary>
    private static void DrawDiscarded(GraphView view)
    {
        if (view.DiscardedListings == 0)
        {
            return;
        }

        var one = view.DiscardedListings == 1;
        var dearest = view.DearestDiscarded ?? 0;

        ImGui.PushStyleColor(ImGuiCol.Text, Palette.GoldShadow);
        Layout.TextWrapped(
            $"{view.DiscardedListings} {(one ? "Listing is" : "Listings are")} not drawn: " +
            $"{(one ? "it asks" : "the dearest asks")} {dearest:N0} gil a unit, over " +
            $"{InsaneListing.Multiple:N0} times the dearest Sale. Nothing that far above the market " +
            "is an offer, and left on the picture it would flatten everything else onto the axis.");
        ImGui.PopStyleColor();
        UiProbe.Widest("discard line");
        ImGui.Spacing();
    }

    /// <summary>
    /// What is deliberately absent, said out loud.
    ///
    /// The ticket's last acceptance criterion: nothing here claims a band, an Estimate or a
    /// forecast, and their absence is to be visible rather than faked. A picture that simply
    /// lacked them would read as a picture that had nothing to say; this says which of them are
    /// coming and which one never will.
    /// </summary>
    private static void DrawWhatIsNotDrawn()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
        Layout.TextWrapped(
            "Observations only. There is no fitted price on this picture yet, no band around one, " +
            "and there will never be a predicted range - an interval says how well a number is " +
            "pinned down, not where the next Sale will land.");
        ImGui.PopStyleColor();
        UiProbe.Widest("what is not drawn");
    }

    private static bool First(GraphWindow preset) => preset == GraphWindow.Presets[0];

    private static string Label(GraphWindow preset) =>
        preset.Days is { } days ? $"{days}d" : "All";

    private static string Slice(RollupWidth width) => width.Span switch
    {
        { TotalHours: < 24 } span => $"{span.TotalHours.ToString("0", CultureInfo.InvariantCulture)}-hour slices",
        { TotalDays: 1 } => "one-day slices",
        { } span => $"{span.TotalDays.ToString("0", CultureInfo.InvariantCulture)}-day slices",
    };

    private static string NameOf(uint itemId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            var name = sheet.GetRowOrDefault(itemId)?.Name.ExtractText();

            return string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read the name of item {ItemId}", itemId);

            return $"Item {itemId}";
        }
    }
}

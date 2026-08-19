using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImPlot;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Graph;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// The graph itself, drawn through ImPlot.
///
/// <b>ImPlot through the binding Dalamud already ships, and no new dependency.</b>
/// <c>Dalamud.Bindings.ImPlot.dll</c> and <c>cimplot.dll</c> sit beside <c>Dalamud.dll</c> at the
/// same build as the running host, so this costs the package nothing. Hand-drawing the whole chart
/// was considered on the graph ticket and rejected: axes, tick formatting, time scales and legends
/// are what ImPlot does well, and the marks it has no primitive for compose on its draw list -
/// which is exactly what the per-Sale dot radius below does.
///
/// <b>Every string in this file is unapproved copy</b>, including the axis and legend labels. What
/// the picture is allowed to claim was settled on the graph ticket; how it says it has not been.
///
/// <b>Nothing here computes anything.</b> Which slices are occupied, where the line breaks, how
/// big a dot is, whether a Listing's start is traced - all of it arrives decided in the
/// <see cref="GraphView"/> and is asserted headlessly. What is left here is pixels.
/// </summary>
internal static class MarketGraph
{
    /// <summary>Legend text for the Sale dots, per Quality. Unapproved copy.</summary>
    private const string SalesLabel = "Sales";

    /// <summary>Legend text for the Listing lines, per Quality. Unapproved copy.</summary>
    private const string ListingsLabel = "on the board";

    /// <summary>
    /// Legend text for the folded line, per Quality. Unapproved copy.
    ///
    /// It says <i>price</i>, and the wording is load-bearing rather than a nicety. The line is the
    /// unit-weighted mean Unit Price of the Sales in each slice; a first pass labelled it "sold
    /// per day", which names volume. A reader taking units-per-day off a price line is reading a
    /// number that is not on the picture at all - the same class of silent wrong number the
    /// glossary exists to prevent, arrived at through a legend instead of a type.
    /// </summary>
    private const string RollupLabel = "mean price per";

    /// <summary>
    /// How wide a Listing seen exactly once is drawn, in pixels.
    ///
    /// A Listing whose review time the Source did not report has no span to draw - its line would
    /// be zero pixels long and simply absent, which would report "nothing is listed" for a board
    /// that is full. A short tick is the honest minimum: it says a price is being asked without
    /// claiming to know for how long.
    /// </summary>
    private const float TickWidth = 7f;

    /// <summary>
    /// Draws the picture.
    /// </summary>
    /// <param name="view">What to draw.</param>
    /// <param name="size">The plot's size. Pass -1 on an axis to fill the region.</param>
    /// <param name="forceAxis">
    /// Whether the window bounds have moved. The axis is forced on the frame they do and left
    /// alone afterwards, so a preset is applied without the plot fighting the reader every frame.
    /// </param>
    internal static void Draw(GraphView view, Vector2 size, bool forceAxis)
    {
        if (!ImPlot.BeginPlot("##emm-graph", size, ImPlotFlags.NoTitle | ImPlotFlags.NoMenus))
        {
            return;
        }

        try
        {
            // NO Y AXIS LABEL, and it is not an omission. ImPlot draws one rotated, through its
            // own vertical-text routine, which writes glyph quads straight into the draw list and
            // assumes the bound texture is the font atlas. Dalamud manages textures differently,
            // so the glyphs sample from whatever is bound and the label renders as noise - seen in
            // game, not guessed at. The unit is stated in ordinary horizontal text above the plot
            // instead, where it is legible.
            ImPlot.SetupAxes(string.Empty, string.Empty, ImPlotAxisFlags.None, ImPlotAxisFlags.None);
            ImPlot.SetupAxisScale(ImAxis.X1, ImPlotScale.Time);

            var condition = forceAxis ? ImPlotCond.Always : ImPlotCond.Once;

            ImPlot.SetupAxisLimits(ImAxis.X1, view.FromSeconds, view.ToSeconds, condition);

            // The price axis is set from what is actually drawn rather than left to fit itself.
            // Only the Rollup line goes through a plotting primitive, so an automatic fit sees the
            // slice means and not the Sales they came from - and a Sale dearer than every slice
            // mean is exactly what an outlier is. Fitted, those sit above the axis and are clipped.
            if (view.Axis is { } axis)
            {
                var padded = axis.Padded(0.06);

                ImPlot.SetupAxisLimits(ImAxis.Y1, padded.Lowest, padded.Highest, condition);
            }

            ImPlot.SetupLegend(ImPlotLocation.NorthWest, ImPlotLegendFlags.None);

            foreach (var layer in view.Layers)
            {
                DrawLayer(layer, view.Width);
            }
        }
        finally
        {
            ImPlot.EndPlot();
        }
    }

    private static void DrawLayer(GraphLayer layer, RollupWidth width)
    {
        var colour = layer.Quality == Quality.High ? Palette.QualityHigh : Palette.QualityNormal;
        var quality = layer.Quality == Quality.High ? "HQ" : "NQ";

        DrawRollups(layer, colour, $"{quality} {RollupLabel} {Slice(width)}");
        DrawListings(layer, colour, $"{quality} {ListingsLabel}");
        DrawSales(layer, colour, $"{quality} {SalesLabel}");
    }

    /// <summary>
    /// The folded line, broken wherever nothing was observed.
    ///
    /// The NaN in the view is what makes the hole: ImPlot breaks a line at one by default, and the
    /// flag that would make it join across instead (SkipNaN) is deliberately not passed. That is
    /// the ticket's "a hole in the data is drawn as a hole" in one decision - and the reason the
    /// NaN is put there by Core rather than here is that a renderer's default is not something a
    /// test can hold on to.
    /// </summary>
    private static void DrawRollups(GraphLayer layer, Vector4 colour, string label)
    {
        if (layer.Line.Count == 0 || layer.Line.IsEmpty)
        {
            return;
        }

        var at = ToArray(layer.Line.At);
        var value = ToArray(layer.Line.Value);

        ImPlot.SetNextLineStyle(colour, 2f);
        ImPlot.PlotLine(label, ref at[0], ref value[0], at.Length);
    }

    /// <summary>
    /// One dot per Sale, sized by Stack.
    ///
    /// Hand-drawn rather than scattered, because ImPlot gives a whole series one marker size and
    /// the size is the point here: a Sale of forty units and a Sale of one must not be the same
    /// mark. The legend entry is a dummy item carrying the colour, which is what keeps the layer
    /// named in the legend even though nothing about it went through a plotting primitive.
    /// </summary>
    private static void DrawSales(GraphLayer layer, Vector4 colour, string label)
    {
        // BeginItem rather than PlotDummy, so that clicking the legend entry actually hides these.
        // PlotDummy registers the entry and returns nothing, so the entry greyed out on a click
        // and the dots carried on being drawn - a control that appears to do something and does
        // not, which is worse than no control. BeginItem returns false when the item is hidden.
        ImPlot.SetNextLineStyle(colour, 2f);

        if (!ImPlot.BeginItem(label, ImPlotItemFlags.None, ImPlotCol.Line))
        {
            return;
        }

        try
        {
            if (layer.Sales.Count == 0)
            {
                return;
            }

            var packed = ImGui.GetColorU32(colour);
            var draw = ImPlot.GetPlotDrawList();

            ImPlot.PushPlotClipRect();

            foreach (var sale in layer.Sales)
            {
                draw.AddCircleFilled(ImPlot.PlotToPixels(sale.At, sale.UnitPrice), sale.Radius, packed);
            }

            ImPlot.PopPlotClipRect();
        }
        finally
        {
            ImPlot.EndItem();
        }
    }

    /// <summary>
    /// One line per Listing on the board, at the price it is asking, running back to when it was
    /// put up.
    ///
    /// Never a dot, and that is the whole reason this is drawn separately: a Sale is what a buyer
    /// paid and a Listing is what a seller hopes for. Given the same mark on the same axis the two
    /// read as one population, and every median a reader takes off the picture is wrong in the
    /// direction that costs gil.
    /// </summary>
    private static void DrawListings(GraphLayer layer, Vector4 colour, string label)
    {
        var dimmed = Palette.Listing(colour);

        ImPlot.SetNextLineStyle(dimmed, 1f);

        if (!ImPlot.BeginItem(label, ImPlotItemFlags.None, ImPlotCol.Line))
        {
            return;
        }

        try
        {
            if (layer.Listings.Count == 0)
            {
                return;
            }

            var packed = ImGui.GetColorU32(dimmed);
            var draw = ImPlot.GetPlotDrawList();

            ImPlot.PushPlotClipRect();

            foreach (var listing in layer.Listings)
            {
                var right = ImPlot.PlotToPixels(listing.To, listing.UnitPrice);
                var left = ImPlot.PlotToPixels(listing.From, listing.UnitPrice);

                // An untraced Listing has no span at all, so it gets the tick rather than a line
                // of no length. Widening a traced-but-short line to the same minimum would be
                // claiming a duration the Source never reported.
                if (right.X - left.X < TickWidth)
                {
                    left.X = right.X - TickWidth;
                }

                draw.AddLine(left, right, packed, 1.5f);
            }

            ImPlot.PopPlotClipRect();
        }
        finally
        {
            ImPlot.EndItem();
        }
    }

    /// <summary>The slice width in words, for the legend. Unapproved copy.</summary>
    private static string Slice(RollupWidth width) => width.Span switch
    {
        { TotalHours: < 24 } span => $"{span.TotalHours:F0}h",
        { TotalDays: 1 } => "day",
        { } span => $"{span.TotalDays:F0}d",
    };

    /// <summary>
    /// ImPlot takes a reference to the first element and walks it, so the values have to be in one
    /// contiguous block that will not move.
    /// </summary>
    private static double[] ToArray(IReadOnlyList<double> values)
    {
        var copy = new double[values.Count];

        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = values[i];
        }

        return copy;
    }
}

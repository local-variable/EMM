using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Ingest;
using EorzeanMarketMaster.Ingest;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// The Scan section: where a figure came from, how old it is, and what the next refresh will cost
/// before it is pressed.
///
/// <b>Every string in this file is unapproved copy.</b> The wording of what EMM tells a Player
/// about its own citizenship is a decision for the maintainer, not for the ticket that made the
/// numbers true.
///
/// <b>Nothing here reads the store directly.</b> The surface draws what
/// <see cref="ScanHost.View"/> last held; the store is one connection and a frame may not wait on
/// it. That is also why the figures move once a second rather than every frame - they are hours
/// old by nature and a query per frame would tell nobody anything.
///
/// Laid out to wrap rather than to scroll: the in-game self-test drives this section at 640x400
/// and fails it on any horizontal scroll extent at all.
/// </summary>
internal static class ScanTab
{
    /// <summary>
    /// The wide refresh's label. Named once because its width is measured before it is drawn.
    ///
    /// "Refresh everything" rather than "sweep" throughout the surface, on the maintainer's
    /// ruling. The code below the seam still calls it a sweep - that is #10's word for the thing
    /// the fifteen-minute floor governs, and the map and spec are written in it.
    /// </summary>
    private const string RefreshEverythingLabel = "Refresh everything tracked";

    internal static void Draw(ScanHost? host)
    {
        if (host is null)
        {
            ImGui.TextWrapped(
                "The store could not be opened, so there is nowhere to put what a refresh would " +
                "fetch. EMM is running without it; see the log for why.");
            return;
        }

        var view = host.View;

        DrawWareSelector(host, view);
        ImGui.Spacing();

        DrawReading(view);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawRefreshControls(host, view);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawCeilings();

        if (view.LastReport is { } report)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawLastRefresh(report);
        }
    }

    /// <summary>Which Ware the Player is looking at. An Item id and a Quality make one Ware.</summary>
    private static void DrawWareSelector(ScanHost host, ScanView view)
    {
        var itemId = (int)host.Selected.ItemId;
        var highQuality = host.Selected.Quality == Quality.High;
        var changed = false;

        ImGui.SetNextItemWidth(120f);

        if (ImGui.InputInt("Item##emm-scan-item", ref itemId))
        {
            changed = true;
        }

        ImGui.SameLine();

        if (ImGui.Checkbox("HQ##emm-scan-hq", ref highQuality))
        {
            changed = true;
        }

        if (changed && itemId > 0)
        {
            host.Select(new WareId((uint)itemId, highQuality ? Quality.High : Quality.Normal));
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
        ImGui.TextWrapped(NameOf(host.Selected));

        if (view.World is null)
        {
            ImGui.TextWrapped("Not logged in, so there is no World to read a board on.");
        }

        ImGui.PopStyleColor();
    }

    /// <summary>What EMM holds for that Ware: the Source, the Freshness, and the board itself.</summary>
    private static void DrawReading(ScanView view)
    {
        if (view.Reading is not { HasObservation: true } reading || reading.Snapshot is null)
        {
            ImGui.TextWrapped(
                "Nothing observed yet. EMM shows what it has stored and never fetches behind a " +
                "draw, so this stays empty until a refresh is asked for.");
            return;
        }

        // Short labels take a SameLine; anything that can run long gets its own wrapped line. The
        // window is resizable down to 620 wide and the self-test drives this section at 640x400.
        ImGui.TextColored(Palette.Gold, "Source");
        ImGui.SameLine();
        ImGui.TextUnformatted(SourceOf(reading.Snapshot.Source));

        ImGui.TextColored(Palette.Gold, "Freshness");
        ImGui.SameLine();
        ImGui.TextColored(GradeColour(reading.Grade), Age(reading.Age));

        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
        ImGui.TextWrapped(GradeOf(reading.Grade, reading.Calibration));
        ImGui.PopStyleColor();

        var listings = reading.Snapshot.Listings;

        ImGui.TextColored(Palette.Gold, "Board");
        ImGui.SameLine();
        ImGui.TextUnformatted(listings.Count == 0
            ? "nothing listed when this was observed"
            : $"{listings.Count} listings, cheapest {listings.Min(l => l.UnitPrice.Gil):N0} gil/unit");
    }

    /// <summary>
    /// The two refreshes, each carrying its cost before it is pressed.
    ///
    /// Both buttons sit on one row, and that is a layout decision rather than a tidiness one: the
    /// docked size is under 700px wide by #13's ruling, and a first in-game pass found the sweep
    /// control sitting below the fold at 640x400 - drawn every frame, and invisible.
    /// </summary>
    private static void DrawRefreshControls(ScanHost host, ScanView view)
    {
        var now = DateTimeOffset.UtcNow;
        var busy = view.Running || view.World is null;
        var sweep = view.SweepVerdict;
        var queued = sweep?.State == RefreshGateState.Queued;

        // DEBUG BUILDS ONLY, AND UNANNOUNCED BY REQUEST: holding Ctrl re-enables the wide refresh
        // while it is queued, so the fifteen-minute floor does not have to be waited out to test
        // the thing behind it.
        //
        // Compiled out of a release, and that is not caution for its own sake. The block below
        // tells the Player in as many words that the ceilings are fixed in code and that there is
        // no setting for them - a hidden bypass in the shipped package would make EMM's own copy
        // untrue, and would hand anyone who found it a way to take the fifteen-minute floor off a
        // service EMM only reads by permission. A maintainer testing their own build is a
        // different case from a Player holding a key.
        //
        // The forced run answers to a throwaway gate rather than to a weakened rule, so it leaves
        // the real floor and the stored instant exactly where they were.
        var ignoreTheFloor = false;
#if DEBUG
        ignoreTheFloor = queued && ImGui.GetIO().KeyCtrl;
#endif

        ImGui.BeginDisabled(busy);

        if (ImGui.Button("Refresh this Ware##emm-scan-point") && view.World is { } world)
        {
            host.RefreshPoint(now, world);
        }

        ImGui.EndDisabled();

        if (sweep is not null)
        {
            // Measured, not hoped. A bare SameLine() puts the second button past the right edge
            // whenever the body is narrow - a 640px window with the rail expanded leaves about
            // 400px - and a child window with no horizontal scrollbar CLIPS it rather than
            // wrapping or scrolling. The control is submitted, is hit-testable, and cannot be
            // seen, which from the outside is indistinguishable from a control that was never
            // drawn. That cost a whole in-game pass to find, so the widths are checked instead.
            if (FitsBeside(RefreshEverythingLabel))
            {
                ImGui.SameLine();
            }

            ImGui.BeginDisabled(busy || (queued && !ignoreTheFloor));

            if (ImGui.Button($"{RefreshEverythingLabel}##emm-scan-sweep"))
            {
                host.RefreshRing(now, ignoreTheFloor);
            }

            ImGui.EndDisabled();
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
        ImGui.TextWrapped(view.PointCost is { } point
            ? $"This Ware: {Cost(point)}"
            : "This Ware: no cost to quote yet");

        ImGui.TextWrapped(sweep is null
            ? "Everything tracked: nothing is tracked on this World yet, so there is nothing to refresh."
            : $"Everything tracked: {view.TrackedWares:N0} Wares - {Cost(sweep.Cost)}");

        ImGui.PopStyleColor();

        if (sweep is not null && queued)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Palette.Gold);
            ImGui.TextWrapped(
                $"Queued. Refreshing everything runs at most once every {Minutes(Citizenship.SweepFloor)}; " +
                $"this one can start in {Countdown(sweep.Countdown)}.");
            ImGui.PopStyleColor();
        }
    }

    /// <summary>
    /// The ceilings, stated as facts. There is deliberately no control anywhere near them: they
    /// are not settings, and a surface that put them beside a slider would say otherwise.
    ///
    /// The one-line form is always visible and the reasoning folds away behind it. Putting the
    /// whole block behind a closed header would have been the easy way to buy back vertical space
    /// and would have quietly stopped the ceilings being <i>shown</i>, which is the thing the
    /// ticket asked for.
    /// </summary>
    private static void DrawCeilings()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Gold);
        ImGui.TextWrapped(
            $"{Citizenship.SustainedRequestsPerSecond:0.#} request/second - " +
            $"{Citizenship.MaxConnections} connections - one refresh of everything per " +
            $"{Minutes(Citizenship.SweepFloor)}. Fixed in code, not settings.");
        ImGui.PopStyleColor();

        if (!ImGui.CollapsingHeader("What EMM will not do to Universalis##emm-scan-ceilings"))
        {
            return;
        }

        ImGui.TextWrapped(
            $"{Citizenship.SustainedRequestsPerSecond:0.#} request per second, which is " +
            $"{Citizenship.ShareOfPublishedRate:P0} of the {Citizenship.PublishedRequestsPerSecond:0.#} " +
            "per second it publishes. No burst allowance is claimed at all: the limits are counted " +
            "per address and EMM may not be the only thing behind yours.");

        ImGui.TextWrapped(
            $"At most {Citizenship.MaxConnections} connections against the " +
            $"{Citizenship.PublishedMaxConnections} it allows - and requests go one at a time, so " +
            "one is what is ever open.");

        ImGui.TextWrapped(
            $"Refreshing everything runs at most once every {Minutes(Citizenship.SweepFloor)}. " +
            "Refreshing a single Ware is not the same thing and is never held back.");

        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
        ImGui.TextWrapped(
            "These are fixed in EMM's code. There is no setting for them and there will not be one.");
        ImGui.PopStyleColor();
    }

    /// <summary>What the last refresh actually did, against what it was quoted at.</summary>
    private static void DrawLastRefresh(IngestReport report)
    {
        if (!ImGui.CollapsingHeader("Last refresh##emm-scan-report", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        if (!report.Ran)
        {
            ImGui.TextWrapped(
                $"Held back: refreshing everything runs at most once every " +
                $"{Minutes(Citizenship.SweepFloor)}. Nothing was sent.");
            return;
        }

        ImGui.TextWrapped(
            $"{report.RequestsMade} of {report.Batches.Count} requests answered, " +
            $"{Bytes(report.BytesReceived)} received against {Bytes(report.Verdict.Cost.EstimatedBytes)} " +
            $"estimated. {report.SnapshotsWritten} observations and {report.SalesWritten} new Sales stored.");

        if (report.RequestsFailed > 0)
        {
            var first = report.Batches.First(batch => !batch.Succeeded);

            ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
            ImGui.TextWrapped(
                $"{report.RequestsFailed} did not answer: {first.Failure}. EMM kept what it already held.");
            ImGui.PopStyleColor();
        }

        if (report.WithoutData.Count > 0)
        {
            ImGui.TextWrapped(
                $"{report.WithoutData.Count} Wares came back with nothing at all. That is not the " +
                "same as an empty board - the Source cannot tell the two apart - so nothing was " +
                "stored for them rather than a bare board being recorded on its behalf.");
        }

        if (report.DiscardedRows > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
            ImGui.TextWrapped($"{report.DiscardedRows} rows were not believable and were dropped.");
            ImGui.PopStyleColor();
        }
    }

    /// <summary>
    /// Whether a button carrying this label still fits on the line just drawn.
    /// </summary>
    /// <param name="label">The button's visible text, without its id suffix.</param>
    /// <returns>Whether to put it on the same line.</returns>
    private static bool FitsBeside(string label)
    {
        var style = ImGui.GetStyle();
        var needed = ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f) + style.ItemSpacing.X;

        return needed <= ImGui.GetContentRegionAvail().X;
    }

    private static string NameOf(WareId ware)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            var name = sheet.GetRowOrDefault(ware.ItemId)?.Name.ExtractText();

            return string.IsNullOrWhiteSpace(name) ? $"Item {ware.ItemId}" : name;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read the name of item {ItemId}", ware.ItemId);

            return $"Item {ware.ItemId}";
        }
    }

    private static string SourceOf(Source source) => source switch
    {
        Source.Aggregator => "Universalis",
        Source.OpenedBoard => "a board opened in game",
        Source.ImportedStore => "another plugin's store, imported",
        _ => "unknown",
    };

    private static string GradeOf(FreshnessGrade grade, WorldFreshness calibration) => grade switch
    {
        FreshnessGrade.Fresh => $"fresh for this World (its median is {Age(calibration.Median)})",
        FreshnessGrade.Aging => $"older than typical here (median {Age(calibration.Median)})",
        FreshnessGrade.Stale => $"stale for this World (its 90th percentile is {Age(calibration.Ninetieth)})",
        _ => $"not graded yet - {calibration.Sample} of {WorldFreshness.MinimumSample} observations " +
             "needed to know what old means on this World",
    };

    private static Vector4 GradeColour(FreshnessGrade grade) => grade switch
    {
        FreshnessGrade.Fresh => Palette.Green,
        FreshnessGrade.Stale => Palette.GoldShadow,
        _ => Palette.Gold,
    };

    private static string Cost(RefreshCost cost)
    {
        var floor = cost.PacingFloor >= TimeSpan.FromSeconds(5)
            ? $", at least {Countdown(cost.PacingFloor)}"
            : string.Empty;

        return $"{cost.Requests:N0} requests, about {Bytes(cost.EstimatedBytes)}{floor}";
    }

    private static string Age(TimeSpan? age) => age switch
    {
        null => "unknown",
        { TotalMinutes: < 90 } value => $"{value.TotalMinutes:F0} min old",
        { TotalHours: < 48 } value => $"{value.TotalHours:F0} h old",
        { } value => $"{value.TotalDays:F0} d old",
    };

    private static string Countdown(TimeSpan remaining) => remaining.TotalMinutes >= 1
        ? $"{remaining.TotalMinutes:F0} min"
        : $"{remaining.TotalSeconds:F0} s";

    private static string Minutes(TimeSpan span) =>
        $"{span.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes";

    private static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
    };
}

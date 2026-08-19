using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Holdings;
using EorzeanMarketMaster.Holdings;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// The Holdings section: what the Player owns, where it is, and how old each of those statements
/// is.
///
/// <b>Every string in this file is unapproved copy.</b> What EMM is allowed to claim about a
/// Retainer it has not opened is a decision this ticket made; the words it says it in are the
/// maintainer's.
///
/// <b>Nothing here reads the game or the store.</b> The surface draws what
/// <see cref="HoldingsHost.Shown"/> last held, for the same reason the Scan and Pricing sections
/// do. The filters are held on the host too, so the narrowing is recomputed when it changes rather
/// than once a frame.
///
/// <b>The table drops to three columns when the body is narrow rather than letting five fight over
/// 400px.</b> The in-game self-test drives this section at 640x400 with the rail expanded, and a
/// table whose columns are all too thin to read is a table nobody reads.
/// </summary>
internal static class HoldingsTab
{
    /// <summary>The refresh control's label. Named once because its width is measured before it is drawn.</summary>
    private const string RefreshLabel = "Refresh every Retainer";

    /// <summary>The way out of a walk. Named once because its width is measured before it is drawn.</summary>
    private const string StopLabel = "Stop";

    /// <summary>Below this much body width the table drops its two widest columns.</summary>
    private const float CompactBelow = 560f;

    /// <summary>The icon square. One line's height, so a row is a row rather than a band.</summary>
    private const float IconSide = 20f;

    internal static void Draw(HoldingsHost? host)
    {
        if (host is null)
        {
            Layout.TextWrapped(
                "The store could not be opened, so there is nowhere to remember what EMM sees. " +
                "Holdings are last-seen state and need somewhere to be kept; see the log for why.");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        DrawRefresh(host, now);
        ImGui.Spacing();
        Layout.Separator();
        ImGui.Spacing();

        DrawTotals(host);
        ImGui.Spacing();

        DrawFilters(host);
        ImGui.Spacing();

        DrawTable(host.Shown, now);

        ImGui.Spacing();
        Layout.Separator();
        ImGui.Spacing();

        DrawRetainers(host.View, now);
    }

    /// <summary>
    /// The control the ticket asked for, and the sentence that keeps it honest.
    ///
    /// It is enabled only at a summoning bell because that is the only place the game will report
    /// every Retainer at once - and it refreshes counts rather than contents, because that is all
    /// the bell exposes. Saying so beside the button is not an apology: a Player who presses it and
    /// believes their Listings have been re-read would act on a week-old board.
    /// </summary>
    private static void DrawRefresh(HoldingsHost host, DateTimeOffset now)
    {
        var blocked = host.CannotScan;

        ImGui.BeginDisabled(blocked is not null);

        if (ImGui.Button($"{RefreshLabel}##emm-holdings-scan"))
        {
            host.Scan(now);
        }

        ImGui.EndDisabled();

        // The way out, and it only exists while there is something to get out of. A walk opens
        // every Retainer in turn and that is a minute of the Player's time they may want back.
        if (host.Walking)
        {
            if (Layout.FitsBeside(StopLabel))
            {
                ImGui.SameLine();
            }

            if (ImGui.Button($"{StopLabel}##emm-holdings-stop"))
            {
                host.StopWalking();
            }
        }

        UiProbe.Widest("refresh control");

        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
        Layout.TextWrapped(Explain(host, blocked));
        ImGui.PopStyleColor();
        UiProbe.Widest("refresh explanation");

        if (host.Walking)
        {
            var (read, missed) = host.WalkProgress;

            ImGui.PushStyleColor(ImGuiCol.Text, Palette.Gold);
            Layout.TextWrapped(missed == 0
                ? $"{read:N0} Retainers read so far."
                : $"{read:N0} Retainers read so far, {missed:N0} skipped.");
            ImGui.PopStyleColor();
            UiProbe.Widest("walk progress");
        }

        if (host.LastScan is { } scan)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, scan.Refusal is null ? Palette.Gold : Palette.Muted);
            Layout.TextWrapped(Describe(scan, host));
            ImGui.PopStyleColor();
            UiProbe.Widest("last refresh");
        }
    }

    /// <summary>
    /// What the control will do, or why it is waiting.
    ///
    /// <b>The first line is different depending on whether EMM can have Retainers opened for
    /// it</b>, and that is the capability tier surfacing rather than an apology. With the
    /// automation plugin present the press reads every Retainer's contents; without it the same
    /// press reads what the bell exposes, which is counts. Saying which one is about to happen is
    /// the difference between a Player who knows their Listings were re-read and one who assumes it.
    /// </summary>
    private static string Explain(HoldingsHost host, string? blocked) => blocked switch
    {
        null when host.CanWalk =>
            "Hands the Retainer list to AutoRetainer to walk, and reads each Retainer's stock and " +
            "Listings as it opens them. EMM only reads - it lists nothing, prices nothing and " +
            "changes nothing - and it hands each Retainer straight back.",

        null =>
            "Reads every Retainer's Listing count, item count, gil and market expiry. AutoRetainer " +
            "is not available to open them, and the bell exposes counts and not contents - so what " +
            "EMM knows a Retainer is holding still changes only when you open that Retainer.",

        "not at a summoning bell" =>
            "Available at a summoning bell. It is the only place the game reports every Retainer " +
            "at once.",

        _ => $"Waiting - {blocked}.",
    };

    private static string Describe(HoldingsScan scan, HoldingsHost host)
    {
        if (scan.Refusal is { } refusal)
        {
            return $"Nothing refreshed: {refusal}.";
        }

        var companion = host.CompanionReady
            ? $" {scan.FromCompanion} covered by {CompanionHoldings.PluginName}."
            : string.Empty;

        return $"{scan.Retainers} Retainers refreshed, {scan.ReadInFull} read in full.{companion}";
    }

    /// <summary>
    /// One line for the whole question, before any of it is broken down.
    ///
    /// Drawn from the unfiltered view on purpose. "What do I own" is not a question about what the
    /// controls are currently showing, and a total that moved when a filter changed would be
    /// answering a different question under the same heading.
    /// </summary>
    private static void DrawTotals(HoldingsHost host)
    {
        var view = host.View;

        if (!host.Loaded)
        {
            Layout.TextWrapped("Reading what EMM had stored...");
            UiProbe.Widest("totals");
            return;
        }

        if (view.DistinctWares == 0)
        {
            Layout.TextWrapped(
                "Nothing owned that EMM has seen. It reads a Character's bags while that Character " +
                "is logged in, and a Retainer's stock only while that Retainer is open - so this " +
                "fills in as Retainers are visited rather than all at once.");
            UiProbe.Widest("totals");
            return;
        }

        // A heading of its own rather than a label with the figures beside it. The docked window is
        // under 700px by the interface ruling and the self-test drives this section at 640x400 with
        // the rail expanded, which leaves the body around 400px - and a line that cannot wrap is
        // one that gets clipped by the child and cannot be seen at all.
        ImGui.TextColored(Palette.Gold, "Owned");
        UiProbe.Widest("totals heading");

        Layout.TextWrapped(
            $"{view.DistinctWares:N0} Wares - {view.UnitsListed:N0} units listed, " +
            $"{view.UnitsUnlisted:N0} not");
        UiProbe.Widest("totals");

        if (!host.CompanionReady)
        {
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
        Layout.TextWrapped(
            $"{CompanionHoldings.PluginName} is installed and is read as an additional Source. It " +
            "covers Retainers EMM has not opened itself, and it cannot say when it last looked at " +
            "them - so what it contributes carries an unknown age rather than a fresh one.");
        ImGui.PopStyleColor();
        UiProbe.Widest("companion note");
    }

    /// <summary>
    /// The two filters: the game's own Category, and where the units are.
    ///
    /// Both populations come from what EMM actually holds, so neither control can offer a choice
    /// that produces an empty table. They sit on one row where there is room and stack where there
    /// is not, measured rather than hoped - a bare SameLine at 400px puts the second one past the
    /// edge, where a child window clips it and it is drawn and unseeable.
    /// </summary>
    private static void DrawFilters(HoldingsHost host)
    {
        if (host.View.DistinctWares == 0)
        {
            return;
        }

        var width = MathF.Min(190f, MathF.Max(Layout.BodyWidth() * 0.46f, 110f));

        DrawTypeFilter(host, width);

        if (Layout.FitsBeside(LocationLabel(host)))
        {
            ImGui.SameLine();
        }

        DrawLocationFilter(host, width);
        UiProbe.Widest("filters");
    }

    /// <summary>
    /// The Ware type filter: the seven structural types, and only the ones actually owned.
    ///
    /// Types rather than the game's own Categories. The Item table has some fifty of those, so the
    /// first spelling of this control was a forty-entry list with no two Wares in it that wanted
    /// treating alike - whereas the seven are the partition EMM itself reasons in.
    /// </summary>
    private static void DrawTypeFilter(HoldingsHost host, float width)
    {
        ImGui.SetNextItemWidth(width);

        if (!ImGui.BeginCombo("##emm-holdings-type", host.Type?.ToString() ?? "Everything"))
        {
            return;
        }

        if (ImGui.Selectable("Everything", host.Type is null))
        {
            host.Show((WareType?)null);
        }

        foreach (var type in host.Types)
        {
            if (ImGui.Selectable($"{type}##emm-type-{(int)type}", host.Type == type))
            {
                host.Show(type);
            }
        }

        ImGui.EndCombo();
    }

    private static void DrawLocationFilter(HoldingsHost host, float width)
    {
        ImGui.SetNextItemWidth(width);

        if (!ImGui.BeginCombo("##emm-holdings-location", LocationLabel(host)))
        {
            return;
        }

        if (ImGui.Selectable("Everywhere", host.Location is null))
        {
            host.Show((ShownLocation?)null);
        }

        foreach (var location in host.Locations)
        {
            if (ImGui.Selectable($"{location.Label}##emm-loc-{location.Label}", host.Location == location))
            {
                host.Show(location);
            }
        }

        ImGui.EndCombo();
    }

    private static string LocationLabel(HoldingsHost host) => host.Location?.Label ?? "Everywhere";

    /// <summary>
    /// Every Ware owned, as a table: its icon, its name, how many, where, and how old that is.
    ///
    /// <b>The row count is not capped and the clipper is why.</b> A Player with thirty Retainers can
    /// own several hundred distinct Wares, and an earlier draft simply stopped at two hundred - a
    /// silent truncation of the answer to "what do I own". Only the rows on screen are built, so the
    /// list can be complete and cheap at the same time.
    /// </summary>
    private static void DrawTable(HoldingsView view, DateTimeOffset now)
    {
        if (view.Wares.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
            Layout.TextWrapped("Nothing owned matches those filters.");
            ImGui.PopStyleColor();
            UiProbe.Widest("empty table");
            return;
        }

        var width = Layout.BodyWidth();
        var compact = width < CompactBelow;

        const ImGuiTableFlags Flags =
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.BordersInnerV |
            ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.NoHostExtendX;

        if (!ImGui.BeginTable("##emm-holdings", compact ? 3 : 5, Flags, new Vector2(width, 0f)))
        {
            return;
        }

        var quantityWidth = ImGui.CalcTextSize("999,999").X;

        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, IconSide);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, quantityWidth);

        if (!compact)
        {
            ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("Last seen", ImGuiTableColumnFlags.WidthFixed,
                ImGui.CalcTextSize("unknown age").X);
        }

        ImGui.TableHeadersRow();

        var clipper = new ImGuiListClipper();

        clipper.Begin(view.Wares.Count, IconSide + (ImGui.GetStyle().CellPadding.Y * 2f));

        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                DrawRow(view.Wares[i], compact, now);
            }
        }

        clipper.End();
        ImGui.EndTable();
        UiProbe.Widest("holdings table");
    }

    private static void DrawRow(OwnedWare owned, bool compact, DateTimeOffset now)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        WareIcon.Draw(owned.Ware, IconSide);

        ImGui.TableNextColumn();
        Cell(NameOf(owned.Ware));

        // The full row as a tooltip, because the two widest columns are the ones a narrow window
        // drops and a clipped cell is the one a Player most wants to read.
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{NameOf(owned.Ware)}\n{Where(owned)}\n{FreshnessOf(owned, now)}");
        }

        ImGui.TableNextColumn();
        Cell($"{owned.Units:N0}");

        if (compact)
        {
            return;
        }

        ImGui.TableNextColumn();
        Cell(Where(owned));

        ImGui.TableNextColumn();

        var age = owned.Age(now);

        Cell(age is { } known ? Age(known) : "unknown age",
            age is null ? Palette.Muted : Palette.GoldHighlight);
    }

    /// <summary>
    /// One cell's text, centred against the icon rather than sitting on the row's top edge.
    ///
    /// Centred by hand and not with <c>AlignTextToFramePadding</c>: that aligns text with framed
    /// controls, and there are none in this row - it would push the text three pixels past centre
    /// rather than to it. The icon is the tallest thing in the row, so it is what everything else
    /// is measured against.
    /// </summary>
    private static void Cell(string text, Vector4? colour = null)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((IconSide - ImGui.GetTextLineHeight()) * 0.5f));

        if (colour is { } tint)
        {
            ImGui.TextColored(tint, text);
        }
        else
        {
            ImGui.TextUnformatted(text);
        }
    }

    /// <summary>
    /// Every Retainer the bell reported, and how far what EMM holds for it can be relied on.
    ///
    /// Drawn from the unfiltered view: this is a statement about coverage rather than about what is
    /// on screen, and a Player narrowing the table to one Retainer still needs the others.
    /// </summary>
    private static void DrawRetainers(HoldingsView view, DateTimeOffset now)
    {
        if (view.Retainers.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
            Layout.TextWrapped(
                "No Retainer list read yet. Stand at a summoning bell and refresh to see every " +
                "Retainer's counts at once.");
            ImGui.PopStyleColor();
            UiProbe.Widest("no roster");
            return;
        }

        ImGui.TextColored(Palette.Gold, "Retainers");
        UiProbe.Widest("retainer heading");

        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Muted);
        Layout.TextWrapped(view.RosterReadAt is { } read
            ? $"counts as of {Age(now - read)}"
            : "never read");
        ImGui.PopStyleColor();
        UiProbe.Widest("roster age");

        var width = Layout.BodyWidth();
        var compact = width < CompactBelow;

        const ImGuiTableFlags Flags =
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.BordersInnerV |
            ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.NoHostExtendX;

        if (!ImGui.BeginTable("##emm-retainers", compact ? 3 : 5, Flags, new Vector2(width, 0f)))
        {
            return;
        }

        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("Listed", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Listed").X);

        if (!compact)
        {
            ImGui.TableSetupColumn("Stock", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Stock").X);
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("999,999,999").X);
        }

        ImGui.TableSetupColumn("What EMM knows", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableHeadersRow();

        foreach (var standing in view.Retainers)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(standing.Summary.Retainer.Retainer);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{standing.Summary.MarketItemCount:N0}");

            if (!compact)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{standing.Summary.ItemCount:N0}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{standing.Summary.Gil:N0}");
            }

            ImGui.TableNextColumn();

            // The colour is on this cell alone. An earlier draft tinted the whole line, which put
            // three colours down a column of otherwise identical rows and made the last Retainer
            // look like it was being singled out for something.
            ImGui.TextColored(StandingColour(standing.Standing), Standing(standing, now));

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Standing(standing, now) + "\n" + LastRead(standing, now));
            }
        }

        ImGui.EndTable();
        UiProbe.Widest("retainer table");
    }

    /// <summary>
    /// What EMM is allowed to say about a Retainer's contents, given what the bell counted.
    ///
    /// The wording of the disagreement case is load-bearing. The count nets a Sale against a
    /// relist, and it was measured lagging its own Retainer's container by seconds - so it can say
    /// that something moved and it can never say what, and a surface that called it a Sale would be
    /// asserting something nobody observed.
    /// </summary>
    private static string Standing(RetainerStanding standing, DateTimeOffset now) =>
        standing.Standing switch
        {
            ContentsStanding.NeverSeen => "never opened - counts only",

            ContentsStanding.BeingRead => "open now, so its count is still catching up",

            ContentsStanding.MarketLapsed => "market lapsed, so its count means nothing",

            ContentsStanding.Agrees => $"matches the {standing.ListedKnown:N0} EMM last saw listed",

            ContentsStanding.MayHaveMoved =>
                $"something moved - EMM saw {standing.ListedKnown:N0}, the bell counts " +
                $"{standing.Summary.MarketItemCount:N0}",

            // Named rather than left to fall through the arm above. A standing added later would
            // otherwise inherit "something moved", which is a claim about the game.
            _ => LastRead(standing, now),
        };

    /// <summary>
    /// How old EMM's own reading of a Retainer is.
    ///
    /// In the tooltip rather than the cell. It was a clause on every row and it is the same clause
    /// on every row, so it took a column's width to say what the Player only wants when they are
    /// asking about one Retainer in particular.
    /// </summary>
    private static string LastRead(RetainerStanding standing, DateTimeOffset now) =>
        standing.TrueAsOf is { } seen
            ? $"EMM last read it {Age(now - seen)}."
            : "EMM has not read it itself, and the Source that covered it cannot say when it last looked.";

    /// <summary>
    /// Where the units are, naming the Retainers rather than only counting them.
    ///
    /// The three places are kept apart because they mean different things: listed is earning,
    /// stock is waiting for a slot, and a bag is not with the Retainer that would sell it.
    /// </summary>
    private static string Where(OwnedWare owned)
    {
        var parts = new List<(int Units, string Place)>();

        if (owned.Listed > 0)
        {
            parts.Add((owned.Listed, $"listed by {Named(owned, HoldingPlace.Listed)}"));
        }

        if (owned.InStock > 0)
        {
            parts.Add((owned.InStock, $"with {Named(owned, HoldingPlace.Stock)}"));
        }

        if (owned.InBags > 0)
        {
            parts.Add((owned.InBags, "in bags"));
        }

        // A Ware that is all in one place says where, and nothing else. The quantity is in the
        // column immediately to the left, and repeating it here made every row read "9,976 |
        // 9,976 in bags" - a column of numbers restating the column beside it, which is most of
        // what made the table tiring to look down.
        return parts.Count == 1
            ? parts[0].Place
            : string.Join(", ", parts.Select(part => $"{part.Units:N0} {part.Place}"));
    }

    private static string Named(OwnedWare owned, HoldingPlace place)
    {
        var retainers = owned.Places
            .Where(row => row.Place == place && row.Retainer is not null)
            .Select(row => row.Retainer!.Value.Retainer)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return retainers.Count switch
        {
            0 => "a Retainer",
            1 => retainers[0],
            2 => $"{retainers[0]} and {retainers[1]}",
            _ => $"{retainers[0]} and {retainers.Count - 1:N0} others",
        };
    }

    private static string FreshnessOf(OwnedWare owned, DateTimeOffset now) =>
        owned.Age(now) is { } age
            ? $"last seen {Age(age)}"
            : "part of it from a Source that cannot say how old it is";

    private static Vector4 StandingColour(ContentsStanding standing) => standing switch
    {
        ContentsStanding.Agrees => Palette.Green,
        ContentsStanding.MayHaveMoved => Palette.Gold,
        _ => Palette.Muted,
    };

    private static string Age(TimeSpan age) => age switch
    {
        { TotalSeconds: < 90 } => "just now",
        { TotalMinutes: < 90 } value => $"{value.TotalMinutes:F0} min ago",
        { TotalHours: < 48 } value => $"{value.TotalHours:F0} h ago",
        var value => $"{value.TotalDays:F0} d ago",
    };

    private static string NameOf(WareId ware) =>
        ware.Quality == Quality.High
            ? $"{ItemFacts.Name(ware.ItemId)} (HQ)"
            : ItemFacts.Name(ware.ItemId);
}

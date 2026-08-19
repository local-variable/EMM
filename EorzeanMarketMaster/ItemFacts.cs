using System;
using System.Collections.Generic;
using EorzeanMarketMaster.Core;

namespace EorzeanMarketMaster;

/// <summary>
/// The three things EMM needs out of the game's Item table to draw a row: what it is called, what
/// it looks like, and which of the seven structural types it belongs to.
///
/// <b>Cached, because a surface asks per row per frame.</b> A Holdings table with two hundred rows
/// would otherwise take six hundred sheet lookups a frame to draw figures that never change inside
/// a session. The Item table is static data; only the language could move it, and a language switch
/// reloads the plugin.
/// </summary>
internal static class ItemFacts
{
    private static readonly Dictionary<uint, Facts> Known = [];

    /// <summary>What one Item is called, or a legible stand-in where the sheet has no row for it.</summary>
    /// <param name="itemId">The Item.</param>
    /// <returns>The name.</returns>
    internal static string Name(uint itemId) => Of(itemId).Name;

    /// <summary>The Item's icon id, or 0 where the sheet has no row for it.</summary>
    /// <param name="itemId">The Item.</param>
    /// <returns>The icon id.</returns>
    internal static uint Icon(uint itemId) => Of(itemId).Icon;

    /// <summary>The Item's structural type - the seven-way split EMM groups by.</summary>
    /// <param name="itemId">The Item.</param>
    /// <returns>The type.</returns>
    internal static WareType Type(uint itemId) => Of(itemId).Type;

    private static Facts Of(uint itemId)
    {
        if (Known.TryGetValue(itemId, out var known))
        {
            return known;
        }

        var facts = Read(itemId);

        Known[itemId] = facts;

        return facts;
    }

    private static Facts Read(uint itemId)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();

            if (sheet.GetRowOrDefault(itemId) is { } row)
            {
                var name = row.Name.ExtractText();
                var search = row.ItemSearchCategory.ValueNullable;

                return new Facts(
                    string.IsNullOrWhiteSpace(name) ? Unnamed(itemId) : name,
                    row.Icon,
                    Classify(
                        search?.Name.ExtractText() ?? string.Empty,
                        search?.Category ?? 0,
                        row.StackSize));
            }
        }
        catch (Exception ex)
        {
            // A sheet that cannot be read is a reason to say less about this Item, not a reason to
            // take the frame down. Cached either way, so the failure is not retried per row per
            // frame, and the log carries the cause once.
            Plugin.Log.Warning(ex, "EMM: could not read item {ItemId} from the sheet", itemId);
        }

        return new Facts(Unnamed(itemId), 0, WareType.Miscellany);
    }

    /// <summary>
    /// The seven-way structural split, from the undercut modelling that measured it.
    ///
    /// The order of the tests is the classification - each one claims what it claims and hands the
    /// rest on - so it is transcribed rather than rearranged. Materia first because it is its own
    /// thing whatever else is true of it; the game's own coarse market group next, because Weapons,
    /// Armour and Housing are settled facts that no search category should be able to argue with;
    /// then the two named sets; then stackability, which is what separates bulk stock from the
    /// one-offs left over.
    ///
    /// <b>The named sets are matched on the game's own English category names</b>, which is what
    /// the modelling did. A client running in another language will drop those two tests and file
    /// their Wares by stackability instead - wrong, but wrong in a way that only softens a filter
    /// here. It becomes load-bearing when a Strategy is assigned per type, and the ticket that does
    /// that is the one that should replace these with sheet row ids.
    /// </summary>
    private static WareType Classify(string searchCategory, byte marketGroup, uint stackSize) =>
        searchCategory switch
        {
            "Materia" => WareType.Materia,
            _ when marketGroup is Weapons or Armour => WareType.Gear,
            _ when marketGroup == Housing => WareType.Furnishing,
            _ when Collectibles.Contains(searchCategory) => WareType.Collectible,
            _ when Consumables.Contains(searchCategory) => WareType.Consumable,
            _ => stackSize > 1 ? WareType.Material : WareType.Miscellany,
        };

    /// <summary>The game's own coarsest market grouping, on <c>ItemSearchCategory.Category</c>.</summary>
    private const byte Weapons = 1;
    private const byte Armour = 2;
    private const byte Housing = 4;

    private static readonly HashSet<string> Collectibles = new(StringComparer.Ordinal)
        { "Minions", "Orchestrion Components", "Paintings", "Registrable Miscellany" };

    private static readonly HashSet<string> Consumables = new(StringComparer.Ordinal)
        { "Meals", "Medicine", "Ingredients", "Seafood" };

    private static string Unnamed(uint itemId) => $"Item {itemId}";

    private readonly record struct Facts(string Name, uint Icon, WareType Type);
}

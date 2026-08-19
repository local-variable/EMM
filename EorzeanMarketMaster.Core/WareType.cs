namespace EorzeanMarketMaster.Core;

/// <summary>
/// The structural type of a Ware: the seven-way split EMM groups by before the Player has grouped
/// anything themselves.
///
/// <b>Classified from game data alone - search category, the game's own coarse market group, and
/// whether the Ware stacks.</b> That is deliberate and load-bearing: EMM has to be able to type a
/// Ware it has never seen trade, on install day or the first time a patch item appears, so a
/// taxonomy derived from observed behaviour would be useless exactly when it is most needed.
///
/// <b>Whether the split tracks behaviour was measured rather than assumed.</b> It came out of the
/// undercut modelling as the grouping the fitted model recalibrates per, which is why the store has
/// carried a <c>ware_type</c> column since the schema's first migration.
///
/// <b>Not a Group.</b> The glossary reserves Group for a set the Player defines, and a Group can
/// be built out of these; these are given by the game and cannot be edited. The distinction matters
/// because one of them is a preference and the other is a fact.
///
/// The numbers are explicit and permanent: they are what <c>calibration.ware_type</c> stores, and
/// renumbering them later would silently re-file every fit ever recorded.
/// </summary>
public enum WareType
{
    /// <summary>Materia. Its own type because it trades like nothing else does.</summary>
    Materia = 0,

    /// <summary>Stackable crafting and gathering stock.</summary>
    Material = 1,

    /// <summary>Food, medicine, ingredients, seafood - bought to be used up.</summary>
    Consumable = 2,

    /// <summary>Weapons and armour. Unique, and priced on demand rather than on throughput.</summary>
    Gear = 3,

    /// <summary>Housing and furnishing.</summary>
    Furnishing = 4,

    /// <summary>Minions, orchestrion rolls, paintings and the rest of the collected things.</summary>
    Collectible = 5,

    /// <summary>Everything else: unstackable and in none of the above.</summary>
    Miscellany = 6,
}

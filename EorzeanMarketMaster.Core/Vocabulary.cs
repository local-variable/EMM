namespace EorzeanMarketMaster.Core;

/// <summary>
/// The game's own quality distinction, and the dimension that separates two Wares of one Item.
/// Not every Item can be HQ.
/// </summary>
public enum Quality
{
    /// <summary>Normal Quality.</summary>
    Normal,

    /// <summary>High Quality.</summary>
    High,
}

/// <summary>
/// A Ware: an Item at a Quality, and the thing a price attaches to. An HQ tincture and its NQ
/// counterpart are two Wares of one Item, so the Quality is part of the identity rather than a
/// property hanging off it - every price, Floor and History in EMM is a fact about a Ware.
/// </summary>
/// <param name="ItemId">The game's own item table id. Quality-agnostic on its own.</param>
/// <param name="Quality">Which of the Item's two Wares this is.</param>
public readonly record struct WareId(uint ItemId, Quality Quality);

/// <summary>
/// A World: one game server, and the only place a board physically exists.
///
/// Carried on every observation the store holds, because a Market is one World's board and a
/// figure from another World describes a Market the Player would have to travel to. Keyed on the
/// game's own World id rather than the name: names are display text, they are localised, and the
/// store outlives any particular rendering of them.
/// </summary>
/// <param name="Id">The game's own World table id.</param>
public readonly record struct WorldId(uint Id);

/// <summary>
/// A Retainer, identified the way the automation surface EMM drives identifies it: by its name
/// within its owning Character. There is no stable numeric id available on that surface, so
/// keying on one here would mean inventing a mapping that the write path could not honour.
/// </summary>
/// <param name="Character">The owning Character's name.</param>
/// <param name="Retainer">The Retainer's name, unique within that Character.</param>
public readonly record struct RetainerId(string Character, string Retainer);

/// <summary>
/// Gil for one unit of a Ware.
///
/// A type rather than a <c>long</c> on purpose. The glossary retires the bare word "price"
/// because it hides whether a figure is per unit or per Stack, and a naked <c>long</c> reinstates
/// exactly that ambiguity: a Stack total assigned into a per-unit field is a wrong number that
/// compiles, ships, and prices every Listing it touches wrong. Identity got <see cref="WareId"/>
/// and <see cref="RetainerId"/> for the same reason; money is the field where being wrong costs
/// the Player gil.
/// </summary>
public readonly record struct UnitPrice
{
    /// <summary>Wraps a per-unit gil figure.</summary>
    /// <param name="gil">Gil for one unit. Never negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">The figure is negative.</exception>
    public UnitPrice(long gil)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gil);
        Gil = gil;
    }

    /// <summary>The figure, in gil, for one unit.</summary>
    public long Gil { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Gil} gil/unit";
}

/// <summary>
/// The breadth a price, series or comparison is measured over. Carried alongside every figure,
/// because a Data Centre or Region figure describes Markets elsewhere and acting on it means
/// travelling - so it is never quoted as a price available here.
/// </summary>
public enum Scope
{
    /// <summary>One World's board. The only Scope at which a Listing physically exists.</summary>
    World,

    /// <summary>A group of Worlds a Character can travel between. Analysis only.</summary>
    DataCentre,

    /// <summary>A group of Data Centres. Analysis only.</summary>
    Region,
}

/// <summary>
/// Where an observation came from.
///
/// Stored on every row rather than inferred, because the three differ in Freshness and in
/// trustworthiness and a figure blended from several has to be able to say so. It is also what
/// makes an imported row identifiable after the fact: imported rows are irreplaceable, since the
/// snapshot they were read out of gets overwritten by the plugin that owns it.
/// </summary>
public enum Source
{
    /// <summary>The aggregator EMM polls. Sampled, and as fresh as its last upload.</summary>
    Aggregator,

    /// <summary>A board the Player opened in game. Ground truth at the instant it was read.</summary>
    OpenedBoard,

    /// <summary>A store belonging to another plugin, read once. Not refetchable.</summary>
    ImportedStore,
}

/// <summary>
/// How much EMM can do unaided, detected at runtime and stated on first run. EMM never hard-
/// depends on another plugin: an absent one lowers the tier rather than failing the load.
/// </summary>
public enum CapabilityTier
{
    /// <summary>EMM alone. It computes and proposes; the Player acts.</summary>
    Solo,

    /// <summary>EMM acts inside a window another plugin holds open for it.</summary>
    Assisted,

    /// <summary>EMM's sell side runs unattended under Mandate.</summary>
    Autonomous,
}

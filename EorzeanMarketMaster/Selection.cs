using EorzeanMarketMaster.Core;

namespace EorzeanMarketMaster;

/// <summary>
/// Which Ware the Player is looking at, shared by every surface that shows one.
///
/// One object rather than one per section, because two surfaces each remembering their own Ware is
/// how a Player ends up reading a Freshness for one Item beside a graph of another. A Ware is the
/// subject of the whole window, not a property of a tab.
///
/// <b>The configuration fields behind this are still named for the Scan section</b>, which is
/// where the selection used to live. Renaming a persisted field silently discards what every
/// existing install has stored, so it is left alone here and flagged as a change that needs a
/// ruling of its own rather than made in passing.
/// </summary>
internal sealed class Selection
{
    private readonly Configuration configuration;

    internal Selection(Configuration configuration)
    {
        this.configuration = configuration;

        Ware = new WareId(
            configuration.ScanItemId,
            configuration.ScanHighQuality ? Quality.High : Quality.Normal);
    }

    /// <summary>The Ware on show.</summary>
    internal WareId Ware { get; private set; }

    /// <summary>
    /// Rises every time the selection changes. What a surface holding derived state watches, so
    /// that "the Player picked a different Ware" is one comparison rather than a remembered copy
    /// of the Ware itself.
    /// </summary>
    internal int Revision { get; private set; }

    /// <summary>
    /// Points every surface at a different Ware and remembers it across a restart.
    /// </summary>
    /// <param name="ware">The Ware.</param>
    internal void Select(WareId ware)
    {
        if (ware == Ware)
        {
            return;
        }

        Ware = ware;
        Revision++;

        configuration.ScanItemId = ware.ItemId;
        configuration.ScanHighQuality = ware.Quality == Quality.High;
        configuration.Save();
    }
}

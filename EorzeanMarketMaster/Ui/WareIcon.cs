using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Holdings;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// The Item's own icon, drawn the same way wherever a Ware is named.
///
/// One place rather than one per section, because the two things that are easy to get subtly
/// different are the two that matter: the High Quality variant of the icon, which is a different
/// texture and not an overlay EMM draws, and what happens when the texture is not ready. A Ware
/// carries its Quality in its identity, so the icon has to as well or an HQ row is illustrated with
/// its NQ counterpart.
/// </summary>
internal static class WareIcon
{
    /// <summary>
    /// Draws the icon at a given size, occupying the space either way.
    ///
    /// <b>A missing texture draws as a gap of the right size rather than as nothing.</b> Dalamud
    /// loads textures asynchronously, so the first frame a row appears will often have none - and a
    /// row that collapsed to text height for one frame and then grew would make a table jitter
    /// every time it was scrolled.
    /// </summary>
    /// <param name="ware">The Ware, whose Quality picks the variant.</param>
    /// <param name="side">The square's side, in pixels.</param>
    internal static void Draw(WareId ware, float side)
    {
        var icon = ItemFacts.Icon(ware.ItemId);

        if (icon != 0)
        {
            var texture = Plugin.TextureProvider
                .GetFromGameIcon(new GameIconLookup(icon, ware.Quality == Quality.High))
                .GetWrapOrDefault();

            if (texture is not null)
            {
                ImGui.Image(texture.Handle, new Vector2(side, side));
                return;
            }
        }

        ImGui.Dummy(new Vector2(side, side));
    }

    /// <summary>
    /// Draws the icon at one line's height and leaves the cursor beside it, for a name that follows.
    ///
    /// Sized to the text rather than to a constant so the two sit on one line at any font scale,
    /// and spaced by the style's inner spacing - the gap ImGui uses between parts of one control
    /// rather than between two separate ones, because an icon and the name it illustrates are one
    /// thing.
    /// </summary>
    /// <param name="ware">The Ware.</param>
    internal static void Inline(WareId ware)
    {
        Draw(ware, ImGui.GetTextLineHeight());
        ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
    }
}

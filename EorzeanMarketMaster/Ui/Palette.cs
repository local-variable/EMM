using System.Numerics;

namespace EorzeanMarketMaster.Ui;

/// <summary>
/// EMM's accent colours, taken from the approved plugin icon so the UI and the icon agree.
/// The literal RGB values are the constants in assets/icon/generate_icon.py — gold on deep navy,
/// with the icon's green reserved for a price series.
/// </summary>
internal static class Palette
{
    private static Vector4 Rgb(int r, int g, int b, float a = 1f)
        => new(r / 255f, g / 255f, b / 255f, a);

    public static readonly Vector4 NavyTop = Rgb(14, 26, 43);
    public static readonly Vector4 NavyBottom = Rgb(24, 42, 66);
    public static readonly Vector4 Gold = Rgb(232, 180, 84);
    public static readonly Vector4 GoldHighlight = Rgb(247, 219, 154);
    public static readonly Vector4 GoldShadow = Rgb(176, 122, 40);
    public static readonly Vector4 Green = Rgb(52, 160, 82);

    /// <summary>Rail entry that is not the active one.</summary>
    public static readonly Vector4 RailIdle = Rgb(150, 165, 186);

    /// <summary>Backing plate behind the icon rail.</summary>
    public static readonly Vector4 RailBackground = Rgb(14, 26, 43, 0.55f);

    /// <summary>Selected rail entry's backing plate.</summary>
    public static readonly Vector4 RailActiveBackground = Rgb(232, 180, 84, 0.16f);

    /// <summary>Hovered rail entry, kept dimmer than the selected plate so the two read apart.</summary>
    public static readonly Vector4 RailHoverBackground = Rgb(232, 180, 84, 0.08f);

    public static readonly Vector4 Muted = Rgb(150, 165, 186);

    /// <summary>
    /// The Normal Quality Ware, on any graph.
    ///
    /// <b>Fixed to the entity and never to the position of a series.</b> A Ware is an Item at a
    /// Quality, so NQ is blue wherever it appears - whether it is drawn alone, drawn beside HQ, or
    /// the only one the Item has. A palette that coloured "the first series" would make the same
    /// Ware change colour when the other was toggled, which is the one thing a reader comparing an
    /// HQ premium cannot afford.
    ///
    /// This value and its HQ counterpart were checked as a pair for colour-vision separation in
    /// the graph prototype, in both light and dark, along with every other mark on the picture. A
    /// first choice for the Player's own acts failed that check against this blue, which is why
    /// the check exists rather than being assumed.
    /// </summary>
    public static readonly Vector4 QualityNormal = Rgb(57, 135, 229);

    /// <summary>The High Quality Ware, on any graph. Fixed to the entity - see <see cref="QualityNormal"/>.</summary>
    public static readonly Vector4 QualityHigh = Rgb(217, 89, 38);

    /// <summary>
    /// A Listing line, in the Quality's colour but dimmed.
    ///
    /// Dimmed rather than recoloured, because a Listing belongs to the same Ware as the Sales
    /// beside it and a third hue would read as a third entity. It is the mark that differs - a
    /// line against a dot - and the weight says which is the observation and which is the hope.
    /// </summary>
    /// <param name="quality">The Ware's Quality.</param>
    /// <returns>The colour to draw its Listings in.</returns>
    public static Vector4 Listing(Vector4 quality) => quality with { W = 0.45f };
}

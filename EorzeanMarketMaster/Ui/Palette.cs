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
}

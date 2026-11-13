using System.Globalization;

namespace Cuedeck.Common;

/// <summary>
/// A colour as sLight's shows hold one (Godot's <c>Color</c>): red, green, blue and alpha from 0 to 1, in sRGB as
/// picked. Not the UI's linear <c>float4</c>; see <see cref="UI.Colors"/> for those.
/// </summary>
readonly record struct Rgb(float R, float G, float B, float A = 1)
{
    public static readonly Rgb White = new(1, 1, 1);
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb Transparent = new(0, 0, 0, 0);

    public static Rgb operator +(Rgb a, Rgb b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);

    public static Rgb operator *(Rgb a, Rgb b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

    /// <summary>
    /// Godot's <c>Color.from_hsv()</c>.
    /// </summary>
    public static Rgb FromHsv(double h, double s, double v, double alpha = 1)
    {
        if (s == 0)
            return new Rgb((float)v, (float)v, (float)v, (float)alpha);
        h *= 6.0;
        h %= 6;
        var i = (int)Math.Floor(h);
        var f = h - i;
        var p = v * (1 - s);
        var q = v * (1 - s * f);
        var t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return new Rgb((float)r, (float)g, (float)b, (float)alpha);
    }

    /// <summary>
    /// The hue, 0 to 1 round the colour wheel from red (Godot's <c>Color.h</c>); 0 for a grey.
    /// </summary>
    public float Hue
    {
        get
        {
            var max = Math.Max(R, Math.Max(G, B));
            var min = Math.Min(R, Math.Min(G, B));
            var delta = max - min;
            if (delta == 0)
                return 0;
            float h;
            if (R == max)
                h = (G - B) / delta;
            else if (G == max)
                h = 2 + (B - R) / delta;
            else
                h = 4 + (R - G) / delta;
            h /= 6;
            return h < 0 ? h + 1 : h;
        }
    }

    /// <summary>
    /// Godot's <c>to_html(false)</c>: "rrggbb", lower case, without a hash.
    /// </summary>
    public string ToHtml() => $"{Channel(R):x2}{Channel(G):x2}{Channel(B):x2}";

    static int Channel(float c) => Math.Clamp(Gd.RoundToInt(c * 255.0), 0, 255);

    /// <summary>
    /// Godot's <c>Color.from_string()</c>: a hex code ("#rgb", "rgb", "#rrggbb", "rrggbbaa", with or without the hash)
    /// or a colour's name ("red", "Dark Orange", "web_gray"), else <paramref name="fallback"/>.
    /// </summary>
    public static Rgb FromString(string text, Rgb fallback) => TryParse(text, out var c) ? c : fallback;

    public static bool TryParse(string text, out Rgb colour)
    {
        colour = default;
        if (string.IsNullOrEmpty(text))
            return false;
        if (TryHex(text, out colour))
            return true;
        return NamedColours.TryGet(text, out colour);
    }

    static bool TryHex(string text, out Rgb colour)
    {
        colour = default;
        var hex = text.StartsWith('#') ? text[1..] : text;
        if (hex.Length is not (3 or 4 or 6 or 8) || !hex.All(char.IsAsciiHexDigit))
            return false;
        int Digit(int i) => int.Parse(hex.AsSpan(i, 1), NumberStyles.HexNumber);
        int Pair(int i) => int.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber);
        if (hex.Length <= 4)
        {
            float Short(int i) => Digit(i) * 17 / 255f;
            colour = new Rgb(Short(0), Short(1), Short(2), hex.Length == 4 ? Short(3) : 1);
        }
        else
        {
            colour = new Rgb(Pair(0) / 255f, Pair(2) / 255f, Pair(4) / 255f, hex.Length == 8 ? Pair(6) / 255f : 1);
        }
        return true;
    }
}

/// <summary>
/// The colour names Godot knows (its X11-based list, with web_ variants for where the web's differ), for
/// <see cref="Rgb.FromString"/>: so a colour wheel slot labelled "Deep Red" or "Lavender" gets the colour sLight gave it.
/// </summary>
static class NamedColours
{
    /// <summary>
    /// Looks up <paramref name="name"/>, ignoring case, spaces, hyphens, underscores, apostrophes and dots, as Godot
    /// does.
    /// </summary>
    public static bool TryGet(string name, out Rgb colour)
    {
        var key = new string(name.Where(c => c is not (' ' or '-' or '_' or '\'' or '.')).ToArray()).ToUpperInvariant();
        if (Table.TryGetValue(key, out var rgb))
        {
            colour = key == "TRANSPARENT" ? Rgb.Transparent : new Rgb(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
            return true;
        }
        colour = default;
        return false;
    }

    static readonly Dictionary<string, uint> Table = new()
    {
        ["ALICEBLUE"] = 0xF0F8FF, ["ANTIQUEWHITE"] = 0xFAEBD7, ["AQUA"] = 0x00FFFF, ["AQUAMARINE"] = 0x7FFFD4,
        ["AZURE"] = 0xF0FFFF, ["BEIGE"] = 0xF5F5DC, ["BISQUE"] = 0xFFE4C4, ["BLACK"] = 0x000000,
        ["BLANCHEDALMOND"] = 0xFFEBCD, ["BLUE"] = 0x0000FF, ["BLUEVIOLET"] = 0x8A2BE2, ["BROWN"] = 0xA52A2A,
        ["BURLYWOOD"] = 0xDEB887, ["CADETBLUE"] = 0x5F9EA0, ["CHARTREUSE"] = 0x7FFF00, ["CHOCOLATE"] = 0xD2691E,
        ["CORAL"] = 0xFF7F50, ["CORNFLOWERBLUE"] = 0x6495ED, ["CORNSILK"] = 0xFFF8DC, ["CRIMSON"] = 0xDC143C,
        ["CYAN"] = 0x00FFFF, ["DARKBLUE"] = 0x00008B, ["DARKCYAN"] = 0x008B8B, ["DARKGOLDENROD"] = 0xB8860B,
        ["DARKGRAY"] = 0xA9A9A9, ["DARKGREEN"] = 0x006400, ["DARKKHAKI"] = 0xBDB76B, ["DARKMAGENTA"] = 0x8B008B,
        ["DARKOLIVEGREEN"] = 0x556B2F, ["DARKORANGE"] = 0xFF8C00, ["DARKORCHID"] = 0x9932CC, ["DARKRED"] = 0x8B0000,
        ["DARKSALMON"] = 0xE9967A, ["DARKSEAGREEN"] = 0x8FBC8F, ["DARKSLATEBLUE"] = 0x483D8B, ["DARKSLATEGRAY"] = 0x2F4F4F,
        ["DARKTURQUOISE"] = 0x00CED1, ["DARKVIOLET"] = 0x9400D3, ["DEEPPINK"] = 0xFF1493, ["DEEPSKYBLUE"] = 0x00BFFF,
        ["DIMGRAY"] = 0x696969, ["DODGERBLUE"] = 0x1E90FF, ["FIREBRICK"] = 0xB22222, ["FLORALWHITE"] = 0xFFFAF0,
        ["FORESTGREEN"] = 0x228B22, ["FUCHSIA"] = 0xFF00FF, ["GAINSBORO"] = 0xDCDCDC, ["GHOSTWHITE"] = 0xF8F8FF,
        ["GOLD"] = 0xFFD700, ["GOLDENROD"] = 0xDAA520, ["GRAY"] = 0xBEBEBE, ["GREEN"] = 0x00FF00,
        ["GREENYELLOW"] = 0xADFF2F, ["HONEYDEW"] = 0xF0FFF0, ["HOTPINK"] = 0xFF69B4, ["INDIANRED"] = 0xCD5C5C,
        ["INDIGO"] = 0x4B0082, ["IVORY"] = 0xFFFFF0, ["KHAKI"] = 0xF0E68C, ["LAVENDER"] = 0xE6E6FA,
        ["LAVENDERBLUSH"] = 0xFFF0F5, ["LAWNGREEN"] = 0x7CFC00, ["LEMONCHIFFON"] = 0xFFFACD, ["LIGHTBLUE"] = 0xADD8E6,
        ["LIGHTCORAL"] = 0xF08080, ["LIGHTCYAN"] = 0xE0FFFF, ["LIGHTGOLDENROD"] = 0xFAFAD2, ["LIGHTGRAY"] = 0xD3D3D3,
        ["LIGHTGREEN"] = 0x90EE90, ["LIGHTPINK"] = 0xFFB6C1, ["LIGHTSALMON"] = 0xFFA07A, ["LIGHTSEAGREEN"] = 0x20B2AA,
        ["LIGHTSKYBLUE"] = 0x87CEFA, ["LIGHTSLATEGRAY"] = 0x778899, ["LIGHTSTEELBLUE"] = 0xB0C4DE, ["LIGHTYELLOW"] = 0xFFFFE0,
        ["LIME"] = 0x00FF00, ["LIMEGREEN"] = 0x32CD32, ["LINEN"] = 0xFAF0E6, ["MAGENTA"] = 0xFF00FF,
        ["MAROON"] = 0xB03060, ["MEDIUMAQUAMARINE"] = 0x66CDAA, ["MEDIUMBLUE"] = 0x0000CD, ["MEDIUMORCHID"] = 0xBA55D3,
        ["MEDIUMPURPLE"] = 0x9370DB, ["MEDIUMSEAGREEN"] = 0x3CB371, ["MEDIUMSLATEBLUE"] = 0x7B68EE, ["MEDIUMSPRINGGREEN"] = 0x00FA9A,
        ["MEDIUMTURQUOISE"] = 0x48D1CC, ["MEDIUMVIOLETRED"] = 0xC71585, ["MIDNIGHTBLUE"] = 0x191970, ["MINTCREAM"] = 0xF5FFFA,
        ["MISTYROSE"] = 0xFFE4E1, ["MOCCASIN"] = 0xFFE4B5, ["NAVAJOWHITE"] = 0xFFDEAD, ["NAVYBLUE"] = 0x000080,
        ["OLDLACE"] = 0xFDF5E6, ["OLIVE"] = 0x808000, ["OLIVEDRAB"] = 0x6B8E23, ["ORANGE"] = 0xFFA500,
        ["ORANGERED"] = 0xFF4500, ["ORCHID"] = 0xDA70D6, ["PALEGOLDENROD"] = 0xEEE8AA, ["PALEGREEN"] = 0x98FB98,
        ["PALETURQUOISE"] = 0xAFEEEE, ["PALEVIOLETRED"] = 0xDB7093, ["PAPAYAWHIP"] = 0xFFEFD5, ["PEACHPUFF"] = 0xFFDAB9,
        ["PERU"] = 0xCD853F, ["PINK"] = 0xFFC0CB, ["PLUM"] = 0xDDA0DD, ["POWDERBLUE"] = 0xB0E0E6,
        ["PURPLE"] = 0xA020F0, ["REBECCAPURPLE"] = 0x663399, ["RED"] = 0xFF0000, ["ROSYBROWN"] = 0xBC8F8F,
        ["ROYALBLUE"] = 0x4169E1, ["SADDLEBROWN"] = 0x8B4513, ["SALMON"] = 0xFA8072, ["SANDYBROWN"] = 0xF4A460,
        ["SEAGREEN"] = 0x2E8B57, ["SEASHELL"] = 0xFFF5EE, ["SIENNA"] = 0xA0522D, ["SILVER"] = 0xC0C0C0,
        ["SKYBLUE"] = 0x87CEEB, ["SLATEBLUE"] = 0x6A5ACD, ["SLATEGRAY"] = 0x708090, ["SNOW"] = 0xFFFAFA,
        ["SPRINGGREEN"] = 0x00FF7F, ["STEELBLUE"] = 0x4682B4, ["TAN"] = 0xD2B48C, ["TEAL"] = 0x008080,
        ["THISTLE"] = 0xD8BFD8, ["TOMATO"] = 0xFF6347, ["TRANSPARENT"] = 0xFFFFFF, ["TURQUOISE"] = 0x40E0D0,
        ["VIOLET"] = 0xEE82EE, ["WEBGRAY"] = 0x808080, ["WEBGREEN"] = 0x008000, ["WEBMAROON"] = 0x800000,
        ["WEBPURPLE"] = 0x800080, ["WHEAT"] = 0xF5DEB3, ["WHITE"] = 0xFFFFFF, ["WHITESMOKE"] = 0xF5F5F5,
        ["YELLOW"] = 0xFFFF00, ["YELLOWGREEN"] = 0x9ACD32,
    };
}

namespace Cuedeck.Common;

/// <summary>
/// Arithmetic that behaves as sLight's GDScript did, so ported code lands on the same DMX values: Godot rounds halves
/// away from zero (C# rounds them to even by default), and <c>int(x)</c> truncates towards zero.
/// </summary>
static class Gd
{
    /// <summary>
    /// Godot's <c>round()</c>: to the nearest whole number, halves away from zero.
    /// </summary>
    public static double Round(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Godot's <c>int(round(x))</c>.
    /// </summary>
    public static int RoundToInt(double x) => (int)Math.Round(x, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Godot's <c>clampi(int(round(x)), 0, 255)</c>: a DMX value.
    /// </summary>
    public static int Byte(double x) => Math.Clamp(RoundToInt(x), 0, 255);

    public static double Lerp(double from, double to, double weight) => from + (to - from) * weight;

    /// <summary>
    /// Godot's <c>fmod()</c>: the remainder with the sign of <paramref name="x"/>, as C#'s <c>%</c> on doubles.
    /// </summary>
    public static double Fmod(double x, double y) => x % y;

    /// <summary>
    /// Godot's <c>snappedf()</c>: <paramref name="x"/> to the nearest multiple of <paramref name="step"/>.
    /// </summary>
    public static double Snapped(double x, double step) => step == 0 ? x : Math.Floor(x / step + 0.5) * step;

    /// <summary>
    /// Godot's <c>int(s)</c> on a string (<c>String.to_int()</c>): its digits read as a whole number, ignoring anything
    /// else, up to a decimal point; a minus before the first non-zero digit makes it negative. "10/1" reads as 101, so
    /// split first.
    /// </summary>
    public static int ToInt(string s)
    {
        long value = 0;
        var sign = 1;
        foreach (var c in s)
        {
            if (c is >= '0' and <= '9')
            {
                value = value * 10 + (c - '0');
                if (value > int.MaxValue)
                    return sign > 0 ? int.MaxValue : int.MinValue;
            }
            else if (c == '-' && value == 0)
                sign = -sign;
            else if (c == '.')
                break;
        }
        return (int)(sign * value);
    }

    /// <summary>
    /// Godot's <c>float(s)</c> on a string: the number it starts with (after any spaces), or 0.
    /// </summary>
    public static double ToFloat(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?");
        return m.Success && double.TryParse(m.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>
    /// Godot's <c>String.capitalize()</c>: camelCase and snake_case split into words, each with a capital first
    /// letter and the rest lower case ("american-dj" → "American-dj", "x4bar" → "X 4 Bar").
    /// </summary>
    public static string Capitalize(string s)
    {
        // _camelcase_to_underscore()
        var split = new System.Text.StringBuilder();
        var start = 0;
        for (var i = 1; i < s.Length; i++)
        {
            char prev = s[i - 1], cur = s[i];
            var nextLower = i + 1 < s.Length && char.IsLower(s[i + 1]);
            var a = char.IsLower(prev) && char.IsUpper(cur);
            var b = (char.IsUpper(prev) || char.IsAsciiDigit(prev)) && char.IsUpper(cur) && nextLower;
            var c = char.IsAsciiDigit(prev) && char.IsLower(cur) && nextLower;
            var d = (char.IsUpper(prev) || char.IsLower(prev)) && char.IsAsciiDigit(cur);
            if (a || b || c || d)
            {
                split.Append(s, start, i - start).Append('_');
                start = i;
            }
        }
        split.Append(s, start, s.Length - start);
        var words = split.ToString().ToLowerInvariant().Replace('_', ' ').Trim().Split(' ');
        return string.Join(" ", words.Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>
    /// Godot's <c>naturalnocasecmp_to()</c>, roughly: case ignored, and runs of digits compared as numbers, so
    /// "Par 9" sorts before "Par 10".
    /// </summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                    i++;
                while (j < b.Length && char.IsAsciiDigit(b[j]))
                    j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length)
                    return na.Length.CompareTo(nb.Length);
                var cmp = string.CompareOrdinal(na, nb);
                if (cmp != 0)
                    return cmp;
                continue;
            }
            var ca = char.ToLowerInvariant(a[i]);
            var cb = char.ToLowerInvariant(b[j]);
            if (ca != cb)
                return ca.CompareTo(cb);
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>
    /// Godot's <c>is_equal_approx()</c> for doubles.
    /// </summary>
    public static bool ApproxEqual(double a, double b)
    {
        if (a == b)
            return true;
        var tolerance = 0.00001 * Math.Abs(a);
        if (tolerance < 0.00001)
            tolerance = 0.00001;
        return Math.Abs(a - b) < tolerance;
    }
}

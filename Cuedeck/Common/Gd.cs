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

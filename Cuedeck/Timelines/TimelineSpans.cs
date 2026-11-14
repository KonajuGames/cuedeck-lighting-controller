using Cuedeck.Common;

namespace Cuedeck.Timelines;

/// <summary>
/// A timeline's rows, from the bottom up as they stack.
/// </summary>
enum TimelineRow
{
    Position,
    Strobe,
    Movement,
    Colour,
    Dimmer,
}

/// <summary>
/// What a timeline lights: every fixture (the master), one fixture, or one head of one.
/// </summary>
enum TimelineTarget
{
    Master,
    Fixture,
    Head,
}

/// <summary>
/// A strobe's speed. One saved without a speed is Medium.
/// </summary>
enum StrobeSpeed
{
    Slow,
    Medium,
    Fast,
}

/// <summary>
/// A colour row's span: <see cref="C0"/> at its start fading to <see cref="C1"/> at its end (the same for a steady
/// colour).
/// </summary>
sealed record ColourSpan(double T0, double T1, Rgb C0, Rgb C1)
{
    /// <summary>
    /// Its colour at <paramref name="t"/>, which it spans.
    /// </summary>
    public Rgb At(double t)
    {
        var span = T1 - T0;
        return Lerp(C0, C1, span <= 0 ? 0 : (t - T0) / span);
    }

    /// <summary>
    /// Godot's <c>Color.lerp()</c>: channel by channel, in sRGB, in 32-bit floats as Godot works it out.
    /// </summary>
    public static Rgb Lerp(Rgb a, Rgb b, double weight)
    {
        var w = (float)weight;
        return new(a.R + (b.R - a.R) * w, a.G + (b.G - a.G) * w, a.B + (b.B - a.B) * w, a.A + (b.A - a.A) * w);
    }

    /// <summary>
    /// Godot's <c>Color.is_equal_approx()</c>.
    /// </summary>
    public static bool ApproxEqual(Rgb a, Rgb b) => Gd.ApproxEqual(a.R, b.R) && Gd.ApproxEqual(a.G, b.G) && Gd.ApproxEqual(a.B, b.B) && Gd.ApproxEqual(a.A, b.A);
}

/// <summary>
/// A span of a row that holds one thing over its time (a strobe's speed, a position, a movement), which joins with
/// a touching span holding the same.
/// </summary>
abstract record TimelineSpan(double T0, double T1)
{
    public abstract TimelineSpan Over(double t0, double t1);

    /// <summary>
    /// Whether it holds the same as <paramref name="other"/>, whatever their times (so they may join).
    /// </summary>
    public bool SameButTimes(TimelineSpan other) => Over(0, 0).Equals(other.Over(0, 0));
}

/// <summary>
/// A strobe row's span: the strobe on at a speed.
/// </summary>
sealed record StrobeSpan(double T0, double T1, StrobeSpeed Speed = StrobeSpeed.Medium) : TimelineSpan(T0, T1)
{
    public override TimelineSpan Over(double t0, double t1) => this with { T0 = t0, T1 = t1 };
}

/// <summary>
/// A position row's span: a named position (by its id) held.
/// </summary>
sealed record PositionSpan(double T0, double T1, string Position = "") : TimelineSpan(T0, T1)
{
    public override TimelineSpan Over(double t0, double t1) => this with { T0 = t0, T1 = t1 };
}

/// <summary>
/// A movement row's span: a shape the pan and tilt trace around their position, a cycle every
/// <see cref="Period"/> seconds, <see cref="Size"/> of their range, starting <see cref="Phase"/> of a cycle in
/// (fixtures fanned out across one movement).
/// </summary>
sealed record MovementSpan(double T0, double T1, string Effect = "none", double Period = MovementSpan.PERIOD, double Size = MovementSpan.SIZE, double Phase = 0)
    : TimelineSpan(T0, T1)
{
    /// <summary>
    /// A new movement's size (of the pan and tilt range) and, without a beat grid, its cycle.
    /// </summary>
    public const double SIZE = 0.04;
    public const double PERIOD = 4.0;

    /// <summary>
    /// The largest movement (the timeline's Large), what the row draws its shapes at full size for.
    /// </summary>
    public const double SIZE_MAX = 0.08;

    /// <summary>
    /// The effects: none (hold still: beating a master movement) and the shapes.
    /// </summary>
    public static readonly string[] Effects = ["none", "circle", "figure8", "pan_sweep", "tilt_sweep"];

    public static readonly Dictionary<string, string> EffectNames = new()
    {
        ["none"] = "None",
        ["circle"] = "Circle",
        ["figure8"] = "Figure 8",
        ["pan_sweep"] = "Pan Sweep",
        ["tilt_sweep"] = "Tilt Sweep",
    };

    public override TimelineSpan Over(double t0, double t1) => this with { T0 = t0, T1 = t1 };

    /// <summary>
    /// Where it puts the pan and tilt, from their centre, <paramref name="dt"/> seconds into it: the shape's point at
    /// that part of its cycle (plus its own phase), times its size (pan right, tilt up). Nothing for none.
    /// </summary>
    public (double Pan, double Tilt) Offset(double dt)
    {
        var period = Math.Max(Period, 0.05);
        var a = Math.Tau * Gd.FPosMod(dt / period + Phase, 1);
        return Effect switch
        {
            "circle" => (Math.Cos(a) * Size, Math.Sin(a) * Size),
            "figure8" => (Math.Sin(a) * Size, Math.Sin(2 * a) * 0.5 * Size),
            "pan_sweep" => (Math.Sin(a) * Size, 0),
            "tilt_sweep" => (0, Math.Sin(a) * Size),
            _ => (0, 0),
        };
    }
}

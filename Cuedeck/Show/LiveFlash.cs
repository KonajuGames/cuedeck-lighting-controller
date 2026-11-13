using System.Diagnostics;
using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Fixtures;

namespace Cuedeck.Show;

enum FlashKind
{
    Blackout,
    Strobe,
    White,
    Colour,
}

/// <summary>
/// How fast a strobe goes, as song timelines and the live flashes ask for one.
/// </summary>
enum StrobeSpeed
{
    Slow,
    Medium,
    Fast,
}

/// <summary>
/// The live flashes: buttons that go over everything else in the frame (the console, song timelines, chases and
/// effects) while held, or latched until pressed again. For a drop the timeline didn't see coming.
/// <list type="bullet">
/// <item>Blackout: every fixture's intensity to 0 (dimmer and colour channels; pan, tilt and the rest stay, so movers
/// don't swing).</item>
/// <item>Strobe: a STROBE channel set to strobe fast, or on a fixture without one, its intensity flashed in
/// software.</item>
/// <item>White: dimmer, red, green, blue and white full, amber and UV off; a colour wheel to its whitest slot.</item>
/// <item>Colour: full in <see cref="Colour"/>; a colour wheel to its nearest slot.</item>
/// </list>
/// Strobe goes with White or Colour (a white or coloured strobe); on its own it strobes the colours already playing.
/// White beats Colour, and Blackout beats everything. Under White or Colour without Strobe, a STROBE channel is held
/// open, so the flash doesn't inherit a timeline's strobing. <see cref="Layer"/> is a top layer: after the virtual
/// dimmers, so a fixture dimmed that way still flashes, and before the grand master.
/// </summary>
sealed class LiveFlash
{
    public static readonly string[] Names = ["Blackout", "Strobe", "White", "Colour"];

    public const double SoftStrobeHz = 10;

    /// <summary>
    /// The part of each software strobe cycle that's lit.
    /// </summary>
    public const double SoftStrobeDuty = 0.34;

    /// <summary>
    /// Where in a strobing range each speed sits.
    /// </summary>
    static readonly double[] StrobeSpeedFraction = [0.2, 0.55, 1.0];

    readonly HashSet<FlashKind> _on = [];
    readonly List<FlashKind> _order = [];

    /// <summary>
    /// The Colour flash's colour.
    /// </summary>
    public Rgb Colour { get; set; } = new(1, 0, 0);

    /// <summary>
    /// Buttons and keys toggle the flashes instead of holding them.
    /// </summary>
    public bool Latch { get; set; }

    /// <summary>
    /// The fixtures it flashes.
    /// </summary>
    public Func<IEnumerable<PatchedFixture>>? Fixtures { get; set; }

    /// <summary>
    /// The seconds the software strobe runs by.
    /// </summary>
    public Func<double> Now { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>
    /// A flash went on or off (buttons and feedback pads follow).
    /// </summary>
    public event Action? Changed;

    public bool IsOn(FlashKind kind) => _on.Contains(kind);

    public bool AnyOn => _on.Count > 0;

    /// <summary>
    /// A button, key or pad went down: the flash goes on (latched, it toggles).
    /// </summary>
    public void Press(FlashKind kind)
    {
        if (Latch)
            Toggle(kind);
        else
            SetOn(kind, true);
    }

    /// <summary>
    /// ...and came back up: off again (latched, nothing).
    /// </summary>
    public void Release(FlashKind kind)
    {
        if (!Latch)
            SetOn(kind, false);
    }

    public void Toggle(FlashKind kind) => SetOn(kind, !IsOn(kind));

    public void SetOn(FlashKind kind, bool on)
    {
        if (kind < FlashKind.Blackout || kind > FlashKind.Colour || on == IsOn(kind))
            return;
        if (on)
        {
            _on.Add(kind);
            _order.Add(kind);
        }
        else
        {
            _on.Remove(kind);
            _order.Remove(kind);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_on.Count == 0)
            return;
        _on.Clear();
        _order.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// The top layer: <paramref name="n"/> {channel: value} maps.
    /// </summary>
    public IReadOnlyList<Dictionary<int, int>> Layer(int n)
    {
        if (_on.Count == 0 || Fixtures is null)
            return [.. Enumerable.Range(0, n).Select(_ => new Dictionary<int, int>())];
        return Output(Fixtures(), n, _order, Colour, Now());
    }

    /// <summary>
    /// The flashes in <paramref name="kinds"/> over <paramref name="fixtures"/>, as <paramref name="n"/>
    /// {channel: value} maps, at <paramref name="t"/> seconds (for the software strobe).
    /// </summary>
    public static List<Dictionary<int, int>> Output(IEnumerable<PatchedFixture> fixtures, int n, IReadOnlyCollection<FlashKind> kinds, Rgb flashColour, double t)
    {
        var output = Enumerable.Range(0, n).Select(_ => new Dictionary<int, int>()).ToList();
        if (kinds.Count == 0)
            return output;
        var blackout = kinds.Contains(FlashKind.Blackout);
        var strobe = kinds.Contains(FlashKind.Strobe);
        var white = kinds.Contains(FlashKind.White);
        Rgb? tint = white ? Rgb.White : kinds.Contains(FlashKind.Colour) ? flashColour : null;
        var softLit = Gd.Fmod(t * SoftStrobeHz, 1.0) < SoftStrobeDuty;
        foreach (var fx in fixtures)
        {
            var u = fx.Universe;
            if (u < 0 || u >= n)
                continue;
            var chans = fx.Channels;
            var o = output[u];
            var hasRgb = fx.Profile.HeadGroups(fx.Mode).Count > 0;
            var hasStrobe = chans.Any(c => c.Role == Roles.Strobe);
            // Lit this frame: always, but in the software strobe's off part.
            var lit = !blackout && (!strobe || hasStrobe || softLit);
            for (var k = 0; k < chans.Count; k++)
            {
                var ch = chans[k];
                var c = fx.Start + k;
                switch (ch.Role)
                {
                    case Roles.Dimmer:
                        o[c] = lit ? 255 : 0;
                        break;
                    case Roles.Strobe:
                        if (blackout || ch.Fine)
                            continue;
                        if (strobe)
                            o[c] = StrobeValue(ch, StrobeSpeed.Fast);
                        else if (tint is not null)
                            o[c] = OpenValue(ch);
                        break;
                    case Roles.ColorWheel:
                        if (tint is { } wheelTint && lit && !hasRgb && !ch.Fine)
                        {
                            var v = FixtureProfile.NearestWheelValue(ch, wheelTint);
                            if (v >= 0)
                                o[c] = v;
                        }
                        break;
                    default:
                        if (!Roles.Colour.Contains(ch.Role))
                            continue;
                        if (!lit)
                            o[c] = 0;
                        else if (tint is { } colour)
                            o[c] = TintValue(ch.Role, colour, white);
                        // Strobe alone: the colours playing, unchanged.
                        break;
                }
            }
        }
        return output;
    }

    /// <summary>
    /// A colour or white channel's level for a White or Colour flash.
    /// </summary>
    static int TintValue(string role, Rgb tint, bool white) =>
        role switch
        {
            Roles.Red => Gd.RoundToInt(tint.R * 255.0),
            Roles.Green => Gd.RoundToInt(tint.G * 255.0),
            Roles.Blue => Gd.RoundToInt(tint.B * 255.0),
            Roles.White => white ? 255 : 0,
            _ => 0, // amber, UV
        };

    /// <summary>
    /// The value that holds a STROBE channel open (lit, not strobing): the start of a range labelled open, else of
    /// one that's neither strobing nor closed, else 0.
    /// </summary>
    public static int OpenValue(FixtureChannel ch)
    {
        foreach (var r in ch.Ranges)
            if (r.Label.ToLowerInvariant().Contains("open"))
                return r.Lo;
        foreach (var r in ch.Ranges)
        {
            var label = r.Label.ToLowerInvariant();
            if (!(label.Contains("strob") || label.Contains("puls") || label.Contains("closed") || label.Contains("black") || label.Contains("random")))
                return r.Lo;
        }
        return 0;
    }

    /// <summary>
    /// The value that strobes a STROBE channel at <paramref name="speed"/>: that far through its strobing range (the
    /// first labelled strobe or pulse), else through 20 to 240.
    /// </summary>
    public static int StrobeValue(FixtureChannel ch, StrobeSpeed speed = StrobeSpeed.Fast)
    {
        int lo = 20, hi = 240;
        foreach (var r in ch.Ranges)
        {
            var label = r.Label.ToLowerInvariant();
            if (label.Contains("strob") || label.Contains("puls"))
            {
                (lo, hi) = (r.Lo, r.Hi);
                break;
            }
        }
        return Gd.RoundToInt(Gd.Lerp(lo, hi, StrobeSpeedFraction[Math.Clamp((int)speed, 0, 2)]));
    }

    public JsonObject ToJson() => new() { ["colour"] = Colour.ToHtml(), ["latch"] = Latch };

    public void FromJson(JsonNode? d)
    {
        Colour = Rgb.FromString(d.Str("colour", "ff0000"), new Rgb(1, 0, 0));
        Latch = d.Bool("latch", false);
        Clear();
    }
}

using System.Numerics;
using Cuedeck.Common;

namespace Cuedeck.Fixtures;

/// <summary>
/// One head's light, as the 3D view shows it.
/// </summary>
/// <param name="Level">0 to 1, with the fixture's dimmer and shutter applied.</param>
sealed record HeadState(Rgb Color, float Level, Vector3 Offset);

/// <summary>
/// What a fixture looks like from its live DMX, for the 3D view: where it points, its beam, and each head's colour
/// and level.
/// </summary>
/// <param name="Dimmer">Head 0's level, the single-head shortcut.</param>
/// <param name="Pan">Degrees either side of centre.</param>
/// <param name="Tilt">Degrees either side of centre.</param>
/// <param name="StrobeHz">0 when it isn't strobing.</param>
/// <param name="Gobo">The gobo's base64 artwork, or "" for none (or none known).</param>
/// <param name="GoboRotation">Degrees a second.</param>
/// <param name="Zoom">The beam's angle in degrees.</param>
/// <param name="Blackout">The shutter's closed.</param>
/// <param name="Color">Head 0's colour, the single-head shortcut.</param>
sealed record FixtureState(
    float Dimmer,
    float Pan,
    float Tilt,
    float StrobeHz,
    string Gobo,
    float GoboRotation,
    float Zoom,
    bool Blackout,
    Rgb Color,
    IReadOnlyList<HeadState> Heads
);

/// <summary>
/// Turns a fixture's slice of a universe's composited output into a <see cref="FixtureState"/>. Pure and stateless:
/// call it every frame for each fixture.
/// </summary>
static class DmxRender
{
    static int Read(ReadOnlySpan<byte> buffer, int i) => i >= 0 && i < buffer.Length ? buffer[i] : 0;

    /// <summary>
    /// The colour of the slot <paramref name="raw"/> falls into on <paramref name="channel"/>, or null.
    /// </summary>
    public static Rgb? SlotColour(FixtureChannel channel, int raw) => channel.SlotAt(raw) is { } slot ? FixtureProfile.SlotColour(slot) : null;

    static float StrobeHz(FixtureChannel channel, int raw)
    {
        if (channel.Ranges.Count > 0)
        {
            if (channel.SlotAt(raw) is not { } slot)
                return 0;
            var label = slot.Label.ToLowerInvariant();
            if (label.Contains("strob") || label.Contains("pulse") || label.Contains("-fast") || label.Contains("slow"))
            {
                var lo = slot.Lo;
                var hi = Math.Max(slot.Hi, lo + 1);
                return (float)Gd.Lerp(1, 25, (raw - lo) / (double)(hi - lo));
            }
            return 0;
        }
        if (raw is >= 20 and <= 240)
            return (float)Gd.Lerp(1, 22, (raw - 20) / 220.0);
        return 0;
    }

    static bool ShutterClosed(FixtureChannel channel, int raw)
    {
        if (channel.SlotAt(raw) is not { } slot)
            return false;
        var label = slot.Label.ToLowerInvariant();
        return label.Contains("closed") || label.Contains("black");
    }

    /// <summary>
    /// The state of a fixture patched with <paramref name="profile"/> in <paramref name="mode"/> at
    /// <paramref name="start"/> (0-based) in <paramref name="output"/>, a universe's composited output.
    /// </summary>
    public static FixtureState Evaluate(FixtureProfile profile, int mode, ReadOnlySpan<byte> output, int start)
    {
        var chans = profile.Channels(mode);
        var physical = profile.Physical;
        var beamDeg = physical.BeamDeg;

        float pan = 0, tilt = 0, strobeHz = 0, goboRotation = 0;
        var zoom = (float)beamDeg;
        var gobo = "";
        var blackout = false;

        // The fixture-wide channels.
        var dim = -1.0;
        Rgb? wheelHue = null;
        var hasWheel = false;
        for (var ci = 0; ci < chans.Count; ci++)
        {
            var ch = chans[ci];
            // A fine channel is its coarse channel's low byte, read with it: never a channel of its own, whatever role
            // it carries.
            if (ch.Fine)
                continue;
            var raw = Read(output, start + ci);
            var hasFine = ci + 1 < chans.Count && chans[ci + 1].Fine;
            var wide = hasFine ? raw * 256 + Read(output, start + ci + 1) : raw;
            var full = hasFine ? 65535.0 : 255.0;
            switch (ch.Role)
            {
                case Roles.Dimmer:
                    dim = wide / full;
                    break;
                case Roles.Pan:
                    pan = (float)((wide / full - 0.5) * physical.PanRange);
                    break;
                case Roles.Tilt:
                    tilt = (float)((wide / full - 0.5) * physical.TiltRange);
                    break;
                case Roles.Zoom:
                    zoom = (float)Gd.Lerp(Math.Max(beamDeg * 0.6, 3), Math.Min(beamDeg * 3, 70), raw / 255.0);
                    break;
                case Roles.Strobe:
                    strobeHz = StrobeHz(ch, raw);
                    if (ShutterClosed(ch, raw))
                        blackout = true;
                    break;
                case Roles.Gobo:
                    gobo = ch.SlotAt(raw)?.Image ?? "";
                    break;
                case Roles.GoboRotation:
                    if (Math.Abs(raw - 128) > 10)
                        goboRotation = (raw - 128f) / 127f * 240f;
                    break;
                case Roles.ColorWheel:
                    hasWheel = true;
                    if (SlotColour(ch, raw) is { } wc)
                        wheelHue = wc;
                    break;
            }
        }

        var master = dim >= 0 ? dim : 1.0;

        // Each head's colour and level.
        var heads = new List<HeadState>();
        foreach (var g in profile.HeadGroups(mode))
        {
            float r = Read(output, start + g.R) / 255f, gr = Read(output, start + g.G) / 255f, b = Read(output, start + g.B) / 255f;
            foreach (var ei in g.Extra)
            {
                var v = Read(output, start + ei) / 255f;
                switch (chans[ei].Role)
                {
                    case Roles.White:
                        (r, gr, b) = (r + v, gr + v * 0.95f, b + v * 0.85f);
                        break;
                    case Roles.Amber:
                        (r, gr) = (r + v, gr + v * 0.55f);
                        break;
                    case Roles.Uv:
                        (r, b) = (r + v * 0.45f, b + v);
                        break;
                }
            }
            var level = Math.Clamp(Math.Max(Math.Max(r, gr), b), 0f, 1f);
            var hue = level > 0.001f ? new Rgb(r / level, gr / level, b / level) : Rgb.Black;
            if (wheelHue is { } wheel)
                hue = level > 0.001f ? hue * wheel : wheel;
            heads.Add(new HeadState(hue, blackout ? 0 : (float)Math.Clamp(level * master, 0, 1), g.Offset));
        }

        if (heads.Count == 0)
        {
            // No RGB: one implicit head, lit by the dimmer or colour wheel. A wheel resting on an open slot resolves
            // no colour (it isn't a filter) but the fixture is still lit, so having the channel counts.
            var lit = dim >= 0 || hasWheel;
            heads.Add(new HeadState(wheelHue ?? Rgb.White, lit && !blackout ? (float)Math.Clamp(master, 0, 1) : 0, Vector3.Zero));
        }

        return new FixtureState(heads[0].Level, pan, tilt, strobeHz, gobo, goboRotation, zoom, blackout, heads[0].Color, heads);
    }
}

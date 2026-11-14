using System.Numerics;
using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.Perform;
using Cuedeck.Show;
using Cuedeck.Timelines;
using StrobeSpeed = Cuedeck.Timelines.StrobeSpeed;

namespace Cuedeck.Tests.Perform;

/// <summary>
/// A song's timelines into DMX values, and decks mixed (the first half of sLight's tests/dj_perform_test.gd).
/// </summary>
public sealed class SongOutputTests
{
    static FixtureProfile P(string id, params string[] roles) => Profiles.Simple(id, id, [.. roles.Select(r => (r, r))]);

    /// <summary>
    /// A par with a dimmer at 1, an RGB par without at 11, a two-head bar without at 21, a mover with pan / tilt and a
    /// dimmer at 41.
    /// </summary>
    internal static List<PerformFixture> Fixtures() =>
        [
            new("a", 0, 0, P("dpar", "DIMMER", "RED", "GREEN", "BLUE")),
            new("b", 0, 10, P("rgb", "RED", "GREEN", "BLUE")),
            new("c", 0, 20, P("bar", "RED", "GREEN", "BLUE", "RED", "GREEN", "BLUE")),
            new("d", 0, 40, P("mover", "PAN", "TILT", "DIMMER", "RED", "GREEN", "BLUE")),
        ];

    internal static List<byte[]> Bases()
    {
        var b = new byte[512];
        for (var i = 1; i < 4; i++)
            b[i] = 200; // par a: white
        (b[10], b[11], b[12]) = (200, 100, 50); // par b: orange
        for (var i = 20; i < 26; i++)
            b[i] = 100; // bar c: grey
        return [b];
    }

    /// <summary>
    /// A track with a dimmer line through these (seconds, level) points.
    /// </summary>
    internal static TimelineTrack Nodes(params (double T, double L)[] points) => new() { DimmerNodes = [.. points.Select(p => new Vector2((float)p.T, (float)p.L))] };

    static TimelineTrack Colour(double t0, double t1, Rgb c0, Rgb c1) => new() { ColourSegments = [new(t0, t1, c0, c1)] };

    static TimelineTrack Flags(TimelineTrack t, bool mute = false, bool solo = false)
    {
        t.Mute = mute;
        t.Solo = solo;
        return t;
    }

    static TimelineTrack Half() => Nodes((0, 0.5));

    static Dictionary<int, int> Out(Dictionary<string, TimelineTrack> tracks, double t, List<PerformFixture> fx, List<byte[]> bases, PositionLibrary? lib = null) =>
        SongOutput.Of(tracks, t, fx, bases, 1, lib)[0];

    static int? V(Dictionary<int, int> o, int ch) => o.TryGetValue(ch, out var v) ? v : null;

    static readonly Rgb Red = new(1, 0, 0);

    [Fact]
    public void A_songs_timelines_into_DMX_values()
    {
        var fx = Fixtures();
        var bases = Bases();
        var all = SongOutput.Of(new Dictionary<string, TimelineTrack>(), 5, fx, bases, 1);
        Assert.Single(all);
        Assert.Empty(all[0]); // no timelines: drives nothing

        // The master at 50 %.
        var o = Out(new() { ["master"] = Half() }, 5, fx, bases);
        Assert.Equal((128, 128), (V(o, 0), V(o, 42)));
        Assert.False(o.ContainsKey(1) || o.ContainsKey(43)); // colour left alone where there's a dimmer
        Assert.Equal((100, 50, 25), (V(o, 10), V(o, 11), V(o, 12))); // no dimmer channel: the virtual dimmer
        Assert.Equal((50, 50), (V(o, 20), V(o, 25))); // every head of a bar without one
        Assert.False(o.ContainsKey(40) || o.ContainsKey(41)); // pan / tilt untouched

        // A fixture's own line overrides the master's; the others keep the master's.
        o = Out(new() { ["master"] = Half(), ["a"] = Nodes((0, 0.2), (10, 1.0)) }, 5, fx, bases);
        Assert.Equal((Gd.RoundToInt(255 * 0.6), 128), (V(o, 0), V(o, 42)));
        o = Out(new() { ["master"] = Half(), ["a"] = Nodes((2, 0.2), (4, 0.2)) }, 5, fx, bases);
        Assert.Equal(128, V(o, 0)); // outside its first-to-last node: the master's again
        Assert.Empty(Out(new() { ["a"] = Nodes((3, 0.9)) }, 5, fx, bases)); // a single node: no stretch

        // Only a head of the bar.
        o = Out(new() { ["c/h1"] = Nodes((0, 0), (10, 0)) }, 5, fx, bases);
        Assert.Equal((0, 0), (V(o, 23), V(o, 25)));
        Assert.False(o.ContainsKey(20) || o.ContainsKey(10));

        // A head's line with a shared dimmer: the dimmer at the brightest head (the other's at the console's 80 %),
        // that head's colour scaled to 50 %.
        var bar2 = new PerformFixture("e", 0, 60, P("bar2", "DIMMER", "RED", "GREEN", "BLUE", "RED", "GREEN", "BLUE"));
        var b2 = Bases();
        for (var i = 61; i < 67; i++)
            b2[0][i] = 180;
        b2[0][60] = 204;
        o = Out(new() { ["e/h0"] = Nodes((0, 0.5), (10, 0.5)) }, 5, [bar2], b2);
        Assert.Equal((204, 113, 180), (V(o, 60), V(o, 61), V(o, 64)));
        // A head overrides its fixture, which overrides the master (head 2 full, head 1 at the fixture's 40 %).
        o = Out(new() { ["master"] = Nodes((0, 0.2)), ["e"] = Nodes((0, 0.4), (10, 0.4)), ["e/h1"] = Nodes((0, 1.0), (10, 1.0)) }, 5, [bar2], b2);
        Assert.Equal((255, 72, 180), (V(o, 60), V(o, 61), V(o, 64)));
        o = Out(new() { ["master"] = Half(), ["c/h0"] = Nodes((0, 1.0), (10, 1.0)) }, 5, fx, bases);
        Assert.Equal((100, 50), (V(o, 20), V(o, 23))); // no dimmer channel: a head's line over the master's

        // A fixture on a universe that isn't there: skipped.
        Assert.Empty(Out(new() { ["master"] = Nodes((0, 1.0)) }, 5, [new("z", 3, 0, P("dpar", "DIMMER"))], bases));

        // Colour.
        o = Out(new() { ["master"] = Colour(0, 10, Red, Red) }, 5, fx, bases);
        Assert.Equal((255, 0, 0), (V(o, 1), V(o, 2), V(o, 3)));
        Assert.False(o.ContainsKey(0)); // the dimmer channel left alone
        Assert.Equal((255, 0, 255, 255, 255), (V(o, 10), V(o, 11), V(o, 20), V(o, 23), V(o, 43)));
        Assert.Empty(Out(new() { ["master"] = Colour(0, 10, Red, Red) }, 12, fx, bases)); // outside the segment
        o = Out(new() { ["master"] = Colour(0, 10, new(0, 0, 0), new(0, 0, 1)) }, 5, fx, bases);
        Assert.Equal(128, V(o, 3)); // a gradient: half way at the middle
        o = Out(
            new()
            {
                ["master"] = Colour(0, 10, Red, Red),
                ["c"] = Colour(0, 10, new(0, 1, 0), new(0, 1, 0)),
                ["c/h1"] = Colour(0, 10, new(0, 0, 1), new(0, 0, 1)),
            },
            5,
            fx,
            bases
        );
        Assert.Equal((255, 255, 0, 255, 0), (V(o, 10), V(o, 21), V(o, 20), V(o, 25), V(o, 24))); // head over fixture over master
        o = Out(new() { ["b"] = Colour(0, 10, Rgb.White, Rgb.White), ["master"] = Half() }, 5, fx, bases);
        Assert.Equal((128, 128), (V(o, 10), V(o, 11))); // no dimmer channel: the timeline's colour, dimmed
        Assert.Equal(128, V(o, 0));
        Assert.Null(V(o, 1)); // with one: colour and dimmer separate

        // Strobe.
        var strober = new PerformFixture(
            "s",
            0,
            80,
            Profiles.Make(
                "st",
                "st",
                """
                [{"name": "Shutter", "role": "STROBE", "ranges": [{"lo": 0, "hi": 9, "label": "Closed"}, {"lo": 10, "hi": 19, "label": "Open"},
                    {"lo": 20, "hi": 240, "label": "Strobe slow-fast"}]}, {"name": "Dimmer", "role": "DIMMER"}]
                """
            )
        );
        var plain = new PerformFixture("q", 0, 90, P("plainstrobe", "STROBE", "DIMMER"));
        TimelineTrack Strobe(StrobeSpeed speed) => new() { StrobeSegments = [new(0, 10, speed)] };
        o = Out(new() { ["s"] = Strobe(StrobeSpeed.Fast), ["q"] = Strobe(StrobeSpeed.Fast) }, 5, [strober, plain], bases);
        Assert.Equal((240, 240), (V(o, 80), V(o, 90))); // the fast end of its strobe range, or of 20-240
        Assert.False(o.ContainsKey(81));
        Assert.Equal(64, V(Out(new() { ["s"] = Strobe(StrobeSpeed.Slow) }, 5, [strober], bases), 80));
        Assert.Equal(141, V(Out(new() { ["s"] = Strobe(StrobeSpeed.Medium) }, 5, [strober], bases), 80));
        Assert.Equal(141, V(Out(new() { ["s"] = Strobe(StrobeSpeed.Slow), ["master"] = Strobe(StrobeSpeed.Medium) }, 5, [strober], bases), 80)); // the faster
        var mixed = SongOutput.Mix(
            [SongOutput.Of(new Dictionary<string, TimelineTrack> { ["s"] = Strobe(StrobeSpeed.Slow) }, 5, [strober], bases, 1), SongOutput.Of(new Dictionary<string, TimelineTrack> { ["s"] = Strobe(StrobeSpeed.Fast) }, 5, [strober], bases, 1)],
            [[]],
            1
        );
        Assert.Equal(240, mixed[0][80]); // two decks strobing: the faster wins (HTP)
        Assert.Empty(Out(new() { ["s"] = Strobe(StrobeSpeed.Fast) }, 12, [strober], bases));
        o = Out(new() { ["master"] = Strobe(StrobeSpeed.Fast) }, 5, [strober, plain, .. fx], bases);
        Assert.Equal((240, 240), (V(o, 80), V(o, 90))); // the master's: every fixture with a strobe channel
        Assert.False(o.ContainsKey(0));
    }

    [Fact]
    public void Positions_and_movement()
    {
        var fx = Fixtures();
        var bases = Bases();
        var lib = new PositionLibrary();
        var left = lib.Add();
        var right = lib.Add();
        var spot16 = new PerformFixture(
            "h",
            0,
            100,
            Profiles.Make(
                "s16",
                "s16",
                """
                [{"name": "Pan", "role": "PAN"}, {"name": "Pan fine", "role": "PAN_FINE", "fine": true},
                 {"name": "Tilt", "role": "TILT"}, {"name": "Tilt fine", "role": "TILT_FINE", "fine": true}, {"name": "Dimmer", "role": "DIMMER"}]
                """
            )
        );
        lib.SetPoint(left.Id, "h", new Vector2(0.25f, 0.75f));
        lib.SetPoint(left.Id, "d", new Vector2(0.5f, 1.0f));
        lib.SetPoint(right.Id, "d", new Vector2(1.0f, 0.0f));
        TimelineTrack At(string id) => new() { PositionSegments = [new(0, 10, id)] };

        var o = Out(new() { ["master"] = At(left.Id) }, 5, [spot16, .. fx], bases, lib);
        Assert.Equal(Gd.RoundToInt(0.25 * 65535), V(o, 100) * 256 + V(o, 101)); // 16-bit across coarse and fine
        Assert.Equal(Gd.RoundToInt(0.75 * 65535), V(o, 102) * 256 + V(o, 103));
        Assert.Equal((128, 255), (V(o, 40), V(o, 41))); // an 8-bit mover: its point for that position
        Assert.False(o.ContainsKey(104) || o.ContainsKey(42)); // dimmers untouched
        o = Out(new() { ["master"] = At(left.Id), ["d"] = At(right.Id) }, 5, fx, bases, lib);
        Assert.Equal((255, 0), (V(o, 40), V(o, 41))); // its own row beats the master's
        Assert.Empty(Out(new() { ["master"] = At(left.Id) }, 12, fx, bases, lib));
        Assert.Empty(Out(new() { ["master"] = At(left.Id) }, 5, fx, bases, null)); // no positions: nothing
        Assert.Empty(Out(new() { ["master"] = At("deleted") }, 5, fx, bases, lib));

        // Left out of show generation: the master doesn't drive it, its own rows do.
        TimelineTrack Everything() =>
            new()
            {
                DimmerNodes = [new(0, 0.5f)],
                ColourSegments = [new(0, 10, Red, Red)],
                PositionSegments = [new(0, 10, left.Id)],
                StrobeSegments = [new(0, 10, StrobeSpeed.Fast)],
                MovementSegments = [new(0, 10, "circle", 2.0, 0.1)],
            };
        var plain = new PerformFixture("q", 0, 90, P("plainstrobe", "STROBE", "DIMMER"));
        var leftOut = fx.Append(plain).Select(f => f with { ExcludeGenerate = f.Uid is "d" or "q" }).ToList();
        o = Out(new() { ["master"] = Everything() }, 5, leftOut, bases, lib);
        Assert.Equal((128, 128), (V(o, 0), V(o, 10)));
        Assert.Null(V(o, 90));
        Assert.False(o.ContainsKey(91) || o.ContainsKey(40) || o.ContainsKey(41) || o.ContainsKey(42) || o.ContainsKey(43));
        o = Out(new() { ["master"] = Everything(), ["d"] = Nodes((0, 0.4), (10, 0.4)) }, 5, leftOut, bases, lib);
        Assert.Equal(102, V(o, 42)); // ...but its own rows still drive it
        Assert.False(o.ContainsKey(40) || o.ContainsKey(43));

        var avg = SongOutput.AverageChannels(fx, 1);
        var mixed = SongOutput.Mix([SongOutput.Of(new Dictionary<string, TimelineTrack> { ["master"] = At(left.Id) }, 5, fx, bases, 1, lib), SongOutput.Of(new Dictionary<string, TimelineTrack> { ["d"] = At(right.Id) }, 5, fx, bases, 1, lib)], avg, 1);
        Assert.Equal((192, 128), (mixed[0][40], mixed[0][41])); // two decks moving it: the average

        // Movement: around the position, or the console's pan / tilt.
        var circle = new List<MovementSpan> { new(0, 10, "circle", 2.0, 0.1) };
        TimelineTrack Both() => new() { PositionSegments = [new(0, 10, left.Id)], MovementSegments = [.. circle] };
        o = Out(new() { ["master"] = Both() }, 4, fx, bases, lib); // two cycles in, at angle 0: +size pan
        Assert.Equal((Gd.RoundToInt(0.6 * 255), 255), (V(o, 40), V(o, 41)));
        o = Out(new() { ["master"] = Both() }, 4.5, fx, bases, lib); // a quarter round: up (tilt clamped at the top)
        Assert.Equal((128, 255), (V(o, 40), V(o, 41)));
        o = Out(new() { ["master"] = Both() }, 5, fx, bases, lib); // half round
        Assert.Equal(Gd.RoundToInt(0.4 * 255), V(o, 40));
        var console = Bases();
        (console[0][40], console[0][41]) = (64, 128);
        o = Out(new() { ["master"] = new() { MovementSegments = [.. circle] } }, 4, fx, console, lib);
        Assert.Equal((Gd.RoundToInt((64.0 / 255.0 + 0.1) * 255), 128), (V(o, 40), V(o, 41))); // around the console's
        var still = new TimelineTrack { MovementSegments = [new(0, 10, "none")] };
        o = Out(new() { ["master"] = Both(), ["d"] = still }, 4.5, fx, bases, lib);
        Assert.Equal((128, 255), (V(o, 40), V(o, 41))); // a fixture's None beats the master's circle
        Assert.Empty(Out(new() { ["master"] = new() { MovementSegments = [new(0, 10, "none")] } }, 4.5, fx, bases, lib));
        o = Out(new() { ["master"] = new() { MovementSegments = [.. circle] } }, 4.5, [spot16], bases, lib);
        Assert.Equal(0, V(o, 100) * 256 + V(o, 101)); // 16-bit: around the console's (0 / 0, clamped)
        Assert.Equal(Gd.RoundToInt(0.1 * 65535), V(o, 102) * 256 + V(o, 103));
    }

    [Fact]
    public void A_colour_wheel_fixture()
    {
        var spot = Profiles.Make(
            "spot",
            "Wheel Spot",
            """
            [{"name": "Dimmer", "role": "DIMMER"},
             {"name": "Colour", "role": "COLOR_WHEEL", "ranges": [
                {"lo": 0, "hi": 9, "label": "Open"},
                {"lo": 10, "hi": 19, "label": "Red", "color": "#ff0000"},
                {"lo": 20, "hi": 29, "label": "Green", "color": "#00ff00"},
                {"lo": 30, "hi": 39, "label": "Blue", "color": "#0000ff"}]}]
            """
        );
        List<PerformFixture> fx = [new("s", 0, 0, spot)];
        List<byte[]> bases = [[]];
        var blue = new Rgb(0.1f, 0.2f, 1.0f);
        var green = new Rgb(0.1f, 1.0f, 0.1f);
        var tracks = new Dictionary<string, TimelineTrack> { ["master"] = Colour(0, 10, blue, blue) };
        Assert.Equal(34, V(Out(tracks, 5, fx, bases), 1)); // the master's colour's nearest slot (blue)
        tracks["s"] = Colour(0, 10, green, green);
        Assert.Equal(24, V(Out(tracks, 5, fx, bases), 1)); // ...or its own (green)
        Assert.False(Out([], 5, fx, bases).ContainsKey(1)); // left alone without a colour
    }

    [Fact]
    public void Mute_and_solo()
    {
        var fx = Fixtures();
        var bases = Bases();
        Assert.Empty(Out(new() { ["master"] = Flags(Half(), mute: true) }, 5, fx, bases)); // the master muted: nothing
        var o = Out(new() { ["master"] = Half(), ["a"] = Flags(new(), mute: true) }, 5, fx, bases);
        Assert.False(o.ContainsKey(0));
        Assert.Equal((128, 50), (V(o, 42), V(o, 20))); // a fixture muted: the master's still on the rest
        o = Out(new() { ["master"] = Half(), ["c"] = Flags(new(), mute: true) }, 5, fx, bases);
        Assert.False(o.ContainsKey(20) || o.ContainsKey(23)); // its heads muted too
        o = Out(new() { ["master"] = Half(), ["c/h0"] = Flags(new(), mute: true) }, 5, fx, bases);
        Assert.False(o.ContainsKey(20));
        Assert.Equal((50, 128), (V(o, 23), V(o, 0))); // a head muted: just that head
        o = Out(new() { ["master"] = Half(), ["b"] = Flags(new(), solo: true) }, 5, fx, bases);
        Assert.Equal(100, V(o, 10));
        Assert.False(o.ContainsKey(0) || o.ContainsKey(20) || o.ContainsKey(42)); // a fixture soloed: only it
        o = Out(new() { ["master"] = Half(), ["c"] = Flags(new(), solo: true), ["c/h1"] = Flags(new(), mute: true) }, 5, fx, bases);
        Assert.Equal(50, V(o, 20));
        Assert.False(o.ContainsKey(23) || o.ContainsKey(0)); // its heads too, unless muted
        o = Out(new() { ["master"] = Half(), ["c/h1"] = Flags(new(), solo: true) }, 5, fx, bases);
        Assert.Equal(50, V(o, 23));
        Assert.False(o.ContainsKey(20) || o.ContainsKey(0) || o.ContainsKey(10)); // a head soloed: only it
        o = Out(new() { ["master"] = Half(), ["a"] = Flags(new(), solo: true), ["c/h0"] = Flags(new(), solo: true) }, 5, fx, bases);
        Assert.Equal((128, 50), (V(o, 0), V(o, 20)));
        Assert.False(o.ContainsKey(23) || o.ContainsKey(42)); // two soloed: both play
        Assert.Empty(Out(new() { ["master"] = Half(), ["a"] = Flags(new(), mute: true, solo: true) }, 5, fx, bases)); // soloed but muted
    }

    [Fact]
    public void Mixing_decks()
    {
        var fx = Fixtures();
        var avg = SongOutput.AverageChannels(fx, 1);
        Assert.Equal([40, 41], avg[0].Order()); // pan / tilt averaged; the rest HTP
        List<Dictionary<int, int>> a = [new() { [0] = 100, [10] = 50, [40] = 0, [41] = 100 }];
        List<Dictionary<int, int>> b = [new() { [0] = 200, [10] = 20, [40] = 200 }];
        var m = SongOutput.Mix([a, b], avg, 1);
        Assert.Equal((200, 50), (m[0][0], m[0][10])); // HTP: the highest wins
        Assert.Equal((100, 100), (m[0][40], m[0][41])); // movement: the average of the decks driving it
        Assert.Equal(a[0], SongOutput.Mix([a], avg, 1)[0]); // one deck: as is
    }
}

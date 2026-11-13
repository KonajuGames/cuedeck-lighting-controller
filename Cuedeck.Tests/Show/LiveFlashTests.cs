using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.Tests.Show;

/// <summary>
/// sLight's live_flash_test.gd: blackout, strobe, white and colour over everything else. (Its MIDI and OSC trigger
/// parts come with the triggers' port, its DJ Perform Mode keys with that mode's.)
/// </summary>
public class LiveFlashTests
{
    static readonly Rgb Red = new(1, 0, 0);

    static PatchedFixture Fixture(FixtureProfile profile, int start, int universe = 0) =>
        new() { Uid = profile.Id, Universe = universe, Start = start, Profile = profile };

    /// <summary>
    /// A par: dimmer, red, green, blue, white, amber, pan.
    /// </summary>
    static PatchedFixture Par(int universe = 0) =>
        Fixture(Profiles.Simple("flashpar", "Flash Par", ("Dimmer", "DIMMER"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"), ("White", "WHITE"), ("Amber", "AMBER"), ("Pan", "PAN")), 10, universe);

    /// <summary>
    /// An RGB bar head without a dimmer or strobe channel.
    /// </summary>
    static PatchedFixture RgbHead() => Fixture(Profiles.Simple("flashrgb", "Flash RGB", ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE")), 30);

    static PatchedFixture Strober() =>
        Fixture(
            Profiles.Make(
                "flashstrobe",
                "Flash Strobe",
                """
                [{"name":"Shutter","role":"STROBE","ranges":[{"lo":0,"hi":9,"label":"Closed"},{"lo":10,"hi":19,"label":"Open"},{"lo":20,"hi":240,"label":"Strobe slow-fast"}]},
                 {"name":"Dimmer","role":"DIMMER"},{"name":"Red","role":"RED"},{"name":"Green","role":"GREEN"},{"name":"Blue","role":"BLUE"}]
                """
            ),
            50
        );

    static Dictionary<int, int> Out(IEnumerable<PatchedFixture> fx, FlashKind[] kinds, Rgb colour, double t = 0) => LiveFlash.Output(fx, 1, kinds, colour, t)[0];

    [Fact]
    public void Blackout_white_and_colour()
    {
        PatchedFixture[] fx = [Par(), RgbHead()];
        Assert.Empty(Out(fx, [], Red));

        var o = Out(fx, [FlashKind.Blackout], Red);
        Assert.Equal([0, 0, 0, 0], new[] { o[10], o[11], o[14], o[15] });
        Assert.False(o.ContainsKey(16)); // pan left alone, so movers don't swing
        Assert.Equal((0, 0), (o[30], o[32])); // a fixture without a dimmer: its colours

        o = Out(fx, [FlashKind.White], Red);
        Assert.Equal([255, 255, 255, 255], new[] { o[10], o[11], o[13], o[14] });
        Assert.Equal(0, o[15]); // amber off
        Assert.Equal([255, 255, 255], new[] { o[30], o[31], o[32] });

        var c = new Rgb(1, 0.5f, 0);
        o = Out(fx, [FlashKind.Colour], c);
        Assert.Equal([255, 255, 128, 0], new[] { o[10], o[11], o[12], o[13] });
        Assert.Equal((0, 0), (o[14], o[15]));
        Assert.Equal(255, Out(fx, [FlashKind.Colour, FlashKind.White], c)[12]); // white beats colour
        o = Out(fx, [FlashKind.White, FlashKind.Blackout], c);
        Assert.Equal((0, 0), (o[10], o[11])); // blackout beats everything

        var two = LiveFlash.Output([Par(universe: 1)], 2, [FlashKind.White], c, 0);
        Assert.Empty(two[0]);
        Assert.Equal(255, two[1][10]);
    }

    [Fact]
    public void Strobe()
    {
        PatchedFixture[] fx = [Strober(), RgbHead()];
        var o = Out(fx, [FlashKind.Strobe], Red);
        Assert.Equal((240, 255), (o[50], o[51])); // strobing fast, dimmer full
        Assert.False(o.ContainsKey(52)); // in the colours already playing
        Assert.False(o.ContainsKey(30)); // no strobe channel: the software strobe's lit part, as it was

        o = Out(fx, [FlashKind.Strobe], Red, 0.05);
        Assert.Equal((0, 0), (o[30], o[32])); // ...dark the rest of it
        Assert.Equal((240, 255), (o[50], o[51])); // its own shutter strobes

        o = Out(fx, [FlashKind.Strobe, FlashKind.White], Red);
        Assert.Equal([240, 255, 255], new[] { o[50], o[52], o[30] });
        Assert.Equal(10, Out(fx, [FlashKind.White], Red)[50]); // white alone opens the shutter

        var plain = Profiles.Simple("plain", "Plain", ("Strobe", "STROBE"));
        Assert.Equal(0, LiveFlash.OpenValue(plain.Channels(0)[0]));
    }

    [Fact]
    public void A_colour_wheel_fixture()
    {
        var spot = Profiles.Make(
            "flashspot",
            "Flash Spot",
            """
            [{"name":"Dimmer","role":"DIMMER"},{"name":"Colour","role":"COLOR_WHEEL","ranges":[
              {"lo":0,"hi":9,"label":"Open","color":"#ffffff"},{"lo":10,"hi":19,"label":"Red","color":"#ff0000"},{"lo":20,"hi":29,"label":"Blue","color":"#0000ff"}]}]
            """
        );
        PatchedFixture[] fx = [Fixture(spot, 0)];
        Assert.Equal(14, Out(fx, [FlashKind.Colour], Red).GetValueOrDefault(1, -1));
        Assert.Equal(4, Out(fx, [FlashKind.White], Red).GetValueOrDefault(1, -1));
        Assert.False(Out(fx, [FlashKind.Blackout], Red).ContainsKey(1)); // blackout leaves the wheel
    }

    [Fact]
    public void Held_and_latched()
    {
        var fl = new LiveFlash();
        var changes = 0;
        fl.Changed += () => changes++;
        fl.Press(FlashKind.Strobe);
        Assert.True(fl.IsOn(FlashKind.Strobe) && fl.AnyOn);
        fl.Release(FlashKind.Strobe);
        Assert.False(fl.AnyOn);
        Assert.Equal(2, changes);

        fl.Latch = true;
        fl.Press(FlashKind.White);
        fl.Release(FlashKind.White);
        Assert.True(fl.IsOn(FlashKind.White));
        fl.Press(FlashKind.White);
        Assert.False(fl.IsOn(FlashKind.White));
        fl.Toggle(FlashKind.Colour);
        fl.Clear();
        Assert.False(fl.AnyOn);
        Assert.Equal(2, fl.Layer(2).Count);
        Assert.Empty(fl.Layer(2)[0]);

        fl.Colour = new Rgb(0, 1, 0);
        var back = new LiveFlash();
        back.FromJson(fl.ToJson());
        Assert.Equal(new Rgb(0, 1, 0), back.Colour);
        Assert.True(back.Latch);
        back.FromJson(new JsonObject());
        Assert.Equal(Red, back.Colour);
        Assert.False(back.Latch);
    }

    [Fact]
    public void It_goes_over_the_virtual_dimmers_and_under_the_grand_master()
    {
        using var u = new DmxUniverse();
        u.SetChannel(30, 200);
        u.SetVirtualDimmer(7, [30, 31, 32]);
        u.SetVirtualDimmerLevel(7, 0);
        u.ComputeOutput(1, new Dictionary<int, int> { [31] = 100 });
        Assert.Equal((0, 0), (u.Output[30], u.Output[31]));
        u.ComputeOutput(1, new Dictionary<int, int> { [31] = 100 }, new Dictionary<int, int> { [30] = 255, [31] = 255 });
        Assert.Equal((255, 255), (u.Output[30], u.Output[31]));
        u.ComputeOutput(0.5, null, new Dictionary<int, int> { [30] = 255 });
        Assert.Equal(127, u.Output[30]);
    }

    [Fact]
    public void In_a_show_it_flashes_the_patch_and_is_saved_with_it()
    {
        using var show = new LightingShow();
        var par = Par();
        show.Patch.Add(0, par.Profile, 10, name: "Par");
        var u = show.Dmx.Universes[0];
        u.SetChannel(10, 100);
        show.Dmx.Tick(false);
        Assert.Equal(100, u.Output[10]);

        show.Flash.Press(FlashKind.Blackout);
        show.Dmx.Tick(false);
        Assert.Equal(0, u.Output[10]);
        show.Flash.Release(FlashKind.Blackout);
        show.Dmx.Tick(false);
        Assert.Equal(100, u.Output[10]);

        show.Flash.Latch = true;
        show.Flash.Colour = new Rgb(0, 0, 1);
        var d = ShowFile.ToJson(show);
        Assert.True(d.Get("live_flash").Bool("latch"));
        Assert.Equal("0000ff", d.Get("live_flash").Str("colour"));

        show.Flash.Press(FlashKind.White);
        ShowFile.Apply(show, Json.Parse("""{"universes":[{}],"live":[{}]}""")!);
        Assert.False(show.Flash.AnyOn);
        Assert.False(show.Flash.Latch);
    }
}

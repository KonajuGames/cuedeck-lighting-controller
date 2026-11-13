using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;
using Cuedeck.Show;

namespace Cuedeck.Tests.Show;

/// <summary>
/// sLight's palettes_test.gd, through the show rather than its Palettes tab: recording palettes, cues linking to them,
/// updating, applying, switching a cue between tracking and block, deleting, and the show file.
/// </summary>
public sealed class PaletteTests : IDisposable
{
    readonly LightingShow _show = new();
    readonly DmxUniverse _u0;
    readonly PatchedFixture _spot;
    readonly PatchedFixture _par;

    public PaletteTests()
    {
        _u0 = _show.Dmx.Universes[0];
        // Spot at 1: dimmer, pan, tilt, red, green, blue, gobo (channels 0-6). Par at 21: red, green, blue (20-22).
        _spot = _show.Patch.Add(0, Profiles.Simple("pspot", "Pal Spot", ("Dimmer", "DIMMER"), ("Pan", "PAN"), ("Tilt", "TILT"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"), ("Gobo", "GOBO")), 0, name: "Spot");
        _par = _show.Patch.Add(0, Profiles.Rgb(), 20, name: "Par");
        _show.Groups.Add(new FixtureGroup { Name = "Pars", Members = { _par.Uid } });
        _u0.SetAll(0);
    }

    public void Dispose() => _show.Dispose();

    void Rgb(int[] spot, int[] par)
    {
        for (var i = 0; i < 3; i++)
        {
            _u0.SetChannel(3 + i, spot[i]);
            _u0.SetChannel(20 + i, par[i]);
        }
    }

    List<byte[]> Bases => [.. _show.Dmx.Universes.Select(u => u.Data)];

    Palette? Record(PaletteKind kind, string name, IEnumerable<PatchedFixture>? fixtures = null) => _show.Palettes.Record(kind, name, fixtures ?? _show.Patch.All, Bases);

    int Level(int cue, int channel) => _show.Cues.StandingLook(cue)!.Level(0, channel);

    [Fact]
    public void Recording()
    {
        Rgb([255, 0, 0], [255, 0, 0]);
        var p = Record(PaletteKind.Colour, "Colour 1")!;
        Assert.Equal(2, p.Values.Count);
        Assert.Equal([3, 4, 5], p.Values[_spot.Uid].Keys); // just the colour channels, by offset
        Assert.NotNull(Record(PaletteKind.Beam, "Beam 1"));
        Assert.Single(_show.Palettes.OfKind(PaletteKind.Beam).Single().Values); // only the fixture with a gobo
        Assert.Equal(new Rgb(1, 0, 0), PaletteLibrary.Swatch(p, _show.Patch.All));
    }

    Palette RecordRedAndThreeCues()
    {
        Rgb([255, 0, 0], [255, 0, 0]);
        var red = Record(PaletteKind.Colour, "Red")!;
        _u0.SetChannel(0, 255);
        _show.Cues.Record(-1); // tracking: dimmer up, both red
        // Only what a tracking cue changes links: the par goes 254, which isn't the palette.
        Rgb([255, 0, 0], [254, 0, 0]);
        _show.Cues.Record(0);
        Rgb([255, 0, 0], [255, 0, 0]);
        _show.Cues.TrackingEnabled = false;
        _show.Cues.Record(1); // a block, all red
        _show.Cues.TrackingEnabled = true;
        return red;
    }

    [Fact]
    public void Cues_link_to_palettes()
    {
        var red = RecordRedAndThreeCues();
        var c = _show.Cues.Cues[0];
        Assert.Equal(red.Id, c.Refs[_spot.Uid]["colour"]);
        Assert.Equal(red.Id, c.Refs[_par.Uid]["colour"]);
        Assert.False(c.Levels.Universes[0].ContainsKey(3)); // not stored in the cue
        Assert.False(c.Levels.Universes[0].ContainsKey(20));
        Assert.Equal(255, c.Levels.Universes[0][0]); // the dimmer is
        Assert.Equal(["Red"], _show.Cues.PaletteNames(c));
        Assert.Equal((255, 255, 0), (Level(1, 3), Level(1, 20), Level(1, 4))); // it plays back red

        var c2 = _show.Cues.Cues[1];
        Assert.False(c2.Refs.ContainsKey(_par.Uid));
        Assert.Equal(254, c2.Levels.Universes[0][20]); // not quite the palette's: raw values
        Assert.False(c2.Refs.ContainsKey(_spot.Uid)); // the unchanged spot isn't touched

        var c3 = _show.Cues.Cues[2];
        Assert.Equal(red.Id, c3.Refs[_par.Uid]["colour"]); // a block links too
        Assert.False(c3.Levels.Universes[0].ContainsKey(20));
    }

    [Fact]
    public void Updating_and_applying()
    {
        var red = RecordRedAndThreeCues();
        Rgb([0, 0, 200], [0, 0, 200]);
        _show.Palettes.Update(red, _show.Patch.All, Bases);
        Assert.Equal(2, _show.Cues.PaletteUsers(red.Id));
        Assert.Equal((0, 200, 200), (Level(1, 3), Level(1, 5), Level(1, 22))); // every cue using it follows
        Assert.Equal(254, Level(2, 20)); // not the one with raw values

        Rgb([0, 0, 0], [0, 0, 0]);
        _show.Cues.GoTo(3);
        _show.Dmx.Advance(5);
        Assert.Equal((200, 200), (_u0.GetChannel(5), _u0.GetChannel(22))); // GO plays the palette as it is now

        Rgb([0, 255, 0], [0, 255, 0]);
        _show.LoadLook(_show.Palettes.Applied(red, _show.Patch.All, Bases));
        Assert.Equal((0, 200, 200), (_u0.GetChannel(4), _u0.GetChannel(5), _u0.GetChannel(22)));
        Assert.Equal(255, _u0.GetChannel(0)); // leaving their other channels
    }

    [Fact]
    public void Groups_and_kinds()
    {
        RecordRedAndThreeCues();
        var pars = _show.Patch.All.Where(f => _show.GroupMembers("Pars")!.Contains(f.Uid)).ToList();
        Rgb([10, 10, 10], [0, 255, 255]);
        var cyan = Record(PaletteKind.Colour, "Cyan", pars)!;
        Assert.Single(cyan.Values); // the group only
        Rgb([1, 2, 3], [9, 9, 9]);
        _show.LoadLook(_show.Palettes.Applied(cyan, pars, Bases));
        Assert.Equal((255, 1), (_u0.GetChannel(21), _u0.GetChannel(3)));
        Assert.Null(Record(PaletteKind.Position, "Position 1", pars)); // pars have no position

        _u0.SetChannel(1, 100);
        _u0.SetChannel(2, 50);
        var pos = Record(PaletteKind.Position, "Position 1")!;
        Assert.Single(pos.Values);
        _show.Cues.Record(2);
        Assert.Equal(pos.Id, _show.Cues.Cues[3].Refs[_spot.Uid]["position"]);
    }

    [Fact]
    public void A_cue_made_a_block_and_back_still_uses_its_palette_and_looks_the_same()
    {
        RecordRedAndThreeCues();
        var before = Json.Stringify(_show.Cues.StandingLook(1)!.ToJson());
        _show.Cues.SetTracking(0, false);
        var c = _show.Cues.Cues[0];
        Assert.False(c.Tracking);
        Assert.NotEmpty(c.Refs);
        Assert.False(c.Levels.Universes[0].ContainsKey(3));
        Assert.False(c.Levels.Universes[0].ContainsKey(20));
        Assert.Equal(before, Json.Stringify(_show.Cues.StandingLook(1)!.ToJson()));
        _show.Cues.SetTracking(0, true);
        Assert.Equal(before, Json.Stringify(_show.Cues.StandingLook(1)!.ToJson()));
        Assert.False(c.Levels.Universes[0].ContainsKey(20));
    }

    [Fact]
    public void Deleting_a_palette_puts_its_values_in_the_cues()
    {
        var red = RecordRedAndThreeCues();
        Rgb([0, 0, 200], [0, 0, 200]);
        _show.Palettes.Update(red, _show.Patch.All, Bases);
        var looks = Enumerable.Range(1, 3).Select(i => _show.Cues.StandingLook(i)!.Buffers(1)[0]).ToList();
        Assert.Equal(2, _show.Cues.BakePalette(red.Id));
        _show.Palettes.Remove(red);
        Assert.Null(_show.Palettes.Find(red.Id));
        Assert.Equal(0, _show.Cues.PaletteUsers(red.Id));
        Assert.Equal(200, _show.Cues.Cues[0].Levels.Universes[0][22]);
        for (var i = 0; i < 3; i++)
            Assert.Equal(looks[i], _show.Cues.StandingLook(i + 1)!.Buffers(1)[0]);
        Assert.Empty(_show.Cues.PaletteNames(_show.Cues.Cues[0]));
    }

    [Fact]
    public void The_show_file()
    {
        RecordRedAndThreeCues();
        _u0.SetChannel(1, 100);
        var pos = Record(PaletteKind.Position, "Position 1")!;
        _show.Cues.Record(2);
        var d = ShowFile.ToJson(_show);
        var n = _show.Palettes.Palettes.Count;
        var look = Json.Stringify(_show.Cues.StandingLook(4)!.ToJson());

        ShowFile.Apply(_show, Json.Parse("""{"universes":[{}],"live":[{}]}""")!);
        Assert.Empty(_show.Palettes.Palettes);
        ShowFile.Apply(_show, d);
        Assert.Equal(n, _show.Palettes.Palettes.Count);
        Assert.Equal(look, Json.Stringify(_show.Cues.StandingLook(4)!.ToJson()));

        var p = Record(PaletteKind.Beam, "Beam 1")!;
        Assert.Single(_show.Palettes.Palettes, x => x.Id == p.Id); // never reuses an id
        Assert.NotEqual(pos.Id, p.Id);

        // A fixture taken out of the patch: its references are skipped.
        Assert.NotEmpty(_show.Cues.Cues[3].Refs);
        _show.Patch.Remove(_show.Patch.Universes[0].Fixtures[0]);
        Assert.False(_show.Cues.StandingLook(4)!.Universes[0].ContainsKey(1));
    }
}

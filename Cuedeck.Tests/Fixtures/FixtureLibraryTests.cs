using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.Tests.Fixtures;

/// <summary>
/// The bundled fixture library (fixture_profile_test.gd's library part, and more), the patch's Flash, and presets.
/// </summary>
public class FixtureLibraryTests
{
    static readonly FixtureLibrary Library = new(FixtureLibrary.DefaultFolder);

    [Fact]
    public void The_bundled_library_is_there()
    {
        Assert.True(Library.Available);
        Assert.Equal(638, Library.Count);
        Assert.Contains("Cameo", Library.Manufacturers);
        Assert.Contains("moving_head", Library.Categories);
        Assert.StartsWith("638 fixtures · OFL ", Library.SourceNote);
    }

    [Fact]
    public void A_bundled_fixture_has_its_manufacturer_and_model_matching_its_entry()
    {
        var e = Library.Entry("cameo-auro-beam-150")!;
        var p = Library.Resolve("cameo-auro-beam-150")!;
        Assert.Equal(("Cameo", "Auro Beam 150"), (p.Manufacturer, p.Model));
        Assert.Equal((e.Maker, e.Model), (p.Manufacturer, p.Model));
        Assert.Equal(e.Modes.Count, p.ModeCount);
        Assert.Equal(e.Modes[0].Channels, p.ChannelCount(0));
        Assert.Same(p, Library.Resolve("cameo-auro-beam-150")); // read once
        Assert.Null(Library.Resolve("no-such-fixture"));
    }

    [Fact]
    public void An_older_cached_profile_takes_its_names_from_its_entry()
    {
        var legacy = FixtureProfile.FromJson(Json.Parse("""{"id":"x","profile_name":"Cameo Auro Beam 150","modes":[]}"""));
        FixtureLibrary.NamesFrom(legacy, Library.Entry("cameo-auro-beam-150")!);
        Assert.Equal(("Cameo", "Auro Beam 150"), (legacy.Manufacturer, legacy.Model));
    }

    [Fact]
    public void Searching()
    {
        var all = Library.Search();
        Assert.Equal(Library.Count, all.Count);
        var cameo = Library.Search("cameo");
        Assert.NotEmpty(cameo);
        Assert.All(cameo, e => Assert.Contains("cameo", e.Haystack));
        Assert.All(Library.Search("auro beam"), e => Assert.Contains("auro", e.Haystack));
        Assert.All(Library.Search(maker: "Cameo"), e => Assert.Equal("Cameo", e.Maker));
        Assert.All(Library.Search(category: "par"), e => Assert.Equal("par", e.Category));
        Assert.All(Library.Search(needsPanTilt: true), e => Assert.True(e.PanTilt));
        Assert.All(Library.Search(maxChannels: 4), e => Assert.True(e.NarrowestMode <= 4));
        Assert.Empty(Library.Search("no such fixture anywhere"));
    }

    [Fact]
    public void Every_bundled_fixture_resolves()
    {
        foreach (var e in Library.Entries)
            Assert.NotNull(Library.Resolve(e.Id));
    }

    [Fact]
    public void Flash_lights_a_fixture_white_and_puts_it_back()
    {
        using var show = new LightingShow();
        var rgbaw = Profiles.Simple("p", "P", ("Dimmer", "DIMMER"), ("R", "RED"), ("G", "GREEN"), ("B", "BLUE"), ("W", "WHITE"), ("A", "AMBER"), ("Pan", "PAN"));
        var f = show.Patch.Add(0, rgbaw, 10);
        var u = show.Dmx.Universes[0];
        u.SetChannel(15, 77); // amber
        u.SetChannel(16, 40); // pan
        var saved = show.Patch.FlashOn(f);
        Assert.Equal([255, 255, 255, 255, 255, 0, 40], Enumerable.Range(10, 7).Select(u.GetChannel));
        show.Patch.FlashOff(f, saved);
        Assert.Equal([0, 0, 0, 0, 0, 77, 40], Enumerable.Range(10, 7).Select(u.GetChannel));

        var wheel = FixtureProfile.BuiltIn().First(p => p.Id == "mh_spot");
        Assert.Equal(4, Patch.FlashTargets(wheel.Channels(0))[4]); // the open slot's middle
        Assert.Empty(Patch.FlashTargets(Profiles.Simple("pt", "PT", ("Pan", "PAN"), ("Tilt", "TILT")).Channels(0)));
    }

    [Fact]
    public void Presets_hold_every_universes_levels()
    {
        using var show = new LightingShow();
        show.Dmx.Universes[0].SetChannel(3, 9);
        show.Patch.AddUniverse();
        show.Dmx.Universes[1].SetChannel(0, 200);
        var preset = show.PresetToJson();
        Assert.Equal(2, preset.Arr("universes").Count);

        using var other = new LightingShow();
        other.Dmx.Universes[0].SetChannel(100, 1);
        Assert.Null(other.ApplyPreset(Json.Parse(Json.Stringify(preset))));
        Assert.Equal(2, other.Dmx.UniverseCount); // grown to fit
        Assert.Equal((9, 0, 200), (other.Dmx.Universes[0].GetChannel(3), other.Dmx.Universes[0].GetChannel(100), other.Dmx.Universes[1].GetChannel(0)));

        // A narrower preset doesn't take universes away; an old flat one is universe 1's.
        Assert.Null(other.ApplyPreset(Json.Parse("""{"5":50}""")));
        Assert.Equal(2, other.Dmx.UniverseCount);
        Assert.Equal(50, other.Dmx.Universes[0].GetChannel(5));
        Assert.NotNull(other.ApplyPreset(new JsonArray()));
    }
}

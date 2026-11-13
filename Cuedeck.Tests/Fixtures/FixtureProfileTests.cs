using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Fixtures;

namespace Cuedeck.Tests.Fixtures;

/// <summary>
/// sLight's fixture_profile_test.gd: manufacturer and model, saved and loaded, older profiles' single name, and colour
/// wheels. (Its bundled-library and GDTF parts come with the fixture library's port.)
/// </summary>
public class FixtureProfileTests
{
    static FixtureProfile Par() => Profiles.Make("acme-par", "Par 7", """[{"name":"Red","role":"RED"},{"name":"Green","role":"GREEN"},{"name":"Blue","role":"BLUE"}]""", manufacturer: "Acme");

    [Fact]
    public void A_profile_has_a_manufacturer_and_model()
    {
        var p = Par();
        Assert.Equal("Acme", p.Manufacturer);
        Assert.Equal("Par 7", p.Model);
        Assert.Equal("Acme Par 7", p.FullName);
        Assert.Equal("Solo", new FixtureProfile("x", "Solo").FullName);
    }

    [Fact]
    public void They_are_saved_and_loaded_back()
    {
        var d = Par().ToJson();
        Assert.Equal("Acme", d.Str("manufacturer"));
        Assert.Equal("Par 7", d.Str("model"));
        Assert.False(d.ContainsKey("profile_name"));
        var back = FixtureProfile.FromJson(d);
        Assert.Equal("Acme", back.Manufacturer);
        Assert.Equal("Par 7", back.Model);
    }

    [Fact]
    public void An_older_profiles_name_becomes_its_model()
    {
        var old = FixtureProfile.FromJson(Json.Parse("""{"id":"old","profile_name":"My Wash","modes":[]}"""));
        Assert.Equal("My Wash", old.Model);
        Assert.Equal("", old.Manufacturer);
    }

    [Fact]
    public void The_built_ins_are_generic_with_their_models()
    {
        Assert.All(FixtureProfile.BuiltIn(), b =>
        {
            Assert.Equal("Generic", b.Manufacturer);
            Assert.NotEqual("", b.Model);
        });
    }

    /// <summary>
    /// A spot with a dimmer and a colour wheel: open, red, green, blue (by colour), deep yellow (by name), then a
    /// rotation that isn't a colour.
    /// </summary>
    static FixtureProfile Spot() =>
        Profiles.Make(
            "spot",
            "Wheel Spot",
            """
            [{"name":"Dimmer","role":"DIMMER"},
             {"name":"Colour","role":"COLOR_WHEEL","ranges":[
               {"lo":0,"hi":9,"label":"Open"},
               {"lo":10,"hi":19,"label":"Red","color":"#ff0000"},
               {"lo":20,"hi":29,"label":"Green","color":"#00ff00"},
               {"lo":30,"hi":39,"label":"Blue","color":"#0000ff"},
               {"lo":40,"hi":49,"label":"Deep Yellow"},
               {"lo":50,"hi":255,"label":"Rotation"}]}]
            """,
            manufacturer: "Acme"
        );

    [Fact]
    public void A_wheels_colour_slots_leave_out_its_rotation()
    {
        Assert.Equal(5, FixtureProfile.WheelSlots(Spot().Channels(0)[1]).Count);
    }

    [Fact]
    public void The_nearest_slot_is_red_for_a_crimson_yellow_by_its_name_and_open_for_a_near_white()
    {
        var ch = Spot().Channels(0)[1];
        Assert.Equal(14, FixtureProfile.NearestWheelValue(ch, new Rgb(0.95f, 0.1f, 0.2f)));
        Assert.Equal(44, FixtureProfile.NearestWheelValue(ch, new Rgb(1.0f, 0.9f, 0.05f)));
        Assert.Equal(4, FixtureProfile.NearestWheelValue(ch, new Rgb(1.0f, 0.95f, 0.9f)));
    }

    [Fact]
    public void A_wheel_only_fixture_has_its_wheels_colours_but_one_with_RGB_has_none()
    {
        var spot = Spot();
        Assert.Equal(5, spot.WheelColours(0).Count);
        Assert.Contains(Rgb.White, spot.WheelColours(0));
        var wheel = spot.Channels(0)[1].ToJson();
        var hybrid = new FixtureProfile("h", "Hybrid", [new JsonObject { ["name"] = "R", ["role"] = "RED" }, new JsonObject { ["name"] = "G", ["role"] = "GREEN" }, new JsonObject { ["name"] = "B", ["role"] = "BLUE" }, wheel]);
        Assert.Empty(hybrid.WheelColours(0));
    }

    [Fact]
    public void Channels_are_filled_in_and_kept_in_range()
    {
        var p = Profiles.Make("x", "X", """[{"role":"PAN"},{"name":"T","role":"TILT","min":300,"max":-5},{"name":"D","role":"DIMMER","default":300,"ranges":[{"lo":20,"hi":10}]}]""");
        var chans = p.Channels(0);
        Assert.Equal("Ch", chans[0].Name);
        Assert.Equal(128, chans[0].Default); // pan parks in the middle
        Assert.Equal((0, 255), (chans[1].Min, chans[1].Max));
        Assert.Equal(255, chans[2].Default);
        Assert.Equal(new RangeSlot(10, 20, "10-20"), chans[2].Ranges[0]);
    }

    [Fact]
    public void Heads_are_each_RGB_triplet_with_the_whites_after_it()
    {
        var bar = Profiles.Make("bar", "Bar", """
            [{"role":"DIMMER"},{"role":"RED"},{"role":"GREEN"},{"role":"BLUE"},{"role":"WHITE"},
             {"role":"RED"},{"role":"GREEN"},{"role":"BLUE"},{"role":"UV"}]
            """);
        var heads = bar.HeadGroups(0);
        Assert.Equal(2, heads.Count);
        Assert.Equal((1, 2, 3), (heads[0].R, heads[0].G, heads[0].B));
        Assert.Equal([4], heads[0].Extra);
        Assert.Equal([8], heads[1].Extra);
        // No spacing given: 15 cm apart, centred.
        Assert.Equal(-0.075f, heads[0].Offset.X, 5);
        Assert.Equal(0.075f, heads[1].Offset.X, 5);
        Assert.Equal("Primary Multi-cell", bar.DefaultShowCategory(0));
    }

    [Fact]
    public void Colour_names_and_hex_codes_read_as_Godot_reads_them()
    {
        Assert.Equal(new Rgb(1, 0, 0), Rgb.FromString("#ff0000", Rgb.Transparent));
        Assert.Equal(new Rgb(1, 0, 0), Rgb.FromString("f00", Rgb.Transparent));
        Assert.Equal(Rgb.FromString("dark orange", Rgb.Transparent), Rgb.FromString("DARK_ORANGE", Rgb.Transparent));
        Assert.Equal(Rgb.Transparent, Rgb.FromString("deep", Rgb.Transparent));
        Assert.Equal("ff8000", new Rgb(1, 0.5f, 0).ToHtml());
    }
}

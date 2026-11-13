using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.UI;

namespace Cuedeck.Tests.Fixtures;

/// <summary>
/// Importing fixture definitions: GDTF (fixture_profile_test.gd's GDTF part, with hand-written description.xml files
/// as Cuedeck has no MVR export yet) and Open Fixture Library JSON.
/// </summary>
public class FixtureImportTests
{
    // ------------------------------------------------------------------ OFL --

    const string OflSpot = """
        {
          "name": "Test Spot",
          "categories": ["Moving Head", "Color Changer"],
          "physical": { "lens": { "degreesMinMax": [16, 16] }, "focus": { "panMax": 540, "tiltMax": 270 } },
          "wheels": {
            "Color Wheel": { "slots": [ { "type": "Open" }, { "type": "Color", "name": "Red", "colors": ["#ff0000"] }, { "type": "Color", "name": "Blue", "colors": ["#0000ff"] } ] }
          },
          "availableChannels": {
            "Pan": { "fineChannelAliases": ["Pan fine"], "capability": { "type": "Pan", "angleStart": "0deg", "angleEnd": "540deg" } },
            "Tilt": { "capability": { "type": "Tilt" } },
            "Dimmer": { "defaultValue": "50%", "capability": { "type": "Intensity" } },
            "Color Wheel": {
              "capabilities": [
                { "dmxRange": [0, 9], "type": "WheelSlot", "slotNumber": 1 },
                { "dmxRange": [10, 19], "type": "WheelSlot", "slotNumber": 2 },
                { "dmxRange": [20, 29], "type": "WheelSlot", "slotNumber": 3 },
                { "dmxRange": [30, 255], "type": "WheelRotation", "comment": "Rainbow" }
              ]
            },
            "Shutter": { "defaultValue": 255, "capabilities": [ { "dmxRange": [0, 127], "type": "ShutterStrobe", "shutterEffect": "Open" }, { "dmxRange": [128, 255], "type": "NoFunction" } ] }
          },
          "modes": [
            { "name": "9ch", "channels": ["Pan", "Pan fine", "Tilt", "Dimmer", "Color Wheel", "Shutter", null, "Mystery"] },
            { "name": "4ch", "channels": ["Pan", "Tilt", "Dimmer", "Color Wheel"] }
          ]
        }
        """;

    static ImportResult Ofl(string json) => FixtureImport.FromOfl(Json.Parse(json)!);

    [Fact]
    public void An_ofl_fixture_reads_its_modes_roles_and_defaults()
    {
        var r = Ofl(OflSpot);
        var p = r.Profile!;
        Assert.Equal(("test_spot", "Test Spot", ""), (p.Id, p.Model, p.Manufacturer));
        Assert.Equal(["9ch", "4ch"], p.ModeNames);
        var ch = p.Channels(0);
        Assert.Equal(
            [Roles.Pan, Roles.PanFine, Roles.Tilt, Roles.Dimmer, Roles.ColorWheel, Roles.Strobe, Roles.Generic, Roles.Generic],
            ch.Select(c => c.Role)
        );
        Assert.True(ch[1].Fine);
        Assert.Equal(128, ch[0].Default); // no default given: pan parks centred
        Assert.Equal(127, ch[3].Default); // "50%": 50 × 2.55 is a hair under 127.5 in doubles, in Godot too
        Assert.Equal(255, ch[5].Default);
        Assert.Equal("Unused", ch[6].Name);
        Assert.Equal(["Channel 'Mystery' is not in availableChannels"], r.Warnings);
    }

    [Fact]
    public void An_ofl_wheel_names_and_colours_its_slots()
    {
        var wheel = Ofl(OflSpot).Profile!.Channels(0)[4];
        Assert.Equal(
            [("Slot 1", ""), ("Red", "#ff0000"), ("Blue", "#0000ff"), ("Rainbow", "")],
            wheel.Ranges.Select(s => (s.Label, s.Color))
        );
        var shutter = Ofl(OflSpot).Profile!.Channels(0)[5];
        Assert.Equal(["ShutterStrobe", "—"], shutter.Ranges.Select(s => s.Label));
    }

    [Fact]
    public void An_ofl_fixture_takes_its_physical_side()
    {
        var phys = Ofl(OflSpot).Profile!.Physical;
        Assert.Equal(("moving_head", 16.0, 540.0, 270.0), (phys.Category, phys.BeamDeg, phys.PanRange, phys.TiltRange));
    }

    [Fact]
    public void An_ofl_matrix_becomes_heads_spaced_by_its_width()
    {
        var r = Ofl(
            """
            {
              "name": "Bar 4",
              "categories": ["Pixel Bar"],
              "physical": { "dimensions": [600, 100, 100] },
              "matrix": { "pixelCount": [4, 1, 1] },
              "availableChannels": { "Dimmer": { "capability": { "type": "Intensity" } } },
              "templateChannels": {
                "Red $pixelKey": { "capability": { "type": "ColorIntensity", "color": "Red" } },
                "Green $pixelKey": { "capability": { "type": "ColorIntensity", "color": "Green" } },
                "Blue $pixelKey": { "capability": { "type": "ColorIntensity", "color": "Blue" } }
              },
              "modes": [ { "name": "13ch", "channels": ["Dimmer", { "insert": "matrixChannels", "repeatFor": "eachPixelXYZ", "channelOrder": "perPixel", "templateChannels": ["Red $pixelKey", "Green $pixelKey", "Blue $pixelKey"] }] } ]
            }
            """
        );
        var p = r.Profile!;
        Assert.Equal(13, p.ChannelCount(0));
        Assert.Equal("Red (1, 1, 1)", p.Channels(0)[1].Name);
        Assert.Equal(Roles.Blue, p.Channels(0)[12].Role);
        Assert.Equal("strip", p.Physical.Category);
        // 600 mm over a span of 1.5 grid steps each side: 0.15 m apart.
        Assert.Equal([-0.225, -0.075, 0.075, 0.225], p.Physical.Heads.Select(h => Math.Round(h[0], 6)));
        Assert.Equal(4, p.HeadGroups(0).Count);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void An_ofl_fixture_with_no_modes_fails() => Assert.Equal("The OFL fixture has no modes.", Ofl("""{ "name": "X", "availableChannels": {} }""").Error);

    // ----------------------------------------------------------------- GDTF --

    const string GdtfSpot = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.1">
          <FixtureType Name="Spot" LongName="Spot 300" Manufacturer="Acme">
            <Wheels>
              <Wheel Name="Color1">
                <Slot Name="Open" Color="0.3127,0.3290,100.0"/>
                <Slot Name="Red" Color="0.64,0.33,21.26"/>
              </Wheel>
              <Wheel Name="Gobo1">
                <Slot Name="Open"/>
                <Slot Name="Star" MediaFileName="star"/>
              </Wheel>
            </Wheels>
            <Geometries>
              <Geometry Name="Base" Model="Base">
                <Axis Name="Yoke" Model="Yoke" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,-0.1,1}">
                  <Axis Name="Head" Model="Head">
                    <Beam Name="Beam" BeamAngle="18"/>
                  </Axis>
                </Axis>
              </Geometry>
            </Geometries>
            <Models>
              <Model Name="Base" File="base"/>
            </Models>
            <DMXModes>
              <DMXMode Name="Standard" Geometry="Base">
                <DMXChannels>
                  <DMXChannel Offset="1,2" Geometry="Yoke">
                    <LogicalChannel Attribute="Pan">
                      <ChannelFunction Name="Pan" Attribute="Pan" DMXFrom="0/2" Default="32768/2" PhysicalFrom="-270" PhysicalTo="270"/>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel Offset="3" Geometry="Head">
                    <LogicalChannel Attribute="Tilt">
                      <ChannelFunction Name="Tilt" Attribute="Tilt" DMXFrom="0/1" PhysicalFrom="-135" PhysicalTo="135"/>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel Offset="4" Geometry="Beam" InitialFunction="Beam_Dimmer.Dimmer.Dimmer">
                    <LogicalChannel Attribute="Dimmer">
                      <ChannelFunction Name="Dimmer" Attribute="Dimmer" DMXFrom="0/1" Default="255/1"/>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel Offset="6" Geometry="Beam">
                    <LogicalChannel Attribute="Color1" Wheel="Color1">
                      <ChannelFunction Name="Open" Attribute="Color1" DMXFrom="0/1" Default="0/1" WheelSlotIndex="1"/>
                      <ChannelFunction Name="Red" Attribute="Color1" DMXFrom="10/1" WheelSlotIndex="2"/>
                      <ChannelFunction Name="Spin" Attribute="Color1WheelSpin" DMXFrom="128/1"/>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel Offset="7" Geometry="Beam">
                    <LogicalChannel Attribute="Gobo1" Wheel="Gobo1">
                      <ChannelFunction Name="Open" Attribute="Gobo1" DMXFrom="0/1"/>
                      <ChannelFunction Name="Star" Attribute="Gobo1" DMXFrom="64/1"/>
                    </LogicalChannel>
                  </DMXChannel>
                  <DMXChannel Offset="None">
                    <LogicalChannel Attribute="Virtual"/>
                  </DMXChannel>
                </DMXChannels>
              </DMXMode>
            </DMXModes>
          </FixtureType>
        </GDTF>
        """;

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void A_gdtf_reads_its_names_and_channels()
    {
        var r = FixtureImport.FromGdtfXml(Utf8(GdtfSpot));
        var p = r.Profile!;
        Assert.Equal(("acme_spot_300", "Acme", "Spot 300"), (p.Id, p.Manufacturer, p.Model));
        var ch = p.Channels(0);
        Assert.Equal(
            [("Pan", Roles.Pan), ("Pan fine", Roles.PanFine), ("Tilt", Roles.Tilt), ("Dimmer", Roles.Dimmer), ("Ch 5", Roles.Generic), ("Color1", Roles.ColorWheel), ("Gobo1", Roles.Gobo)],
            ch.Select(c => (c.Name, c.Role))
        );
        Assert.Equal((128, 0), (ch[0].Default, ch[1].Default)); // 32768 split into its two bytes
        Assert.Equal(128, ch[2].Default); // none given: tilt parks centred
        Assert.Equal(255, ch[3].Default);
        Assert.Equal(["Standard: DMX channel 5 not defined, left as GENERIC"], r.Warnings); // no archive to look for the gobo in
    }

    [Fact]
    public void A_gdtf_wheel_takes_its_slots_colours()
    {
        var wheel = FixtureImport.FromGdtfXml(Utf8(GdtfSpot)).Profile!.Channels(0)[5];
        Assert.Equal([(0, 9, "Open"), (10, 127, "Red"), (128, 255, "Spin")], wheel.Ranges.Select(s => (s.Lo, s.Hi, s.Label)));
        Assert.Equal("#ffffff", wheel.Ranges[0].Color);
        Assert.Equal("#ff0000", wheel.Ranges[1].Color);
        Assert.Equal("", wheel.Ranges[2].Color); // the third slot in the wheel: there isn't one
    }

    [Fact]
    public void A_gdtf_takes_its_physical_side_and_geometry()
    {
        var p = FixtureImport.FromGdtfXml(Utf8(GdtfSpot)).Profile!;
        Assert.Equal(("moving_head", 18.0, 540.0, 270.0), (p.Physical.Category, p.Physical.BeamDeg, p.Physical.PanRange, p.Physical.TiltRange));
        Assert.Equal("Base", p.Geometry.Get("tree").Str("name"));
        Assert.Equal(("Yoke", "Head"), (p.Geometry.Str("pan_geo"), p.Geometry.Str("tilt_geo")));
        var yoke = p.Geometry.Get("tree").Arr("children")[0];
        Assert.Equal("axis", yoke.Str("kind"));
        Assert.Equal(-0.1, Json.AsDouble(yoke.Arr("mat")[14]), 6);
        Assert.Equal(18.0, yoke.Arr("children")[0].Arr("children")[0].Num("beam_deg"));
    }

    [Fact]
    public void A_gdtf_archive_brings_its_gobo_pictures_and_models()
    {
        using var dir = new TempFolder();
        var path = dir.File("spot.gdtf");
        var picture = new byte[128 * 96 * 4];
        Array.Fill(picture, (byte)200);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(zip, "description.xml", Utf8(GdtfSpot));
            Write(zip, "wheels/Star.png", Png.Encode(new Png.Image(128, 96, picture)));
            Write(zip, "models/gltf/base.glb", [1, 2, 3]);
        }
        var r = FixtureImport.FromPath(path);
        var star = r.Profile!.Channels(0)[6].Ranges[1];
        var image = Png.Decode(Convert.FromBase64String(star.Image));
        Assert.Equal((64, 48), (image.Width, image.Height)); // shrunk to 64 px at most
        Assert.Equal(200, image.Pixels[0]);
        Assert.Equal([1, 2, 3], r.ModelBytes["Base"]);
        Assert.DoesNotContain(r.Warnings, w => w.StartsWith("Gobo image", StringComparison.Ordinal));
    }

    static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(bytes);
    }

    [Fact]
    public void A_gdtf_pixel_bar_repeats_its_module_at_each_reference()
    {
        var xml = """
            <GDTF>
              <FixtureType Name="Bar" Manufacturer="Acme">
                <Geometries>
                  <Geometry Name="Body">
                    <GeometryReference Name="P1" Geometry="Pixel" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{-0.3,0,0,1}"><Break DMXOffset="1" DMXBreak="1"/></GeometryReference>
                    <GeometryReference Name="P2" Geometry="Pixel" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{-0.1,0,0,1}"><Break DMXOffset="4" DMXBreak="1"/></GeometryReference>
                    <GeometryReference Name="P3" Geometry="Pixel" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0.1,0,0,1}"><Break DMXOffset="7" DMXBreak="1"/></GeometryReference>
                    <GeometryReference Name="P4" Geometry="Pixel" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0.3,0,0,1}"><Break DMXOffset="10" DMXBreak="1"/></GeometryReference>
                  </Geometry>
                </Geometries>
                <DMXModes>
                  <DMXMode Name="12ch">
                    <DMXChannels>
                      <DMXChannel Offset="1" Geometry="Pixel"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
                      <DMXChannel Offset="2" Geometry="Pixel"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
                      <DMXChannel Offset="3" Geometry="Pixel"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
                    </DMXChannels>
                  </DMXMode>
                </DMXModes>
              </FixtureType>
            </GDTF>
            """;
        var r = FixtureImport.FromGdtfXml(Utf8(xml));
        var p = r.Profile!;
        Assert.Equal(12, p.ChannelCount(0));
        Assert.Equal(Roles.Blue, p.Channels(0)[11].Role);
        Assert.Empty(r.Warnings);
        // Four heads along the bar, centred, unrotated.
        Assert.Equal([-0.3, -0.1, 0.1, 0.3], p.Physical.Heads.Select(h => Math.Round(h[0], 6)));
        Assert.All(p.Physical.Heads, h => Assert.Equal([0.0, 0, 0, 0, 0], h.Skip(1).Select(v => Math.Round(v, 6))));
    }

    [Fact]
    public void Heads_come_from_the_geometries_the_red_channels_drive()
    {
        // Two cells, the second 0.2 m along GDTF's x and turned 90 degrees about its z (up), which is Godot's y.
        var xml = """
            <GDTF>
              <FixtureType Name="Duo" Manufacturer="Acme">
                <Geometries>
                  <Geometry Name="Body">
                    <Geometry Name="Cell1" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}"/>
                    <Geometry Name="Cell2" Position="{0,1,0,0}{-1,0,0,0}{0,0,1,0}{0.2,0,0,1}"/>
                  </Geometry>
                </Geometries>
                <DMXModes>
                  <DMXMode Name="6ch">
                    <DMXChannels>
                      <DMXChannel Offset="1" Geometry="Cell1"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
                      <DMXChannel Offset="2" Geometry="Cell1"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
                      <DMXChannel Offset="3" Geometry="Cell1"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
                      <DMXChannel Offset="4" Geometry="Cell2"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
                      <DMXChannel Offset="5" Geometry="Cell2"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
                      <DMXChannel Offset="6" Geometry="Cell2"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
                    </DMXChannels>
                  </DMXMode>
                </DMXModes>
              </FixtureType>
            </GDTF>
            """;
        var heads = FixtureImport.FromGdtfXml(Utf8(xml)).Profile!.Physical.Heads;
        Assert.Equal(2, heads.Count);
        Assert.Equal([0.0, 0, 0, 0, 0, 0], heads[0].Select(v => Math.Round(v, 6)));
        Assert.Equal([0.2, 0, 0, 0, 90, 0], heads[1].Select(v => Math.Round(v, 6)));
    }

    [Fact]
    public void Imports_fail_with_a_reason()
    {
        using var dir = new TempFolder();
        Assert.Equal("File not found.", FixtureImport.FromPath(dir.File("none.gdtf")).Error);
        File.WriteAllText(dir.File("x.txt"), "");
        Assert.Equal("Unsupported file type — need .gdtf or .json.", FixtureImport.FromPath(dir.File("x.txt")).Error);
        File.WriteAllText(dir.File("x.json"), """{ "hello": 1 }""");
        Assert.Equal("Unrecognised JSON (expected an Open Fixture Library fixture).", FixtureImport.FromPath(dir.File("x.json")).Error);
        File.WriteAllText(dir.File("x.gdtf"), "not a zip");
        Assert.Equal("Can't open .gdtf (not a ZIP archive?).", FixtureImport.FromPath(dir.File("x.gdtf")).Error);
        Assert.Equal("description.xml has no <FixtureType>.", FixtureImport.FromGdtfXml(Utf8("<GDTF/>")).Error);
        Assert.Equal("No <DMXMode> found.", FixtureImport.FromGdtfXml(Utf8("<GDTF><FixtureType Name='X'/></GDTF>")).Error);
    }

    [Fact]
    public void A_saved_profile_reads_back_as_itself()
    {
        using var dir = new TempFolder();
        var p = Profiles.DimmedRgb();
        File.WriteAllText(dir.File("p.json"), Json.Stringify(p.ToJson()));
        var r = FixtureImport.FromPath(dir.File("p.json"));
        Assert.True(Json.SameTree(p.ToJson(), r.Profile!.ToJson()));
    }

    [Fact]
    public void Roles_from_names()
    {
        Assert.Equal(Roles.Red, FixtureImport.RoleFor("ColorAdd_R"));
        Assert.Equal(Roles.GoboRotation, FixtureImport.RoleFor("Gobo1Pos"));
        Assert.Equal(Roles.Generic, FixtureImport.RoleFor("Span")); // not pan
        Assert.Equal(Roles.ColorWheel, FixtureImport.RoleFor("CTO"));
        Assert.Equal(Roles.Strobe, FixtureImport.RoleFor("Shutter1"));
        Assert.Equal("imported", FixtureImport.SafeIdHint("!!"));
        Assert.Equal("acme_par_64_rgb", FixtureImport.SafeIdHint("Acme Par-64/RGB"));
    }

    [Fact]
    public void Godot_strings_read_as_godot_did()
    {
        Assert.Equal(10, Gd.ToInt("10"));
        Assert.Equal(-3, Gd.ToInt(" -3"));
        Assert.Equal(1, Gd.ToInt("1.9"));
        Assert.Equal(0, Gd.ToInt("abc"));
        Assert.Equal(2.5, Gd.ToFloat("2.5deg"));
        Assert.Equal(0, Gd.ToFloat("x"));
        Assert.Equal("Auro Beam 150", Gd.Capitalize("auro beam 150"));
        Assert.Equal("American Dj", Gd.Capitalize("american_dj"));
        Assert.Equal("X 4 Bar", Gd.Capitalize("x4bar"));
        Assert.True(Gd.NaturalCompare("Par 9", "Par 10") < 0);
        Assert.True(Gd.NaturalCompare("apple", "Banana") < 0);
    }
}

using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.Tests.Fixtures;

/// <summary>
/// Custom profiles saved, edited, deleted and imported; the profile editor's text; and an edited profile going to the
/// fixtures patched with it (show_file_test.gd's _test_profile_update).
/// </summary>
public class ProfileStoreTests
{
    [Fact]
    public void A_new_profile_is_saved_and_read_back()
    {
        using var dir = new TempFolder();
        var store = new ProfileStore(dir.File("profiles"));
        var builtIns = store.Profiles.Count;
        var p = Profiles.DimmedRgb();
        p.Id = store.UniqueId(ProfileStore.SafeId("Acme Wash-1"));
        Assert.Equal("acme_wash_1", p.Id);
        store.Save(p);
        Assert.Same(p, store.Profiles[^1]);
        Assert.True(store.HasFile(p));
        Assert.Equal("acme_wash_1_2", store.UniqueId("acme_wash_1"));

        var again = new ProfileStore(dir.File("profiles"));
        Assert.Equal(builtIns + 1, again.Profiles.Count);
        Assert.True(Json.SameTree(p.ToJson(), again.Profiles[^1].ToJson()));
    }

    [Fact]
    public void An_edited_built_in_keeps_its_place_and_resets_on_delete()
    {
        using var dir = new TempFolder();
        var store = new ProfileStore(dir.File("profiles"));
        var original = store.Profiles[1];
        Assert.Equal($"'{original.Model}' is a built-in profile and can't be deleted.", store.Delete(original));

        var edited = original.Clone();
        edited.Model = "My Version";
        store.Save(edited);
        Assert.Same(edited, store.Profiles[1]);
        Assert.Equal("My Version", new ProfileStore(dir.File("profiles")).Profiles[1].Model);

        Assert.Equal("Reset 'My Version' to its built-in default.", store.Delete(edited));
        Assert.Equal(original.Model, store.Profiles[1].Model);
        Assert.False(store.HasFile(edited));
    }

    [Fact]
    public void A_custom_profile_is_deleted()
    {
        using var dir = new TempFolder();
        var store = new ProfileStore(dir.File("profiles"));
        var p = Profiles.Rgb();
        p.Id = "mine";
        store.Save(p);
        Assert.Equal("Deleted profile 'RGB'.", store.Delete(p));
        Assert.DoesNotContain(store.Profiles, x => x.Id == "mine");
        Assert.False(File.Exists(store.FilePath("mine")));
    }

    [Fact]
    public void An_imported_fixture_is_saved_with_an_id_of_its_own_and_its_models()
    {
        using var dir = new TempFolder();
        var store = new ProfileStore(dir.File("profiles"), dir.File("models"));
        var ofl = dir.File("bar.json");
        File.WriteAllText(
            ofl,
            """{ "name": "Bar", "availableChannels": { "Red": {}, "Green": {}, "Blue": {} }, "modes": [ { "name": "3ch", "channels": ["Red", "Green", "Blue", "Lost"] } ] }"""
        );
        var (p, message) = store.Import(ofl);
        Assert.Equal("bar", p!.Id);
        Assert.Equal("Imported 'Bar' — 1 mode(s), 4 ch.  1 approximation(s); check it in Edit...", message);
        var (p2, _) = store.Import(ofl);
        Assert.Equal("bar_2", p2!.Id);
        Assert.True(File.Exists(store.FilePath("bar_2")));

        Assert.Equal((null, "Import failed: File not found."), (store.Import(dir.File("nothing.gdtf")).Profile, store.Import(dir.File("nothing.gdtf")).Message));
    }

    [Fact]
    public void A_gdtfs_models_go_beside_it()
    {
        using var dir = new TempFolder();
        var store = new ProfileStore(dir.File("profiles"), dir.File("models"));
        var path = dir.File("spot.gdtf");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("description.xml").Open())
                s.Write(
                    System.Text.Encoding.UTF8.GetBytes(
                        """
                        <GDTF><FixtureType Name="Cube" Manufacturer="Acme">
                          <Geometries><Geometry Name="Body" Model="Body Model"/></Geometries>
                          <Models><Model Name="Body Model" File="body"/></Models>
                          <DMXModes><DMXMode Name="1ch"><DMXChannels><DMXChannel Offset="1"><LogicalChannel Attribute="Dimmer"/></DMXChannel></DMXChannels></DMXMode></DMXModes>
                        </FixtureType></GDTF>
                        """
                    )
                );
            using (var s = zip.CreateEntry("models/gltf/body.glb").Open())
                s.Write([9, 9]);
        }
        var p = store.Import(path).Profile!;
        Assert.Equal("acme_cube", p.Id);
        Assert.Equal("acme_cube", p.Geometry.Str("models_dir"));
        Assert.Equal("body_model.glb", p.Geometry.Get("models").Str("Body Model"));
        Assert.Equal([9, 9], File.ReadAllBytes(Path.Combine(dir.File("models"), "acme_cube", "body_model.glb")));
    }

    // ---------------------------------------------------------- editor text --

    [Fact]
    public void Ranges_as_text_and_back()
    {
        var slots = ProfileText.ParseRanges(" 0-9:Open, 10-19:Red:#f00, 20 - 29, junk, 30-255:: ");
        Assert.Equal([new(0, 9, "Open"), new(10, 19, "Red", "#f00"), new(20, 29, ""), new RangeSlot(30, 255, "", "")], slots);
        Assert.Equal("0-9:Open, 10-19:Red:#f00, 20-29, 30-255", ProfileText.FormatRanges(slots));
        Assert.Equal("5-6::#00f", ProfileText.FormatRanges([new(5, 6, "", "#00f")]));
    }

    [Fact]
    public void Gobo_pictures_stay_with_their_slots_through_an_edit()
    {
        var originals = new List<RangeSlot> { new(0, 63, "Star", "#ccc", "PNGDATA") };
        var edited = ProfileText.CarryImages(ProfileText.ParseRanges("0-31:star, 32-63:Moon"), originals);
        Assert.Equal(new RangeSlot(0, 31, "star", "#ccc", "PNGDATA"), edited[0]);
        Assert.Equal("", edited[1].Image);
    }

    [Fact]
    public void Heads_as_text_and_back()
    {
        Assert.Equal("-0.15, 0, 0\n0.15, 0, 0, 0, 45.5, 0", ProfileText.FormatHeads([[-0.15, 0, 0, 0, 0, 0], [0.15, 0, 0, 0, 45.5, 0]]));
        var (heads, error) = ProfileText.ParseHeads("-0.15, 0, 0\n\n0.15; 0; 0; 0; 45.5; 0\n");
        Assert.Equal("", error);
        Assert.Equal([[-0.15, 0, 0, 0, 0, 0], [0.15, 0, 0, 0, 45.5, 0]], heads!);
        Assert.Equal("Head line 2: \"x\" isn't a number.", ProfileText.ParseHeads("1,2,3\n1,x,3").Error);
        Assert.Equal("Head line 1 needs x, y, z (and optionally rot x, y, z) — it has 2 value(s).", ProfileText.ParseHeads("1,2").Error);
    }

    // ------------------------------------------------- the patch takes edits --

    static FixtureProfile GoboSpot(JsonArray goboRanges, int mode1Channels = 1)
    {
        var gobo = new JsonObject { ["name"] = "Gobo", ["role"] = "GOBO", ["ranges"] = goboRanges.DeepClone() };
        var m1 = new JsonArray(gobo.DeepClone());
        for (var i = 0; i < mode1Channels - 1; i++)
            m1.Add(new JsonObject { ["name"] = "Dimmer", ["role"] = "DIMMER" });
        var modes = new JsonArray(
            new JsonObject { ["name"] = "Basic", ["channels"] = new JsonArray(new JsonObject { ["name"] = "Dimmer", ["role"] = "DIMMER" }, gobo) },
            new JsonObject { ["name"] = "Gobo only", ["channels"] = m1 }
        );
        return new FixtureProfile("gobospot", "Gobo Spot", null, modes, new JsonObject { ["beam_deg"] = 15.0 });
    }

    static JsonArray Slots(params (int Lo, int Hi, string Label)[] slots) =>
        new([.. slots.Select(s => (JsonNode)new JsonObject { ["lo"] = s.Lo, ["hi"] = s.Hi, ["label"] = s.Label })]);

    [Fact]
    public void An_edited_profile_goes_to_its_patched_fixtures()
    {
        var show = new LightingShow();
        var old = Slots((0, 127, "Open"), (128, 255, "Star"));
        var a = show.Patch.Add(0, GoboSpot(old), 0, 0, "A");
        var b = show.Patch.Add(0, GoboSpot(old), 10, 1, "B");
        var par = show.Patch.Add(0, Profiles.Rgb(), 20, 0, "Par");
        show.Dmx.Universes[0].SetChannel(0, 200);
        show.Dmx.Universes[0].SetChannel(1, 140);
        show.Dmx.Universes[0].SetChannel(20, 90);
        var changes = 0;
        show.Patch.Changed += () => changes++;

        // The gobo's slots edited; mode 1 gets another channel (a new footprint).
        var edited = GoboSpot(Slots((0, 63, "Open"), (64, 191, "Star"), (192, 255, "Shake")), 2);
        edited.Physical.BeamDeg = 30;
        Assert.Equal((1, 1), show.Patch.UpdateProfile(edited));

        Assert.Equal(3, a.Profile.Channels(0)[1].Ranges.Count);
        Assert.NotSame(edited, a.Profile); // its own copy
        Assert.Equal((200, 140, 90), (show.Dmx.Universes[0].GetChannel(0), show.Dmx.Universes[0].GetChannel(1), show.Dmx.Universes[0].GetChannel(20)));
        Assert.Equal(1, b.Profile.ChannelCount(1)); // its channels kept…
        Assert.Equal(2, b.Profile.Channels(1)[0].Ranges.Count);
        Assert.Equal(30, b.Profile.Physical.BeamDeg); // …its physical side taken
        Assert.Equal("RGB", par.Profile.Model);
        Assert.Equal(1, changes);

        Assert.Equal(
            "Updated profile 'Gobo Spot' — 1 patched fixture updated; 1 needs a re-patch for the channel count change (beam and pan/tilt apply now).",
            ProfileText.UpdateMessage("Gobo Spot", 1, 1)
        );
        Assert.Equal("Updated profile 'Gobo Spot' — 2 patched fixtures updated.", ProfileText.UpdateMessage("Gobo Spot", 2, 0));
        Assert.Equal("Updated profile 'X'.", ProfileText.UpdateMessage("X", 0, 0));
    }
}

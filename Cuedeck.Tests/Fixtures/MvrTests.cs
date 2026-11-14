using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.Tests.Fixtures;

/// <summary>
/// My Virtual Rig (.mvr) import and export, as sLight's <c>MvrIO</c> does them: a ZIP of a scene description and the
/// GDTFs it names; Z up and millimetres there, Y up and metres here. sLight had no tests of its own for it.
/// </summary>
public sealed class MvrTests : IDisposable
{
    readonly TempFolder _dir = new();
    readonly LightingShow _show = new();

    public void Dispose()
    {
        _show.Dispose();
        _dir.Dispose();
    }

    static FixtureProfile Spot()
    {
        var p = new FixtureProfile(
            "spot",
            "Spot 250",
            modes: (JsonArray)Cuedeck.Common.Json.Parse(
                """
                [{"name": "Standard", "channels": [{"name": "Dimmer", "role": "DIMMER"}, {"name": "Pan", "role": "PAN"},
                  {"name": "Pan fine", "role": "PAN_FINE", "fine": true}, {"name": "Tilt", "role": "TILT"},
                  {"name": "Red", "role": "RED"}, {"name": "Green", "role": "GREEN"}, {"name": "Blue", "role": "BLUE"}]},
                 {"name": "Basic", "channels": [{"name": "Dimmer", "role": "DIMMER", "default": 0}]}]
                """
            )!,
            manufacturer: "Acme"
        );
        return p;
    }

    static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 0.002f, $"expected {expected}, got {actual}");

    [Fact]
    public void A_rig_exported_and_read_back()
    {
        var spot = Spot();
        _show.Patch.AddUniverse();
        _show.Patch.Add(0, spot, 0, 0, "Left", new Vector3(1, 4.5f, -2), new Vector3(90, 30, 0));
        _show.Patch.Add(1, spot, 10, 1, "Right", new Vector3(-1, 4, -3), new Vector3(90, 0, 0));
        var path = _dir.File("rig.mvr");
        Assert.Equal("Exported 2 fixtures to rig.mvr", Mvr.Export(path, _show));

        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.NotNull(zip.GetEntry("GeneralSceneDescription.xml"));
            Assert.NotNull(zip.GetEntry("gdtf/spot.gdtf")); // one GDTF per profile
            Assert.Equal(2, zip.Entries.Count);
        }

        var rig = Mvr.Import(path);
        Assert.Null(rig.Error);
        Assert.Empty(rig.Warnings);
        Assert.Equal(2, rig.Fixtures.Count);
        var (l, r) = (rig.Fixtures[0], rig.Fixtures[1]);
        Assert.Equal(("Left", 0, 0, 0), (l.Name, l.Universe, l.Start, l.Mode));
        Assert.Equal(("Right", 1, 10, 1), (r.Name, r.Universe, r.Start, r.Mode)); // its mode found by name
        Near(new Vector3(1, 4.5f, -2), l.Position);
        Near(new Vector3(-1, 4, -3), r.Position);
        Assert.Equal(30, l.Rotation.Y, 2); // only the heading comes back, as sLight's (to the export's four places)
        Assert.Equal(0, l.Rotation.X);
        Assert.Same(l.Profile, r.Profile); // one profile for both

        // The GDTF made for the profile reads back as it: its modes and channels' roles, the fine pan folded in.
        var p = l.Profile;
        Assert.Equal(["Standard", "Basic"], p.ModeNames);
        Assert.Equal(["DIMMER", "PAN", "PAN_FINE", "TILT", "RED", "GREEN", "BLUE"], p.Channels(0).Select(c => c.Role));
        Assert.Equal("Acme", p.Manufacturer);
    }

    /// <summary>
    /// An MVR from elsewhere: <paramref name="scene"/>'s fixtures and trusses, and these files beside it.
    /// </summary>
    string Rig(string scene, params (string Name, byte[] Bytes)[] files)
    {
        var path = _dir.File("from elsewhere.mvr");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var gsd = zip.CreateEntry("GeneralSceneDescription.xml");
        using (var w = new StreamWriter(gsd.Open(), Encoding.UTF8))
            w.Write($"""<?xml version="1.0" encoding="UTF-8"?><GeneralSceneDescription verMajor="1" verMinor="6"><Scene><Layers><Layer name="L"><ChildList>{scene}</ChildList></Layer></Layers></Scene></GeneralSceneDescription>""");
        foreach (var (name, bytes) in files)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(bytes);
        }
        return path;
    }

    [Fact]
    public void A_rig_from_elsewhere()
    {
        var gdtf = Mvr.Gdtf(Spot());
        var path = Rig(
            """
            <Fixture name="Wash 1"><Matrix>{1,0,0}{0,1,0}{0,0,1}{2000,3000,5000}</Matrix><GDTFSpec>spot.gdtf</GDTFSpec><GDTFMode>Basic</GDTFMode>
             <Addresses><Address break="0">1025</Address></Addresses></Fixture>
            <Fixture name="Mystery"><Matrix>{1,0,0}{0,1,0}{0,0,1}{0,0,0}</Matrix><GDTFSpec>missing.gdtf</GDTFSpec><GDTFMode>X</GDTFMode>
             <Addresses><Address>1</Address></Addresses></Fixture>
            <Truss name="Front"><Matrix>{1,0,0}{0,1,0}{0,0,1}{0,-4000,6000}</Matrix></Truss>
            """,
            ("spot.gdtf", gdtf) // at the archive's root rather than in gdtf/
        );
        var rig = Mvr.Import(path);
        var f = Assert.Single(rig.Fixtures);
        // Address 1025: the third universe's first channel.
        Assert.Equal(("Wash 1", 2, 0, 1), (f.Name, f.Universe, f.Start, f.Mode));
        Near(new Vector3(2, 5, -3), f.Position); // MVR's (x, y, z) mm is (x, z, -y) m here
        Assert.Contains(rig.Warnings, w => w.Contains("Mystery"));
        Near(new Vector3(0, 6, 4), Assert.Single(rig.Trusses));

        // Put into the show: universes enough for it, its profile kept, its fixtures patched, its truss hung.
        var store = new ProfileStore(_dir.File("profiles"));
        var said = Mvr.Apply(rig, _show, store, "from elsewhere.mvr");
        Assert.Equal("Imported 1 fixtures, 1 trusses from from elsewhere.mvr  (1 notes — see the log)", said);
        Assert.Equal(3, _show.Patch.Universes.Count);
        var patched = Assert.Single(_show.Patch.All);
        Assert.Equal(("Wash 1", 2, 0, 1), (patched.Name, patched.Universe, patched.Start, patched.Mode));
        Assert.Contains(store.Profiles, p => p.Id == f.Profile.Id);
        var truss = Assert.Single(_show.Visualizer.Trusses);
        Assert.Equal(3, truss.Length);
    }

    [Fact]
    public void Files_that_arent_rigs()
    {
        File.WriteAllText(_dir.File("junk.mvr"), "not a zip");
        Assert.Equal("Can't open .mvr (not a ZIP?).", Mvr.Import(_dir.File("junk.mvr")).Error);
        using (var zip = ZipFile.Open(_dir.File("empty.mvr"), ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");
        Assert.Equal("No GeneralSceneDescription.xml in the archive.", Mvr.Import(_dir.File("empty.mvr")).Error);
        Assert.Equal("MVR had no usable fixtures.", Mvr.Apply(Mvr.Import(Rig("")), _show, new ProfileStore(_dir.File("p")), "x.mvr"));
    }
}

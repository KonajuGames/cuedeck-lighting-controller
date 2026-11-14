using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Show;
using Cuedeck.Visualizer;

namespace Cuedeck.Tests.Visualizer;

/// <summary>
/// What the 3D pass is given to draw, worked out without the GPU: the shapes and their places.
/// </summary>
public sealed class SceneFrameTests : IDisposable
{
    readonly LightingShow _show = new();

    public void Dispose() => _show.Dispose();

    [Fact]
    public void The_shapes_are_closed_and_their_normals_unit_length()
    {
        foreach (var kind in Enum.GetValues<MeshKind>())
        {
            var m = MeshData.Of(kind);
            Assert.Equal(0, m.Indices.Count % 3);
            Assert.All(m.Indices, i => Assert.InRange(i, 0u, (uint)m.Positions.Count - 1));
            Assert.All(m.Normals, n => Assert.Equal(1, n.Length(), 4));
        }
        var box = MeshData.Box();
        Assert.Equal(36, box.Indices.Count);
        Assert.All(box.Positions, p => Assert.Equal(0.5f, Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z)))));
    }

    [Fact]
    public void The_room_trusses_and_fixture_bodies()
    {
        var mover = _show.Patch.Add(0, Profiles.Simple("mh", "Mover", ("Pan", "PAN"), ("Tilt", "TILT"), ("Dimmer", "DIMMER")), 0);
        var par = _show.Patch.Add(0, Profiles.Simple("par", "Par", ("Dimmer", "DIMMER")), 3);
        var viz = _show.Visualizer;
        var truss = viz.AddTruss();
        var rigs = new Dictionary<PatchedFixture, FixtureRig> { [mover] = new(mover.Profile, 0), [par] = new(par.Profile, 0) };
        var colours = new Dictionary<string, float4> { [par.Profile.FullName] = new(1, 0, 0, 1) };
        var f = SceneFrame.Build(viz, _show.Patch, rigs, colours, 1.5f);

        // The floor and back wall, the room's size.
        Assert.Equal(MeshKind.Plane, f.Items[0].Mesh);
        Assert.Equal(26, Vector3.TransformNormal(Vector3.UnitX, f.Items[0].World).Length(), 4);
        Assert.Equal(-10, Vector3.Transform(Vector3.Zero, f.Items[1].World).Z, 4);
        // The truss, selected: lit.
        var t = f.Items[2];
        Assert.Equal(MeshKind.Box, t.Mesh);
        Assert.Equal(4, Vector3.TransformNormal(Vector3.UnitX, t.World).Length(), 4);
        Assert.True(t.Glow > 0);
        // A moving head: base, two arms and a head; a par: a can, in its model's colour.
        Assert.Single(f.Items, i => i.Mesh == MeshKind.Base);
        Assert.Single(f.Items, i => i.Mesh == MeshKind.Cylinder && i.Colour == new float4(1, 0, 0, 1));
        Assert.Equal(4, f.Items.Count(i => i.Mesh is MeshKind.Box)); // the truss, two arms and the head
        Assert.DoesNotContain(f.Items, i => i.Mesh == MeshKind.Ring);
        viz.Select(par);
        f = SceneFrame.Build(viz, _show.Patch, rigs, colours, 1.5f);
        Assert.Single(f.Items, i => i.Mesh == MeshKind.Ring); // its selection ring

        // The camera, and the room's backdrop and haze.
        Assert.Equal(viz.Camera.Position, f.CameraPosition);
        Assert.Equal((float)(viz.Haze * VizScene.HAZE_MAX), f.HazeDensity, 6);
        viz.WorkLight = false;
        Assert.Equal(new float4(0, 0, 0, 0), SceneFrame.Build(viz, _show.Patch, rigs, colours, 1.5f).WorkLight);
    }

    [Fact]
    public void Each_lit_head_is_a_spotlight()
    {
        var par = _show.Patch.Add(0, Profiles.Simple("rgb", "RGB par", ("Dimmer", "DIMMER"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE")), 0, position: new Vector3(0, 4, -2));
        var rig = new FixtureRig(par.Profile, 0);
        var rigs = new Dictionary<PatchedFixture, FixtureRig> { [par] = rig };
        var output = new byte[512];
        rig.Update(DmxRender.Evaluate(par.Profile, 0, output, 0), 0.1);
        Assert.Empty(SceneFrame.Build(_show.Visualizer, _show.Patch, rigs, new Dictionary<string, float4>(), 1.5f).Lights); // dark: none

        output[0] = 255; // full, red
        output[1] = 255;
        rig.Update(DmxRender.Evaluate(par.Profile, 0, output, 0), 0.1);
        var light = Assert.Single(SceneFrame.Build(_show.Visualizer, _show.Patch, rigs, new Dictionary<string, float4>(), 1.5f).Lights);
        // Hung pointing down (tilted 90°), from the front of its can.
        Assert.True(Vector3.Distance(light.Position, new Vector3(0, 3.87f, -2)) < 1e-3f, $"{light.Position}");
        Assert.True(Vector3.Dot(light.Direction, -Vector3.UnitY) > 0.9999f, $"{light.Direction}");
        Assert.Equal(SpotLight.ENERGY, light.Colour.X, 3);
        Assert.Equal(0, light.Colour.Y, 6);
        Assert.Equal((float)Math.Cos(rig.HalfAngle * Math.PI / 180), light.CosCone, 6);

        output[0] = 128; // half: half the energy, in linear light
        rig.Update(DmxRender.Evaluate(par.Profile, 0, output, 0), 0.1);
        light = Assert.Single(SceneFrame.Build(_show.Visualizer, _show.Patch, rigs, new Dictionary<string, float4>(), 1.5f).Lights);
        Assert.Equal(SpotLight.ENERGY * 128 / 255.0, light.Colour.X, 1);
    }
}

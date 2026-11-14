using System.Numerics;
using System.Text.Json.Nodes;
using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Show;
using Cuedeck.Visualizer;

namespace Cuedeck.Tests.Visualizer;

/// <summary>
/// The visualizer's scene without the screen: the orbit camera, picking by ray, and what's saved with the show (as
/// sLight's <c>viz</c>).
/// </summary>
public sealed class VizSceneTests : IDisposable
{
    static readonly float2 Size = new(900, 600);

    readonly LightingShow _show = new();
    readonly VizScene _viz;

    public VizSceneTests() => _viz = new VizScene(_show.Patch);

    public void Dispose() => _show.Dispose();

    /// <summary>
    /// Where a point in the room lands on a <see cref="Size"/> view, by the camera's own matrices.
    /// </summary>
    float2 Project(Vector3 p)
    {
        var clip = Vector4.Transform(new Vector4(p, 1), _viz.Camera.View * _viz.Camera.Projection(Size.X / Size.Y));
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        return new float2((ndc.X + 1) / 2 * Size.X, (1 - ndc.Y) / 2 * Size.Y);
    }

    [Fact]
    public void The_orbit_camera()
    {
        var cam = _viz.Camera;
        Assert.Equal(["Orbit", "Front", "Audience", "FOH high", "Top"], _viz.Views.Select(v => v.Name));
        Assert.Equal(new Vector3(0, 1.5f, -3), cam.Target);
        Assert.Equal(15, cam.Distance);
        // It looks at its target from its distance.
        Assert.Equal(15, Vector3.Distance(cam.Position, cam.Target), 4);
        var (origin, dir) = cam.Ray(Size / 2, Size);
        Assert.True(Vector3.Dot(dir, Vector3.Normalize(cam.Target - origin)) > 0.9999f); // the middle of the view: at the target
        var target = Project(cam.Target);
        Assert.True(Math.Abs(target.X - 450) < 0.5 && Math.Abs(target.Y - 300) < 0.5, $"{target}");
        // A ray through any point of the view comes back to that point.
        var (o2, d2) = cam.Ray(new float2(700, 120), Size);
        var back = Project(o2 + d2 * 10);
        Assert.True(Math.Abs(back.X - 700) < 0.5 && Math.Abs(back.Y - 120) < 0.5, $"{back}");

        // Orbiting, panning and zooming, held in range.
        cam.Orbit(new float2(0, 1000));
        Assert.Equal(1.45, cam.Pitch, 6);
        cam.Zoom(1000);
        Assert.Equal(120, cam.Distance);
        cam.Zoom(0.0001);
        Assert.Equal(2, cam.Distance);
        var before = cam.Target;
        cam.Pan(new float2(100, 0));
        Assert.NotEqual(before, cam.Target);

        // Saved views: applied, updated, added, deleted (never the last).
        _viz.ApplyView(4);
        Assert.True(cam.Ortho);
        Assert.Equal(1.4, cam.Pitch, 6);
        var (_, topDir) = cam.Ray(Size / 2, Size);
        Assert.True(topDir.Y < -0.95f); // looking down
        _viz.SaveViewAs("Mine");
        Assert.Equal("Mine", _viz.Views[^1].Name);
        cam.Orbit(new float2(50, 0));
        _viz.UpdateView(5);
        Assert.Equal(cam.Yaw, _viz.Views[5].Yaw);
        _viz.DeleteView(5);
        Assert.Equal(5, _viz.Views.Count);
    }

    [Fact]
    public void Picking_by_ray()
    {
        var profile = Profiles.Simple("par", "Par", ("Dimmer", "DIMMER"));
        var a = _show.Patch.Add(0, profile, 0, position: new Vector3(-2, 4, -2));
        var b = _show.Patch.Add(0, profile, 1, position: new Vector3(2, 4, -2));
        var truss = _viz.AddTruss();
        truss.Position = new Vector3(0, 6, -6);
        truss.Length = 8;
        _viz.Select(null);
        Assert.Same(a, _viz.Pick(Project(new Vector3(-2, 4.15f, -2)), Size));
        Assert.Same(b, _viz.Pick(Project(new Vector3(2, 4.15f, -2)), Size));
        Assert.Same(truss, _viz.Pick(Project(new Vector3(3, 6, -6)), Size)); // along its length
        Assert.Null(_viz.Pick(new float2(5, 595), Size));

        // Pressing on a fixture selects it and drags it; on nothing, orbits, and a click there clears the selection.
        _viz.BeginDrag(Project(new Vector3(-2, 4.15f, -2)), Size, pan: false);
        Assert.Same(a, _viz.Selected);
        _viz.DragTo(Project(new Vector3(-1, 4.15f, -2)), Size);
        _viz.EndDrag();
        Assert.True(a.Position.X > -1.9f);
        var yaw = _viz.Camera.Yaw;
        _viz.BeginDrag(new float2(5, 595), Size, pan: false);
        _viz.DragTo(new float2(55, 595), Size);
        _viz.EndDrag();
        Assert.NotEqual(yaw, _viz.Camera.Yaw); // orbited
        Assert.Same(a, _viz.Selected); // a drag isn't a click
        _viz.BeginDrag(new float2(5, 595), Size, pan: false);
        _viz.EndDrag();
        Assert.Null(_viz.Selected);
    }

    [Fact]
    public void Saved_with_the_show_as_sLights_viz()
    {
        var d = _viz.ToJson();
        Assert.Equal("Black Box", d.Str("room"));
        Assert.Equal(0.3, d.Num("haze"), 6); // the room's haze
        Assert.True(d.Bool("work_light"));
        Assert.False(d.Bool("shadows"));
        Assert.Equal([0, 1.5, -3, 0.4, 0.35, 15], d.Arr("cam").Select(n => Math.Round(Json.AsDouble(n), 6)));
        Assert.Equal(5, d.Arr("views").Count);
        Assert.Equal("Orbit", d.Arr("views")[0].Str("name"));

        // A show from sLight.
        var saved = Json.Parse(
            """
            {"room": "Club", "haze": 0.5, "work_light": false, "shadows": true,
             "cam": [1.0, 2.0, 3.0, 0.1, 0.2, 9.0],
             "views": [{"name": "Only", "target": [0, 1, 2], "yaw": 0.5, "pitch": 0.25, "dist": 12.0, "fov": 40.0, "ortho": true}],
             "trusses": [[0.0, 5.0, -4.0, 6.0, 90.0]],
             "props": [{"path": "C:/nowhere/set.glb", "pos": [1, 0, 2], "scale": 2.0, "rot_y": 45.0}]}
            """
        )!;
        _viz.FromJson(saved);
        Assert.Equal(("Club", 0.5, false, true), (_viz.Room.Name, _viz.Haze, _viz.WorkLight, _viz.Shadows));
        Assert.Equal(new Vector3(1, 2, 3), _viz.Camera.Target);
        Assert.Equal(9, _viz.Camera.Distance);
        Assert.Equal("Only", Assert.Single(_viz.Views).Name);
        var t = Assert.Single(_viz.Trusses);
        Assert.Equal((new Vector3(0, 5, -4), 6.0, 90.0), (t.Position, t.Length, t.Heading));
        var p = Assert.Single(_viz.Props); // kept, though its model isn't there
        Assert.Equal(("C:/nowhere/set.glb", 2.0, 45.0), (p.Path, p.Scale, p.Heading));
        Assert.True(Json.SameTree(saved, _viz.ToJson()), Json.Stringify(_viz.ToJson()));

        // Rooms: each its own haze; an unknown one leaves it as it was.
        _viz.SetRoom("Arena");
        Assert.Equal(0.008 / VizScene.HAZE_MAX, _viz.Haze, 6);
        _viz.FromJson(Json.Parse("""{"room": "Moon"}""")!);
        Assert.Equal("Arena", _viz.Room.Name);

        // In the show file: written, and read back.
        _viz.SetRoom("Club");
        _show.Visualizer.FromJson(_viz.ToJson());
        var file = ShowFile.ToJson(_show);
        Assert.Equal("Club", file.Obj("viz").Str("room"));
        using var again = new LightingShow();
        ShowFile.Apply(again, file);
        Assert.Equal("Club", again.Visualizer.Room.Name);
    }
}

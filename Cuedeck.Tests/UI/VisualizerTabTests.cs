using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Input;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Visualizer;
using Cuedeck.Visualizer;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The 3D Visualizer tab, driven as someone would (the picture itself is the GPU's, so isn't here): picking a fixture
/// on the view, its properties, typing a position, Undo and Redo by button and by Ctrl+Z / Ctrl+Y over the view (the
/// rest of sLight's tests/visualizer_undo_test.gd), trusses, the room, the views and Hide UI.
/// </summary>
public sealed class VisualizerTabTests : ConsoleHarness
{
    readonly VisualizerView _viz;
    readonly PatchedFixture _a;

    public VisualizerTabTests()
    {
        _a = Show.Patch.Add(0, Profiles.Simple("par", "Par", ("Dimmer", "DIMMER")), 0, name: "A", position: new Vector3(1, 4, -2));
        Show.Patch.RaiseChanged();
        _viz = Screen.Console.Visualizer;
        _viz.Root = Ui;
        Click(Find<Button>(b => b.Text == "3D Visualizer"));
        Frame();
    }

    VizScene Scene => Show.Visualizer;

    /// <summary>
    /// Where a point in the room is on screen, by the camera and the view's size.
    /// </summary>
    float2 OnScreen(Vector3 p)
    {
        var size = _viz.Surface3D.Size;
        var clip = Vector4.Transform(new Vector4(p, 1), Scene.Camera.View * Scene.Camera.Projection(size.X / size.Y));
        return _viz.Surface3D.ScreenPosition + new float2((clip.X / clip.W + 1) / 2 * size.X, (1 - clip.Y / clip.W) / 2 * size.Y);
    }

    void TypeInto(NumberBox box, string text)
    {
        Click(All(box).OfType<TextBox>().First());
        Ui.KeyDown(Key.A, Modifiers.Control);
        Type(text);
        Ui.KeyDown(Key.Enter, Modifiers.None);
        Frame();
    }

    [Fact]
    public void Picking_typing_and_undoing()
    {
        Assert.True(_viz.IsShown());
        Assert.False(_viz.Properties.Visible); // nothing selected
        Assert.False(_viz.UndoButton.Enabled || _viz.RedoButton.Enabled);
        Assert.True(_viz.Rigs.ContainsKey(_a)); // its rig follows the patch

        // A click on the fixture selects it: its properties show.
        ClickAt(OnScreen(_a.Position + new Vector3(0, 0.15f, 0)));
        Assert.Same(_a, Scene.Selected);
        Assert.True(_viz.Properties.Visible);
        Assert.StartsWith("A   (U1 ch 1-1)", _viz.PropertiesTitle);
        Assert.Equal(1, _viz.XBox.Value, 6);

        // Typing X moves it, an undoable step; Enter lets go of the box, so Ctrl+Z over the view is the move's.
        TypeInto(_viz.XBox, "3");
        Assert.Equal(3, _a.Position.X, 5);
        Assert.True(_viz.UndoButton.Enabled);
        Assert.Null(Ui.Focused);
        Ui.MouseMove(_viz.Surface3D.ScreenPosition + new float2(20, 400));
        Assert.True(_viz.KeyDown(Key.Z, Modifiers.Control, Ui.Mouse));
        Frame();
        Assert.Equal(1, _a.Position.X, 5);
        Assert.Equal(1, _viz.XBox.Value, 6); // the box too
        Assert.True(_viz.KeyDown(Key.Y, Modifiers.Control, Ui.Mouse));
        Frame();
        Assert.Equal(3, _a.Position.X, 5);
        Ui.MouseMove(new float2(5, 5)); // not over the view: not its keys
        Assert.False(_viz.KeyDown(Key.Z, Modifiers.Control, Ui.Mouse));
        Click(_viz.UndoButton);
        Assert.Equal(1, _a.Position.X, 5);
        Click(_viz.RedoButton);
        Assert.Equal(3, _a.Position.X, 5);

        // Its tilt.
        TypeInto(_viz.TiltBox, "45");
        Assert.Equal(new Vector3(45, 0, 0), _a.Rotation);

        // A click on nothing clears the selection.
        ClickAt(_viz.Surface3D.ScreenPosition + new float2(20, _viz.Surface3D.Size.Y - 20));
        Assert.Null(Scene.Selected);
        Assert.False(_viz.Properties.Visible);
    }

    [Fact]
    public void Trusses_rooms_views_and_hiding_the_controls()
    {
        Click(Find<Button>(b => b.Text == "Add Truss"));
        var truss = Assert.Single(Scene.Trusses);
        Assert.Same(truss, Scene.Selected);
        Assert.Equal("Truss", _viz.PropertiesTitle);
        TypeInto(_viz.SizeBox, "6");
        Assert.Equal(6, truss.Length, 6);
        Click(Find<Button>(b => b.Text == "Delete" && All(_viz.Properties).Contains(b)));
        Assert.Empty(Scene.Trusses);

        // A model placed as a set piece: selected, its scale and heading to set.
        var model = Dir.File("set.glb");
        File.WriteAllBytes(model, Cuedeck.Tests.Visualizer.GltfModelTests.Triangle());
        _viz.AskForModel = () => model;
        Click(Find<Button>(b => b.Text == "Load Model…"));
        var prop = Assert.Single(Scene.Props);
        Assert.Same(prop, Scene.Selected);
        Assert.Equal("Model: set.glb", _viz.PropertiesTitle);
        TypeInto(_viz.SizeBox, "2");
        Assert.Equal(2, prop.Scale, 6);

        // MVR import and export say how they went on the status line.
        _viz.ImportMvr = () => "Imported 2 fixtures, 0 trusses from rig.mvr";
        Click(Find<Button>(b => b.Text == "Import MVR…"));
        Assert.Equal("Imported 2 fixtures, 0 trusses from rig.mvr", Screen.StatusText);
        _viz.ExportMvr = () => ""; // cancelled: nothing said
        Click(Find<Button>(b => b.Text == "Export MVR…"));
        Assert.Equal("Imported 2 fixtures, 0 trusses from rig.mvr", Screen.StatusText);

        Choose(Find<Dropdown<string>>(d => d.Selected == "Black Box"), "Club");
        Assert.Equal("Club", Scene.Room.Name);
        Choose(Find<Dropdown<int>>(_ => true), "Top");
        Assert.True(Scene.Camera.Ortho);
        Assert.True(Find<Checkbox>(c => c.Text == "Ortho").Checked);

        Click(Find<Button>(b => b.Text == "Hide UI"));
        Assert.False(_viz.Toolbar.Visible);
        Click(Find<Button>(b => b.Text == "Show UI"));
        Assert.True(_viz.Toolbar.Visible);

        // Off the tab, nothing's drawn.
        Click(Find<Button>(b => b.Text == "Patch"));
        Assert.False(_viz.IsShown());
        Assert.Null(((Cuedeck.Rendering.IScene3DSource)_viz).SceneSize);
    }

    [Fact]
    public void Screenshot_and_record()
    {
        var source = (Cuedeck.Rendering.IScene3DSource)_viz;
        var opened = new List<string>();
        _viz.RenderFolder = Dir.File("render");
        _viz.OpenFolder = opened.Add;
        Assert.False(source.CaptureThisFrame()); // nothing wanted

        // Screenshot: the next frame's picture, saved as a PNG in the render folder, which is shown.
        Click(Find<Button>(b => b.Text == "Screenshot"));
        Assert.True(source.CaptureThisFrame());
        Assert.False(source.CaptureThisFrame()); // just the one
        var size = _viz.Surface3D.Size;
        var (w, h) = ((int)size.X, (int)size.Y);
        source.Captured(w, h, new byte[w * h * 4]);
        var shot = Assert.Single(Directory.GetFiles(_viz.RenderFolder, "shot_*.png"));
        Assert.Equal(w, Cuedeck.UI.Png.Decode(File.ReadAllBytes(shot)).Width);
        Assert.StartsWith("Saved shot_", Screen.StatusText);
        Assert.Equal([_viz.RenderFolder], opened);

        // Record: frames as they're due, then Stop finishes the MP4 (silent here: no sound to capture).
        Click(Find<Button>(b => b.Text == "Record"));
        Assert.True(_viz.IsRecording);
        Assert.Equal("Stop", Find<Button>(b => b.Text is "Stop").Text);
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(40); // over a thirtieth of a second
            Frame();
            if (source.CaptureThisFrame())
                source.Captured(w, h, new byte[w * h * 4]);
        }
        Click(Find<Button>(b => b.Text == "Stop"));
        Assert.False(_viz.IsRecording);
        var rec = Assert.Single(Directory.GetFiles(_viz.RenderFolder, "rec_*.mp4"));
        Assert.True(new FileInfo(rec).Length > 1000);
        Assert.Matches(@"^Saved rec_.*\.mp4 \(\d+ frames", Screen.StatusText);
    }
}

using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Visualizer;
using Cuedeck.Visualizer;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The 3D Visualizer, docked on the right of the window, driven as someone would (the picture itself is the GPU's, so isn't here): picking a fixture
/// on the view, its properties, typing a position, Undo and Redo by button and by Ctrl+Z / Ctrl+Y over the view (the
/// rest of sLight's tests/visualizer_undo_test.gd), trusses, the room, the views, Hide UI, and folding it away.
/// </summary>
public sealed class VisualizerTabTests : ConsoleHarness
{
    readonly VisualizerView _viz;
    readonly PatchedFixture _a;

    public VisualizerTabTests()
    {
        _a = Show.Patch.Add(0, Profiles.Simple("par", "Par", ("Dimmer", "DIMMER")), 0, name: "A", position: new Vector3(1, 4, -2));
        Show.Patch.RaiseChanged();
        _viz = Screen.Visualizer;
        _viz.Root = Ui;
        Screen.VizDock.Collapsed = false;
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

        Choose(All(_viz).OfType<Dropdown<string>>().First(d => d.Selected == "Black Box"), "Club");
        Assert.Equal("Club", Scene.Room.Name);
        Choose(All(_viz).OfType<Dropdown<int>>().First(), "Top");
        Assert.True(Scene.Camera.Ortho);
        Assert.True(Find<Checkbox>(c => c.Text == "Ortho").Checked);

        Click(Find<Button>(b => b.Text == "Hide UI"));
        Assert.False(_viz.Toolbar.Visible);
        Click(Find<Button>(b => b.Text == "Show UI"));
        Assert.True(_viz.Toolbar.Visible);

        // Folded away, nothing's drawn.
        Click(Screen.VizDock.Fold);
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
        Assert.Equal(w, Png.Decode(File.ReadAllBytes(shot)).Width);
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

    [Fact]
    public void Popping_out_and_docking()
    {
        var asked = new List<string>();
        _viz.PopOutRequested += () => asked.Add("out");
        _viz.DockRequested += () => asked.Add("dock");
        Click(Find<Button>(b => b.Text == "Pop Out"));
        Assert.Equal(["out"], asked);

        // Gone to its own window: the dock gives the console its room; Dock to Main asks to come back.
        var docked = Screen.Console.Size.X;
        Screen.VizDock.Release();
        _viz.Floating = true;
        Frame();
        Assert.DoesNotContain(_viz, All(Screen));
        Assert.Equal(Screen.ScreenSize.X, Screen.Console.Size.X);
        Assert.Equal("Dock to Main", All(_viz).OfType<Button>().Single(b => b.Text is "Dock to Main" or "Pop Out").Text);
        Screen.VizDock.Take();
        _viz.Floating = false;
        Frame();
        Assert.Contains(_viz, All(Screen)); // back in the dock, showing
        Assert.Equal(docked, Screen.Console.Size.X);
        Assert.True(_viz.IsShown());
    }

    [Fact]
    public void It_sits_to_the_right_of_every_view_and_folds_away()
    {
        Assert.DoesNotContain(All(Screen.Console).OfType<Button>(), b => b.Text == "3D Visualizer"); // no tab of its own
        Assert.True(_viz.ScreenPosition.X >= Screen.Console.ScreenPosition.X + Screen.Console.Size.X);
        Assert.True(_viz.IsShown());

        // The strip's button folds it away to the right, giving the console the room; again brings it back.
        var console = Screen.Console.Size.X;
        Click(Screen.VizDock.Fold);
        Assert.True(Screen.VizDock.Collapsed);
        Assert.False(_viz.IsShown());
        Assert.False(_viz.Visible);
        Assert.Equal(Screen.ScreenSize.X - VizDock.STRIP, Screen.Console.Size.X);
        Assert.Equal("Show the 3D Visualizer", Screen.VizDock.Fold.Tooltip);
        Click(Screen.VizDock.Fold);
        Assert.False(Screen.VizDock.Collapsed);
        Assert.True(_viz.IsShown());
        Assert.Equal(console, Screen.Console.Size.X);
    }

    [Fact]
    public void Dragging_the_strip_resizes_it()
    {
        var strip = Screen.VizDock.Strip;
        var grab = new float2(strip.ScreenPosition.X + 4, strip.ScreenPosition.Y + 20); // above the fold button
        Ui.MouseMove(grab);
        Assert.Same(strip, Ui.Hovered);
        Assert.Equal(Cursor.ResizeHorizontal, strip.Cursor);
        var wide = _viz.Size.X;

        // 200 to the left: the visualizer 200 wider, the console 200 narrower.
        var console = Screen.Console.Size.X;
        Ui.MouseButton(MouseButton.Left, down: true);
        Ui.MouseMove(grab - new float2(200, 0));
        Frame();
        Ui.MouseButton(MouseButton.Left, down: false);
        Frame();
        Assert.Equal(wide + 200, _viz.Size.X);
        Assert.Equal(console - 200, Screen.Console.Size.X);
        Assert.Equal((Screen.VizDock.Size.X) / ScreenSize.X, Screen.VizDock.Share, 3);

        // Never so wide the view's squeezed, nor narrower than its least.
        Ui.MouseMove(strip.ScreenPosition + new float2(4, 20));
        Ui.MouseButton(MouseButton.Left, down: true);
        Ui.MouseMove(new float2(0, 20));
        Frame();
        Assert.Equal(VizDock.MIN_VIEW, Screen.VizDock.ScreenPosition.X);
        Ui.MouseMove(new float2(ScreenSize.X, 20));
        Frame();
        Ui.MouseButton(MouseButton.Left, down: false);
        Frame();
        Assert.Equal(VizDock.MIN_WIDTH, Screen.VizDock.Size.X);

        // Folded away, there's nothing to drag.
        Click(Screen.VizDock.Fold);
        Assert.False(strip.IsInteractive);
    }

    [Fact]
    public void Its_toolbar_wraps_where_its_narrow()
    {
        // Every button within the view, clear of Hide UI, however narrow the dock.
        Screen.ScreenSize = new float2(1024, 640);
        Frame();
        var right = All(_viz).OfType<Button>().Single(b => b.Text == "Hide UI").ScreenPosition.X;
        foreach (var button in All(_viz.Toolbar).OfType<Button>())
            Assert.True(button.ScreenPosition.X + button.Size.X <= right, button.Text);
    }

    [Fact]
    public void Where_the_visualizer_was_is_kept_between_runs()
    {
        var path = Dir.File("ui_state.json");
        Assert.Equal(new VizWindowState(), VizWindowState.Load(path)); // nothing kept: docked, controls shown
        new VizWindowState(true, (100, 200, 1000, 620), false).Save(path);
        Assert.Equal(new VizWindowState(true, (100, 200, 1000, 620), false), VizWindowState.Load(path));
        // sLight's form.
        var d = Cuedeck.Common.Json.Parse(File.ReadAllText(path));
        Assert.Equal([100, 200, 1000, 620], d.Arr("rect").Select(n => Cuedeck.Common.Json.AsInt(n)));
        Assert.True(Cuedeck.Common.Json.AsBool(d.Get("floating")));
        new VizWindowState(false, (1, 2, 3, 4), true).Save(path);
        Assert.Null(VizWindowState.Load(path).Rect); // docked: no window to remember
        new VizWindowState(Collapsed: true, DockShare: 0.5f).Save(path);
        Assert.True(VizWindowState.Load(path).Collapsed); // folded away in the dock
        Assert.Equal(0.5f, VizWindowState.Load(path).DockShare); // and half the window wide when it isn't
    }
}

using System.Diagnostics;
using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Input;
using Cuedeck.Rendering;
using Cuedeck.Show;
using Cuedeck.UI.Console;
using Cuedeck.Visualizer;

namespace Cuedeck.UI.Visualizer;

/// <summary>
/// The 3D Visualizer tab: the room with the patched fixtures, drawn by the renderer's 3D pass (<see cref="Scene3D"/>)
/// and played live from each universe's output. Drag on nothing to orbit, Shift-drag or middle-drag to slide, the
/// wheel to move in and out; press on a fixture, truss or set piece to select it and drag it on the floor, or type
/// its position and turn below. Over it, the toolbar (haze, work light, shadows and room; the camera's views, field
/// of view and Ortho; Undo, Redo, Add Truss, Load Model…, Auto-arrange, and MVR import and export) and Hide UI. sLight's <c>VisualizerPanel</c>; its
/// scene is <see cref="VizScene"/>.
/// </summary>
sealed class VisualizerView : Region, IScene3DSource, IPointerHook
{
    static readonly float4 OverlayBackground = Colors.Hex(0x14161C, 0.88f);

    readonly LightingShow _show;
    readonly Surface _surface;
    readonly Panel _toolbar;
    readonly Panel _props;
    readonly Button _uiToggle;
    readonly Slider _haze;
    readonly Checkbox _workLight;
    readonly Checkbox _shadows;
    readonly Dropdown<string> _room;
    readonly Dropdown<int> _view;
    readonly NumberBox _fov;
    readonly Checkbox _ortho;
    readonly Button _undo;
    readonly Button _redo;
    readonly Label _propTitle;
    readonly NumberBox _x;
    readonly NumberBox _y;
    readonly NumberBox _z;
    readonly Row _turnRow;
    readonly NumberBox _heading;
    readonly NumberBox _tilt;
    readonly Row _sizeRow;
    readonly Label _sizeLabel;
    readonly NumberBox _size;
    readonly NumberBox _itemHeading;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dictionary<PatchedFixture, FixtureRig> _rigs = [];
    readonly Button _record;
    readonly Button _popOut;
    NamePrompt _prompt;
    bool _floating;
    readonly Label _recLabel;
    readonly Queue<int> _grabs = new();
    bool _shotWanted;
    bool _shotPending;
    Mp4Writer? _rec;
    FramePacer? _pacer;
    int _due;
    string _recFile = "";
    IDisposable? _audioTap;
    double _lastTick;
    bool _uiVisible = true;
    bool _panning;
    float2 _panFrom;

    public VisualizerView(Fonts fonts, ContextMenu menu, NamePrompt prompt, LightingShow show)
    {
        _prompt = prompt;
        _show = show;
        _surface = Add(new Surface(this));

        // ---- the toolbar: look, camera, scene.
        _toolbar = Add(new Panel { Padding = 8, Spacing = 6, Background = OverlayBackground });
        var look = _toolbar.Add(new Row(8));
        look.Add(new Label(fonts.Body, "Haze") { Color = Theme.Text });
        _haze = look.Add(new Slider { Min = 0, Max = 1, Length = 110, ShowFill = true });
        _haze.Changed += s => Scene.Haze = s.Value;
        _workLight = look.Add(new Checkbox(fonts.Body, "Work light"));
        _workLight.Changed += c => Scene.WorkLight = c.Checked;
        _shadows = look.Add(new Checkbox(fonts.Body, "Shadows") { Tooltip = "The brightest lights (up to eight) cast shadows" });
        _shadows.Changed += c => Scene.Shadows = c.Checked;
        look.Add(new Label(fonts.Body, "Room") { Color = Theme.Text });
        _room = look.Add(
            new Dropdown<string>(fonts.Body)
            {
                Menu = menu,
                Width = 130,
                Items = () => [.. VizScene.Rooms.Select(r => r.Name)],
            }
        );
        _room.Chosen += Scene.SetRoom;

        var camera = _toolbar.Add(new Row(8));
        camera.Add(new Label(fonts.Body, "View") { Color = Theme.Text });
        _view = camera.Add(
            new Dropdown<int>(fonts.Body)
            {
                Menu = menu,
                Width = 130,
                Items = () => [.. Enumerable.Range(0, Scene.Views.Count)],
                Text = i => i < Scene.Views.Count ? Scene.Views[i].Name : "",
            }
        );
        _view.Chosen += Scene.ApplyView;
        var saveAs = camera.Add(new Button(fonts.Body, "Save as…") { Padding = 4 });
        saveAs.Clicked += _ =>
            _prompt.Show(
                "Save Camera View",
                $"View {Scene.Views.Count + 1}",
                name => name.Trim() == "" ? "Give the view a name." : null,
                name =>
                {
                    Scene.SaveViewAs(name);
                    _view.Selected = Scene.Views.Count - 1;
                }
            );
        var update = camera.Add(new Button(fonts.Body, "Update") { Padding = 4, Tooltip = "Keep where the camera is now as this view" });
        update.Clicked += _ => Scene.UpdateView(_view.Selected);
        var delete = camera.Add(new Button(fonts.Body, "Delete") { Padding = 4 });
        delete.Clicked += _ =>
        {
            Scene.DeleteView(_view.Selected);
            _view.Selected = Math.Min(_view.Selected, Scene.Views.Count - 1);
            Scene.ApplyView(_view.Selected);
        };
        camera.Add(new Label(fonts.Body, "FOV") { Color = Theme.Text });
        _fov = camera.Add(new NumberBox(fonts.Body, 15, 100, 1, 0, 60));
        _fov.Changed += b => Scene.Camera.Fov = b.Value;
        _ortho = camera.Add(new Checkbox(fonts.Body, "Ortho"));
        _ortho.Changed += c => Scene.Camera.Ortho = c.Checked;

        var scene = _toolbar.Add(new Row(8));
        _undo = scene.Add(new Button(fonts.Body, "Undo") { Padding = 4, Tooltip = "Undo the last move (Ctrl+Z)" });
        _undo.Clicked += _ => Scene.Undo();
        _redo = scene.Add(new Button(fonts.Body, "Redo") { Padding = 4, Tooltip = "Redo the move undone (Ctrl+Y)" });
        _redo.Clicked += _ => Scene.Redo();
        var addTruss = scene.Add(new Button(fonts.Body, "Add Truss") { Padding = 4 });
        addTruss.Clicked += _ => Scene.AddTruss();
        var loadModel = scene.Add(new Button(fonts.Body, "Load Model…") { Padding = 4, Tooltip = "Place a glTF model (a set piece, a stage element) where the camera's looking" });
        loadModel.Clicked += _ =>
        {
            if (AskForModel() is { } path)
                Scene.AddProp(path);
        };
        var arrange = scene.Add(new Button(fonts.Body, "Auto-arrange") { Padding = 4, Tooltip = "Every fixture back in rows over the stage, pointing down" });
        arrange.Clicked += _ => Scene.AutoArrange();
        var importMvr = scene.Add(new Button(fonts.Body, "Import MVR…") { Padding = 4, Tooltip = "Patch a rig from a My Virtual Rig file: its fixtures' GDTFs, addresses and places, and its trusses" });
        importMvr.Clicked += _ =>
        {
            if (ImportMvr is { } import)
                Status?.Invoke(import());
        };
        var exportMvr = scene.Add(new Button(fonts.Body, "Export MVR…") { Padding = 4, Tooltip = "Save the patch as a My Virtual Rig file, with a GDTF for each fixture type" });
        exportMvr.Clicked += _ =>
        {
            if (ExportMvr is { } export)
                Status?.Invoke(export());
        };
        var shot = scene.Add(new Button(fonts.Body, "Screenshot") { Padding = 4, Tooltip = "Save the 3D view as a PNG" });
        shot.Clicked += _ => _shotWanted = true;
        _record = scene.Add(new Button(fonts.Body, "Record") { Padding = 4, Tooltip = "Record the 3D view to an MP4, with what the computer's playing" });
        _record.Clicked += _ => ToggleRecording();
        _popOut = scene.Add(new Button(fonts.Body, "Pop Out") { Padding = 4, Tooltip = "The visualizer in a window of its own (another screen, say); close it to bring it back" });
        _popOut.Clicked += _ =>
        {
            if (Floating)
                DockRequested?.Invoke();
            else
                PopOutRequested?.Invoke();
        };
        _recLabel = scene.Add(new Label(fonts.Body, "") { Color = Theme.Muted });

        // ---- the selected item's properties.
        _props = Add(new Panel { Padding = 8, Spacing = 6, Background = OverlayBackground });
        _propTitle = _props.Add(new Label(fonts.Bold, "") { Color = Theme.Text });
        var where = _props.Add(new Row(6));
        NumberBox Pos(string name)
        {
            where.Add(new Label(fonts.Body, name) { Color = Theme.Text });
            var b = where.Add(new NumberBox(fonts.Body, -40, 40, 0.1, 2, 70));
            b.Changed += _ => EditFromBoxes();
            return b;
        }
        _x = Pos("X");
        _y = Pos("Y");
        _z = Pos("Z");
        _turnRow = _props.Add(new Row(6));
        _turnRow.Add(new Label(fonts.Body, "Heading") { Color = Theme.Text });
        _heading = _turnRow.Add(new NumberBox(fonts.Body, -180, 180, 1, 0, 70));
        _heading.Changed += _ => EditFromBoxes();
        _turnRow.Add(new Label(fonts.Body, "Tilt down") { Color = Theme.Text });
        _tilt = _turnRow.Add(new NumberBox(fonts.Body, -180, 180, 1, 0, 70));
        _tilt.Changed += _ => EditFromBoxes();
        _sizeRow = _props.Add(new Row(6));
        _sizeLabel = _sizeRow.Add(new Label(fonts.Body, "Length") { Color = Theme.Text });
        _size = _sizeRow.Add(new NumberBox(fonts.Body, 0.05, 40, 0.1, 2, 70));
        _size.Changed += _ => EditFromBoxes();
        _sizeRow.Add(new Label(fonts.Body, "Heading") { Color = Theme.Text });
        _itemHeading = _sizeRow.Add(new NumberBox(fonts.Body, -180, 180, 1, 0, 70));
        _itemHeading.Changed += _ => EditFromBoxes();
        var del = _sizeRow.Add(new Button(fonts.Body, "Delete") { Padding = 4 });
        del.Clicked += _ => Scene.DeleteSelected();

        // Enter in a box lets go of the keyboard, so Ctrl+Z then undoes the move rather than the typing.
        foreach (var box in new[] { _x, _y, _z, _heading, _tilt, _size, _itemHeading, _fov })
            box.Submitted += _ => Root?.Focus(null);

        _uiToggle = Add(new Button(fonts.Body, "Hide UI") { Padding = 4 });
        _uiToggle.Clicked += _ => UiVisible = !UiVisible;
    }

    VizScene Scene => _show.Visualizer;

    /// <summary>
    /// The root it's in, to let go of the keyboard (set by whoever adds it).
    /// </summary>
    public Root? Root { get; set; }

    /// <summary>
    /// Asks for an MVR and imports it, returning what to say ("" when none was chosen); null when there's no way to.
    /// </summary>
    public Func<string>? ImportMvr { get; set; }

    /// <summary>
    /// Asks where to export an MVR and does, returning what to say ("" when cancelled).
    /// </summary>
    public Func<string>? ExportMvr { get; set; }

    /// <summary>
    /// Something for the status line.
    /// </summary>
    public event Action<string>? Status;

    /// <summary>
    /// Where screenshots and recordings go (<c>render</c> in the app's folder).
    /// </summary>
    public string RenderFolder { get; set; } = Path.Combine(AppPaths.Folder, "render");

    /// <summary>
    /// Shows a folder (Explorer), as sLight opened its render folder after a screenshot or recording.
    /// </summary>
    public Action<string> OpenFolder { get; set; } = _ => { };

    /// <summary>
    /// Starts capturing what the computer's playing for a recording, calling back with interleaved samples on its own
    /// thread; returns the capture (to stop) and its rate and channels, or null for a silent recording.
    /// </summary>
    public Func<Action<float[]>, (IDisposable Capture, int Rate, int Channels)?> StartAudio { get; set; } = _ => null;

    public bool IsRecording => _rec is not null;

    /// <summary>
    /// Pop Out was pressed: the visualizer wants a window of its own.
    /// </summary>
    public event Action? PopOutRequested;

    /// <summary>
    /// Dock to Main was pressed: back into its tab.
    /// </summary>
    public event Action? DockRequested;

    /// <summary>
    /// Whether it's in a window of its own (its button then docks it).
    /// </summary>
    public bool Floating
    {
        get => _floating;
        set
        {
            _floating = value;
            _popOut.Text = value ? "Dock to Main" : "Pop Out";
        }
    }

    /// <summary>
    /// The menu and name prompt its dropdowns and Save as… use: the window's it's in.
    /// </summary>
    public void UseDialogs(ContextMenu menu, NamePrompt prompt)
    {
        _room.Menu = _view.Menu = menu;
        _prompt = prompt;
    }

    /// <summary>
    /// Asks for a glTF model file to place; null when none's chosen.
    /// </summary>
    public Func<string?> AskForModel { get; set; } = () => null;

    /// <summary>
    /// Where imported profiles' models are kept (<c>fixture_models</c>), so fixtures from GDTF are drawn with them;
    /// null to draw every fixture schematically.
    /// </summary>
    public string? ModelsFolder { get; set; }

    /// <summary>
    /// Whether the tab's on screen (set by the console, which knows).
    /// </summary>
    public Func<bool> IsShown { get; set; } = () => true;

    /// <summary>
    /// Whether the toolbar and properties are shown (Hide UI / Show UI).
    /// </summary>
    public bool UiVisible
    {
        get => _uiVisible;
        set
        {
            _uiVisible = value;
            _uiToggle.Text = value ? "Hide UI" : "Show UI";
        }
    }

    public Button UndoButton => _undo;
    public Button RedoButton => _redo;
    public Panel Toolbar => _toolbar;
    public Panel Properties => _props;
    public string PropertiesTitle => _propTitle.Text;
    public NumberBox XBox => _x;
    public NumberBox TiltBox => _tilt;
    public NumberBox SizeBox => _size;
    public Element Surface3D => _surface;

    /// <summary>
    /// The rig each patched fixture's drawn from, eased towards its DMX each frame.
    /// </summary>
    public IReadOnlyDictionary<PatchedFixture, FixtureRig> Rigs => _rigs;

    /// <summary>
    /// The boxes changed: the selected item takes what they say, an undoable step.
    /// </summary>
    void EditFromBoxes()
    {
        if (Scene.Selected is not { } item || Scene.StateOf(item) is not { } st)
            return;
        var size = item is PatchedFixture ? st.Size : _size.Value;
        var heading = item is PatchedFixture ? _heading.Value : _itemHeading.Value;
        Scene.EditSelected(st with { Position = new Vector3((float)_x.Value, (float)_y.Value, (float)_z.Value), Heading = heading, Tilt = _tilt.Value, Size = size });
    }

    /// <summary>
    /// Each frame: the rigs follow the patch and the DMX, and the controls show the scene.
    /// </summary>
    public void Update()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var delta = Math.Clamp(now - _lastTick, 0, 0.25);
        _lastTick = now;
        UpdateRigs(delta);
        if (_rec is { } rec && _pacer is { } pacer)
        {
            _due += pacer.Due(delta);
            _recLabel.Text = $"REC  {rec.FrameCount}";
        }

        _haze.Value = (float)Scene.Haze;
        _workLight.Checked = Scene.WorkLight;
        _shadows.Checked = Scene.Shadows;
        _room.Selected = Scene.Room.Name;
        _view.Selected = Math.Clamp(_view.Selected, 0, Math.Max(Scene.Views.Count - 1, 0));
        _fov.Value = Scene.Camera.Fov;
        _ortho.Checked = Scene.Camera.Ortho;
        _undo.Enabled = Scene.CanUndo;
        _redo.Enabled = Scene.CanRedo;
        _toolbar.Visible = _uiVisible;

        var selected = Scene.Selected;
        _props.Visible = _uiVisible && selected is not null;
        if (selected is null || Scene.StateOf(selected) is not { } st)
            return;
        _turnRow.Visible = selected is PatchedFixture;
        _sizeRow.Visible = selected is not PatchedFixture;
        _propTitle.Text = selected switch
        {
            PatchedFixture f => $"{f.Name}   (U{f.Universe + 1} ch {f.Start + 1}-{f.Start + f.Profile.ChannelCount(f.Mode)})",
            SetPiece p => "Model: " + p.Name,
            _ => "Truss",
        };
        _sizeLabel.Text = selected is SetPiece ? "Scale" : "Length";
        _x.Value = st.Position.X;
        _y.Value = st.Position.Y;
        _z.Value = st.Position.Z;
        _heading.Value = st.Heading;
        _itemHeading.Value = st.Heading;
        _tilt.Value = st.Tilt;
        _size.Value = st.Size;
    }

    void UpdateRigs(double delta)
    {
        var patched = _show.Patch.All.ToHashSet();
        foreach (var gone in _rigs.Keys.Where(f => !patched.Contains(f)).ToList())
            _rigs.Remove(gone);
        foreach (var f in patched)
        {
            if (!_rigs.TryGetValue(f, out var rig) || rig.Profile != f.Profile || rig.Mode != f.Mode)
                _rigs[f] = rig = new FixtureRig(f.Profile, f.Mode, ModelsFolder);
            var output = f.Universe < _show.Dmx.UniverseCount ? _show.Dmx.Universes[f.Universe].Output : [];
            rig.Update(DmxRender.Evaluate(f.Profile, f.Mode, output, f.Start), delta);
        }
    }

    // ------------------------------------------------------------ 3D source --

    // ------------------------------------------------- screenshot and record --

    bool IScene3DSource.CaptureThisFrame()
    {
        var shot = _shotWanted;
        _shotWanted = false;
        if (!shot && _due == 0)
            return false;
        _shotPending |= shot;
        _grabs.Enqueue(_due);
        _due = 0;
        return true;
    }

    void IScene3DSource.Captured(int width, int height, byte[] rgba)
    {
        var repeats = _grabs.Count > 0 ? _grabs.Dequeue() : 0;
        if (_shotPending)
        {
            _shotPending = false;
            SaveShot(width, height, rgba);
        }
        if (_rec is { } rec && repeats > 0)
        {
            var bgra = Pictures.ToBgra(rgba, width, height, rec.Width, rec.Height);
            for (var i = 0; i < repeats; i++)
                rec.PushFrame(bgra);
            // The loopback says nothing in a silence: fill it, so the sound keeps time with the picture.
            if (rec.Audio is { } a && rec.AudioSeconds < rec.VideoSeconds - 0.25)
                rec.PushAudio(new short[(int)((rec.VideoSeconds - rec.AudioSeconds) * a.Rate) * a.Channels]);
        }
    }

    /// <summary>
    /// Saves a picture of the 3D view as a PNG in the render folder.
    /// </summary>
    void SaveShot(int width, int height, byte[] rgba)
    {
        try
        {
            Directory.CreateDirectory(RenderFolder);
            var name = Pictures.Stamped("shot", "png", DateTime.Now);
            File.WriteAllBytes(Path.Combine(RenderFolder, name), Png.Encode(new Png.Image(width, height, rgba)));
            _recLabel.Text = "Saved " + name;
            Status?.Invoke("Saved " + name);
            OpenFolder(RenderFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status?.Invoke("Screenshot failed: " + e.Message);
        }
    }

    /// <summary>
    /// Record: starts an MP4 of the view (and what's playing) in the render folder; again, finishes it.
    /// </summary>
    public void ToggleRecording()
    {
        if (_rec is { } rec)
        {
            _audioTap?.Dispose();
            _audioTap = null;
            var said = rec.Finish(_recFile);
            rec.Dispose();
            _rec = null;
            _pacer = null;
            _due = 0;
            _record.Text = "Record";
            _recLabel.Text = said;
            Status?.Invoke(said);
            OpenFolder(RenderFolder);
            return;
        }
        var (w, h) = Pictures.VideoSize((int)_surface.Size.X, (int)_surface.Size.Y);
        if (w < 16 || h < 16)
        {
            _recLabel.Text = "no picture to record";
            return;
        }
        Directory.CreateDirectory(RenderFolder);
        _recFile = Pictures.Stamped("rec", "mp4", DateTime.Now);
        Mp4Writer? writer = null;
        // What's playing, if it can be heard: a recording starts its sound with its picture.
        (IDisposable Capture, int Rate, int Channels)? audio = null;
        audio = StartAudio(samples => writer?.PushAudio(Pictures.ToPcm16(samples)));
        var format = audio is { Rate: 44100 or 48000, Channels: 1 or 2 } a ? (a.Rate, a.Channels) : ((int, int)?)null;
        if (format is null)
            audio?.Capture.Dispose();
        var (started, error) = Mp4Writer.Start(Path.Combine(RenderFolder, _recFile), w, h, FramePacer.FPS, 12000, format);
        if (started is null)
        {
            audio?.Capture.Dispose();
            _recLabel.Text = "recorder failed to start";
            Status?.Invoke("Recording failed to start: " + error);
            return;
        }
        writer = started;
        _rec = started;
        _audioTap = format is null ? null : audio?.Capture;
        _pacer = new FramePacer();
        _due = 1; // the first frame now
        _record.Text = "Stop";
        _recLabel.Text = "REC → " + _recFile + (format is null ? "  (no sound)" : "");
    }

    float2? IScene3DSource.SceneSize => IsShown() && _surface.Size.X >= 1 && _surface.Size.Y >= 1 ? _surface.Size : null;

    SceneFrame IScene3DSource.BuildScene() =>
        SceneFrame.Build(Scene, _show.Patch, _rigs, ModelColours.Of(_show.Patch), _surface.Size.X / Math.Max(_surface.Size.Y, 1));

    // ----------------------------------------------------------------- keys --

    /// <summary>
    /// Ctrl+Z undoes and Ctrl+Y (or Ctrl+Shift+Z) redoes a move, while the mouse is over the view. Returns whether it
    /// was one of those.
    /// </summary>
    public bool KeyDown(Key key, Modifiers mods, float2? mouse)
    {
        if (!IsShown() || mouse is not { } m || !Contains(m) || (mods & Modifiers.Control) == 0)
            return false;
        if (key == Key.Y || key == Key.Z && (mods & Modifiers.Shift) != 0)
            Scene.Redo();
        else if (key == Key.Z)
            Scene.Undo();
        else
            return false;
        return true;
    }

    // --------------------------------------------------------------- layout --

    protected override void MeasureContent()
    {
        _surface.SetSize(Available);
        _toolbar.Measure();
        _props.Measure();
        _uiToggle.Measure();
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _surface.Arrange(position);
        _toolbar.Arrange(position + new float2(8, 8));
        _props.Arrange(position + new float2(8, Size.Y - _props.Size.Y - 8));
        _uiToggle.Arrange(position + new float2(Size.X - _uiToggle.Size.X - 8, 8));
    }

    // ---------------------------------------------------- the middle button --

    bool IPointerHook.Button(MouseButton button, bool down, float2 at, Modifiers modifiers)
    {
        if (button != MouseButton.Middle)
            return false;
        if (down && IsShown() && _surface.Contains(at))
        {
            _panning = true;
            _panFrom = at;
            return true;
        }
        if (!down && _panning)
        {
            _panning = false;
            return true;
        }
        return false;
    }

    bool IPointerHook.Move(float2 at)
    {
        if (!_panning)
            return false;
        Scene.Camera.Pan(at - _panFrom);
        _panFrom = at;
        return true;
    }

    Cursor? IPointerHook.Cursor => _panning ? Cursor.Grab : null;

    /// <summary>
    /// Where the scene's drawn, taking the mouse: orbit, slide, zoom, pick and drag.
    /// </summary>
    sealed class Surface(VisualizerView owner) : Element
    {
        public override bool IsInteractive => true;
        public override Cursor Cursor => Cursor.Arrow;

        public void SetSize(float2 size) => Size = size;

        VizScene Scene => owner.Scene;

        public override float2 Measure() => Size;

        public override void Draw(Batch batch) => batch.AddTexture(Scene3D.TEXTURE, ScreenPosition, Size);

        public override void PointerDown(float2 point, Modifiers modifiers) => Scene.BeginDrag(point - ScreenPosition, Size, (modifiers & Modifiers.Shift) != 0);

        public override void PointerDrag(float2 point) => Scene.DragTo(point - ScreenPosition, Size);

        public override void PointerUp(float2 point) => Scene.EndDrag();

        public override bool Wheel(float2 notches)
        {
            Scene.Camera.Zoom(notches.Y > 0 ? 0.9 : 1.1);
            return true;
        }
    }
}

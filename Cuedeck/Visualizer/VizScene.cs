using System.Numerics;
using System.Text.Json.Nodes;
using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Show;

namespace Cuedeck.Visualizer;

/// <summary>
/// A room the 3D view can be: its floor's size, the walls' and backdrop's colours (tinted rather than black, so the
/// fixtures' bodies stand out yet the beams still show) and its usual haze.
/// </summary>
sealed record Room(string Name, Vector2 Floor, Rgb Wall, Rgb Sky, double Haze);

/// <summary>
/// A length of truss to hang fixtures from: a bar, metres long, turned by its heading (degrees).
/// </summary>
sealed class Truss
{
    public Vector3 Position { get; set; }
    public double Length { get; set; } = 4;
    public double Heading { get; set; }
}

/// <summary>
/// A glTF model placed in the room as a set piece: its file, where it is, its size and its heading.
/// </summary>
sealed class SetPiece
{
    public string Path { get; set; } = "";
    public Vector3 Position { get; set; }
    public double Scale { get; set; } = 1;
    public double Heading { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// Its model's bounds, unscaled (a metre cube until the model's read).
    /// </summary>
    public (Vector3 Centre, Vector3 Size) Bounds { get; set; } = (new Vector3(0, 0.5f, 0), Vector3.One);
}

/// <summary>
/// Where something in the room is, as the properties panel shows it and an undo step keeps it: its position, its
/// heading and (a fixture's) tilt down and roll in degrees, and its size (a truss's length or a model's scale).
/// </summary>
sealed record ItemState(Vector3 Position, double Heading, double Tilt = 0, double Roll = 0, double Size = 0);

/// <summary>
/// The 3D Visualizer's scene, without drawing it: the room and its haze, the work light and shadows switches, the
/// camera and its saved views, the trusses and set pieces, and where the patched fixtures are (kept on the patch). It
/// picks what's under the mouse, drags it on the floor at its own height, takes typed positions and turns for the
/// selected item, arranges the fixtures, and keeps an undo history of all those moves (each drag, typed value or
/// Auto-arrange one step; adding and deleting trusses aren't steps). Saved with the show as sLight's <c>viz</c>.
/// sLight's <c>VisualizerPanel</c> without its screen.
/// </summary>
sealed class VizScene
{
    public const double HAZE_MAX = 0.05;

    /// <summary>
    /// Moves the undo history keeps.
    /// </summary>
    public const int UNDO_LIMIT = 100;

    public static readonly Room[] Rooms =
    [
        new("Black Box", new Vector2(26, 20), new Rgb(0.16f, 0.17f, 0.22f), new Rgb(0.10f, 0.11f, 0.15f), 0.015),
        new("Club", new Vector2(16, 13), new Rgb(0.20f, 0.13f, 0.24f), new Rgb(0.12f, 0.08f, 0.15f), 0.03),
        new("Arena", new Vector2(44, 30), new Rgb(0.13f, 0.17f, 0.24f), new Rgb(0.08f, 0.11f, 0.16f), 0.008),
    ];

    public static readonly Rgb FloorColour = new(0.20f, 0.20f, 0.23f);

    static readonly Vector3 FixtureBoxCentre = new(0, 0.15f, 0);
    static readonly Vector3 FixtureBoxSize = new(0.5f, 0.5f, 0.5f);

    readonly Patch _patch;
    readonly List<List<Change>> _undo = [];
    readonly List<List<Change>> _redo = [];
    object? _selected;

    /// <summary>
    /// One thing a step moved: a fixture by its uid (so it survives the patch being rebuilt), a truss or set piece by
    /// itself.
    /// </summary>
    sealed record Change(object Ref, ItemState Before, ItemState After);

    public VizScene(Patch patch)
    {
        _patch = patch;
        Reset();
    }

    public Room Room { get; private set; } = Rooms[0];

    /// <summary>
    /// 0 to 1 of <see cref="HAZE_MAX"/>.
    /// </summary>
    public double Haze { get; set; }

    public bool WorkLight { get; set; } = true;
    public bool Shadows { get; set; }
    public OrbitCamera Camera { get; private set; } = new();
    public List<CameraView> Views { get; } = [];
    public List<Truss> Trusses { get; } = [];
    public List<SetPiece> Props { get; } = [];

    /// <summary>
    /// Something changed that the screen shows: the selection, the history, the room or a view.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Back to a new show's: the black box, the starting views, no trusses or set pieces, nothing to undo.
    /// </summary>
    public void Reset()
    {
        Camera = new OrbitCamera();
        Views.Clear();
        Views.AddRange(
            [
                new("Orbit", 0, 1.5, -3, 0.4, 0.35, 15, 55),
                new("Front", 0, 2.0, -4, 0.0, 0.05, 15, 50),
                new("Audience", 0, 2.5, -5, 0.0, 0.18, 24, 40),
                new("FOH high", 0, 2.0, -5, 0.15, 0.55, 20, 45),
                new("Top", 0, 1.0, -4, 0.0, 1.4, 24, 50, true),
            ]
        );
        WorkLight = true;
        Shadows = false;
        Trusses.Clear();
        Props.Clear();
        SetRoom(Rooms[0].Name);
        Camera.Apply(Views[0]);
        _selected = null;
        ClearHistory();
    }

    /// <summary>
    /// Sets the room, and its haze. An unknown one changes nothing.
    /// </summary>
    public void SetRoom(string name)
    {
        if (Rooms.FirstOrDefault(r => r.Name == name) is not { } room)
            return;
        Room = room;
        Haze = room.Haze / HAZE_MAX;
        Changed?.Invoke();
    }

    // ----------------------------------------------------------------- views --

    public void ApplyView(int i)
    {
        if (i >= 0 && i < Views.Count)
            Camera.Apply(Views[i]);
    }

    public void SaveViewAs(string name)
    {
        name = name.Trim();
        if (name == "")
            return;
        Views.Add(Camera.ToView(name));
        Changed?.Invoke();
    }

    /// <summary>
    /// View <paramref name="i"/> becomes where the camera is now.
    /// </summary>
    public void UpdateView(int i)
    {
        if (i >= 0 && i < Views.Count)
            Views[i] = Camera.ToView(Views[i].Name);
    }

    /// <summary>
    /// Deletes view <paramref name="i"/>, unless it's the last.
    /// </summary>
    public void DeleteView(int i)
    {
        if (i < 0 || i >= Views.Count || Views.Count <= 1)
            return;
        Views.RemoveAt(i);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------- selection --

    /// <summary>
    /// The fixture (<see cref="PatchedFixture"/>), <see cref="Truss"/> or <see cref="SetPiece"/> selected; null for
    /// none, or once it's been unpatched or deleted.
    /// </summary>
    public object? Selected
    {
        get
        {
            if (_selected is PatchedFixture f && !_patch.All.Contains(f) || _selected is Truss t && !Trusses.Contains(t) || _selected is SetPiece p && !Props.Contains(p))
                _selected = null;
            return _selected;
        }
    }

    public void Select(object? item)
    {
        if (ReferenceEquals(item, _selected))
            return;
        _selected = item;
        Changed?.Invoke();
    }

    /// <summary>
    /// What's under <paramref name="point"/> of a view <paramref name="size"/> pixels big: the nearest fixture, truss
    /// or set piece the ray hits, or null.
    /// </summary>
    public object? Pick(float2 point, float2 size)
    {
        var (origin, dir) = Camera.Ray(point, size);
        object? best = null;
        var bestT = float.PositiveInfinity;
        void Try(object item, Matrix4x4 world, Vector3 centre, Vector3 boxSize)
        {
            if (HitBox(origin, dir, world, centre, boxSize) is { } t && t < bestT)
                (best, bestT) = (item, t);
        }
        foreach (var f in _patch.All)
            Try(f, FixtureRig.Placement(f.Position, f.Rotation), FixtureBoxCentre, FixtureBoxSize);
        foreach (var tr in Trusses)
            Try(tr, TrussWorld(tr), Vector3.Zero, new Vector3((float)tr.Length, 0.16f, 0.16f));
        foreach (var p in Props)
            Try(p, PropWorld(p), p.Bounds.Centre, Vector3.Clamp(p.Bounds.Size, new Vector3(0.2f), new Vector3(100)));
        return best;
    }

    public static Matrix4x4 TrussWorld(Truss t) => Matrix4x4.CreateRotationY((float)(t.Heading * Math.PI / 180)) * Matrix4x4.CreateTranslation(t.Position);

    public static Matrix4x4 PropWorld(SetPiece p) =>
        Matrix4x4.CreateScale((float)p.Scale) * Matrix4x4.CreateRotationY((float)(p.Heading * Math.PI / 180)) * Matrix4x4.CreateTranslation(p.Position);

    /// <summary>
    /// Where along the ray it meets a box (<paramref name="centre"/> and <paramref name="size"/> in
    /// <paramref name="world"/>'s frame), or null.
    /// </summary>
    static float? HitBox(Vector3 origin, Vector3 dir, Matrix4x4 world, Vector3 centre, Vector3 size)
    {
        if (!Matrix4x4.Invert(world, out var inv))
            return null;
        var o = Vector3.Transform(origin, inv) - centre;
        var d = Vector3.TransformNormal(dir, inv);
        var half = size / 2;
        float near = float.NegativeInfinity,
            far = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var (oa, da, ha) = (o[axis], d[axis], half[axis]);
            if (Math.Abs(da) < 1e-9f)
            {
                if (Math.Abs(oa) > ha)
                    return null;
                continue;
            }
            var t1 = (-ha - oa) / da;
            var t2 = (ha - oa) / da;
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            near = Math.Max(near, t1);
            far = Math.Min(far, t2);
            if (near > far)
                return null;
        }
        // In world units: the local ray's length differs by the transform's scale.
        var hit = far < 0 ? (float?)null : Math.Max(near, 0);
        return hit is { } h ? Vector3.Distance(origin, Vector3.Transform(o + centre + d * h, world)) : null;
    }

    // ---------------------------------------------------------------- moving --

    enum DragMode
    {
        None,
        Orbit,
        Pan,
        Move,
    }

    DragMode _drag;
    float2 _dragFrom;
    float2 _dragLast;
    bool _dragMoved;
    object? _moving;
    ItemState? _moveBefore;

    /// <summary>
    /// The mouse went down at <paramref name="point"/>: on an item, it's selected and dragged; on nothing, the camera
    /// orbits; with <paramref name="pan"/> (Shift, or the middle button), it slides.
    /// </summary>
    public void BeginDrag(float2 point, float2 size, bool pan)
    {
        (_dragFrom, _dragLast, _dragMoved) = (point, point, false);
        if (pan)
        {
            _drag = DragMode.Pan;
            return;
        }
        if (Pick(point, size) is { } hit)
            BeginMove(hit);
        else
            _drag = DragMode.Orbit;
    }

    /// <summary>
    /// Selects <paramref name="item"/> and starts dragging it on the floor.
    /// </summary>
    public void BeginMove(object item)
    {
        Select(item);
        _moving = item;
        _moveBefore = StateOf(item);
        _drag = DragMode.Move;
    }

    public void DragTo(float2 point, float2 size)
    {
        if (float2.Distance(point, _dragFrom) > 3)
            _dragMoved = true;
        var delta = point - _dragLast;
        _dragLast = point;
        switch (_drag)
        {
            case DragMode.Orbit:
                Camera.Orbit(delta);
                break;
            case DragMode.Pan:
                Camera.Pan(delta);
                break;
            case DragMode.Move when _moving is not null && StateOf(_moving) is { } st:
                // Along the floor, at its own height.
                var (o, d) = Camera.Ray(point, size);
                if (Math.Abs(d.Y) < 1e-6f)
                    break;
                var t = (st.Position.Y - o.Y) / d.Y;
                if (t < 0)
                    break;
                var p = o + d * t;
                SetState(_moving, st with { Position = new Vector3(p.X, st.Position.Y, p.Z) });
                Changed?.Invoke();
                break;
        }
    }

    /// <summary>
    /// The mouse came up: a drag of an item is one undo step; a click on nothing clears the selection.
    /// </summary>
    public void EndDrag()
    {
        if (_drag == DragMode.Orbit && !_dragMoved)
            Select(null);
        if (_drag == DragMode.Move && _moving is not null && _moveBefore is not null && StateOf(_moving) is { } after)
            Record([new Change(RefOf(_moving), _moveBefore, after)]);
        _drag = DragMode.None;
        _moving = null;
        _moveBefore = null;
    }

    public bool IsDragging => _drag != DragMode.None;

    /// <summary>
    /// The properties panel changed a value: the selected item takes <paramref name="state"/> (a fixture's roll goes
    /// to 0, as sLight's did), one undoable step.
    /// </summary>
    public void EditSelected(ItemState state)
    {
        if (Selected is not { } item || StateOf(item) is not { } before)
            return;
        if (item is PatchedFixture)
            state = state with { Roll = 0 };
        SetState(item, state);
        Record([new Change(RefOf(item), before, StateOf(item)!)]);
    }

    /// <summary>
    /// Where an item is (and how it's turned, how long or big it is); null for something that isn't in the room.
    /// </summary>
    public ItemState? StateOf(object item) =>
        item switch
        {
            PatchedFixture f => new ItemState(f.Position, f.Rotation.Y, f.Rotation.X, f.Rotation.Z),
            Truss t => new ItemState(t.Position, t.Heading, Size: t.Length),
            SetPiece p => new ItemState(p.Position, p.Heading, Size: p.Scale),
            _ => null,
        };

    static void SetState(object item, ItemState st)
    {
        switch (item)
        {
            case PatchedFixture f:
                f.Position = st.Position;
                f.Rotation = new Vector3((float)st.Tilt, (float)st.Heading, (float)st.Roll);
                break;
            case Truss t:
                (t.Position, t.Heading, t.Length) = (st.Position, st.Heading, st.Size);
                break;
            case SetPiece p:
                (p.Position, p.Heading, p.Scale) = (st.Position, st.Heading, Math.Max(st.Size, 0.01));
                break;
        }
    }

    static object RefOf(object item) => item is PatchedFixture f ? f.Uid : item;

    /// <summary>
    /// What an undo step's reference names, if it's still there.
    /// </summary>
    object? Resolve(object reference) =>
        reference switch
        {
            string uid => _patch.FindByUid(uid),
            Truss t => Trusses.Contains(t) ? t : null,
            SetPiece p => Props.Contains(p) ? p : null,
            _ => null,
        };

    /// <summary>
    /// Puts every fixture back in rows over the stage (each universe's a row further back), level, one step.
    /// </summary>
    public void AutoArrange()
    {
        var changes = new List<Change>();
        for (var u = 0; u < _patch.Universes.Count; u++)
        {
            var i = 0;
            foreach (var f in _patch.Universes[u].Fixtures)
            {
                var before = StateOf(f)!;
                f.Position = Patch.AutoPlace(i) + new Vector3(0, 0, u * -2f);
                f.Rotation = new Vector3(90, 0, 0);
                changes.Add(new Change(f.Uid, before, StateOf(f)!));
                i++;
            }
        }
        Record(changes);
    }

    /// <summary>
    /// Adds a 4 m truss 4 m over what the camera's looking at, selected.
    /// </summary>
    public Truss AddTruss()
    {
        var t = new Truss { Position = Camera.Target + new Vector3(0, 4, 0) };
        Trusses.Add(t);
        Select(t);
        return t;
    }

    /// <summary>
    /// Adds a set piece from a model file where the camera's looking, selected.
    /// </summary>
    public SetPiece AddProp(string path)
    {
        var p = new SetPiece { Path = path, Position = Camera.Target };
        Props.Add(p);
        Select(p);
        return p;
    }

    /// <summary>
    /// Deletes the selected truss or set piece (fixtures are unpatched in the patch).
    /// </summary>
    public void DeleteSelected()
    {
        if (Selected is Truss t)
            Trusses.Remove(t);
        else if (Selected is SetPiece p)
            Props.Remove(p);
        else
            return;
        Select(null);
    }

    // ----------------------------------------------------------- undo / redo --

    /// <summary>
    /// Adds a step of what <paramref name="changes"/> really changed; nothing changed, no step.
    /// </summary>
    void Record(List<Change> changes)
    {
        var real = changes.Where(c => c.Before != c.After).ToList();
        if (real.Count == 0)
            return;
        _undo.Add(real);
        if (_undo.Count > UNDO_LIMIT)
            _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke();
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;

    /// <summary>
    /// Undoes the last move (skipping anything since unpatched or deleted). Returns whether there was one.
    /// </summary>
    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (var k = step.Count - 1; k >= 0; k--)
            if (Resolve(step[k].Ref) is { } item)
                SetState(item, step[k].Before);
        _redo.Add(step);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var c in step)
            if (Resolve(c.Ref) is { } item)
                SetState(item, c.After);
        _undo.Add(step);
        Changed?.Invoke();
        return true;
    }

    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ file --

    /// <summary>
    /// The scene as sLight saves it (the show's <c>viz</c>).
    /// </summary>
    public JsonObject ToJson() =>
        new()
        {
            ["room"] = Room.Name,
            ["haze"] = Haze,
            ["work_light"] = WorkLight,
            ["shadows"] = Shadows,
            ["cam"] = new JsonArray(Camera.Target.X, Camera.Target.Y, Camera.Target.Z, Camera.Yaw, Camera.Pitch, Camera.Distance),
            ["views"] = new JsonArray([.. Views.Select(v => (JsonNode)v.ToJson())]),
            ["trusses"] = new JsonArray([.. Trusses.Select(t => (JsonNode)new JsonArray(t.Position.X, t.Position.Y, t.Position.Z, t.Length, t.Heading))]),
            ["props"] = new JsonArray(
                [
                    .. Props.Select(p =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["path"] = p.Path,
                                ["pos"] = new JsonArray(p.Position.X, p.Position.Y, p.Position.Z),
                                ["scale"] = p.Scale,
                                ["rot_y"] = p.Heading,
                            }
                    ),
                ]
            ),
        };

    /// <summary>
    /// Takes a saved scene: what it has replaces what's here (what it leaves out stays), and the history's cleared.
    /// </summary>
    public void FromJson(JsonNode? d)
    {
        ClearHistory();
        _selected = null;
        if (d is not JsonObject)
            return;
        SetRoom(d.Str("room", Room.Name));
        if (d.Has("haze"))
            Haze = d.Num("haze");
        if (d.Has("work_light"))
            WorkLight = d.Bool("work_light");
        if (d.Has("shadows"))
            Shadows = d.Bool("shadows");
        if (d.Arr("views") is { Count: > 0 } views)
        {
            Views.Clear();
            Views.AddRange(views.Where(v => v is JsonObject).Select(CameraView.FromJson));
        }
        if (d.Arr("cam") is { Count: 6 } cam)
        {
            Camera.Target = new Vector3((float)Json.AsDouble(cam[0]), (float)Json.AsDouble(cam[1]), (float)Json.AsDouble(cam[2]));
            Camera.Yaw = Json.AsDouble(cam[3]);
            Camera.Pitch = Json.AsDouble(cam[4]);
            Camera.Distance = Json.AsDouble(cam[5]);
            Camera.Clamp();
        }
        if (d.Has("trusses"))
        {
            Trusses.Clear();
            foreach (var tr in d.Arr("trusses"))
                if (tr is JsonArray { Count: >= 5 } a)
                    Trusses.Add(
                        new Truss
                        {
                            Position = new Vector3((float)Json.AsDouble(a[0]), (float)Json.AsDouble(a[1]), (float)Json.AsDouble(a[2])),
                            Length = Json.AsDouble(a[3]),
                            Heading = Json.AsDouble(a[4]),
                        }
                    );
        }
        if (d.Has("props"))
        {
            Props.Clear();
            foreach (var pd in d.Arr("props"))
            {
                if (pd is not JsonObject || pd.Str("path") == "")
                    continue;
                var pos = pd.Arr("pos");
                float At(int i) => i < pos.Count ? (float)Json.AsDouble(pos[i]) : 0;
                Props.Add(
                    new SetPiece
                    {
                        Path = pd.Str("path"),
                        Position = new Vector3(At(0), At(1), At(2)),
                        Scale = pd.Num("scale", 1),
                        Heading = pd.Num("rot_y"),
                    }
                );
            }
        }
        Changed?.Invoke();
    }
}

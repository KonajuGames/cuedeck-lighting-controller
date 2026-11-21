using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.UI.DjEdit;

/// <summary>
/// DJ Edit Mode's list of named positions, below the folders: a row each, its colour's triangle and its name. Click
/// one to open it; hovering shows an ✕ at its right to delete it, and a right-click asks for a menu. sLight's
/// <c>PositionList</c>.
/// </summary>
sealed class PositionList(Font font, PositionLibrary library) : Element
{
    const float X_WIDTH = 22;

    int _hover = -1;
    bool _onX;

    public float Width { get; set; } = 240;

    /// <summary>
    /// The position shown selected ("" for none).
    /// </summary>
    public string SelectedId { get; set; } = "";

    public event Action<string>? Picked;
    public event Action<string>? DeleteRequested;
    public event Action<string, float2>? ContextRequested;

    public float RowHeight => font.LineHeight + 6;

    public IReadOnlyList<string> Ids => [.. library.Positions.Select(p => p.Id)];

    public override bool IsInteractive => library.Positions.Count > 0;

    public override Cursor Cursor => Cursor.Arrow;

    public override float2 Measure() => Size = new float2(Width, library.Positions.Count * RowHeight);

    int RowAt(float2 point) => point.Y < ScreenPosition.Y ? -1 : (int)((point.Y - ScreenPosition.Y) / RowHeight) is var i && i < library.Positions.Count ? i : -1;

    /// <summary>
    /// The screen point in the middle of position <paramref name="id"/>'s row, or of its ✕.
    /// </summary>
    public float2 PointOf(string id, bool x = false)
    {
        var i = library.Positions.FindIndex(p => p.Id == id);
        var y = ScreenPosition.Y + (i + 0.5f) * RowHeight;
        return new float2(x ? ScreenPosition.X + Size.X - X_WIDTH / 2 : ScreenPosition.X + Size.X / 3, y);
    }

    /// <summary>
    /// Whether the ✕ shows on <paramref name="id"/>'s row (the mouse is over it).
    /// </summary>
    public bool ShowsDelete(string id) => _hover >= 0 && _hover < library.Positions.Count && library.Positions[_hover].Id == id;

    public override void PointerMove(float2 point)
    {
        _hover = RowAt(point);
        _onX = _hover >= 0 && point.X >= ScreenPosition.X + Size.X - X_WIDTH;
        Tooltip = _hover < 0 ? null : _onX ? "Delete this position" : library.Positions[_hover].Name;
    }

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        var i = RowAt(point);
        if (i < 0)
            return;
        var id = library.Positions[i].Id;
        if (point.X >= ScreenPosition.X + Size.X - X_WIDTH)
            DeleteRequested?.Invoke(id);
        else
            Picked?.Invoke(id);
    }

    public override void SecondaryClick(float2 point)
    {
        var i = RowAt(point);
        if (i >= 0)
            ContextRequested?.Invoke(library.Positions[i].Id, point);
    }

    public override void Draw(Batch batch)
    {
        if (!IsHovered)
            _hover = -1;
        for (var i = 0; i < library.Positions.Count; i++)
        {
            var p = library.Positions[i];
            var at = ScreenPosition + new float2(0, i * RowHeight);
            if (p.Id == SelectedId)
                batch.AddRect(at, new float2(Size.X, RowHeight), Theme.Fade(Theme.Selection, 0.7f));
            else if (i == _hover)
                batch.AddRect(at, new float2(Size.X, RowHeight), Theme.Tint(0.07f));
            Triangle(batch, at + new float2(6, (RowHeight - 14) / 2), 14, p.Colour);
            batch.PushClip(at, new float2(Size.X - (i == _hover ? X_WIDTH : 0) - 4, RowHeight));
            batch.AddText(font, p.Name, at + new float2(26, 3), Theme.Text);
            batch.PopClip();
            if (i == _hover)
            {
                var m = font.Measure("×");
                batch.AddText(font, "×", at + new float2(Size.X - X_WIDTH + (X_WIDTH - m.X) / 2, 3), _onX ? Theme.Text : Theme.Muted);
            }
        }
    }

    /// <summary>
    /// A position's icon: a small right-facing triangle in its colour, <paramref name="size"/> pixels square at
    /// <paramref name="at"/>.
    /// </summary>
    public static void Triangle(Batch batch, float2 at, float size, Common.Rgb colour)
    {
        var c = Colors.FromSrgb(colour.R, colour.G, colour.B);
        batch.AddTriangle(at + new float2(size * 0.85f, size * 0.5f), at + new float2(size * 0.15f, size * 0.12f), at + new float2(size * 0.15f, size * 0.88f), c);
    }
}

/// <summary>
/// A patched fixture with pan and tilt, as the position editor lists it.
/// </summary>
sealed record MovingFixture(string Uid, string Name);

/// <summary>
/// DJ Edit Mode's editor for one named position: a large square pan / tilt pad, and on its right the position's name
/// (after its colour's triangle) over a list of every fixture with pan and tilt. Select fixtures in the list (Ctrl or
/// Shift for several): their points are highlighted on the pad, in the position's colour, and dragging one moves them
/// all; the others' stay, dimmed, and clicking one selects its fixture. The selected fixtures are lit full white and
/// pointed at the position while the editor's showing (<see cref="Patch.SetHighlight"/>). Ctrl+C copies the selected
/// fixtures' points and Ctrl+V pastes them onto the selected ones, in list order, starting again from the first when
/// there are more of them (into any position). Right-click the list for Snap Together, Spread and Reset. sLight's
/// <c>PositionEditor</c>.
/// </summary>
sealed class PositionEditor : Region
{
    public const float SIDE_WIDTH = 260;
    const float GAP = 12;
    const float GRAB = 12;
    const float DOT = 7;

    /// <summary>
    /// The copied points, in list order: shared by every editor, so they paste into any position.
    /// </summary>
    static List<Vector2> _clip = [];

    readonly Fonts _fonts;
    readonly ContextMenu _menu;
    readonly Patch _patch;
    readonly Pad _pad;
    readonly TextBox _name;
    readonly Label _fixturesLabel;
    readonly ScrollPanel _listScroll;
    readonly Label _none;
    bool _showing;

    public PositionEditor(Fonts fonts, ContextMenu menu, PositionLibrary library, Patch patch)
    {
        _fonts = fonts;
        _menu = menu;
        Library = library;
        _patch = patch;
        _pad = Add(new Pad(this));
        _name = Add(new TextBox(fonts.Body) { Placeholder = "Position name" });
        _name.Submitted += _ => CommitName();
        _name.Cancelled += _ => ShowHeader();
        _fixturesLabel = Add(new Label(fonts.Body, "Fixtures with pan / tilt") { Color = Theme.Muted });
        List = new ListView<MovingFixture>(fonts.Body) { EmptyText = "", TextColor = Theme.Text };
        List.Columns.Add(new ListColumn<MovingFixture>("Fixture", SIDE_WIDTH - 14, f => f.Name));
        List.SelectionChanged += _ => SelectionChanged();
        List.ItemsSecondaryClicked += (_, at) => _menu.Show(at, MenuItems());
        _listScroll = Add(new ScrollPanel { Background = Theme.Well });
        _listScroll.Add(List);
        List.ScrollOwner = _listScroll;
        _none = Add(new Label(fonts.Body, "No patched fixture has pan and tilt channels.") { Color = Theme.Faint, Wrap = true, MaxWidth = SIDE_WIDTH });
        library.Changed += () =>
        {
            if (PositionId != "")
            {
                ShowHeader();
                Highlight();
            }
        };
    }

    public PositionLibrary Library { get; }
    public ListView<MovingFixture> List { get; }
    public TextBox NameBox => _name;
    public string PositionId { get; private set; } = "";

    /// <summary>
    /// The pad's square on screen (top-left, side).
    /// </summary>
    public (float2 At, float Side) PadRect => (_pad.ScreenPosition, _pad.Size.X);

    /// <summary>
    /// Shows position <paramref name="id"/> for editing.
    /// </summary>
    public void Open(string id)
    {
        PositionId = id;
        RefreshFixtures();
        ShowHeader();
    }

    /// <summary>
    /// Whether it's on screen: the selected fixtures are lit only while it is. Hidden, every one's put back.
    /// </summary>
    public bool Showing
    {
        get => _showing;
        set
        {
            if (_showing == value)
                return;
            _showing = value;
            Highlight();
        }
    }

    /// <summary>
    /// The patched fixtures with both pan and tilt, in patch order.
    /// </summary>
    public List<MovingFixture> MovingFixtures()
    {
        var output = new List<MovingFixture>();
        var universes = _patch.Universes;
        for (var ui = 0; ui < universes.Count; ui++)
        {
            foreach (var f in universes[ui].Fixtures)
            {
                var roles = f.Channels.Select(c => c.Role).ToHashSet();
                if (roles.Contains(Roles.Pan) && roles.Contains(Roles.Tilt))
                    output.Add(new MovingFixture(f.Uid, $"U{ui + 1} · {f.Name}"));
            }
        }
        return output;
    }

    /// <summary>
    /// Lists the pan / tilt fixtures again, keeping the selection.
    /// </summary>
    public void RefreshFixtures()
    {
        var keep = SelectedUids();
        List.Items = MovingFixtures();
        SelectUids(keep);
        _none.Visible = List.Items.Count == 0;
        Highlight();
    }

    /// <summary>
    /// The selected fixtures' uids, in list order.
    /// </summary>
    public List<string> SelectedUids() => [.. List.SelectedItems.Select(f => f.Uid)];

    public void SelectUids(IEnumerable<string> uids)
    {
        var set = uids.ToHashSet();
        List.SelectItems(List.Items.Where(f => set.Contains(f.Uid)));
    }

    /// <summary>
    /// The fixtures lit now: the selected ones while it's showing.
    /// </summary>
    public List<string> HighlightedUids() => Showing ? SelectedUids() : [];

    void SelectionChanged() => Highlight();

    /// <summary>
    /// Lights the selected fixtures, pointed at the position's points; puts the rest back.
    /// </summary>
    void Highlight()
    {
        var lit = HighlightedUids().ToHashSet();
        foreach (var f in _patch.All)
        {
            _patch.SetHighlight(f, lit.Contains(f.Uid));
            if (lit.Contains(f.Uid) && PositionId != "")
                _patch.Aim(f, Library.Point(PositionId, f.Uid));
        }
    }

    bool HasPosition => PositionId != "" && Library.Find(PositionId) is not null;

    /// <summary>
    /// Copies the selected fixtures' points. Returns whether there were any.
    /// </summary>
    public bool CopyPoints()
    {
        if (!HasPosition)
            return false;
        var pts = SelectedUids().Select(u => Library.Point(PositionId, u)).ToList();
        if (pts.Count == 0)
            return false;
        _clip = pts;
        return true;
    }

    /// <summary>
    /// Pastes the copied points onto the selected fixtures. Returns whether anything was pasted.
    /// </summary>
    public bool PastePoints()
    {
        if (!HasPosition || _clip.Count == 0)
            return false;
        var targets = SelectedUids();
        for (var i = 0; i < targets.Count; i++)
            Library.SetPoint(PositionId, targets[i], _clip[i % _clip.Count]);
        return targets.Count > 0;
    }

    public static bool HasCopiedPoints => _clip.Count > 0;

    /// <summary>
    /// Whether <see cref="CopyPoints"/> has anything to copy, for the Edit menu.
    /// </summary>
    public bool CanCopyPoints => HasPosition && SelectedUids().Count > 0;

    /// <summary>
    /// Whether <see cref="PastePoints"/> has somewhere to paste and something to paste there, for the Edit menu.
    /// </summary>
    public bool CanPastePoints => HasPosition && _clip.Count > 0 && SelectedUids().Count > 0;

    /// <summary>
    /// The list's right-click menu, on the selection: Snap Together (two or more), Spread (three or more), Reset.
    /// </summary>
    public List<MenuItem> MenuItems()
    {
        var n = SelectedUids().Count;
        return
        [
            new("Snap Together", () => SnapTogether(), n >= 2),
            new("Spread", () => Spread(), n >= 3),
            new("Reset", () => Reset(), n >= 1),
        ];
    }

    /// <summary>
    /// Every selected fixture to their average point. Returns whether it did (two or more selected).
    /// </summary>
    public bool SnapTogether()
    {
        var uids = SelectedUids();
        if (!HasPosition || uids.Count < 2)
            return false;
        var mid = Vector2.Zero;
        foreach (var u in uids)
            mid += Library.Point(PositionId, u);
        mid /= uids.Count;
        foreach (var u in uids)
            Library.SetPoint(PositionId, u, mid);
        return true;
    }

    /// <summary>
    /// Those between the first and last selected (in list order) evenly on the line between their points. Returns
    /// whether it did (three or more selected).
    /// </summary>
    public bool Spread()
    {
        var uids = SelectedUids();
        if (!HasPosition || uids.Count < 3)
            return false;
        var a = Library.Point(PositionId, uids[0]);
        var b = Library.Point(PositionId, uids[^1]);
        for (var i = 1; i < uids.Count - 1; i++)
            Library.SetPoint(PositionId, uids[i], Vector2.Lerp(a, b, (float)i / (uids.Count - 1)));
        return true;
    }

    /// <summary>
    /// The selected fixtures back to the default, centred.
    /// </summary>
    public bool Reset()
    {
        var uids = SelectedUids();
        if (!HasPosition || uids.Count == 0)
            return false;
        foreach (var u in uids)
            Library.ClearPoint(PositionId, u);
        return true;
    }

    /// <summary>
    /// Ctrl+C and Ctrl+V, while it's showing and the name isn't being typed in. Returns whether it used the key.
    /// </summary>
    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (!Showing || _name.IsFocused || (modifiers & Modifiers.Control) == 0 || (modifiers & (Modifiers.Shift | Modifiers.Alt)) != 0)
            return false;
        if (key == Key.C)
        {
            CopyPoints();
            return true;
        }
        if (key == Key.V)
        {
            PastePoints();
            return true;
        }
        return false;
    }

    void ShowHeader()
    {
        if (Library.Find(PositionId) is not { } p)
        {
            _name.Text = "";
            return;
        }
        if (!_name.IsFocused)
            _name.Text = p.Name;
    }

    void CommitName()
    {
        if (PositionId == "")
            return;
        Library.Rename(PositionId, _name.Text);
        _name.Text = Library.Find(PositionId)?.Name ?? _name.Text;
    }

    /// <summary>
    /// Takes focus in the name, its text selected, ready to type over (the list's Rename).
    /// </summary>
    public void FocusName(Root root)
    {
        root.Focus(_name, true);
        _name.SelectAll();
    }

    /// <summary>
    /// Drags the selected fixtures' points from where they were at the press, by a move on the pad of
    /// <paramref name="delta"/> (pad units: pan right, tilt up).
    /// </summary>
    internal void MoveSelected(Dictionary<string, Vector2> from, Vector2 delta)
    {
        foreach (var (uid, p) in from)
            Library.SetPoint(PositionId, uid, p + delta);
    }

    // ---------------------------------------------------------------- layout --

    protected override void MeasureContent()
    {
        var (w, h) = (Available.X, Available.Y);
        var side = Math.Min(w - SIDE_WIDTH - GAP, h);
        _pad.Side = Math.Max(side, 120);
        _pad.Measure();
        _name.Width = SIDE_WIDTH - 20;
        _name.Measure();
        _fixturesLabel.Measure();
        _none.Measure();
        var listH = Math.Max(h - _name.Size.Y - _fixturesLabel.Size.Y - 12 - (_none.Visible ? _none.Size.Y + 6 : 0), 60);
        _listScroll.MinSize = new float2(SIDE_WIDTH, listH);
        _listScroll.MaxWidth = SIDE_WIDTH;
        _listScroll.MaxHeight = listH;
        _listScroll.Measure();
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _pad.Arrange(position);
        var x = position.X + _pad.Size.X + GAP;
        _name.Arrange(new float2(x + 20, position.Y));
        var y = position.Y + _name.Size.Y + 6;
        _fixturesLabel.Arrange(new float2(x, y));
        y += _fixturesLabel.Size.Y + 6;
        _listScroll.Arrange(new float2(x, y));
        _none.Arrange(new float2(x, y + _listScroll.Size.Y + 6));
    }

    public override void Draw(Batch batch)
    {
        base.Draw(batch);
        if (Library.Find(PositionId) is { } p)
            PositionList.Triangle(batch, _name.ScreenPosition - new float2(18, -(_name.Size.Y - 14) / 2), 14, p.Colour);
    }

    /// <summary>
    /// The pad: every pan / tilt fixture's point in the position, the selected ones highlighted in its colour (with
    /// their names) and draggable together, the rest dimmed.
    /// </summary>
    sealed class Pad(PositionEditor editor) : Element
    {
        bool _drag;
        float2 _press;
        Dictionary<string, Vector2> _orig = [];

        public float Side { get; set; } = 400;

        public override bool IsInteractive => true;
        public override Cursor Cursor => Cursor.Arrow;

        public override float2 Measure() => Size = new float2(Side, Side);

        /// <summary>
        /// A point (0 to 1, tilt bottom-up) on screen, and back.
        /// </summary>
        public float2 ToPx(Vector2 v) => ScreenPosition + new float2(v.X * Size.X, (1 - v.Y) * Size.Y);

        public Vector2 ToValue(float2 p) => new((p.X - ScreenPosition.X) / Math.Max(Size.X, 1), 1 - (p.Y - ScreenPosition.Y) / Math.Max(Size.Y, 1));

        /// <summary>
        /// The fixture whose point is nearest <paramref name="p"/> within <see cref="GRAB"/> pixels: one of the
        /// selected (<paramref name="selected"/>), or of the others.
        /// </summary>
        string PointAt(float2 p, bool selected)
        {
            var sel = editor.SelectedUids();
            var best = "";
            var bestD = GRAB;
            foreach (var f in editor.MovingFixtures())
            {
                if (sel.Contains(f.Uid) != selected)
                    continue;
                var q = ToPx(editor.Library.Point(editor.PositionId, f.Uid));
                var d = MathF.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
                if (d <= bestD)
                {
                    best = f.Uid;
                    bestD = d;
                }
            }
            return best;
        }

        public override void PointerDown(float2 point, Modifiers modifiers)
        {
            if (!editor.HasPosition)
                return;
            if (PointAt(point, true) != "")
            {
                _drag = true;
                _press = point;
                _orig = editor.SelectedUids().ToDictionary(u => u, u => editor.Library.Point(editor.PositionId, u));
            }
            else if (PointAt(point, false) is { Length: > 0 } other)
                editor.SelectUids([other]);
        }

        public override void PointerDrag(float2 point)
        {
            if (_drag)
                editor.MoveSelected(_orig, ToValue(point) - ToValue(_press));
        }

        public override void PointerUp(float2 point) => _drag = false;

        public override void Draw(Batch batch)
        {
            var (p, w, h) = (ScreenPosition, Size.X, Size.Y);
            batch.AddRect(p, Size, Colors.FromSrgb(0.09f, 0.10f, 0.13f));
            var grid = Colors.Hex(0xFFFFFF, 0.06f);
            for (var i = 1; i < 8; i++)
            {
                batch.AddRect(p + new float2(w * i / 8, 0), new float2(1, h), grid);
                batch.AddRect(p + new float2(0, h * i / 8), new float2(w, 1), grid);
            }
            batch.AddRect(p + new float2(w / 2, 0), new float2(1, h), Colors.Hex(0xFFFFFF, 0.13f));
            batch.AddRect(p + new float2(0, h / 2), new float2(w, 1), Colors.Hex(0xFFFFFF, 0.13f));
            batch.AddBorder(p, Size, 1, Colors.Hex(0xFFFFFF, 0.18f));
            var small = editor._fonts.Small;
            batch.AddText(small, "Pan →", p + new float2(6, h - 6 - small.LineHeight), Colors.Hex(0xFFFFFF, 0.35f));
            batch.AddText(small, "Tilt ↑", p + new float2(6, 4), Colors.Hex(0xFFFFFF, 0.35f));
            if (editor.Library.Find(editor.PositionId) is not { } pos)
                return;
            var accent = Colors.FromSrgb(pos.Colour.R, pos.Colour.G, pos.Colour.B);
            var sel = editor.SelectedUids();
            var fixtures = editor.MovingFixtures();
            batch.PushClip(p, Size);
            // The others first, dimmed; the selected on top.
            foreach (var selected in new[] { false, true })
            {
                foreach (var f in fixtures)
                {
                    if (sel.Contains(f.Uid) != selected)
                        continue;
                    var q = ToPx(editor.Library.Point(editor.PositionId, f.Uid));
                    if (selected)
                    {
                        batch.AddRect(new float2(q.X, p.Y), new float2(1, h), Theme.Fade(accent, 0.3f));
                        batch.AddRect(new float2(p.X, q.Y), new float2(w, 1), Theme.Fade(accent, 0.3f));
                        batch.AddCircle(q, DOT + 1.5f, Colors.White, 20);
                        batch.AddCircle(q, DOT, accent, 20);
                        batch.AddText(editor._fonts.Body, f.Name, q + new float2(DOT + 4, -editor._fonts.Body.LineHeight / 2), Colors.White);
                    }
                    else
                    {
                        batch.AddCircle(q, DOT * 0.7f, Colors.FromSrgb(0.6f, 0.6f, 0.65f, 0.35f), 16);
                        batch.AddText(small, f.Name, q + new float2(DOT, -small.LineHeight / 2), Colors.Hex(0xFFFFFF, 0.25f));
                    }
                }
            }
            batch.PopClip();
        }
    }
}

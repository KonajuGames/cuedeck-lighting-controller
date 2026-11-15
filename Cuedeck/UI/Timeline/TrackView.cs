using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Timelines;

namespace Cuedeck.UI.Timeline;

/// <summary>
/// What a <see cref="TrackView"/> needs of the screen it's on: the timelines, the waveform whose view it shares, and
/// the screen's own answers for things that span every track.
/// </summary>
interface ITrackHost
{
    SongTimelines Timelines { get; }
    WaveView Wave { get; }

    /// <summary>
    /// The row (of <see cref="SongTimelines.RowList"/>) at screen y <paramref name="y"/>: the nearest on screen.
    /// </summary>
    int RowIndexAt(float y);

    /// <summary>
    /// The song time at screen x <paramref name="x"/>.
    /// </summary>
    double TimeAt(float x);

    /// <summary>
    /// The playhead line dragged on a track: 0 pressed, 1 moved, 2 let go, at <paramref name="t"/>.
    /// </summary>
    void PlayheadDrag(int phase, double t);

    /// <summary>
    /// A right-click on a track's row, at a point and time.
    /// </summary>
    void Context(TrackView track, TimelineRow row, float2 at, double t);

    /// <summary>
    /// A named position's colour, or null if it's been deleted.
    /// </summary>
    float4? PositionColour(string id);
}

/// <summary>
/// One timeline drawn: its rows top to bottom (dimmer, colour, movement, strobe, position), what's on them, the
/// selected areas, the beat grid and the playhead; collapsed, a bar with its name and M and S. A press on an empty
/// part selects (the screen works out the rows), on the playhead drags it, on a dimmer node or the line drags that
/// (Ctrl+click on the line adds a node, Ctrl+right-click a node removes it); a right-click opens the screen's menu.
/// sLight's <c>TimelineTrack</c>'s drawing and input.
/// </summary>
sealed class TrackView(Font small, Font label, ITrackHost host, TimelineTrack track) : Element
{
    public const float ROW_HEIGHT = 10;
    public const float DIMMER_HEIGHT = 80;
    public const float COLLAPSED_HEIGHT = 20;
    public const float COLLAPSED_BUTTON_WIDTH = 22;

    /// <summary>
    /// How near a selected area's end the pointer must be to drag it, and the playhead to drag that, pixels.
    /// </summary>
    public const float EDGE_GRAB = 5;
    public const float PLAYHEAD_GRAB = 4;

    /// <summary>
    /// How near a node or the line the pointer must be to take it, pixels; and the dimmer row's padding.
    /// </summary>
    const float GRAB = 6;
    const float DIMMER_PAD = 4;
    const float NODE_RADIUS = 3;
    const float NODE_HOVER_RADIUS = 4.5f;

    int _hover = -1; // the node under the pointer
    int _drag = -1; // the node being dragged
    bool _dragLine;
    double _lineT;
    float2 _press;
    List<Vector2> _orig = [];
    bool _selecting;
    bool _scrubbing;
    Cursor _cursor = Cursor.Arrow;

    public TimelineTrack Track => track;

    public float Width { get; set; } = 400;

    /// <summary>
    /// Its fixture's colour on the collapsed bar (as on its name plate).
    /// </summary>
    public float4 PlateFill { get; set; } = AppTheme.PlateFill;

    /// <summary>
    /// The screen rectangle it may draw in (the fixtures' scroll panel), or null for anywhere.
    /// </summary>
    public (float2 Min, float2 Max)? Clip { get; set; }

    public bool IsSelecting => _selecting;

    public override bool IsInteractive => true;
    public override Cursor Cursor => _cursor;

    static float RowHeight(TimelineRow r) => r == TimelineRow.Dimmer ? DIMMER_HEIGHT : ROW_HEIGHT;

    public float FullHeight => track.Rows.Sum(RowHeight);

    public override float2 Measure() => Size = new float2(Width, track.Collapsed ? COLLAPSED_HEIGHT : FullHeight);

    /// <summary>
    /// A row's rectangle, relative to the track's top-left: (y, height); empty if collapsed or it hasn't the row.
    /// </summary>
    public (float Y, float H) RowRect(TimelineRow r)
    {
        if (track.Collapsed || !track.HasRow(r))
            return (0, 0);
        var y = Size.Y > 0 ? Size.Y : FullHeight;
        foreach (var row in track.Rows) // bottom to top
        {
            var h = RowHeight(row);
            y -= h;
            if (row == r)
                return (y, h);
        }
        return (0, 0);
    }

    /// <summary>
    /// The row at <paramref name="y"/> pixels from the top, or null.
    /// </summary>
    public TimelineRow? RowAt(float y)
    {
        foreach (var r in track.Rows)
        {
            var (ry, rh) = RowRect(r);
            if (rh > 0 && y >= ry && y < ry + rh)
                return r;
        }
        return null;
    }

    (double Start, double Length) View => host.Wave.Analysis is null ? (0, 1) : (host.Wave.ViewStart, host.Wave.VisibleLength());

    public double TimeToX(double t) => (t - View.Start) / View.Length * Size.X;

    public double XToTime(double x) => View.Start + x / Math.Max(Size.X, 1) * View.Length;

    float LevelY(float rowY, float rowH, double level) => (float)(rowY + rowH - DIMMER_PAD - Math.Clamp(level, 0, 1) * (rowH - 2 * DIMMER_PAD));

    bool DimmerEditing => track.HasRow(TimelineRow.Dimmer);

    /// <summary>
    /// The node within <see cref="GRAB"/> pixels of <paramref name="local"/>, or -1.
    /// </summary>
    public int NodeAt(float2 local)
    {
        var (ry, rh) = RowRect(TimelineRow.Dimmer);
        if (rh <= 0)
            return -1;
        var best = -1;
        var bestD = GRAB;
        for (var i = 0; i < track.DimmerNodes.Count; i++)
        {
            var p = track.DimmerNodes[i];
            var d = Distance(local, new float2((float)TimeToX(p.X), LevelY(ry, rh, p.Y)));
            if (d <= bestD)
            {
                best = i;
                bestD = d;
            }
        }
        return best;
    }

    static float Distance(float2 a, float2 b)
    {
        var d = a - b;
        return MathF.Sqrt(d.X * d.X + d.Y * d.Y);
    }

    /// <summary>
    /// Whether <paramref name="local"/> is on the dimmer line (within <see cref="GRAB"/> pixels of it, where it has
    /// values).
    /// </summary>
    public bool OnLine(float2 local)
    {
        var (ry, rh) = RowRect(TimelineRow.Dimmer);
        if (rh <= 0 || local.Y < ry || local.Y >= ry + rh || local.X < 0 || local.X >= Size.X)
            return false;
        var t = XToTime(local.X);
        if (!track.DimmerDefinedAt(t))
            return false;
        return Math.Abs(local.Y - LevelY(ry, rh, track.DimmerAt(t))) <= GRAB;
    }

    /// <summary>
    /// The playhead's x across the rows, or -1 with none (nothing playable, or out of view).
    /// </summary>
    public double PlayheadX()
    {
        var w = host.Wave;
        if (w.Analysis is null || !w.ShowCursor)
            return -1;
        var x = TimeToX(w.SongPos);
        return x >= 0 && x <= Size.X ? x : -1;
    }

    public bool OnPlayhead(float2 local)
    {
        var x = PlayheadX();
        return x >= 0 && Math.Abs(local.X - x) <= PLAYHEAD_GRAB && RowAt(local.Y) is not null;
    }

    public bool OnSelectionEdge(float2 local)
    {
        if (RowAt(local.Y) is not { } row)
            return false;
        foreach (var (t0, t1) in host.Timelines.Selection.SpansFor(track.Key, row))
            if (Math.Abs(local.X - TimeToX(t0)) <= EDGE_GRAB || Math.Abs(local.X - TimeToX(t1)) <= EDGE_GRAB)
                return true;
        return false;
    }

    public bool InSelection(float2 local) => RowAt(local.Y) is { } row && host.Timelines.Selection.Has(track.Key, row, XToTime(local.X));

    /// <summary>
    /// An M or S button's rectangle on the collapsed bar, relative to the track: side by side at its right end.
    /// </summary>
    public (float2 Pos, float2 Size)? CollapsedButtonRect(int which)
    {
        var bs = NamePlate.ButtonsFor(track);
        var i = Array.IndexOf(bs, which);
        if (!track.Collapsed || i < 0)
            return null;
        var g = NamePlate.BUTTON_GAP;
        var h = MathF.Min(NamePlate.BUTTON_HEIGHT, Size.Y - 2 * g);
        var x = Size.X - (bs.Length - i) * (COLLAPSED_BUTTON_WIDTH + g);
        return (new float2(x, (Size.Y - h) / 2), new float2(COLLAPSED_BUTTON_WIDTH, h));
    }

    int CollapsedButtonAt(float2 local)
    {
        foreach (var b in NamePlate.ButtonsFor(track))
            if (CollapsedButtonRect(b) is { } r && local.X >= r.Pos.X && local.X < r.Pos.X + r.Size.X && local.Y >= r.Pos.Y && local.Y < r.Pos.Y + r.Size.Y)
                return b;
        return -1;
    }

    /// <summary>
    /// The name on the collapsed bar: the plate's lines on one ("Bar 1 · Head 2").
    /// </summary>
    public string CollapsedName => string.Join(" · ", track.Title.Split('\n', StringSplitOptions.RemoveEmptyEntries));

    // ------------------------------------------------------------- input --

    public override void PointerMove(float2 point)
    {
        var local = point - ScreenPosition;
        if (track.Collapsed)
        {
            _cursor = Cursor.Hand;
            Tooltip = CollapsedButtonAt(local) is var b && b >= 0 ? NamePlate.ButtonTip(track, b) : "Click to expand";
            return;
        }
        Tooltip = null;
        SetHover(DimmerEditing ? NodeAt(local) : -1, DimmerEditing && OnLine(local), local);
    }

    void SetHover(int node, bool line, float2 local)
    {
        _hover = node;
        _cursor =
            node >= 0 ? Cursor.Hand
            : OnPlayhead(local) || OnSelectionEdge(local) ? Cursor.ResizeHorizontal
            : line ? Cursor.ResizeVertical
            : InSelection(local) ? Cursor.Move
            : Cursor.Arrow;
    }

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        var local = point - ScreenPosition;
        var ctrl = (modifiers & Modifiers.Control) != 0;
        var shift = (modifiers & Modifiers.Shift) != 0;
        if (track.Collapsed)
        {
            var b = CollapsedButtonAt(local);
            if (b >= 0)
                NamePlate.Toggle(host.Timelines, track, b);
            else
                host.Timelines.SetCollapsed(track, false);
            return;
        }
        var plain = !ctrl && !shift;
        if (plain && OnPlayhead(local) && !(DimmerEditing && NodeAt(local) >= 0))
        {
            _scrubbing = true;
            host.PlayheadDrag(0, XToTime(local.X));
            return;
        }
        var edge = plain && OnSelectionEdge(local);
        var special = DimmerEditing && (NodeAt(local) >= 0 || OnLine(local) && !edge);
        if (!special && RowAt(local.Y) is not null)
        {
            // Empty timeline: start selecting an area.
            _selecting = true;
            var tl = host.Timelines;
            tl.SelectPress(host.RowIndexAt(point.Y), host.TimeAt(point.X), ctrl, shift, EDGE_GRAB * View.Length / Math.Max(Size.X, 1));
            return;
        }
        if (!DimmerEditing)
            return;
        var i = NodeAt(local);
        if (i < 0 && ctrl && OnLine(local))
        {
            i = track.AddNodeAt(XToTime(local.X));
            host.Timelines.MarkEdited();
        }
        _press = local;
        _orig = [.. track.DimmerNodes];
        if (i >= 0)
        {
            _drag = i;
            _hover = i;
            _lockAxis = shift;
        }
        else if (!ctrl && OnLine(local))
        {
            _lineT = XToTime(local.X);
            _dragLine = true;
        }
    }

    bool _lockAxis;

    public override void PointerDrag(float2 point)
    {
        var local = point - ScreenPosition;
        if (_scrubbing)
            host.PlayheadDrag(1, XToTime(local.X));
        else if (_selecting)
            host.Timelines.SelectMove(host.RowIndexAt(point.Y), host.TimeAt(point.X));
        else if (_drag >= 0)
            DragNode(local);
        else if (_dragLine)
        {
            var (_, rh) = RowRect(TimelineRow.Dimmer);
            track.MoveLine(_lineT, -(local.Y - _press.Y) / Math.Max(rh - 2 * DIMMER_PAD, 1), _orig);
        }
    }

    public override void PointerUp(float2 point)
    {
        var local = point - ScreenPosition;
        if (_scrubbing)
        {
            _scrubbing = false;
            host.PlayheadDrag(2, XToTime(local.X));
        }
        else if (_selecting)
        {
            _selecting = false;
            host.Timelines.SelectRelease(host.RowIndexAt(point.Y), host.TimeAt(point.X));
        }
        else if (_drag >= 0 || _dragLine)
        {
            var moved = !track.DimmerNodes.SequenceEqual(_orig);
            _drag = -1;
            _dragLine = false;
            if (moved)
                host.Timelines.MarkEdited();
        }
    }

    /// <summary>
    /// Drags node <see cref="_drag"/> by the pointer's movement since the press (so it doesn't jump to the pointer);
    /// with Shift held at the press, only along the axis the pointer's moved furthest on.
    /// </summary>
    void DragNode(float2 local)
    {
        var (ry, rh) = RowRect(TimelineRow.Dimmer);
        var o = _orig[_drag];
        var delta = local - _press;
        if (_lockAxis)
        {
            if (MathF.Abs(delta.X) >= MathF.Abs(delta.Y))
                delta = delta with { Y = 0 };
            else
                delta = delta with { X = 0 };
        }
        var at = new float2((float)TimeToX(o.X), LevelY(ry, rh, o.Y)) + delta;
        var span = Math.Max(rh - 2 * DIMMER_PAD, 1);
        track.MoveNode(_drag, XToTime(at.X), (ry + rh - DIMMER_PAD - at.Y) / span);
        if (_lockAxis)
        {
            var p = track.DimmerNodes[_drag];
            track.DimmerNodes[_drag] = new Vector2(delta.X == 0 ? o.X : p.X, delta.Y == 0 ? o.Y : p.Y);
        }
    }

    public override void SecondaryClick(float2 point, Modifiers modifiers)
    {
        if (track.Collapsed)
            return;
        var local = point - ScreenPosition;
        var ctrl = (modifiers & Modifiers.Control) != 0;
        if (ctrl && DimmerEditing && NodeAt(local) is var i && i >= 0)
        {
            _drag = -1;
            _dragLine = false;
            track.RemoveNode(i);
            host.Timelines.MarkEdited();
            return;
        }
        if (RowAt(local.Y) is { } row)
            host.Context(this, row, point, XToTime(local.X));
    }

    // ------------------------------------------------------------- drawing --

    public override void Draw(Batch batch)
    {
        if (Clip is { } c)
        {
            if (ScreenPosition.Y + Size.Y < c.Min.Y || ScreenPosition.Y > c.Max.Y)
                return; // scrolled out of view
        }
        var p = ScreenPosition;
        batch.PushClip(p, Size);
        if (track.Collapsed)
        {
            DrawCollapsed(batch);
            batch.PopClip();
            return;
        }
        var i = 0;
        foreach (var row in track.Rows)
        {
            var (ry, rh) = RowRect(row);
            batch.AddRect(p + new float2(0, ry), new float2(Size.X, rh), i % 2 == 0 ? AppTheme.TrackRowA : AppTheme.TrackRowB);
            batch.AddRect(p + new float2(0, ry), new float2(Size.X, 1), AppTheme.TrackSeparator);
            i++;
        }
        DrawGrid(batch);
        foreach (var row in track.Rows)
        {
            var (ry, rh) = RowRect(row);
            switch (row)
            {
                case TimelineRow.Dimmer:
                    DrawDimmer(batch, ry, rh);
                    break;
                case TimelineRow.Colour:
                    DrawColour(batch, ry, rh, track.ColourSegments, 0, 1);
                    break;
                case TimelineRow.Strobe:
                    DrawStrobe(batch, ry, rh, track.StrobeSegments, 0, AppTheme.TrackStrobe);
                    break;
                case TimelineRow.Position:
                    DrawPosition(batch, ry, rh, track.PositionSegments, 0, 1);
                    break;
                case TimelineRow.Movement:
                    DrawMovement(batch, ry, rh, track.MovementSegments, 0, 1);
                    break;
            }
            var font = row == TimelineRow.Dimmer ? label : small;
            var band = Math.Min(rh, 14);
            if (font.LineHeight <= rh + 2)
                batch.AddText(font, TimelineTrack.RowNames[row], p + new float2(4, ry + (band - font.LineHeight) / 2), AppTheme.TrackRowName);
            if (track.Ghost is not null)
                DrawGhost(batch, row, ry, rh);
            DrawSelection(batch, row, ry, rh);
        }
        var x = PlayheadX();
        if (x >= 0)
            batch.AddRect(p + new float2((float)x - 1, 0), new float2(2, Size.Y), host.Wave.CursorColor);
        if (track.Silenced)
            batch.AddRect(p, Size, AppTheme.TrackSilenced);
        batch.PopClip();
    }

    void DrawCollapsed(Batch batch)
    {
        var fill = track.Target == TimelineTarget.Head ? NamePlate.Darken(PlateFill) : PlateFill;
        batch.AddRect(ScreenPosition, Size, fill);
        batch.AddBorder(ScreenPosition, Size, 1, AppTheme.PlateBorder);
        foreach (var b in NamePlate.ButtonsFor(track))
            if (CollapsedButtonRect(b) is { } r)
                NamePlate.DrawButton(batch, small, ScreenPosition + r.Pos, r.Size, b, b == NamePlate.MUTE ? track.Mute : track.Solo);
        var room = (CollapsedButtonRect(NamePlate.MUTE)?.Pos.X ?? Size.X) - 12;
        batch.PushClip(ScreenPosition, new float2(Math.Max(room + 6, 0), Size.Y));
        batch.AddText(label, CollapsedName, ScreenPosition + new float2(6, (Size.Y - label.LineHeight) / 2), track.Silenced ? Theme.Fade(AppTheme.PlateText, 0.4f) : AppTheme.PlateText);
        batch.PopClip();
    }

    void DrawGrid(Batch batch)
    {
        var w = host.Wave;
        if (w.Analysis is null)
            return;
        var (start, length) = View;
        // The waveform's lines, at the track's scale.
        var scale = Size.X / Math.Max(w.Size.X > 0 ? w.Size.X : w.Want.X, 1);
        foreach (var (x, isBar) in w.GridLines(start, start + length))
            batch.AddRect(ScreenPosition + new float2((float)(x * scale), 0), new float2(1, Size.Y), isBar ? AppTheme.TrackGridBar : AppTheme.TrackGridBeat);
    }

    void DrawSelection(Batch batch, TimelineRow row, float ry, float rh)
    {
        var p = ScreenPosition;
        foreach (var (t0, t1) in host.Timelines.Selection.SpansFor(track.Key, row))
        {
            if (TimeToX(t1) < 0 || TimeToX(t0) > Size.X)
                continue;
            var x0 = (float)Math.Clamp(TimeToX(t0), 0, Size.X);
            var x1 = (float)Math.Clamp(TimeToX(t1), 0, Size.X);
            batch.AddRect(p + new float2(x0, ry), new float2(Math.Max(x1 - x0, 1), rh), AppTheme.TrackSelectFill);
            foreach (var t in new[] { t0, t1 })
            {
                var x = (float)TimeToX(t);
                if (x >= 0 && x <= Size.X)
                    batch.AddRect(p + new float2(x, ry), new float2(1, rh), AppTheme.TrackSelectEdge);
            }
        }
    }

    /// <summary>
    /// The dimmer line's points across the view (the view's edges and every node in it), relative to the track: only
    /// where it has values (a fixture's or head's, first node to last).
    /// </summary>
    public List<float2> DimmerPolyline()
    {
        var output = new List<float2>();
        var (ry, rh) = RowRect(TimelineRow.Dimmer);
        if (rh <= 0)
            return output;
        var (start, length) = View;
        if (track.DimmerDefinedSpan(start, start + length) is not { } span)
            return output;
        var (t0, t1) = span;
        var inherits = track.Inherits;
        output.Add(new float2(inherits ? (float)TimeToX(t0) : 0, LevelY(ry, rh, inherits ? track.DimmerAfter(t0) : track.DimmerAt(t0))));
        foreach (var n in track.DimmerNodes)
            if (n.X > t0 && n.X < t1)
                output.Add(new float2((float)TimeToX(n.X), LevelY(ry, rh, n.Y)));
        output.Add(new float2(inherits ? (float)TimeToX(t1) : Size.X, LevelY(ry, rh, track.DimmerAt(t1))));
        return output;
    }

    void DrawDimmer(Batch batch, float ry, float rh)
    {
        var line = DimmerPolyline();
        if (line.Count < 2)
            return;
        var p = ScreenPosition;
        var bottom = ry + rh;
        float4 Fill(float y) => Theme.Fade(Colors.White, 0.5f * Math.Clamp((bottom - y) / rh, 0, 1));
        for (var k = 0; k + 1 < line.Count; k++)
        {
            var a = line[k];
            var b = line[k + 1];
            batch.AddTriangle(p + a, p + b, p + new float2(b.X, bottom), Fill(a.Y), Fill(b.Y), Fill(bottom));
            batch.AddTriangle(p + a, p + new float2(b.X, bottom), p + new float2(a.X, bottom), Fill(a.Y), Fill(bottom), Fill(bottom));
        }
        var pts = line.Select(q => p + q).ToArray();
        batch.AddSmoothPolyline(pts, 1.5f, AppTheme.TrackLine);
        var (start, length) = View;
        for (var i = 0; i < track.DimmerNodes.Count; i++)
        {
            var n = track.DimmerNodes[i];
            if (n.X >= start && n.X <= start + length)
                batch.AddSmoothCircle(p + new float2((float)TimeToX(n.X), LevelY(ry, rh, n.Y)), i == _hover || i == _drag ? NODE_HOVER_RADIUS : NODE_RADIUS, AppTheme.TrackLine);
        }
    }

    void DrawColour(Batch batch, float ry, float rh, IEnumerable<ColourSpan> segs, double shift, float alpha)
    {
        var (start, length) = View;
        var p = ScreenPosition;
        foreach (var sg in segs)
        {
            var (s0, s1) = (sg.T0 + shift, sg.T1 + shift);
            if (s1 < start || s0 > start + length)
                continue;
            var a = Math.Max(s0, start);
            var b = Math.Min(s1, start + length);
            var span = s1 - s0;
            var ca = ColourSpan.Lerp(sg.C0, sg.C1, span <= 0 ? 0 : (a - s0) / span);
            var cb = ColourSpan.Lerp(sg.C0, sg.C1, span <= 0 ? 0 : (b - s0) / span);
            var fa = Colors.FromSrgb(ca.R, ca.G, ca.B, alpha);
            var fb = Colors.FromSrgb(cb.R, cb.G, cb.B, alpha);
            var x0 = (float)TimeToX(a);
            var x1 = Math.Max((float)TimeToX(b), x0 + 1);
            float2 tl = p + new float2(x0, ry + 1), tr = p + new float2(x1, ry + 1), br = p + new float2(x1, ry + rh), bl = p + new float2(x0, ry + rh);
            batch.AddTriangle(tl, tr, br, fa, fb, fb);
            batch.AddTriangle(tl, br, bl, fa, fb, fa);
        }
    }

    /// <summary>
    /// The y of a strobe's dashed line in its row: the bottom for slow, the middle for medium, the top for fast.
    /// </summary>
    static float StrobeLineY(float ry, float rh, StrobeSpeed speed) =>
        speed switch
        {
            StrobeSpeed.Slow => ry + rh - 1.5f,
            StrobeSpeed.Fast => ry + 2,
            _ => ry + rh * 0.5f,
        };

    void DrawStrobe(Batch batch, float ry, float rh, IEnumerable<StrobeSpan> segs, double shift, float4 col)
    {
        var (start, length) = View;
        var p = ScreenPosition;
        foreach (var sg in segs)
        {
            var (a, b) = (sg.T0 + shift, sg.T1 + shift);
            if (b < start || a > start + length)
                continue;
            var mid = StrobeLineY(ry, rh, sg.Speed);
            var x0 = (float)TimeToX(a);
            var x1 = (float)TimeToX(b);
            foreach (var x in new[] { x0, x1 })
                if (x >= 0 && x <= Size.X)
                    batch.AddLine(p + new float2(x, ry + 1), p + new float2(x, ry + rh), 1.5f, col);
            var d0 = Math.Clamp(x0, 0, Size.X);
            var d1 = Math.Clamp(x1, 0, Size.X);
            if (d1 > d0)
                batch.AddDashedLine(p + new float2(d0, mid), p + new float2(d1, mid), 1, 3, col);
        }
    }

    void DrawPosition(Batch batch, float ry, float rh, IEnumerable<PositionSpan> segs, double shift, float alpha)
    {
        var (start, length) = View;
        var p = ScreenPosition;
        var mid = ry + rh * 0.5f;
        var h = rh - 2;
        foreach (var sg in segs)
        {
            var (a, b) = (sg.T0 + shift, sg.T1 + shift);
            if (b < start || a > start + length)
                continue;
            var col = host.PositionColour(sg.Position) ?? AppTheme.TrackPositionGone;
            col = Theme.Fade(col, alpha);
            var faint = Theme.Fade(col, 0.7f);
            var x0 = (float)TimeToX(a);
            var x1 = (float)TimeToX(b);
            var d0 = Math.Clamp(x0 + h * 0.7f, 0, Size.X);
            var d1 = Math.Clamp(x1, 0, Size.X);
            if (d1 > d0)
                batch.AddLine(p + new float2(d0, mid), p + new float2(d1, mid), 2, faint);
            if (x1 >= 0 && x1 <= Size.X)
                batch.AddLine(p + new float2(x1, ry + 2), p + new float2(x1, ry + rh - 1), 1, faint);
            if (x0 + h >= 0 && x0 <= Size.X)
                batch.AddTriangle(p + new float2(x0, ry + 1), p + new float2(x0 + h * 0.85f, mid), p + new float2(x0, ry + rh - 1), col);
        }
    }

    void DrawMovement(Batch batch, float ry, float rh, IEnumerable<MovementSpan> segs, double shift, float alpha)
    {
        var (start, length) = View;
        var p = ScreenPosition;
        var mid = ry + rh * 0.5f;
        var rad = (rh - 2) * 0.5f;
        var col = Theme.Fade(AppTheme.TrackMovement, alpha);
        var faint = Theme.Fade(col, 0.6f);
        foreach (var sg in segs)
        {
            var (a, b) = (sg.T0 + shift, sg.T1 + shift);
            if (b < start || a > start + length)
                continue;
            var x0 = (float)TimeToX(a);
            var x1 = (float)TimeToX(b);
            var c = p + new float2(x0 + rad + 1, mid);
            var d0 = Math.Clamp(x0 + 2 * rad + 3, 0, Size.X);
            var d1 = Math.Clamp(x1, 0, Size.X);
            if (d1 > d0)
            {
                if (sg.Effect == "none")
                    batch.AddDashedLine(p + new float2(d0, mid), p + new float2(d1, mid), 1, 3, faint);
                else
                    batch.AddLine(p + new float2(d0, mid), p + new float2(d1, mid), 1, faint);
            }
            if (x1 >= 0 && x1 <= Size.X)
                batch.AddLine(p + new float2(x1, ry + 2), p + new float2(x1, ry + rh - 1), 1, faint);
            if (c.X + rad < p.X || c.X - rad > p.X + Size.X)
                continue;
            if (sg.Effect == "none")
            {
                batch.AddCircle(c, 1.5f, col, 8);
                continue;
            }
            // The shape itself, traced once round: bigger for a bigger movement.
            var scale = rad * (0.55f + 0.45f * (float)Math.Clamp(sg.Size / MovementSpan.SIZE_MAX, 0, 1));
            var unit = new MovementSpan(0, 1, sg.Effect, 1, 1);
            var pts = new float2[25];
            for (var i = 0; i < 25; i++)
            {
                var (ox, oy) = unit.Offset(i / 24.0);
                pts[i] = c + new float2((float)ox, -(float)oy) * scale;
            }
            batch.AddPolyline(pts, 1.2f, col);
        }
    }

    /// <summary>
    /// A moving selection's ghost on this row: the stretch it's leaving dimmed, its content drawn faintly where it
    /// would land.
    /// </summary>
    void DrawGhost(Batch batch, TimelineRow row, float ry, float rh)
    {
        if (track.Ghost is not { } ghost || !ghost.Rows.TryGetValue(row, out var parts))
            return;
        var p = ScreenPosition;
        foreach (var part in parts)
        {
            var x0 = (float)Math.Clamp(TimeToX(ghost.From + part.T0), 0, Size.X);
            var x1 = (float)Math.Clamp(TimeToX(ghost.From + part.T1), 0, Size.X);
            if (x1 > x0)
                batch.AddRect(p + new float2(x0, ry), new float2(x1 - x0, rh), AppTheme.TrackGhostDim);
        }
        foreach (var part in parts)
        {
            var start = ghost.At + part.T0;
            if (part.Nodes is { Count: >= 2 } nodes)
            {
                var line = nodes.Select(n => p + new float2((float)TimeToX(start + n.X), LevelY(ry, rh, n.Y))).ToArray();
                var bottom = p.Y + ry + rh;
                var fill = Theme.Fade(Colors.White, 0.18f);
                for (var k = 0; k + 1 < line.Length; k++)
                {
                    batch.AddTriangle(line[k], line[k + 1], new float2(line[k + 1].X, bottom), fill);
                    batch.AddTriangle(line[k], new float2(line[k + 1].X, bottom), new float2(line[k].X, bottom), fill);
                    batch.AddDashedLine(line[k], line[k + 1], 1.5f, 4, AppTheme.TrackGhostLine);
                }
            }
            if (part.Strobes is { } strobes)
                DrawStrobe(batch, ry, rh, strobes, start, AppTheme.TrackGhostLine);
            if (part.Positions is { } positions)
                DrawPosition(batch, ry, rh, positions, start, 0.6f);
            if (part.Movements is { } movements)
                DrawMovement(batch, ry, rh, movements, start, 0.6f);
            if (part.Colours is { } colours)
                DrawColour(batch, ry, rh, colours, start, 0.6f);
            var gx0 = (float)TimeToX(start);
            var gx1 = (float)TimeToX(ghost.At + part.T1);
            batch.AddBorder(p + new float2(gx0, ry), new float2(Math.Max(gx1 - gx0, 1), rh), 1, AppTheme.TrackGhostLine);
        }
    }
}

/// <summary>
/// The plate beside a track: a filled rectangle as tall as it, with M and S (just M on the master's) along its top and
/// its name written up it below them. A click elsewhere on a fixture's or head's plate collapses its track.
/// sLight's <c>TimelineTrack.NamePlate</c>.
/// </summary>
sealed class NamePlate(Font font, Font small, SongTimelines timelines, TimelineTrack track) : Element
{
    public const int MUTE = 0;
    public const int SOLO = 1;
    public const float BUTTON_HEIGHT = 14;
    public const float BUTTON_GAP = 2;

    /// <summary>
    /// A head's plate is this much darker than its fixture's.
    /// </summary>
    const float HEAD_DARKEN = 0.25f;

    public TimelineTrack Track => track;

    public float2 Want { get; set; } = new(48, 120);

    /// <summary>
    /// The plate's colour: its fixture's model colour.
    /// </summary>
    public float4 Fill { get; set; } = AppTheme.PlateFill;

    public override bool IsInteractive => true;

    public override Cursor Cursor => Collapsible ? Cursor.Hand : Cursor.Arrow;

    public static int[] ButtonsFor(TimelineTrack tr) => tr.Target == TimelineTarget.Master ? [MUTE] : [MUTE, SOLO];

    public int[] Buttons => ButtonsFor(track);

    /// <summary>
    /// Whether a click on it collapses its track (a fixture's or a head's; not the master's).
    /// </summary>
    public bool Collapsible => track.Target != TimelineTarget.Master;

    public static float4 Darken(float4 c) => c with { X = c.X * (1 - HEAD_DARKEN), Y = c.Y * (1 - HEAD_DARKEN), Z = c.Z * (1 - HEAD_DARKEN) };

    public static string ButtonTip(TimelineTrack tr, int which) =>
        which == MUTE
            ? "Mute: leave out of playback"
                + (
                    tr.Target == TimelineTarget.Master ? " (every fixture)"
                    : tr.Target == TimelineTarget.Fixture ? " (its heads too)"
                    : ""
                )
            : "Solo: play only this" + (tr.Target == TimelineTarget.Fixture ? " fixture (its unmuted heads too)" : " head");

    /// <summary>
    /// Toggles mute or solo on a track, as the user.
    /// </summary>
    public static void Toggle(SongTimelines timelines, TimelineTrack tr, int which)
    {
        if (which == MUTE)
            timelines.SetMute(tr, !tr.Mute);
        else
            timelines.SetSolo(tr, !tr.Solo);
    }

    /// <summary>
    /// A button's rectangle relative to the plate: side by side across the top.
    /// </summary>
    public (float2 Pos, float2 Size)? ButtonRect(int which)
    {
        var bs = Buttons;
        var i = Array.IndexOf(bs, which);
        if (i < 0)
            return null;
        var size = Size.X > 0 ? Size : Want;
        var h = MathF.Min(BUTTON_HEIGHT, MathF.Max(size.Y - 2 * BUTTON_GAP, 4));
        var w = (size.X - BUTTON_GAP * (bs.Length + 1)) / bs.Length;
        return (new float2(BUTTON_GAP + i * (w + BUTTON_GAP), BUTTON_GAP), new float2(w, h));
    }

    public int ButtonAt(float2 local)
    {
        foreach (var b in Buttons)
            if (ButtonRect(b) is { } r && local.X >= r.Pos.X && local.X < r.Pos.X + r.Size.X && local.Y >= r.Pos.Y && local.Y < r.Pos.Y + r.Size.Y)
                return b;
        return -1;
    }

    /// <summary>
    /// The space below the buttons, where the name goes: (y, height).
    /// </summary>
    public (float Y, float H) NameRect()
    {
        var top = ButtonRect(MUTE) is { } r ? r.Pos.Y + r.Size.Y : 0;
        var size = Size.X > 0 ? Size : Want;
        return (top, MathF.Max(size.Y - top, 0));
    }

    public string[] Lines => track.Title.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public override float2 Measure() => Size = Want;

    public override void PointerMove(float2 point)
    {
        var b = ButtonAt(point - ScreenPosition);
        Tooltip = b >= 0 ? ButtonTip(track, b) : Collapsible ? "Click to collapse" : null;
    }

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        var b = ButtonAt(point - ScreenPosition);
        if (b >= 0)
            Toggle(timelines, track, b);
        else if (Collapsible)
            timelines.SetCollapsed(track, true);
    }

    public static void DrawButton(Batch batch, Font font, float2 pos, float2 size, int which, bool on)
    {
        batch.AddRect(pos, size, on ? which == MUTE ? AppTheme.PlateMuteOn : AppTheme.PlateSoloOn : AppTheme.PlateButtonOff);
        batch.AddBorder(pos, size, 1, AppTheme.PlateBorder);
        var letter = which == MUTE ? "M" : "S";
        var m = font.Measure(letter);
        batch.AddText(font, letter, pos + (size - m) / 2, on ? AppTheme.PlateButtonTextOn : AppTheme.PlateText);
    }

    public override void Draw(Batch batch)
    {
        var p = ScreenPosition;
        batch.AddRect(p, Size, track.Target == TimelineTarget.Head ? Darken(Fill) : Fill);
        batch.AddBorder(p, Size, 1, AppTheme.PlateBorder);
        foreach (var b in Buttons)
            if (ButtonRect(b) is { } r)
                DrawButton(batch, small, p + r.Pos, r.Size, b, b == MUTE ? track.Mute : track.Solo);
        var lines = Lines;
        if (lines.Length == 0)
            return;
        // A quarter turn anticlockwise, centred on the space below the buttons: the text runs up the plate, each
        // further line to the right of the one before; trimmed if it's too long.
        var (ny, nh) = NameRect();
        var room = MathF.Max(nh - 8, 0);
        var colour = track.Silenced ? Theme.Fade(AppTheme.PlateText, 0.4f) : AppTheme.PlateText;
        var centre = p + new float2(Size.X / 2, ny + nh / 2);
        var x = centre.X - font.LineHeight * lines.Length / 2;
        batch.PushClip(p + new float2(0, ny + 4), new float2(Size.X, room));
        foreach (var line in lines)
        {
            var w = MathF.Min(font.Measure(line).X, room);
            batch.AddTextUp(font, line, new float2(x, centre.Y + w / 2), colour);
            x += font.LineHeight;
        }
        batch.PopClip();
    }
}

using Brutal.Numerics;
using Cuedeck.Audio;
using Cuedeck.Timelines;

namespace Cuedeck.UI.Timeline;

/// <summary>
/// A song's analysed structure as a strip: a coloured block a section (Intro, Verse, Chorus…), lighter the more
/// energy it has, with faint bar ticks. Clicking it moves the playhead there. Unlocked (<see cref="Editable"/>), a
/// border between sections drags (snapped to the timelines' snapping), and a right-click on a section offers its
/// kind, Split Here, Delete Segment and the snapping. It shows what the timelines' view shows. sLight's
/// <c>StructureBar</c>.
/// </summary>
sealed class StructureBar(Font font, SongTimelines timelines, ContextMenu menu) : Element
{
    /// <summary>
    /// How near a border the pointer must be to take it, pixels.
    /// </summary>
    const float BORDER_HIT_PX = 6;

    public static readonly string[] SnapLabels = ["Bar", "Beat", "No Snapping"];

    int _dragBorder = -1;
    int _hoverBorder = -1;
    bool _editable;

    public float Width { get; set; } = 400;
    public float Height { get; set; } = BarRuler.HEIGHT;

    public SongAnalysis? Analysis { get; private set; }

    public double ViewStart { get; private set; }
    public double ViewLen { get; private set; }

    /// <summary>
    /// Shown in place of the sections while there are none (while it's analysed, say).
    /// </summary>
    public string Placeholder { get; set; } = "analyse a song to see its structure";

    /// <summary>
    /// Borders drag and the right-click menu works; locked, clicks just move the playhead.
    /// </summary>
    public bool Editable
    {
        get => _editable;
        set
        {
            _editable = value;
            _dragBorder = -1;
            _hoverBorder = -1;
        }
    }

    /// <summary>
    /// A click asked to move the playhead to a time.
    /// </summary>
    public event Action<double>? SeekRequested;

    /// <summary>
    /// The border being dragged (between that section and the next), or -1.
    /// </summary>
    public int DragBorder => _dragBorder;

    public override bool IsInteractive => Analysis is not null;

    public override Cursor Cursor => Analysis is not null && Editable && (_dragBorder != -1 || _hoverBorder != -1) ? Cursor.ResizeHorizontal : Cursor.Arrow;

    public void SetAnalysis(SongAnalysis? a)
    {
        Analysis = a;
        _dragBorder = -1;
        _hoverBorder = -1;
    }

    public void SetView(double start, double length)
    {
        ViewStart = start;
        ViewLen = length;
    }

    double Dur => Analysis is { } a ? Math.Max(a.Duration, 0.001) : 1;

    double VisLen => ViewLen > 0 ? ViewLen : Dur;

    float W => Size.X > 0 ? Size.X : Width;

    /// <summary>
    /// A time's x from the bar's left edge, and back.
    /// </summary>
    public double TimeToX(double t) => (t - ViewStart) / VisLen * W;

    public double XToTime(double x) => Math.Clamp(ViewStart + x / Math.Max(W, 1) * VisLen, 0, Dur);

    public override float2 Measure() => Size = new float2(Width, Height);

    public override void Draw(Batch batch)
    {
        var p = ScreenPosition;
        var (w, h) = (Size.X, Size.Y);
        if (Analysis is not { Sections.Count: > 0 } a)
        {
            batch.AddRect(p, Size, AppTheme.StructureEmpty);
            batch.AddText(font, Placeholder, p + new float2(8, (h - font.LineHeight) / 2), AppTheme.WaveHint);
            return;
        }
        batch.PushClip(p, Size);
        for (var i = 0; i < a.Sections.Count; i++)
        {
            var sec = a.Sections[i];
            var x0 = (float)TimeToX(sec.Start);
            var x1 = (float)TimeToX(sec.End);
            if (x1 < 0 || x0 > w)
                continue;
            var b = AppTheme.SectionColours.GetValueOrDefault(sec.Label, new Common.Rgb(0.3f, 0.3f, 0.35f));
            // Lightened by its energy (Godot's lightened(): towards white).
            var e = (float)Math.Clamp(sec.Energy, 0, 1) * 0.35f;
            var col = Colors.FromSrgb(b.R + (1 - b.R) * e, b.G + (1 - b.G) * e, b.B + (1 - b.B) * e);
            var lx = Math.Max(x0, 0);
            var rx = Math.Min(x1, w);
            batch.AddRect(p + new float2(lx, 0), new float2(Math.Max(rx - lx, 1), h), col);
            var hot = i > 0 && (i - 1 == _dragBorder || i - 1 == _hoverBorder);
            if (x0 >= 0)
                batch.AddRect(p + new float2(x0 - (hot ? 1 : 0), 0), new float2(hot ? 2 : 1, h), hot ? AppTheme.SectionBorderHot : AppTheme.SectionBorder);
            if (rx - lx > 34)
            {
                batch.PushClip(p + new float2(lx, 0), new float2(rx - lx - 2, h));
                batch.AddText(font, sec.Label, p + new float2(lx + 4, (h - font.LineHeight) / 2), AppTheme.SectionText);
                batch.PopClip();
            }
        }
        // Bar ticks along the bottom, unless they'd crowd together.
        var barPx = a.Downbeats.Length > 1 ? ((double)a.Downbeats[1] - a.Downbeats[0]) / VisLen * w : 0;
        if (barPx >= 4)
        {
            foreach (var db in a.Downbeats)
            {
                var x = (float)TimeToX(db);
                if (x >= 0 && x <= w)
                    batch.AddRect(p + new float2(x, h - 5), new float2(1, 5), AppTheme.StructureTick);
            }
        }
        batch.PopClip();
    }

    /// <summary>
    /// The border under <paramref name="x"/> (pixels from the left edge), or -1.
    /// </summary>
    public int BorderAt(double x)
    {
        if (Analysis is not { } a || W <= 0)
            return -1;
        for (var i = 0; i < a.Sections.Count - 1; i++)
            if (Math.Abs(x - TimeToX(a.Sections[i].End)) <= BORDER_HIT_PX)
                return i;
        return -1;
    }

    public override void PointerMove(float2 point)
    {
        if (Editable && _dragBorder == -1)
            _hoverBorder = BorderAt(point.X - ScreenPosition.X);
    }

    public override void PointerDown(float2 point, Modifiers modifiers) => Press(point.X - ScreenPosition.X);

    public override void PointerDrag(float2 point) => DragTo(point.X - ScreenPosition.X);

    public override void PointerUp(float2 point) => Release();

    /// <summary>
    /// A left press <paramref name="x"/> pixels from the left edge: on a border (unlocked), starts dragging it; else
    /// asks to move the playhead there.
    /// </summary>
    public void Press(double x)
    {
        if (Analysis is null)
            return;
        var b = Editable ? BorderAt(x) : -1;
        if (b != -1)
            _dragBorder = b;
        else
            SeekRequested?.Invoke(XToTime(x));
    }

    public void DragTo(double x)
    {
        if (Analysis is { } a && _dragBorder != -1)
            StructureEdits.MoveBorder(a, _dragBorder, XToTime(x), timelines.Snap);
    }

    public void Release()
    {
        if (Analysis is not { } a || _dragBorder == -1)
            return;
        StructureEdits.DropBorder(a, _dragBorder);
        _dragBorder = -1;
        timelines.StructureEdited();
    }

    public override void SecondaryClick(float2 point)
    {
        if (Analysis is not { } a || !Editable)
            return;
        var t = XToTime(point.X - ScreenPosition.X);
        var idx = a.SectionAt(t);
        if (idx != -1)
            menu.Show(point, MenuFor(idx, t));
    }

    /// <summary>
    /// The right-click menu for section <paramref name="idx"/>, clicked at <paramref name="t"/>: its kind (the
    /// current one ticked), Split Here, Delete Segment, and Snapping.
    /// </summary>
    public List<MenuItem> MenuFor(int idx, double t)
    {
        var a = Analysis!;
        var items = new List<MenuItem>();
        foreach (var label in StructureEdits.Labels)
            items.Add(new MenuItem(label, () => Edited(StructureEdits.Relabel(a, idx, label)), Checked: a.Sections[idx].Label == label));
        items.Add(new MenuItem("Split Here", () => Edited(StructureEdits.Split(a, idx, t, timelines.Snap))));
        items.Add(new MenuItem("Delete Segment", () => Edited(StructureEdits.Delete(a, idx))));
        items.Add(new MenuItem("Snapping", SnapItems(timelines)));
        return items;
    }

    /// <summary>
    /// Bar, Beat and No Snapping, the one in effect ticked.
    /// </summary>
    public static List<MenuItem> SnapItems(SongTimelines timelines) =>
        [.. Enum.GetValues<SnapMode>().Select(m => new MenuItem(SnapLabels[(int)m], () => timelines.Snap = m, Checked: timelines.Snap == m))];

    void Edited(bool changed)
    {
        if (changed)
            timelines.StructureEdited();
    }
}

using Brutal.Numerics;
using Cuedeck.Common;

namespace Cuedeck.UI.Timeline;

/// <summary>
/// The overview's "you are here" box: the zoomed waveform's window over the whole song. Drag it to scroll; press
/// anywhere else to centre the window there (and keep dragging from there). sLight's <c>ViewBox</c>.
/// </summary>
sealed class ViewBox : Element
{
    bool _dragging;
    double _grab; // seconds from the box's start to the pointer

    public float2 Want { get; set; } = new(400, 44);
    public double Duration { get; set; }
    public double Start { get; private set; }
    public double Length { get; private set; }

    /// <summary>
    /// Dragged or clicked: where the window should start.
    /// </summary>
    public event Action<double>? ViewMoved;

    public override bool IsInteractive => Duration > 0;

    public void SetView(double start, double length)
    {
        Start = start;
        Length = length;
    }

    /// <summary>
    /// The box, relative to the element's top-left: (x, width).
    /// </summary>
    public (float X, float Width) BoxSpan()
    {
        if (Duration <= 0)
            return (0, 0);
        var w = Size.X > 0 ? Size.X : Want.X;
        var x0 = (float)(Start / Duration * w);
        var x1 = (float)((Start + Length) / Duration * w);
        return (x0, Math.Max(x1 - x0, 3));
    }

    double TimeAt(float x) => x / Math.Max(Size.X > 0 ? Size.X : Want.X, 1) * Duration;

    public override float2 Measure() => Size = Want;

    public override void Draw(Batch batch)
    {
        if (Duration <= 0 || Length <= 0)
            return;
        var (x, w) = BoxSpan();
        var p = ScreenPosition;
        batch.AddRect(p, new float2(x, Size.Y), AppTheme.ViewBoxDim);
        batch.AddRect(p + new float2(x + w, 0), new float2(Size.X - x - w, Size.Y), AppTheme.ViewBoxDim);
        batch.AddRect(p + new float2(x, 0), new float2(w, Size.Y), AppTheme.ViewBoxFill);
        batch.AddBorder(p + new float2(x, 0), new float2(w, Size.Y), 1.5f, AppTheme.ViewBoxEdge);
    }

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        if (Duration <= 0)
            return;
        var local = point.X - ScreenPosition.X;
        var t = TimeAt(local);
        var (x, w) = BoxSpan();
        if (local >= x && local < x + w)
            _grab = t - Start;
        else
        {
            // Jump: centre the window on the click.
            _grab = Length * 0.5;
            ViewMoved?.Invoke(t - _grab);
        }
        _dragging = true;
    }

    public override void PointerDrag(float2 point)
    {
        if (_dragging)
            ViewMoved?.Invoke(TimeAt(point.X - ScreenPosition.X) - _grab);
    }

    public override void PointerUp(float2 point) => _dragging = false;
}

/// <summary>
/// The strip along the top of the zoomed waveform: bar numbers (from 1 at VirtualDJ's first beat; bars before it
/// count in -1, -2, …), its beat and bar ticks, every 4th bar's brighter, and the playhead's marker. Press or drag on
/// it to move the playhead. sLight's <c>BarRuler</c>.
/// </summary>
sealed class BarRuler(Font font, WaveView wave) : Element
{
    public const float HEIGHT = 18;

    /// <summary>
    /// The closest two bar numbers may be, pixels (zoomed out: every 2nd, 4th, 8th… bar).
    /// </summary>
    public const float LABEL_GAP = 34;

    /// <summary>
    /// Every this many bars, the tick's brighter.
    /// </summary>
    const int PHRASE_BARS = 4;
    const float MARKER_W = 12;
    const float MARKER_H = 9;

    bool _dragging;

    public float Width { get; set; } = 400;

    public event Action<double>? Pressed;
    public event Action<double>? Dragged;
    public event Action<double>? Released;

    public override bool IsInteractive => wave.Analysis is not null;
    public override Cursor Cursor => Cursor.Arrow;

    /// <summary>
    /// The ticks for the view: the waveform's own grid lines.
    /// </summary>
    public List<(double X, bool IsBar)> Ticks() => wave.Analysis is null ? [] : wave.GridLines(wave.ViewStart, wave.ViewStart + wave.VisibleLength());

    /// <summary>
    /// The bar numbers for the view: (x, number), thinned to every 2nd, 4th… bar so they don't collide.
    /// </summary>
    public List<(double X, int Number)> BarLabels()
    {
        var output = new List<(double, int)>();
        var bars = Bars();
        if (bars.Count == 0)
            return output;
        var barPx = bars.Count > 1 ? bars[1].X - bars[0].X : Width;
        var every = 1;
        while (barPx * every < LABEL_GAP && every < 1 << 20)
            every *= 2;
        foreach (var (x, k) in bars)
            if (Gd.PosMod(k, every) == 0)
                output.Add((x, k >= 0 ? k + 1 : k));
        return output;
    }

    /// <summary>
    /// Where every 4th bar (1, 5, 9… and -4, -8…) starts in view, while bar ticks are drawn at all.
    /// </summary>
    public List<double> PhraseTicks()
    {
        if (!Ticks().Any(t => t.IsBar))
            return [];
        return [.. Bars().Where(b => Gd.PosMod(b.K, PHRASE_BARS) == 0).Select(b => b.X)];
    }

    /// <summary>
    /// Every bar start in view: (x, k), k counting bars from 0 at bar 1 (negative before it).
    /// </summary>
    List<(double X, int K)> Bars()
    {
        var output = new List<(double, int)>();
        var beats = wave.GridBeats;
        var per = wave.BeatsPerBar;
        if (wave.Analysis is null || beats.Length < 2)
            return output;
        var t0 = wave.ViewStart;
        var t1 = t0 + wave.VisibleLength();
        var i0 = Gd.BSearch(beats, t0);
        var i1 = Gd.BSearch(beats, t1, false);
        for (var i = i0 + Gd.PosMod(wave.BarPhase - i0, per); i < i1; i += per)
            output.Add((wave.TimeToX(beats[i]), (int)Math.Floor((double)(i - wave.BarPhase) / per)));
        return output;
    }

    public override float2 Measure() => Size = new float2(Width, HEIGHT);

    public override void Draw(Batch batch)
    {
        var p = ScreenPosition;
        var h = Size.Y;
        batch.AddRect(p, Size, AppTheme.RulerBackground);
        if (wave.Analysis is null)
            return;
        batch.PushClip(p, Size);
        foreach (var (x, isBar) in Ticks())
        {
            var top = isBar ? h * 0.6f : h * 0.75f;
            batch.AddRect(p + new float2((float)x, top), new float2(1, h - top), isBar ? AppTheme.RulerTick : AppTheme.RulerBeatTick);
        }
        foreach (var (x, n) in BarLabels())
        {
            batch.AddRect(p + new float2((float)x, h * 0.45f), new float2(1, h * 0.55f), AppTheme.RulerTick);
            batch.AddText(font, n.ToString(System.Globalization.CultureInfo.InvariantCulture), p + new float2((float)x + 3, h - 3 - font.LineHeight), AppTheme.RulerText);
        }
        foreach (var x in PhraseTicks())
            batch.AddRect(p + new float2((float)x, h * 0.45f), new float2(1, h * 0.3f), AppTheme.RulerPhraseTick);
        if (wave.ShowCursor && wave.SongPos >= wave.ViewStart && wave.SongPos <= wave.ViewStart + wave.VisibleLength())
        {
            var x = (float)wave.TimeToX(wave.SongPos);
            batch.AddRect(p + new float2(x - 1, MARKER_H), new float2(2, h - MARKER_H), wave.CursorColor);
            batch.AddTriangle(p + new float2(x - MARKER_W / 2, 0), p + new float2(x + MARKER_W / 2, 0), p + new float2(x, MARKER_H), wave.CursorColor);
        }
        batch.PopClip();
    }

    double TimeAt(float2 point) => Math.Clamp(wave.XToTime(point.X - ScreenPosition.X), 0, wave.Duration());

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        if (wave.Analysis is null)
            return;
        _dragging = true;
        Pressed?.Invoke(TimeAt(point));
    }

    public override void PointerDrag(float2 point)
    {
        if (_dragging)
            Dragged?.Invoke(TimeAt(point));
    }

    public override void PointerUp(float2 point)
    {
        if (!_dragging)
            return;
        _dragging = false;
        Released?.Invoke(TimeAt(point));
    }
}

/// <summary>
/// The pictures a <see cref="TransportButton"/> can show.
/// </summary>
enum TransportGlyph
{
    ToStart,
    Play,
    Pause,
    Locked,
    Unlocked,
    Generate,
}

/// <summary>
/// A square button with a drawn picture (the fonts have no media symbols): the timeline's transport, lock and
/// Generate buttons. Clicking it raises <see cref="Element.Clicked"/>, unless it's disabled. sLight's
/// <c>TransportButton</c>.
/// </summary>
sealed class TransportButton : Element
{
    public float2 Want { get; set; } = new(48, 48);
    public TransportGlyph Glyph { get; set; } = TransportGlyph.Play;
    public bool Disabled { get; set; }

    /// <summary>
    /// The modifier keys held when it was last pressed (Shift+Generate re-analyses first).
    /// </summary>
    public Modifiers PressedWith { get; private set; }

    public override bool IsInteractive => !Disabled;

    public override void PointerDown(float2 point, Modifiers modifiers) => PressedWith = modifiers;

    /// <summary>
    /// Clicks it as the mouse would (for the screen's own use, and tests), unless it's disabled.
    /// </summary>
    public void Press(Modifiers modifiers = Modifiers.None)
    {
        if (Disabled)
            return;
        PressedWith = modifiers;
        RaiseClicked();
    }

    public override float2 Measure() => Size = Want;

    public override void Draw(Batch batch)
    {
        var back = Disabled ? Theme.Panel : IsPressed ? Theme.PanelBorder : IsHovered ? Theme.PanelRaised : Theme.Panel;
        batch.AddRect(ScreenPosition, Size, back);
        batch.AddBorder(ScreenPosition, Size, 1, Theme.PanelBorder);
        var c = Disabled ? Theme.Faint : Theme.Text;
        var s = MathF.Min(MathF.Min(Size.X, Size.Y) * 0.36f, 18);
        var o = ScreenPosition + Size / 2;
        switch (Glyph)
        {
            case TransportGlyph.Play:
                batch.AddTriangle(o + new float2(-s * 0.6f, -s), o + new float2(s, 0), o + new float2(-s * 0.6f, s), c);
                break;
            case TransportGlyph.Pause:
                batch.AddRect(o + new float2(-s * 0.75f, -s), new float2(s * 0.5f, s * 2), c);
                batch.AddRect(o + new float2(s * 0.25f, -s), new float2(s * 0.5f, s * 2), c);
                break;
            case TransportGlyph.ToStart:
                batch.AddRect(o + new float2(-s, -s), new float2(s * 0.3f, s * 2), c);
                batch.AddTriangle(o + new float2(s * 0.8f, -s), o + new float2(-s * 0.55f, 0), o + new float2(s * 0.8f, s), c);
                break;
            case TransportGlyph.Locked or TransportGlyph.Unlocked:
            {
                // A padlock: the body below, the shackle above (raised, its left leg out, when unlocked).
                var w = s * 1.5f;
                var lw = MathF.Max(s * 0.22f, 1.5f);
                var bodyTop = o.Y - s * 0.1f;
                batch.AddRect(new float2(o.X - w * 0.5f, bodyTop), new float2(w, s * 1.1f), c);
                var unlocked = Glyph == TransportGlyph.Unlocked;
                var r = w * 0.32f;
                var arcY = o.Y - s * 0.45f - (unlocked ? s * 0.4f : 0);
                batch.AddArc(new float2(o.X, arcY), r, MathF.PI, MathF.Tau, lw, c, 10);
                batch.AddLine(new float2(o.X + r, arcY), new float2(o.X + r, bodyTop), lw, c);
                batch.AddLine(new float2(o.X - r, arcY), new float2(o.X - r, unlocked ? arcY + s * 0.2f : bodyTop), lw, c);
                break;
            }
            case TransportGlyph.Generate:
            {
                // A circular "rebuild" arrow.
                var rr = s * 0.85f;
                var wd = MathF.Max(s * 0.22f, 1.5f);
                batch.AddArc(o, rr, -MathF.PI * 0.35f, MathF.PI * 1.35f, wd, c, 16);
                var tip = o + new float2(MathF.Cos(-MathF.PI * 0.35f), MathF.Sin(-MathF.PI * 0.35f)) * rr;
                batch.AddTriangle(tip + new float2(-s * 0.55f, -s * 0.15f), tip + new float2(s * 0.25f, -s * 0.5f), tip + new float2(s * 0.2f, s * 0.35f), c);
                break;
            }
        }
    }
}

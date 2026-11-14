using Brutal.Numerics;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Input;

namespace Cuedeck.UI.Timeline;

/// <summary>
/// A song as a colour-coded waveform: each column's height is its loudness, its colour the balance of bass (amber),
/// mid (green) and air (blue). It can show just part of the song (<see cref="ViewStart"/> for
/// <see cref="VisibleLength"/> seconds) and, <see cref="Zoomable"/>, the wheel zooms around the pointer (Shift+wheel
/// scrolls). A beat grid can be drawn over it, and a playhead. Pressing it asks to move the playhead there, and
/// dragging follows. sLight's <c>WaveHeatmap</c>.
/// </summary>
sealed class WaveView : Element
{
    /// <summary>
    /// Wheel zoom per notch.
    /// </summary>
    public const float ZOOM_STEP = 1.25f;

    /// <summary>
    /// Grid lines closer than this many pixels are left out (beats first, then bars).
    /// </summary>
    const float GRID_MIN_PX = 5;

    readonly Font _font;

    /// <summary>
    /// Each detail level's columns: the song's own, then each level merging pairs (bands averaged, peaks maxed), down
    /// to 64 or fewer, so a redraw draws about a column a pixel however long the song.
    /// </summary>
    readonly List<(float[] Lo, float[] Mid, float[] Hi, float[] Peak)> _levels = [];

    bool _seekDragging;

    public WaveView(Font font) => _font = font;

    /// <summary>
    /// The size it takes (its owner sets it before laying out).
    /// </summary>
    public float2 Want { get; set; } = new(400, 92);

    public SongAnalysis? Analysis { get; private set; }

    public double SongPos { get; set; }
    public bool ShowCursor { get; set; } = true;
    public float4 CursorColor { get; set; } = Colors.Hex(0xFFFFFF, 0.9f);

    /// <summary>
    /// Drawn behind the wave; none by default.
    /// </summary>
    public float4 BackgroundColor { get; set; } = Colors.Transparent;

    /// <summary>
    /// While there's no wave yet (the song still being read): 0 to 1 fills that much of it from the left, as a
    /// progress bar. Negative for none.
    /// </summary>
    public double Progress { get; set; } = -1;

    public bool Zoomable { get; set; }

    /// <summary>
    /// The narrowest the wheel zooms in to, seconds.
    /// </summary>
    public double MinView { get; set; } = 2;

    public double ViewStart { get; private set; }

    /// <summary>
    /// The view's length, or 0 for the whole song.
    /// </summary>
    public double ViewLen { get; private set; }

    /// <summary>
    /// The beat grid drawn over it: beat times, beats a bar, and the index of a beat that starts one.
    /// </summary>
    public float[] GridBeats { get; private set; } = [];
    public int BeatsPerBar { get; private set; } = 4;
    public int BarPhase { get; private set; }

    /// <summary>
    /// The view changed (zoom, scroll or <see cref="SetView"/>): its start and length.
    /// </summary>
    public event Action<double, double>? ViewChanged;

    /// <summary>
    /// The left button went down at a time (to move the playhead there), was dragged, and let go.
    /// </summary>
    public event Action<double>? SeekRequested;
    public event Action<double>? SeekDragged;
    public event Action<double>? SeekReleased;

    public override bool IsInteractive => Analysis is not null;
    public override Cursor Cursor => Cursor.Arrow;

    public void SetAnalysis(SongAnalysis? a)
    {
        Analysis = a;
        _levels.Clear();
        if (a is { HasWave: true })
            BuildLevels(a);
        ViewStart = 0;
        ViewLen = 0;
    }

    public double Duration() => Analysis is { } a ? Math.Max(a.Duration, 0.001) : 1;

    public double VisibleLength() => ViewLen > 0 ? ViewLen : Duration();

    public void SetBeatGrid(float[] beats, int perBar = 4, int phase = 0)
    {
        GridBeats = beats;
        BeatsPerBar = Math.Max(perBar, 1);
        BarPhase = phase;
    }

    /// <summary>
    /// Shows <paramref name="length"/> seconds from <paramref name="start"/>, kept within the song; 0 or less shows
    /// the whole song.
    /// </summary>
    public void SetView(double start, double length)
    {
        var dur = Duration();
        var ln = length <= 0 || length >= dur ? dur : Math.Max(length, Math.Min(MinView, dur));
        var st = Math.Clamp(start, 0, Math.Max(dur - ln, 0));
        var newLen = Gd.ApproxEqual(ln, dur) ? 0 : ln;
        if (Gd.ApproxEqual(st, ViewStart) && Gd.ApproxEqual(newLen, ViewLen))
            return;
        ViewStart = st;
        ViewLen = newLen;
        ViewChanged?.Invoke(ViewStart, VisibleLength());
    }

    /// <summary>
    /// Zooms by <paramref name="factor"/> (under 1 zooms in), keeping the time <paramref name="atX"/> pixels in where
    /// it is.
    /// </summary>
    public void ZoomAt(double factor, double atX)
    {
        var w = Math.Max(Width, 1);
        var frac = Math.Clamp(atX / w, 0, 1);
        var t = ViewStart + frac * VisibleLength();
        var ln = Math.Clamp(VisibleLength() * factor, Math.Min(MinView, Duration()), Duration());
        SetView(t - frac * ln, ln);
    }

    float Width => Size.X > 0 ? Size.X : Want.X;

    /// <summary>
    /// A time's x, in pixels from the left edge.
    /// </summary>
    public double TimeToX(double t) => (t - ViewStart) / VisibleLength() * Width;

    public double XToTime(double x) => ViewStart + x / Math.Max(Width, 1) * VisibleLength();

    void BuildLevels(SongAnalysis a)
    {
        var (lo, mid, hi, pk) = (a.WaveLo, a.WaveMid, a.WaveHi, a.WavePeak);
        _levels.Add((lo, mid, hi, pk));
        while (pk.Length > 64)
        {
            var n = (int)Math.Ceiling(pk.Length / 2.0);
            var last = pk.Length - 1;
            float[] nlo = new float[n], nmid = new float[n], nhi = new float[n], npk = new float[n];
            for (var i = 0; i < n; i++)
            {
                var x = i * 2;
                var y = Math.Min(x + 1, last);
                nlo[i] = (lo[x] + lo[y]) * 0.5f;
                nmid[i] = (mid[x] + mid[y]) * 0.5f;
                nhi[i] = (hi[x] + hi[y]) * 0.5f;
                npk[i] = Math.Max(pk[x], pk[y]);
            }
            (lo, mid, hi, pk) = (nlo, nmid, nhi, npk);
            _levels.Add((lo, mid, hi, pk));
        }
    }

    /// <summary>
    /// How many detail levels it has (for tests), and the coarsest's column count.
    /// </summary>
    public (int Count, int Coarsest) Levels => (_levels.Count, _levels.Count > 0 ? _levels[^1].Peak.Length : 0);

    /// <summary>
    /// The beat grid's lines from <paramref name="t0"/> to <paramref name="t1"/>: (x, is a bar), beats left out once
    /// they'd be closer than <see cref="GRID_MIN_PX"/>, then bars too.
    /// </summary>
    public List<(double X, bool IsBar)> GridLines(double t0, double t1)
    {
        var output = new List<(double, bool)>();
        var n = GridBeats.Length;
        if (n == 0)
            return output;
        var i0 = Gd.BSearch(GridBeats, t0);
        var i1 = Gd.BSearch(GridBeats, t1, false);
        if (i1 <= i0)
            return output;
        // The spacing from the local tempo, not the window's average.
        var gap = 0.0;
        if (n > 1)
        {
            var j = Math.Clamp(i0, 0, n - 2);
            gap = ((double)GridBeats[j + 1] - GridBeats[j]) / VisibleLength() * Width;
        }
        var beats = gap >= GRID_MIN_PX;
        if (gap * BeatsPerBar < GRID_MIN_PX)
            return output;
        for (var i = i0; i < i1; i++)
        {
            var isBar = Gd.PosMod(i - BarPhase, BeatsPerBar) == 0;
            if (isBar || beats)
                output.Add((TimeToX(GridBeats[i]), isBar));
        }
        return output;
    }

    public override float2 Measure() => Size = Want;

    public override void Draw(Batch batch)
    {
        var p = ScreenPosition;
        var (w, h) = (Size.X, Size.Y);
        if (Analysis is not { HasWave: true } a)
        {
            batch.AddRect(p, Size, BackgroundColor.W > 0 ? BackgroundColor : Theme.WaveEmpty);
            if (Progress >= 0)
            {
                batch.AddRect(p, new float2(w * (float)Math.Clamp(Progress, 0, 1), h), Theme.WaveProgress);
                return;
            }
            batch.AddText(_font, "analyse a song to see its waveform", p + new float2(8, h * 0.5f - _font.LineHeight / 2), Theme.WaveHint);
            return;
        }
        batch.PushClip(p, Size);
        batch.AddRect(p, Size, BackgroundColor);
        var t0 = ViewStart;
        var t1 = ViewStart + VisibleLength();
        foreach (var sec in a.Sections)
        {
            if (sec.End < t0 || sec.Start > t1)
                continue;
            var x0 = (float)TimeToX(sec.Start);
            var x1 = (float)TimeToX(sec.End);
            var tint = Theme.SectionColours.TryGetValue(sec.Label, out var c) ? Colors.FromSrgb(c.R, c.G, c.B, 0.16f) : Theme.Fade(Theme.SectionUnknown, 0.16f);
            batch.AddRect(p + new float2(x0, 0), new float2(Math.Max(x1 - x0, 1), h), tint);
        }
        // The grid first, so the wave sits in front of it.
        foreach (var (x, isBar) in GridLines(t0, t1))
            batch.AddRect(p + new float2((float)x - (isBar ? 1 : 0.5f), 0), new float2(isBar ? 2 : 1, h), isBar ? Theme.TimelineBarLine : Theme.TimelineBeatLine);
        DrawWave(batch, w, h, t0, t1);
        foreach (var db in a.Downbeats)
        {
            if (db < t0 || db > t1)
                continue;
            batch.AddRect(p + new float2((float)TimeToX(db), h - 4), new float2(1, 4), Theme.WaveDownbeat);
        }
        if (ShowCursor && SongPos >= t0 && SongPos <= t1)
            batch.AddRect(p + new float2((float)TimeToX(SongPos) - 1, 0), new float2(2, h), CursorColor);
        batch.PopClip();
    }

    /// <summary>
    /// The columns in view, each a bar mirrored about the middle, opaque there and fading to 60 % at its tips.
    /// </summary>
    void DrawWave(Batch batch, float w, float h, double t0, double t1)
    {
        if (_levels.Count == 0 || w < 1)
            return;
        var dur = Duration();
        var n0 = _levels[0].Peak.Length;
        // The level with one to two columns a pixel.
        var colsVisible = (t1 - t0) / dur * n0;
        var lvl = Math.Clamp((int)Math.Floor(Math.Log(Math.Max(colsVisible / w, 1.0)) / Math.Log(2.0)), 0, _levels.Count - 1);
        var (lo, mid, hi, pk) = _levels[lvl];
        var n = pk.Length;
        var colDur = dur / n;
        var c0 = Math.Clamp((int)Math.Floor(t0 / colDur), 0, n - 1);
        var c1 = Math.Clamp((int)Math.Ceiling(t1 / colDur), c0 + 1, n);
        var midY = h * 0.5f;
        var bass = Theme.WaveBass;
        var mids = Theme.WaveMid;
        var air = Theme.WaveAir;
        for (var c = c0; c < c1; c++)
        {
            var x0 = (float)TimeToX(c * colDur);
            var x1 = Math.Max((float)TimeToX((c + 1) * colDur), x0 + 1);
            // The dominant band sharpened, so bass and air read rather than a wall of mid.
            var l = lo[c] * lo[c] * 1.3f;
            var m = mid[c] * mid[c];
            var ai = hi[c] * hi[c] * 1.3f;
            var sum = l + m + ai;
            var col = sum > 0.0001f ? (bass * l + mids * m + air * ai) / sum : Theme.WaveNeutral;
            col = col with { W = 1 };
            var half = MathF.Sqrt(Math.Clamp(pk[c], 0, 1)) * (midY - 1);
            var tip = col with { W = 0.6f };
            var p = ScreenPosition;
            float2 tl = p + new float2(x0, midY - half), tr = p + new float2(x1, midY - half);
            float2 ml = p + new float2(x0, midY), mr = p + new float2(x1, midY);
            float2 bl = p + new float2(x0, midY + half), br = p + new float2(x1, midY + half);
            batch.AddTriangle(tl, tr, ml, tip, tip, col);
            batch.AddTriangle(tr, mr, ml, tip, col, col);
            batch.AddTriangle(ml, mr, bl, col, col, tip);
            batch.AddTriangle(mr, br, bl, col, tip, tip);
        }
    }

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        if (Analysis is null)
            return;
        _seekDragging = true;
        SeekRequested?.Invoke(TimeAt(point));
    }

    public override void PointerDrag(float2 point)
    {
        if (_seekDragging)
            SeekDragged?.Invoke(TimeAt(point));
    }

    public override void PointerUp(float2 point)
    {
        if (!_seekDragging)
            return;
        _seekDragging = false;
        SeekReleased?.Invoke(TimeAt(point));
    }

    double TimeAt(float2 point) => Math.Clamp(XToTime(point.X - ScreenPosition.X), 0, Duration());

    /// <summary>
    /// The wheel zooms around the pointer; sideways (or with Shift) it scrolls.
    /// </summary>
    public override bool Wheel(float2 notches)
    {
        if (!Zoomable || Analysis is null)
            return false;
        if (notches.X != 0)
            Pan(notches.X > 0 ? -1 : 1);
        else
            ZoomAt(notches.Y > 0 ? 1 / ZOOM_STEP : ZOOM_STEP, _pointerX);
        return true;
    }

    double _pointerX;

    public override void PointerMove(float2 point) => _pointerX = point.X - ScreenPosition.X;

    /// <summary>
    /// Scrolls the view by a tenth of its width.
    /// </summary>
    public void Pan(double direction) => SetView(ViewStart + direction * VisibleLength() * 0.1, ViewLen);
}

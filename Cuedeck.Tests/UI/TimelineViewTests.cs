using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Dj;
using Cuedeck.Fixtures;
using Cuedeck.Input;
using Cuedeck.Show;
using Cuedeck.Timelines;
using Cuedeck.UI;
using Cuedeck.UI.Timeline;

namespace Cuedeck.Tests.UI;

/// <summary>
/// Plays a song by a clock the test moves on, without a sound card.
/// </summary>
sealed class FakePlayer : ISongPlayer
{
    double _length;
    double _from;
    double _startedAt;
    bool _playing;

    /// <summary>
    /// The test's clock, seconds.
    /// </summary>
    public double Now { get; set; }

    public string Load(PcmAudio audio)
    {
        _length = (double)audio.Frames / audio.SampleRate;
        _playing = false;
        IsLoaded = true;
        return "";
    }

    public void Unload() => IsLoaded = false;

    public bool IsLoaded { get; private set; }

    public double Length => IsLoaded ? _length : 0;

    double Raw => _from + (Now - _startedAt);

    public bool IsPlaying => _playing && Raw < _length;

    public double Position => Math.Clamp(_playing ? Raw : _from, 0, _length);

    public void Play(double from)
    {
        _from = from;
        _startedAt = Now;
        _playing = true;
    }

    public void Stop()
    {
        if (!_playing)
            return;
        _from = Position;
        _playing = false;
    }

    public void Seek(double t)
    {
        _from = t;
        _startedAt = Now;
    }

    public void Dispose() { }
}

/// <summary>
/// DJ Edit Mode's song timeline on screen: music_timeline_test.gd (the waveform view, its overview box, the beat grid,
/// the bar ruler, the structure bar and its snapping, the layout, playback and middle-button panning) and the parts of
/// timeline_track_test.gd that drive the timelines with the mouse (the master track in the timeline, selecting,
/// node editing, the context menu, mute, solo and collapsing), laid out without a window and driven through
/// <see cref="Root"/> as the mouse would. Playback runs on a <see cref="FakePlayer"/>'s clock.
/// </summary>
public sealed class TimelineViewTests : IDisposable
{
    static readonly float2 ScreenSize = new(1100, 700);
    const TimelineRow Rd = TimelineRow.Dimmer, Rc = TimelineRow.Colour, Rm = TimelineRow.Movement, Rs = TimelineRow.Strobe, Rp = TimelineRow.Position;

    readonly TempFolder _dir = new();
    readonly Root _ui = new();
    readonly ContextMenu _menu = new(MainScreenTests.TestFonts.Body);
    readonly FakePlayer _player = new();
    readonly List<PatchedFixture> _fixtures = [];
    readonly SongTimelines _tl;
    readonly TimelineView _view;
    Func<string, SongAnalysis> _analyse = _ => throw new InvalidDataException("not analysed here");

    public TimelineViewTests()
    {
        _tl = new SongTimelines { Fixtures = () => _fixtures, AnalyseStructure = (path, _) => _analyse(path) };
        _view = _ui.Add(new TimelineView(MainScreenTests.TestFonts, _menu, new ColourPicker(_ui, MainScreenTests.TestFonts.Body), _tl, _player));
        _ui.Add(_menu);
        _ui.PointerHooks.Add(_view);
        Frame();
    }

    public void Dispose() => _dir.Dispose();

    void Frame()
    {
        _view.Available = ScreenSize;
        _menu.ScreenSize = ScreenSize;
        _view.Update();
        _ui.Layout(ScreenSize);
        _view.Update();
        _ui.Layout(ScreenSize);
    }

    void Wait(Func<bool> done, double seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting");
            Thread.Sleep(5);
            Frame();
        }
        Frame();
    }

    void Advance(double seconds)
    {
        _player.Now += seconds;
        Frame();
    }

    /// <summary>
    /// A WAV with a loud 10 s burst in the middle of <paramref name="seconds"/> of quiet.
    /// </summary>
    string SynthWav(string name, double seconds = 40)
    {
        const int RATE = 22050;
        var n = (int)(RATE * seconds);
        var path = _dir.File(name);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + n * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(RATE);
        w.Write(RATE * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(n * 2);
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / RATE;
            var amp = t >= 15 && t < 25 ? 0.8 : 0.05;
            w.Write((short)(Math.Sin(t * Math.Tau * 220) * amp * 32767));
        }
        return path;
    }

    static SongAnalysis Wave(double duration, int cols)
    {
        var a = new SongAnalysis
        {
            Duration = duration,
            WaveLo = new float[cols],
            WaveMid = new float[cols],
            WaveHi = new float[cols],
            WavePeak = new float[cols],
        };
        for (var i = 0; i < cols; i++)
        {
            a.WavePeak[i] = (float)(0.5 + 0.5 * Math.Sin(i * 0.05));
            (a.WaveLo[i], a.WaveMid[i], a.WaveHi[i]) = (0.6f, 0.3f, 0.1f);
        }
        return a;
    }

    /// <summary>
    /// A song at 120 BPM from 0.25 s (bars of 2 s), as VirtualDJ knows it.
    /// </summary>
    static DjSong Song(double phase = 0.25) => new() { ScanBpm = 120, Phase = phase, Length = 40 };

    /// <summary>
    /// Opens a song (the synthesised WAV, read for real) and waits for it to load.
    /// </summary>
    string OpenLoaded(DjSong? song = null)
    {
        var wav = SynthWav($"song{Guid.NewGuid():N}.wav");
        _view.Open(wav, song);
        Wait(() => !_view.IsLoading);
        return wav;
    }

    void Click(float2 at, Modifiers mods = Modifiers.None)
    {
        _ui.MouseMove(at);
        _ui.MouseButton(MouseButton.Left, true, mods);
        _ui.MouseButton(MouseButton.Left, false, mods);
        Frame();
    }

    void Drag(float2 a, float2 b, Modifiers mods = Modifiers.None)
    {
        _ui.MouseMove(a);
        _ui.MouseButton(MouseButton.Left, true, mods);
        _ui.MouseMove((a + b) / 2);
        _ui.MouseMove(b);
        _ui.MouseButton(MouseButton.Left, false, mods);
        Frame();
    }

    float2 At(TimelineTrack track, TimelineRow row, double t) => _view.PointAt(track, row, t);

    static float2 Centre(Element e) => e.ScreenPosition + e.Size / 2;

    static PatchedFixture Fx(string key, string name, FixtureProfile profile) => new() { Uid = key, Name = name, Profile = profile };

    static FixtureProfile Par() => Profiles.Simple("par", "RGB par", ("Dimmer", "DIMMER"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));

    static FixtureProfile TwoHeadBar() => Profiles.Simple("bar2", "Bar", ("R", "RED"), ("G", "GREEN"), ("B", "BLUE"), ("R", "RED"), ("G", "GREEN"), ("B", "BLUE"));

    // ----------------------------------------------------------- the waveform --

    [Fact]
    public void The_zoomable_waveform()
    {
        var w = new WaveView(MainScreenTests.TestFonts.Small) { Zoomable = true, Want = new float2(1000, 180) };
        w.SetAnalysis(Wave(100, 10000));
        w.Measure();
        w.Arrange(float2.Zero);
        Assert.True(w.VisibleLength() == 100 && w.ViewStart == 0); // the whole song to start
        Assert.True(w.Levels.Count > 5 && w.Levels.Coarsest <= 64); // a pyramid of detail
        var changes = 0;
        w.ViewChanged += (_, _) => changes++;
        w.SetView(10, 20);
        Assert.True(w.ViewStart == 10 && w.VisibleLength() == 20 && changes == 1);
        Assert.True(Gd.ApproxEqual(w.TimeToX(20), 500) && Gd.ApproxEqual(w.XToTime(250), 15));
        var under = w.XToTime(250);
        w.PointerMove(new float2(250, 10));
        w.Wheel(new float2(0, 1));
        Assert.True(Gd.ApproxEqual(w.VisibleLength(), 16) && Gd.ApproxEqual(w.XToTime(250), under)); // zoomed in, the time under the pointer put
        w.Wheel(new float2(0, -1));
        Assert.True(Gd.ApproxEqual(w.VisibleLength(), 20));
        w.PointerMove(new float2(500, 10));
        for (var i = 0; i < 40; i++)
            w.Wheel(new float2(0, 1));
        Assert.True(Gd.ApproxEqual(w.VisibleLength(), w.MinView)); // stops at the narrowest
        for (var i = 0; i < 40; i++)
            w.Wheel(new float2(0, -1));
        Assert.True(w.ViewLen == 0 && w.VisibleLength() == 100 && w.ViewStart == 0); // and at the whole song
        w.SetView(50, 20);
        w.Wheel(new float2(-1, 0)); // Shift+wheel down: later
        Assert.True(Gd.ApproxEqual(w.ViewStart, 52) && Gd.ApproxEqual(w.VisibleLength(), 20));
        w.SetView(95, 20);
        Assert.Equal(80, w.ViewStart); // kept in the song
        w.SetView(-5, 20);
        Assert.Equal(0, w.ViewStart);
        w.Zoomable = false;
        w.SetView(0, 0);
        Assert.False(w.Wheel(new float2(0, 1)));
        Assert.Equal(100, w.VisibleLength());
    }

    [Fact]
    public void The_overview_box()
    {
        var box = new ViewBox { Want = new float2(1000, 44), Duration = 100 };
        box.Measure();
        box.Arrange(float2.Zero);
        box.SetView(10, 20);
        Assert.Equal((100f, 200f), box.BoxSpan());
        var moved = new List<double>();
        box.ViewMoved += moved.Add;
        box.PointerDown(new float2(150, 10), Modifiers.None); // inside: grabbed 5 s into the window
        box.PointerDrag(new float2(450, 10)); // the pointer at 45 s: the window starts at 40
        box.PointerUp(new float2(450, 10));
        Assert.Equal(40.0, Assert.Single(moved), 4);
        box.PointerDrag(new float2(600, 10));
        Assert.Single(moved); // released: no more dragging
        moved.Clear();
        box.PointerDown(new float2(800, 10), Modifiers.None); // outside: the window centred on 80 s
        Assert.Equal(70.0, Assert.Single(moved), 4);
        box.PointerDrag(new float2(700, 10));
        Assert.Equal(60.0, moved[1], 4); // and dragged on from there
    }

    [Fact]
    public void Beat_grid_lines()
    {
        var w = new WaveView(MainScreenTests.TestFonts.Small) { Want = new float2(1000, 180) };
        w.SetAnalysis(Wave(100, 10000));
        w.Measure();
        w.SetBeatGrid([.. Enumerable.Range(0, 200).Select(i => 0.25f + i * 0.5f)], 4, 2); // bars at beats 2, 6, 10…
        w.SetView(0, 10); // 100 px a second: beats 50 px apart
        var lines = w.GridLines(0, 10);
        Assert.Equal(20, lines.Count);
        Assert.Equal([125, 325, 525, 725, 925], lines.Where(l => l.IsBar).Select(l => (int)Math.Round(l.X)));
        w.SetView(0, 100); // beats 5 px apart: still drawn
        Assert.Equal(200, w.GridLines(0, 100).Count);
        w.SetView(0, 0);
        w.Want = new float2(500, 180);
        w.Measure(); // 2.5 px beats, 10 px bars
        var far = w.GridLines(0, 100);
        Assert.True(far.Count == 50 && far.All(l => l.IsBar));
        w.Want = new float2(100, 180);
        w.Measure(); // bars 2 px apart
        Assert.Empty(w.GridLines(0, 100));
        w.SetBeatGrid([]);
        Assert.Empty(w.GridLines(0, 100));
    }

    [Fact]
    public void The_waveform_envelope()
    {
        var path = SynthWav("burst.wav");
        var a = WaveformEnvelope.Compute(SongDecoder.Decode(path));
        Assert.Equal(40, a.Duration, 3);
        Assert.Equal(4000, a.WavePeak.Length); // 100 columns a second
        Assert.True(a.WavePeak[2000] > 0.9 && a.WavePeak[500] < 0.15 && a.WavePeak[3500] < 0.15); // the burst at 15-25 s
        Assert.Throws<InvalidDataException>(() => WaveformEnvelope.Compute(new PcmAudio([], 1, 22050)));
    }

    // ------------------------------------------------------- loading a song --

    [Fact]
    public void A_song_loads_on_a_worker_thread()
    {
        var wav = SynthWav("song.wav");
        _view.Open(wav);
        Assert.True(_view.IsLoading && _view.Overview.Progress >= 0); // the overview's background is the progress bar
        Assert.Equal("Reading waveform…", _view.StatusText);
        Wait(() => !_view.IsLoading);
        Assert.True(_view.Overview.Progress < 0);
        var w = _view.Wave;
        Assert.Equal(40, w.Analysis!.Duration, 3);
        Assert.Equal(4000, w.Analysis.WavePeak.Length);
        Assert.True(w.ViewStart == 0 && Gd.ApproxEqual(w.VisibleLength(), 40)); // fully zoomed out
        var box = _view.Box;
        Assert.True(box.Start == 0 && Gd.ApproxEqual(box.Length, 40) && Gd.ApproxEqual(box.Duration, 40));
        w.SetView(5, 10);
        Assert.True(box.Start == 5 && box.Length == 10); // zooming moves the box
        Drag(box.ScreenPosition + new float2((float)box.BoxSpan().X + 5, 10), box.ScreenPosition + new float2((float)(box.BoxSpan().X + 5 + 20.0 / 40 * box.Size.X - 5.0 / 40 * box.Size.X), 10));
        Assert.True(Gd.ApproxEqual(w.VisibleLength(), 10) && Math.Abs(w.ViewStart - 20) < 0.1); // dragging the box scrolls
        Assert.Equal("", _view.StatusText); // nothing above the waveforms once it's shown

        // Reopened: from the cache, fully zoomed out again.
        w.SetView(12, 6);
        _view.Open(wav);
        Assert.True(!_view.IsLoading && w.Analysis is not null && w.ViewStart == 0 && Gd.ApproxEqual(w.VisibleLength(), 40));
        _view.Open(_dir.File("no such track.mp3"));
        Assert.True(!_view.IsLoading && _view.StatusText == "File not found.");
        // Switching song mid-read cancels it.
        _view.Open(SynthWav("long.wav", 200));
        _view.Open(wav);
        Assert.True(!_view.IsLoading && _view.Overview.Progress < 0);
        Frame();
        Assert.Equal(wav, _view.CurrentPath);
        Assert.Equal(40, _view.Wave.Analysis!.Duration, 3);
    }

    [Fact]
    public void The_layout()
    {
        OpenLoaded(Song());
        var w = _view.Wave;
        Assert.Equal(TimelineView.WAVE_HEIGHT, w.Size.Y);
        Assert.Equal(_view.Overview.ScreenPosition.Y - 6, w.ScreenPosition.Y + w.Size.Y); // sitting on the overview
        Assert.Equal(w.ScreenPosition.Y, _view.Structure.ScreenPosition.Y + 36); // structure bar and ruler above it
        var ruler = _view.Ruler;
        Assert.True(ruler.ScreenPosition.Y + ruler.Size.Y == w.ScreenPosition.Y && ruler.ScreenPosition.X == w.ScreenPosition.X && ruler.Size.X == w.Size.X);
        Assert.InRange(ruler.Size.Y, 14, 24);
        var (play, reset) = (_view.PlayButton, _view.ResetButton);
        Assert.True(play.Size == new float2(48, 48) && reset.Size == new float2(48, 48));
        Assert.True(play.ScreenPosition.X >= w.ScreenPosition.X + w.Size.X && play.ScreenPosition.Y + 48 == w.ScreenPosition.Y + w.Size.Y); // at its right, Play lowest
        Assert.True(reset.ScreenPosition.X == play.ScreenPosition.X && play.ScreenPosition.Y - (reset.ScreenPosition.Y + 48) == 6);
        Assert.True(reset.ScreenPosition.Y >= ruler.ScreenPosition.Y - 0.5f); // fitting beside the ruler and waveform
        Assert.Equal(w.Size.X, _view.Overview.Size.X);
        // The master's track: every row, 120 px, right above the structure bar, as wide as the waveform.
        var m = _view.MasterTrack;
        Assert.True(m.Track.Rows.Count == 5 && m.Size.Y == 120);
        Assert.True(m.ScreenPosition.Y + m.Size.Y == _view.Structure.ScreenPosition.Y && m.ScreenPosition.X == w.ScreenPosition.X && m.Size.X == w.Size.X);
        var lockB = _view.LockButton;
        Assert.True(lockB.ScreenPosition.Y == _view.Structure.ScreenPosition.Y && lockB.Size == new float2(18, 18));
        Assert.True(_view.GenerateButton.ScreenPosition.X > lockB.ScreenPosition.X + 18 && _view.GenerateButton.ScreenPosition.X + 18 <= play.ScreenPosition.X + 48 + 0.5f);
        var plate = _view.MasterPlate;
        Assert.True(plate.ScreenPosition.Y == m.ScreenPosition.Y && plate.Size.Y == m.Size.Y && plate.ScreenPosition.X >= m.ScreenPosition.X + m.Size.X);
        Assert.Equal(["Master"], plate.Lines);
    }

    [Fact]
    public void The_fixtures_timelines_fill_the_space_above_the_master()
    {
        _fixtures.Add(Fx("0/1", "Par", Par()));
        OpenLoaded();
        var tracks = _view.FixtureTracks;
        Assert.Equal("Par", Assert.Single(tracks).Track.Title);
        var sc = _view.FixtureScroll;
        var m = _view.MasterTrack;
        Assert.True(sc.ScreenPosition.Y + sc.Size.Y <= m.ScreenPosition.Y + 0.5f && sc.ScreenPosition.Y < 60);
        Assert.True(tracks[0].ScreenPosition.X == m.ScreenPosition.X && tracks[0].Size.X == m.Size.X); // exactly as wide as the master
        var plate = _view.FixturePlates[0];
        Assert.True(plate.Size.Y == tracks[0].Size.Y && plate.ScreenPosition.X > tracks[0].ScreenPosition.X + tracks[0].Size.X);
        for (var i = 0; i < 3; i++)
            _fixtures.Add(Fx($"0/{i + 2}", $"LED Bar {i + 1}", Profiles.Simple("bar", "Bar", [.. Enumerable.Range(0, 3).SelectMany(h => new[] { ($"R{h}", "RED"), ($"G{h}", "GREEN"), ($"B{h}", "BLUE") })])));
        _tl.RefreshFixtures();
        Frame();
        Assert.Equal(13, _view.FixtureTracks.Count);
        Assert.Equal(["LED Bar 1", "Head 1"], _view.FixturePlates[2].Lines); // a head's plate: two lines
        Assert.True(sc.CanScrollY); // more than fits: it scrolls
        Assert.Equal(m.ScreenPosition.Y, _view.MasterTrack.ScreenPosition.Y); // without pushing the master down
    }

    // ---------------------------------------------------------------- ruler --

    [Fact]
    public void The_bar_ruler()
    {
        var song = Song();
        OpenLoaded(song);
        var r = _view.Ruler;
        var w = _view.Wave;
        w.SetView(0, 10);
        var labels = r.BarLabels();
        Assert.Equal([1, 2, 3, 4, 5], labels.Select(l => l.Number));
        Assert.All(labels, l => Assert.True(Math.Abs(l.X - w.TimeToX(0.25 + (l.Number - 1) * 2.0)) < 0.5));
        var ticks = r.Ticks();
        Assert.True(ticks.Count == 20 && ticks.Count(t => t.IsBar) == 5);
        Assert.Equal(w.GridLines(0, 10), ticks); // exactly the waveform's grid lines
        w.SetView(20, 10);
        Assert.Equal([11, 12, 13, 14, 15], r.BarLabels().Select(l => l.Number));
        w.SetView(0, 0);
        Assert.Contains(r.Ticks(), t => !t.IsBar); // beats still marked while readable
        // Zoomed right out (400 s): every Nth bar numbered, N a power of two, at least LABEL_GAP apart.
        _view.SetAnalysis(Wave(400, 40000));
        Frame();
        w.SetView(0, 0);
        var far = r.BarLabels();
        var step = far[1].Number - far[0].Number;
        Assert.True(far[0].Number == 1 && step > 1 && (step & (step - 1)) == 0);
        for (var i = 1; i < far.Count; i++)
            Assert.True(far[i].Number - far[i - 1].Number == step && far[i].X - far[i - 1].X >= BarRuler.LABEL_GAP - 0.5);
        Assert.All(r.Ticks(), t => Assert.True(t.IsBar)); // beats too close: bar ticks only
        _view.SetAnalysis(Wave(40, 4000));
        Frame();
        // Bars 1 and 5 brighter.
        w.SetView(0, 10);
        var phrases = r.PhraseTicks();
        Assert.True(phrases.Count == 2 && Math.Abs(phrases[0] - w.TimeToX(0.25)) < 0.5 && Math.Abs(phrases[1] - w.TimeToX(8.25)) < 0.5);
        // Counting in before VirtualDJ's first beat: -2, -1, then 1.
        _tl.Open(_tl.Path, Song(4.25));
        _view.SetAnalysis(Wave(40, 4000));
        Frame();
        w.SetView(0, 10);
        Assert.Equal([-2, -1, 1, 2, 3], r.BarLabels().Select(l => l.Number));
    }

    [Fact]
    public void Moving_the_playhead()
    {
        OpenLoaded(Song());
        var r = _view.Ruler;
        var w = _view.Wave;
        w.SetView(0, 10);
        Frame();
        float2 OnRuler(double t) => r.ScreenPosition + new float2((float)w.TimeToX(t), 9);
        Click(OnRuler(3));
        Assert.True(Math.Abs(_view.PlaybackPosition - 3) < 0.01 && Math.Abs(w.SongPos - 3) < 0.01); // a click on the ruler
        var mt = _view.MasterTrack;
        Assert.True(Math.Abs(mt.PlayheadX() - mt.TimeToX(3)) < 0.5 && mt.PlayheadX() > 0); // down the timelines too
        // Dragging the line on a timeline scrubs; a press beside it selects.
        var y = At(mt.Track, Rc, 0).Y;
        var onLine = new float2(mt.ScreenPosition.X + (float)mt.PlayheadX() + 2, y);
        _ui.MouseMove(onLine);
        Assert.Equal(Cursor.ResizeHorizontal, _ui.Cursor);
        _ui.MouseButton(MouseButton.Left, true);
        _ui.MouseMove(new float2(mt.ScreenPosition.X + (float)mt.TimeToX(5), y));
        Frame();
        Assert.True(_view.IsScrubbing && Math.Abs(w.SongPos - 5) < 0.01 && Math.Abs(mt.PlayheadX() - mt.TimeToX(5)) < 0.5);
        _ui.MouseButton(MouseButton.Left, false);
        Frame();
        Assert.True(!_view.IsScrubbing && Math.Abs(_view.PlaybackPosition - 5) < 0.01 && _tl.Selection.IsEmpty);
        _ui.MouseMove(new float2(mt.ScreenPosition.X + (float)mt.TimeToX(5) + 20, y));
        _ui.MouseButton(MouseButton.Left, true);
        Assert.True(mt.IsSelecting && !_view.IsScrubbing);
        _ui.MouseButton(MouseButton.Left, false);
        _tl.Selection.Clear();
        _view.Seek(3);
        w.SetView(20, 10);
        Assert.True(mt.PlayheadX() < 0); // out of view: not drawn
        // While playing, dragging moves the line; the audio plays on, and jumps there on release.
        w.SetView(0, 10);
        Frame();
        _view.Play();
        Advance(0.3);
        var playingAt = _view.PlaybackPosition;
        _ui.MouseMove(OnRuler(5));
        _ui.MouseButton(MouseButton.Left, true);
        _ui.MouseMove(OnRuler(6.5));
        _ui.MouseMove(OnRuler(7));
        Frame();
        Assert.True(_view.IsScrubbing && Math.Abs(w.SongPos - 7) < 0.01);
        Assert.True(_view.IsPlaying && Math.Abs(_player.Position - playingAt) < 0.05);
        _ui.MouseButton(MouseButton.Left, false);
        Frame();
        Assert.True(!_view.IsScrubbing && Math.Abs(_view.PlaybackPosition - 7) < 0.1);
        Advance(0.3);
        Assert.True(_view.PlaybackPosition > 7.1);
        _view.Pause();
        // On the waveform itself.
        _ui.MouseMove(w.ScreenPosition + new float2((float)w.TimeToX(2), 40));
        _ui.MouseButton(MouseButton.Left, true);
        _ui.MouseMove(w.ScreenPosition + new float2((float)w.TimeToX(4.5), 40));
        Frame();
        Assert.True(Math.Abs(w.SongPos - 4.5) < 0.01);
        _ui.MouseButton(MouseButton.Left, false);
        Frame();
        Assert.True(Math.Abs(_view.PlaybackPosition - 4.5) < 0.01);
    }

    // ------------------------------------------------------------ playback --

    [Fact]
    public void Playback()
    {
        OpenLoaded(Song());
        var w = _view.Wave;
        var (play, reset) = (_view.PlayButton, _view.ResetButton);
        Assert.True(_view.CanPlay && !play.Disabled && !reset.Disabled && play.Glyph == TransportGlyph.Play);
        Assert.True(w.ShowCursor && w.CursorColor == Theme.TimelinePlayhead && _view.Overview.ShowCursor && w.SongPos == 0);
        Assert.True(w.GridBeats.Length == 80 && Gd.ApproxEqual(w.GridBeats[0], 0.25) && w.BarPhase == 0); // VirtualDJ's grid
        Click(Centre(play));
        Assert.True(_view.IsPlaying && play.Glyph == TransportGlyph.Pause);
        Advance(0.6);
        var p1 = _view.PlaybackPosition;
        Assert.True(p1 > 0.3 && p1 < 1 && Math.Abs(w.SongPos - p1) < 0.1);
        // Pausing between frames: the line stays exactly where it was drawn.
        _player.Now += 0.012;
        var drawn = w.SongPos;
        Click(Centre(play));
        Assert.True(w.SongPos == drawn && _view.Overview.SongPos == drawn);
        var held = _view.PlaybackPosition;
        Advance(0.3);
        Assert.True(!_view.IsPlaying && Gd.ApproxEqual(_view.PlaybackPosition, held) && play.Glyph == TransportGlyph.Play);
        Click(Centre(play));
        Advance(0.2);
        Assert.True(_view.PlaybackPosition > held);
        // Back to the start: still playing, the view scrolled back.
        w.SetView(10, 10);
        Frame();
        Click(Centre(reset));
        Assert.True(_view.PlaybackPosition < 0.2 && _view.IsPlaying && w.ViewStart == 0);
        // Paging along with the playhead.
        w.SetView(5, 8);
        _view.Seek(12.8);
        Advance(0.5);
        Assert.InRange(w.ViewStart, 12.5, 13.5);
        // Space.
        _view.Pause();
        Assert.True(_view.KeyDown(Key.Space, Modifiers.None) && _view.IsPlaying);
        _view.KeyDown(Key.Space, Modifiers.None);
        Assert.False(_view.IsPlaying);
        // Played to the end: stopped, back at the start, in view.
        _view.Seek(39.6);
        _view.Play();
        Advance(1.2);
        Assert.True(!_view.IsPlaying && play.Glyph == TransportGlyph.Play);
        Assert.True(_view.PlaybackPosition == 0 && w.SongPos == 0 && w.ViewStart == 0);
        _view.Play();
        Assert.True(_view.IsPlaying && _view.PlaybackPosition < 0.5);
        // Hiding the timeline pauses (and saves).
        _view.Hidden();
        Assert.False(_view.IsPlaying);
        // A song VirtualDJ doesn't know has no grid; a missing file can't play.
        _view.Open(_tl.Path, null);
        Frame();
        Assert.Empty(w.GridBeats);
        _view.Open(_dir.File("gone.mp3"));
        Frame();
        Assert.True(!_view.CanPlay && play.Disabled && reset.Disabled && !w.ShowCursor);
    }

    // -------------------------------------------------------- the structure --

    static SongAnalysis Grid20()
    {
        var a = new SongAnalysis { Duration = 20 };
        a.BeatTimes = [.. Enumerable.Range(0, 40).Select(i => 0.25f + i * 0.5f)];
        a.Downbeats = [.. Enumerable.Range(0, 10).Select(i => 0.25f + i * 2f)];
        a.Sections = [new(0, 8.25, "Verse", 0.4, 0), new(8.25, 20, "Chorus", 0.9, 1)];
        return a;
    }

    [Fact]
    public void Structure_snapping()
    {
        var tl = new SongTimelines();
        var sb = new StructureBar(MainScreenTests.TestFonts.Small, tl, _menu) { Width = 1000, Editable = true };
        var a = Grid20();
        sb.SetAnalysis(a);
        sb.Measure();
        sb.Arrange(float2.Zero);
        void DragBorder(double from, double to)
        {
            sb.Press(sb.TimeToX(from));
            sb.DragTo(sb.TimeToX(to));
            sb.Release();
        }
        Assert.Equal(SnapMode.Bar, tl.Snap); // bars by default
        DragBorder(8.25, 9.1);
        Assert.Equal(8.25, a.Sections[0].End, 6); // 9.1 s: the nearest bar
        tl.Snap = SnapMode.Beat;
        DragBorder(8.25, 9.1);
        Assert.Equal(9.25, a.Sections[0].End, 6); // the nearest beat
        tl.Snap = SnapMode.None;
        DragBorder(9.25, 9.1);
        Assert.True(Math.Abs(a.Sections[0].End - 9.1) < 0.03 && a.Sections[1].Start == a.Sections[0].End); // where it's dropped, its neighbour following
        // Split Here obeys it too.
        tl.Snap = SnapMode.Beat;
        Assert.True(StructureEdits.Split(a, 1, 13.4, tl.Snap));
        Assert.True(a.Sections.Count == 3 && Gd.ApproxEqual(a.Sections[1].End, 13.25));
        // A border dragged right up to the far one: the segment it closed up goes, either side.
        tl.Snap = SnapMode.None;
        StructureEdits.Relabel(a, 1, "Build");
        DragBorder(9.1, 15);
        Assert.Equal(["Verse", "Chorus"], a.Sections.Select(s => s.Label));
        Assert.True(Gd.ApproxEqual(a.Sections[0].End, 13.25) && a.Sections[1].Start == a.Sections[0].End);
        DragBorder(13.25, 4);
        Assert.True(a.Sections.Count == 2 && Gd.ApproxEqual(a.Sections[1].Start, 4));
        DragBorder(4, -1);
        Assert.True(a.Sections.Count == 1 && a.Sections[0].Label == "Chorus" && a.Sections[0].Start == 0 && a.Sections[0].End == 20);
        // The menu: the kinds, Split Here, Delete Segment, and Snapping with the three choices, the current one ticked.
        tl.Snap = SnapMode.Beat;
        var items = sb.MenuFor(0, 5);
        Assert.Equal([.. StructureEdits.Labels, "Split Here", "Delete Segment", "Snapping"], items.Select(i => i.Text));
        Assert.True(items.Single(i => i.Text == "Chorus").Checked);
        var snap = items[^1].Submenu!;
        Assert.Equal(["Bar", "Beat", "No Snapping"], snap.Select(i => i.Text));
        Assert.Equal([false, true, false], snap.Select(i => i.Checked));
        snap[2].Chosen();
        Assert.Equal(SnapMode.None, tl.Snap);
        snap[0].Chosen();
        Assert.Equal(SnapMode.Bar, tl.Snap);
    }

    [Fact]
    public void The_structure_bar_in_the_timeline()
    {
        var gate = new ManualResetEventSlim();
        _analyse = _ =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            var a = new SongAnalysis { Duration = 40, Bpm = 117 };
            a.Sections = [new(0, 13.1, "Intro", 0.3, 0), new(13.1, 27, "Chorus", 0.9, 1), new(27, 40, "Outro", 0.2, 2)];
            return a;
        };
        var song = Song();
        var wav = OpenLoaded(song);
        var (lockB, gen) = (_view.LockButton, _view.GenerateButton);
        Assert.True(_tl.Structure is null && lockB.Disabled && gen.Disabled); // while analysing
        Assert.Contains("analysing", _view.Structure.Placeholder);
        gate.Set();
        Wait(() => _tl.Structure is not null);
        var a = _tl.Structure!;
        Assert.True(!lockB.Disabled && !gen.Disabled);
        Assert.True(a.Bpm == 120 && Gd.ApproxEqual(a.Downbeats[1], 2.25)); // VirtualDJ's grid swapped in
        Assert.All(a.Sections.Skip(1), s => Assert.Contains((float)s.Start, a.Downbeats)); // borders on its bars
        var sb = _view.Structure;
        Assert.True(sb.ScreenPosition.Y + sb.Size.Y == _view.Ruler.ScreenPosition.Y && sb.Size.Y == _view.Ruler.Size.Y && sb.Size.X == _view.Ruler.Size.X);
        // Zoom follows the waveform.
        _view.Wave.SetView(10, 8);
        Frame();
        Assert.True(sb.ViewStart == 10 && sb.ViewLen == 8);
        // Locked to start; the button unlocks and locks it.
        Assert.True(_tl.StructureLocked && !sb.Editable && lockB.Glyph == TransportGlyph.Locked);
        Click(Centre(lockB));
        Assert.True(!_tl.StructureLocked && sb.Editable && lockB.Glyph == TransportGlyph.Unlocked);
        Click(Centre(lockB));
        Assert.True(_tl.StructureLocked && !sb.Editable);
        // Locked: a click sets the playhead, and no border drags.
        Click(sb.ScreenPosition + new float2((float)sb.TimeToX(13), 9));
        Assert.True(Math.Abs(_view.PlaybackPosition - 13) < 0.05 && sb.DragBorder == -1);
        // Unlocked: a border dragged on the zoomed bar moves a bar along.
        Click(Centre(lockB));
        var bEnd = a.Sections[0].End;
        _view.Wave.SetView(Math.Max(bEnd - 6, 0), 12);
        Frame();
        Drag(sb.ScreenPosition + new float2((float)sb.TimeToX(bEnd), 9), sb.ScreenPosition + new float2((float)sb.TimeToX(bEnd + 2.1), 9));
        var newEnd = a.Sections[0].End;
        Assert.True(Math.Abs(newEnd - (bEnd + 2)) < 0.01 && a.Downbeats.Contains((float)newEnd) && a.Sections[1].Start == newEnd);
        Assert.True(_tl.CanUndo); // an edit
        Click(Centre(lockB));
        // Generate: a show from it; again, another variation; undoable.
        _tl.Master.DimmerNodes = [];
        Click(Centre(gen));
        Assert.True(_tl.Master.DimmerNodes.Count > 2 && _tl.Master.ColourSegments.Count > 0);
        Assert.Empty(_tl.Master.MovementSegments); // no positions: no movement
        var first = string.Join(";", _tl.Master.ColourSegments);
        Click(Centre(gen));
        Assert.NotEqual(first, string.Join(";", _tl.Master.ColourSegments));
        Assert.True(_tl.HasUnsavedTimelines && _tl.CanUndo);
        // Reopening keeps the (edited) structure; another song locks it again.
        StructureEdits.Relabel(a, 0, "Bridge");
        _tl.StructureEdited();
        _view.Open(wav, song);
        Frame();
        Assert.True(_tl.Structure == a && a.Sections[0].Label == "Bridge");
        Click(Centre(lockB));
        _view.Open(wav, song);
        Assert.False(_tl.StructureLocked); // the same song: left as it is
        _view.Open(_dir.File("another.wav"));
        Frame();
        Assert.True(_tl.StructureLocked && !sb.Editable);
    }

    // ----------------------------------------------------------- the tracks --

    [Fact]
    public void Ctrl_click_on_the_master_line_adds_a_node()
    {
        OpenLoaded();
        var m = _tl.Master;
        m.DimmerNodes = [];
        Frame();
        var at = At(m, Rd, 10);
        var view = _view.MasterTrack;
        var (ry, _) = view.RowRect(Rd);
        Click(new float2(at.X, view.ScreenPosition.Y + ry + 4), Modifiers.Control); // a flat 100 % line, along the row's top
        Assert.Single(m.DimmerNodes);
        Assert.True(_tl.CanUndo);
        // Dragging the node moves it, not the selection; Ctrl+right-click removes it, without the menu.
        var node = view.ScreenPosition + new float2((float)view.TimeToX(m.DimmerNodes[0].X), ry + 4);
        Drag(node, node + new float2(80, 40));
        Assert.True(_tl.Selection.IsEmpty && m.DimmerNodes[0].X > 10 && m.DimmerNodes[0].Y < 1);
        var moved = view.ScreenPosition + new float2((float)view.TimeToX(m.DimmerNodes[0].X), 0) with { Y = 0 };
        var y = view.ScreenPosition.Y + ry + rhLevel(view, m.DimmerNodes[0].Y);
        _ui.MouseMove(new float2(moved.X, y));
        _ui.MouseButton(MouseButton.Right, true, Modifiers.Control);
        Frame();
        Assert.Empty(m.DimmerNodes);
        Assert.False(_menu.Visible);
    }

    static float rhLevel(TrackView v, double level)
    {
        var (_, rh) = v.RowRect(TimelineRow.Dimmer);
        return (float)(rh - 4 - level * (rh - 8));
    }

    [Fact]
    public void Selecting_with_the_mouse()
    {
        _fixtures.Add(Fx("par", "Par", Par()));
        OpenLoaded(Song(0));
        _view.Wave.SetView(0, 20);
        Frame();
        var m = _tl.Master;
        var par = _tl.FixtureTracks[0];
        var sel = _tl.Selection;
        // A drag on a row selects the whole bars it touches, without touching the line (at 0 %, along the bottom).
        Drag(At(m, Rd, 3.1) - new float2(0, 30), At(m, Rd, 6.9) - new float2(0, 30));
        var b = Assert.Single(sel.Blocks);
        Assert.Equal([TimelineSelection.RowId("master", Rd)], b.Rows);
        Assert.Equal((2.0, 8.0), (b.T0, b.T1));
        // Across tracks: every row between, top to bottom.
        Drag(At(par, Rd, 4) - new float2(0, 30), At(m, Rp, 5));
        var rows = Assert.Single(sel.Blocks).Rows;
        Assert.Equal(7, rows.Count);
        Assert.Equal(TimelineSelection.RowId("par", Rd), rows[0]);
        Assert.Equal(TimelineSelection.RowId("master", Rp), rows[6]);
        // A plain click on a bar selects it.
        Click(At(m, Rm, 9));
        Assert.Equal((8.0, 10.0), (sel.Blocks[0].T0, sel.Blocks[0].T1));
        // Over a selection: the cursor says what a drag would do.
        sel.SetBlocks([SelectionBlock.Of([TimelineSelection.RowId("master", Rd), TimelineSelection.RowId("master", Rm)], 4, 8)]);
        Frame();
        _ui.MouseMove(At(m, Rm, 8));
        Assert.Equal(Cursor.ResizeHorizontal, _ui.Cursor);
        _ui.MouseMove(At(m, Rm, 6));
        Assert.Equal(Cursor.Move, _ui.Cursor);
        // Dragging the end moves it, snapped to the bar.
        Drag(At(m, Rm, 8), At(m, Rm, 9.7));
        Assert.True(sel.Blocks[0].T0 == 4 && Gd.ApproxEqual(sel.Blocks[0].T1, 10));
    }

    [Fact]
    public void Selecting_on_the_master_below_a_scrolled_fixture_list()
    {
        for (var i = 0; i < 12; i++)
            _fixtures.Add(Fx($"par{i}", $"Par {i}", Par()));
        OpenLoaded();
        _view.Wave.SetView(0, 20);
        Frame();
        var m = _tl.Master;
        foreach (var row in new[] { Rd, Rc, Rm })
        {
            _tl.Selection.Clear();
            Drag(At(m, row, 3.1), At(m, row, 6.9));
            Assert.Equal([TimelineSelection.RowId("master", row)], Assert.Single(_tl.Selection.Blocks).Rows);
        }
    }

    [Fact]
    public void The_context_menu()
    {
        _fixtures.Add(Fx("p1", "Par 1", Par()));
        OpenLoaded();
        var sel = _tl.Selection;
        Assert.Equal(["Select All"], _view.ContextItems(false).Select(i => i.Text));
        sel.SetBlocks([SelectionBlock.Of([TimelineSelection.RowId("master", Rd), TimelineSelection.RowId("p1", Rd)], 10, 14)]);
        Assert.Equal(["Delete", "Cut", "Copy", "Dimmer", "Select All", "Deselect"], _view.ContextItems(true).Select(i => i.Text));
        var dimmer = _view.DimmerMenu();
        Assert.Equal(TimelineView.DimmerItems.Select(d => d.Name), dimmer.Select(i => i.Text));
        Assert.False(dimmer.Single(i => i.Text == "Pulse on Every Beat").Enabled); // no beat grid
        // A right-click on the selection opens it there.
        _view.Wave.SetView(0, 20);
        Frame();
        _ui.MouseMove(At(_tl.Master, Rd, 12));
        _ui.MouseButton(MouseButton.Right, true);
        Frame();
        Assert.True(_menu.Visible);
        Assert.Contains("Dimmer", _menu.ItemTexts);
        _menu.Close();
        // Pulse In from the menu; then Undo and Redo are offered.
        _tl.Master.DimmerNodes = [new Vector2(0, 0.4f), new Vector2(40, 0.4f)];
        _tl.MarkEdited();
        dimmer.Single(i => i.Text == "Pulse In").Chosen();
        Assert.True(Gd.ApproxEqual(_tl.Master.DimmerAt(12), 1));
        Assert.Contains("Undo", _view.ContextItems(true).Select(i => i.Text));
        _view.ContextItems(true).Single(i => i.Text == "Undo").Chosen();
        Assert.True(Gd.ApproxEqual(_tl.Master.DimmerAt(12), 0.4));
        Assert.Contains("Redo", _view.ContextItems(true).Select(i => i.Text));
        // A colour selection: a Colour submenu, no Dimmer.
        sel.SetBlocks([SelectionBlock.Of([TimelineSelection.RowId("p1", Rc), TimelineSelection.RowId("master", Rc)], 20, 30)]);
        var items = _view.ContextItems(true).Select(i => i.Text).ToList();
        Assert.True(items.Contains("Colour") && !items.Contains("Dimmer"));
        var colour = _view.ColourMenu();
        Assert.True(colour[1].Text == "Gradient…" && colour[2].Text == "Rainbow" && colour[^1].Text == "Clear Colour");
        colour.Single(i => i.Text == "Blue").Chosen();
        var p1 = _tl.FixtureTracks[0];
        Assert.True(p1.ColourAt(25) == new Rgb(0, 0, 1) && _tl.Master.ColourAt(25) == new Rgb(0, 0, 1) && p1.ColourAt(31) is null);
        // Off the selection: Paste Here once there's something copied.
        _tl.CopySelection();
        sel.Clear();
        var off = _view.ContextItems(false, p1, 50.0 / 60 * 40).Select(i => i.Text).ToList();
        Assert.True(off.Contains("Paste Here") && off.Contains("Undo") && !off.Contains("Delete") && !off.Contains("Copy"));
        // The other rows' submenus.
        Assert.Equal(["Strobe Slow", "Strobe Medium", "Strobe Fast", "Strobe Off"], _view.StrobeMenu().Select(i => i.Text));
        Assert.Equal(["None", "Circle", "Figure 8", "Pan Sweep", "Tilt Sweep", "Small", "Medium", "Large", "1 bar", "2 bars", "4 bars", "Clear Movement"], _view.MovementMenu().Select(i => i.Text));
        var position = _view.PositionMenu();
        Assert.True(!position[0].Enabled && position[^1].Text == "Clear Position"); // no positions: a hint
        _view.Positions = () => [new PositionChoice("a", "Crowd", Theme.Accent), new PositionChoice("b", "Stage", Theme.Focus)];
        Assert.Equal(["Crowd", "Stage", "Clear Position"], _view.PositionMenu().Select(i => i.Text));
    }

    [Fact]
    public void Keys()
    {
        OpenLoaded();
        var m = _tl.Master;
        var sel = _tl.Selection;
        m.DimmerNodes = [new Vector2(1, 0.5f), new Vector2(30, 0.5f)];
        _tl.MarkEdited();
        sel.SetBlocks([SelectionBlock.Of([TimelineSelection.RowId("master", Rd)], 4, 8)]);
        Assert.True(_view.KeyDown(Key.Delete, Modifiers.None) && m.DimmerAt(6) == 0);
        Assert.True(_view.KeyDown(Key.Z, Modifiers.Control) && m.DimmerAt(6) == 0.5);
        Assert.True(_view.KeyDown(Key.Y, Modifiers.Control) && m.DimmerAt(6) == 0);
        _view.KeyDown(Key.Z, Modifiers.Control);
        Assert.True(_view.KeyDown(Key.Z, Modifiers.Control | Modifiers.Shift) && m.DimmerAt(6) == 0); // so does Ctrl+Shift+Z
        _view.KeyDown(Key.C, Modifiers.Control);
        Assert.NotNull(_tl.Clipboard);
        sel.Clear();
        Assert.True(_view.KeyDown(Key.A, Modifiers.Control));
        Assert.True(sel.Blocks[0].T0 == 0 && Gd.ApproxEqual(sel.Blocks[0].T1, 40));
        Assert.False(_view.KeyDown(Key.A, Modifiers.None)); // plain A: nothing
        Assert.False(_view.KeyDown(Key.D, Modifiers.None) || sel.IsEmpty);
        Assert.True(_view.KeyDown(Key.D, Modifiers.Control) && sel.IsEmpty); // Ctrl+D deselects
        _view.KeyDown(Key.A, Modifiers.Control);
        Assert.True(_view.KeyDown(Key.Escape, Modifiers.None) && sel.IsEmpty); // so does Escape
        Assert.False(_view.KeyDown(Key.Escape, Modifiers.None)); // nothing selected: left for others
    }

    [Fact]
    public void Mute_solo_and_collapsing_from_the_plates()
    {
        _fixtures.AddRange([Fx("p1", "Par 1", Par()), Fx("b1", "Bar 1", TwoHeadBar())]);
        OpenLoaded();
        var plates = _view.FixturePlates.ToDictionary(p => p.Track.Key);
        Assert.Equal([NamePlate.MUTE], _view.MasterPlate.Buttons);
        Assert.Equal([NamePlate.MUTE, NamePlate.SOLO], plates["b1/h0"].Buttons);
        float2 Button(NamePlate p, int which) => p.ScreenPosition + p.ButtonRect(which)!.Value.Pos + p.ButtonRect(which)!.Value.Size / 2;
        Click(Button(plates["b1"], NamePlate.MUTE));
        var t = _tl.FixtureTracks.ToDictionary(tr => tr.Key);
        Assert.True(t["b1"].Mute && t["b1/h0"].Silenced && !t["p1"].Silenced);
        Assert.True(_tl.HasUnsavedTimelines && !_tl.CanUndo);
        Click(Button(plates["b1"], NamePlate.MUTE));
        Assert.False(t["b1"].Mute);
        Click(Button(plates["b1/h1"], NamePlate.SOLO));
        Assert.True(t["b1/h1"].Solo && t["b1/h0"].Silenced && t["p1"].Silenced);
        Click(Button(_view.MasterPlate, NamePlate.MUTE));
        Assert.True(_tl.Master.Mute && t.Values.All(tr => tr.Silenced));
        Click(Button(_view.MasterPlate, NamePlate.MUTE));
        // A click on a plate's name collapses its track (its heads with it); a click on the bar expands it.
        var b1 = plates["b1"];
        var (ny, nh) = b1.NameRect();
        Click(b1.ScreenPosition + new float2(b1.Size.X / 2, ny + nh / 2));
        Assert.True(t["b1"].Collapsed && t["b1/h0"].Collapsed && !t["p1"].Collapsed);
        var bar = _view.ViewOf(t["b1"])!;
        Assert.True(bar.Size.Y == TrackView.COLLAPSED_HEIGHT && bar.Size.X > _view.ViewOf(t["p1"])!.Size.X); // across the plate's column too
        Assert.Equal("Bar 1", bar.CollapsedName);
        Assert.Equal("Bar 1 · Head 2", _view.ViewOf(t["b1/h1"])!.CollapsedName);
        // Its M still works, collapsed; a click elsewhere on it expands it.
        var mute = bar.CollapsedButtonRect(NamePlate.MUTE)!.Value;
        Click(bar.ScreenPosition + mute.Pos + mute.Size / 2);
        Assert.True(t["b1"].Collapsed && t["b1"].Mute);
        Click(bar.ScreenPosition + new float2(100, 10));
        Assert.True(!t["b1"].Collapsed && !t["b1/h0"].Collapsed);
        // The master's plate doesn't collapse it.
        var mp = _view.MasterPlate;
        var (my, mh) = mp.NameRect();
        Click(mp.ScreenPosition + new float2(mp.Size.X / 2, my + mh / 2));
        Assert.False(_tl.Master.Collapsed);
    }

    // ---------------------------------------------------------------- panning --

    [Fact]
    public void A_middle_button_drag_pans_the_view()
    {
        OpenLoaded();
        _view.SetAnalysis(Wave(100, 10000));
        Frame();
        var w = _view.Wave;
        w.SetView(40, 20);
        Frame();
        var perPx = 20.0 / w.Size.X;
        var (min, max) = _view.PanArea();
        Assert.True(min.X == w.ScreenPosition.X && max.X == w.ScreenPosition.X + w.Size.X && max.Y == w.ScreenPosition.Y + w.Size.Y && min.Y <= _view.MasterTrack.ScreenPosition.Y);
        var p = w.ScreenPosition + new float2(400, w.Size.Y / 2);
        _ui.MouseMove(p);
        _ui.MouseButton(MouseButton.Middle, true);
        Assert.True(_view.IsPanning && _ui.Cursor == Cursor.Grab);
        _ui.MouseMove(p + new float2(-200, 5));
        Assert.True(Gd.ApproxEqual(w.ViewStart, 40 + 200 * perPx) && Gd.ApproxEqual(w.VisibleLength(), 20));
        _ui.MouseMove(p + new float2(300, 0));
        Assert.True(Gd.ApproxEqual(w.ViewStart, 40 - 300 * perPx) && w.SongPos == 0); // the playhead doesn't move
        _ui.MouseButton(MouseButton.Middle, false);
        Assert.False(_view.IsPanning);
        var after = w.ViewStart;
        _ui.MouseMove(p + new float2(-500, 0));
        Assert.Equal(after, w.ViewStart);
        // Over the master's timeline too; the structure bar follows.
        var mt = _view.MasterTrack;
        var q = mt.ScreenPosition + new float2(600, mt.Size.Y / 2);
        _ui.MouseMove(q);
        _ui.MouseButton(MouseButton.Middle, true);
        _ui.MouseMove(q + new float2(-100, 0));
        _ui.MouseButton(MouseButton.Middle, false);
        Frame();
        Assert.True(Gd.ApproxEqual(w.ViewStart, after + 100 * perPx) && _view.Structure.ViewStart == w.ViewStart);
        // Clamped at the ends.
        _ui.MouseMove(q);
        _ui.MouseButton(MouseButton.Middle, true);
        _ui.MouseMove(q + new float2(5000, 0));
        Assert.Equal(0, w.ViewStart);
        _ui.MouseMove(q + new float2(-50000, 0));
        Assert.Equal(80, w.ViewStart, 6);
        _ui.MouseButton(MouseButton.Middle, false);
        // Not from the buttons' column or the overview.
        _ui.MouseMove(Centre(_view.PlayButton));
        _ui.MouseButton(MouseButton.Middle, true);
        Assert.False(_view.IsPanning);
        _ui.MouseButton(MouseButton.Middle, false);
        _ui.MouseMove(Centre(_view.Overview));
        _ui.MouseButton(MouseButton.Middle, true);
        Assert.False(_view.IsPanning);
        _ui.MouseButton(MouseButton.Middle, false);
    }

    // ---------------------------------------------------------------- drawing --

    [Fact]
    public void It_all_draws()
    {
        _fixtures.AddRange([Fx("p1", "Par 1", Par()), Fx("b1", "Bar 1", TwoHeadBar())]);
        _analyse = _ => new SongAnalysis { Duration = 40, Sections = [new(0, 20, "Verse", 0.4, 0), new(20, 40, "Chorus", 0.9, 1)] };
        OpenLoaded(Song());
        Wait(() => _tl.Structure is not null);
        _tl.GenerateShow();
        _tl.SelectAll();
        _view.Wave.SetView(5, 10);
        Frame();
        var batch = new Batch(MainScreenTests.Atlas);
        _ui.Draw(batch);
        Assert.True(batch.Vertices.Length > 1000);
        // Everything clipped stays inside the clip: nothing drawn left of the screen.
        foreach (var v in batch.Vertices)
            Assert.True(v.Position.X >= -1 && v.Position.Y >= -1);
    }

    [Fact]
    public void Clipped_triangles_stay_inside_the_clip()
    {
        var batch = new Batch(MainScreenTests.Atlas);
        batch.PushClip(new float2(10, 10), new float2(20, 20));
        batch.AddTriangle(new float2(0, 0), new float2(40, 0), new float2(0, 40), Colors.White);
        batch.AddLine(new float2(-100, 15), new float2(100, 15), 2, Colors.White);
        batch.AddTextUp(MainScreenTests.TestFonts.Body, "Master", new float2(12, 28), Colors.White);
        batch.PopClip();
        Assert.True(batch.Vertices.Length >= 6);
        foreach (var v in batch.Vertices)
            Assert.True(v.Position.X >= 9.999f && v.Position.X <= 30.001f && v.Position.Y >= 9.999f && v.Position.Y <= 30.001f);
    }
}

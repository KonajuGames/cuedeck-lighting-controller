using System.Collections.Concurrent;
using Brutal.Numerics;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Dj;
using Cuedeck.Input;
using Cuedeck.Timelines;
using StrobeSpeed = Cuedeck.Timelines.StrobeSpeed;

namespace Cuedeck.UI.Timeline;

/// <summary>
/// A named position as the timeline's Position menu and rows show it.
/// </summary>
sealed record PositionChoice(string Id, string Name, float4 Colour);

/// <summary>
/// DJ Edit Mode's song timeline: a status line; the fixtures' (and heads') timelines, scrolling; the master's; the
/// song's structure, the bar ruler and the zoomable waveform, with Back to start, Play / Pause, the structure's lock
/// and Generate beside them; and the whole song's waveform under it all, its box showing (and moving) the view.
/// <para>
/// A song's waveform and audio are read on a worker thread (the whole-song waveform is the progress bar meanwhile);
/// its structure's analysed by <see cref="SongTimelines"/>. Clicking or dragging the ruler, the waveform or the
/// playhead line moves the playhead (while playing, the audio jumps there on release); the view pages along when the
/// playhead runs off its right. The middle button drags the view along anywhere over the timelines. Keys
/// (<see cref="KeyDown"/>): Space plays and pauses, Delete clears the selection, Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) undo
/// and redo, Ctrl+A / C / X / V select all, copy, cut and paste, Escape or Ctrl+D deselect. Call
/// <see cref="Update"/> each frame. sLight's <c>MusicTimeline</c>'s screen.
/// </para>
/// </summary>
sealed class TimelineView : Region, ITrackHost, IPointerHook
{
    public const float WAVE_HEIGHT = 90;
    public const float OVERVIEW_HEIGHT = 44;
    public const float BUTTON_WIDTH = 48;
    public const float BUTTON_GAP = 6;
    const float GAP = 6;

    /// <summary>
    /// Recently opened songs' waveforms and audio kept in memory.
    /// </summary>
    const int CACHE_SIZE = 6;

    public static readonly (string Name, Rgb Colour)[] ColourPresets =
    [
        ("Red", new(1, 0, 0)),
        ("Orange", new(1, 0.5f, 0)),
        ("Yellow", new(1, 1, 0)),
        ("Green", new(0, 1, 0)),
        ("Cyan", new(0, 1, 1)),
        ("Blue", new(0, 0, 1)),
        ("Purple", new(0.5f, 0, 1)),
        ("Magenta", new(1, 0, 1)),
        ("White", new(1, 1, 1)),
    ];

    public static readonly (DimShape Shape, string Name)[] DimmerItems =
    [
        (DimShape.Black, "Black (0%)"),
        (DimShape.Half, "Half (50%)"),
        (DimShape.Full, "Full (100%)"),
        (DimShape.FadeIn, "Fade In"),
        (DimShape.FadeOut, "Fade Out"),
        (DimShape.PulseIn, "Pulse In"),
        (DimShape.PulseOut, "Pulse Out"),
        (DimShape.Smooth, "Smooth (ramp across)"),
        (DimShape.BeatPulses, "Pulse on Every Beat"),
        (DimShape.BarPulses, "Pulse on Every Bar"),
    ];

    readonly Fonts _fonts;
    readonly ContextMenu _menu;
    readonly ColourPicker? _picker;
    readonly ISongPlayer _player;
    readonly Func<string, PcmAudio> _decode;
    readonly Label _status;
    readonly ScrollPanel _fixtureScroll;
    readonly Panel _fixtureList;
    readonly Label _fixtureEmpty;
    readonly TrackView _master;
    readonly NamePlate _masterPlate;
    readonly StructureBar _structure;
    readonly BarRuler _ruler;
    readonly WaveView _wave;
    readonly WaveView _overview;
    readonly ViewBox _box;
    readonly TransportButton _reset;
    readonly TransportButton _play;
    readonly TransportButton _lock;
    readonly TransportButton _generate;
    readonly List<TrackView> _tracks = [];
    readonly List<NamePlate> _plates = [];
    bool _tracksStale = true;

    // The song being read on a worker thread, and its result once in.
    CancellationTokenSource? _loading;
    readonly ConcurrentQueue<Action> _posted = new();
    readonly Dictionary<string, (SongAnalysis Wave, PcmAudio Audio)> _cache = [];
    readonly List<string> _cacheOrder = [];
    volatile float _progress = -1;

    string _waveNote = "";
    string _playNote = "";
    string _loadedAudio = "";
    double _pos;
    bool _wasPlaying;
    bool _scrubbing;
    float _panX = -1;
    double _panFrom;

    // A colour being picked (Static Colour… or Gradient…): what it's for, and the gradient's ends.
    string _picking = "";
    Rgb _from = new(1, 0, 0);
    Rgb _to = new(0, 0, 1);
    float2 _contextAt;

    public TimelineView(Fonts fonts, ContextMenu menu, ColourPicker? picker, SongTimelines timelines, ISongPlayer player, Func<string, PcmAudio>? decode = null)
    {
        _fonts = fonts;
        _menu = menu;
        _picker = picker;
        _player = player;
        _decode = decode ?? SongDecoder.Decode;
        Timelines = timelines;

        _status = Add(new Label(fonts.Body) { Color = Theme.Muted });
        _fixtureList = new Panel { Spacing = 2 };
        _fixtureEmpty = _fixtureList.Add(new Label(fonts.Body, "No fixtures patched.") { Color = Theme.Faint });
        _fixtureScroll = Add(new ScrollPanel());
        _fixtureScroll.Add(_fixtureList);

        _wave = new WaveView(fonts.Small)
        {
            Zoomable = true,
            CursorColor = Theme.TimelinePlayhead,
            BackgroundColor = Colors.Hex(0x000000),
            Tooltip = "Click or drag: move the playhead · Mouse wheel: zoom · Shift+wheel: scroll",
        };
        _wave.ViewChanged += (start, length) => BoxFollow(start, length);
        _wave.SeekRequested += BeginScrub;
        _wave.SeekDragged += ScrubTo;
        _wave.SeekReleased += EndScrub;

        _master = Add(new TrackView(fonts.Small, fonts.Small, this, timelines.Master));
        _structure = Add(new StructureBar(fonts.Small, timelines, menu) { Editable = false, Tooltip = "Song structure: unlock to drag a border or right-click a section" });
        _structure.SeekRequested += Seek;
        _ruler = Add(new BarRuler(fonts.Small, _wave) { Tooltip = "Click or drag: move the playhead" });
        _ruler.Pressed += BeginScrub;
        _ruler.Dragged += ScrubTo;
        _ruler.Released += EndScrub;
        Add(_wave);

        _reset = Add(new TransportButton { Glyph = TransportGlyph.ToStart, Tooltip = "Back to the start" });
        _reset.Clicked += _ => Rewind();
        _play = Add(new TransportButton { Glyph = TransportGlyph.Play, Tooltip = "Play / pause (Space)" });
        _play.Clicked += _ => TogglePlay();
        _lock = Add(new TransportButton { Glyph = TransportGlyph.Locked });
        _lock.Clicked += _ => Timelines.StructureLocked = !Timelines.StructureLocked;
        _generate = Add(
            new TransportButton
            {
                Glyph = TransportGlyph.Generate,
                Tooltip =
                    "Generate a light show into the timelines from this song's structure, tempo and energy — press again for another variation (replaces them, or just the selected areas; Ctrl+Z undoes). Shift+click analyses the song structure afresh first",
            }
        );
        _generate.Clicked += _ =>
        {
            if ((_generate.PressedWith & Modifiers.Shift) != 0)
                Timelines.ReanalyseAndGenerate();
            else
                Timelines.GenerateShow();
        };
        _masterPlate = Add(new NamePlate(fonts.Body, fonts.Small, timelines, timelines.Master));

        _overview = Add(new WaveView(fonts.Small) { CursorColor = Theme.TimelinePlayhead, BackgroundColor = Colors.Hex(0x000000) });
        _box = Add(new ViewBox { Tooltip = "Drag to scroll · click to jump" });
        _box.ViewMoved += start => _wave.SetView(start, _wave.VisibleLength());

        timelines.TracksRebuilt += () => _tracksStale = true;
        UpdateTransport();
    }

    public SongTimelines Timelines { get; }
    public WaveView Wave => _wave;
    public WaveView Overview => _overview;
    public ViewBox Box => _box;
    public BarRuler Ruler => _ruler;
    public StructureBar Structure => _structure;
    public TransportButton PlayButton => _play;
    public TransportButton ResetButton => _reset;
    public TransportButton LockButton => _lock;
    public TransportButton GenerateButton => _generate;
    public TrackView MasterTrack => _master;
    public NamePlate MasterPlate => _masterPlate;
    public ScrollPanel FixtureScroll => _fixtureScroll;
    public IReadOnlyList<TrackView> FixtureTracks => _tracks;
    public IReadOnlyList<NamePlate> FixturePlates => _plates;

    /// <summary>
    /// The status line: the waveform's loading or error note, and the playback one.
    /// </summary>
    public string StatusText => _status.Text;

    /// <summary>
    /// The named positions, for the Position menu and the rows' colours.
    /// </summary>
    public Func<IReadOnlyList<PositionChoice>>? Positions { get; set; }

    /// <summary>
    /// Each fixture's plate colour by its uid (its model's colour), or the default.
    /// </summary>
    public Func<string, float4?>? PlateColour { get; set; }

    // --------------------------------------------------------------- opening --

    /// <summary>
    /// Shows song <paramref name="path"/>'s timeline (<paramref name="song"/>, VirtualDJ's entry for it, gives the
    /// beat grid). Its waveform and audio are read in the background unless they're cached.
    /// </summary>
    public void Open(string path, DjSong? song = null)
    {
        StopLoading();
        if (path != Timelines.Path)
            StopPlayback();
        Timelines.Open(path, song);
        _tracksStale = true;
        SetAnalysis(null);
        if (_cache.TryGetValue(path, out var cached))
        {
            Touch(path);
            SetAnalysis(cached.Wave);
            LoadAudio(path, cached.Audio);
        }
        else if (!File.Exists(path))
            _waveNote = "File not found.";
        else
        {
            _waveNote = "Reading waveform…";
            _progress = 0;
            _overview.Progress = 0;
            var cancel = new CancellationTokenSource();
            _loading = cancel;
            var decode = _decode;
            Task.Run(() =>
            {
                try
                {
                    var audio = decode(path);
                    cancel.Token.ThrowIfCancellationRequested();
                    var wave = WaveformEnvelope.Compute(audio, WaveformEnvelope.COLS_PER_SEC, f => _progress = f, cancel.Token);
                    wave.Path = path;
                    _posted.Enqueue(() =>
                    {
                        if (_loading != cancel)
                            return;
                        _loading = null;
                        _cache[path] = (wave, audio);
                        Touch(path);
                        SetAnalysis(wave);
                        LoadAudio(path, audio);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    _posted.Enqueue(() =>
                    {
                        if (_loading != cancel)
                            return;
                        _loading = null;
                        _waveNote = $"Couldn't read the waveform: {e.Message.TrimEnd('.')}.";
                        UpdateTransport();
                    });
                }
            });
        }
        UpdateTransport();
    }

    public string CurrentPath => Timelines.Path;

    /// <summary>
    /// Whether the song's waveform and audio are still being read.
    /// </summary>
    public bool IsLoading => _loading is not null;

    void StopLoading()
    {
        _loading?.Cancel();
        _loading = null;
        _progress = -1;
        _overview.Progress = -1;
    }

    void Touch(string path)
    {
        _cacheOrder.Remove(path);
        _cacheOrder.Add(path);
        while (_cacheOrder.Count > CACHE_SIZE)
        {
            _cache.Remove(_cacheOrder[0]);
            _cacheOrder.RemoveAt(0);
        }
    }

    /// <summary>
    /// The song's waveform in (or none): the views reset to the whole song, VirtualDJ's grid laid over it.
    /// </summary>
    internal void SetAnalysis(SongAnalysis? a)
    {
        _wave.SetAnalysis(a);
        _overview.SetAnalysis(a);
        _box.Duration = a?.Duration ?? 0;
        _progress = -1;
        _overview.Progress = -1;
        if (a is null)
        {
            _wave.SetBeatGrid([]);
            _box.SetView(0, 0);
            UpdateCursor();
            return;
        }
        _waveNote = "";
        Timelines.SetDuration(a.Duration);
        _wave.SetBeatGrid(Timelines.GridBeats, Timelines.BeatsPerBar, Timelines.BarPhase);
        _wave.SetView(0, 0); // a song opens fully zoomed out
        BoxFollow(_wave.ViewStart, _wave.VisibleLength());
        UpdateCursor();
    }

    void LoadAudio(string path, PcmAudio audio)
    {
        if (_loadedAudio == path && _player.IsLoaded)
            return;
        _pos = 0;
        var error = _player.Load(audio);
        _loadedAudio = error == "" ? path : "";
        _playNote = error == "" ? "" : $"Can't play this track: {error}.";
        UpdateTransport();
    }

    // -------------------------------------------------------------- playback --

    public bool CanPlay => _player.IsLoaded && _loadedAudio == Timelines.Path && Timelines.Path != "";

    public bool IsPlaying => _player.IsPlaying;

    public bool IsScrubbing => _scrubbing;

    /// <summary>
    /// The playback position, seconds.
    /// </summary>
    public double PlaybackPosition => _player.IsPlaying ? Math.Clamp(_player.Position, 0, Length) : _pos;

    double Length => _player.IsLoaded ? _player.Length : _wave.Analysis is not null ? _wave.Duration() : 0;

    public void TogglePlay()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    public void Play()
    {
        if (!CanPlay || IsPlaying)
            return;
        if (_pos >= Length - 0.05)
            _pos = 0;
        _player.Play(_pos);
        _wasPlaying = true;
        UpdateTransport();
    }

    public void Pause()
    {
        if (!IsPlaying)
            return;
        // Freeze exactly where the playhead was last drawn, rather than reading the audio clock again (later than
        // what's on screen); playing on starts from the same spot.
        _pos = _wave.ShowCursor ? _wave.SongPos : PlaybackPosition;
        _player.Stop();
        _wasPlaying = false;
        UpdateTransport();
    }

    /// <summary>
    /// Back to the start (still playing, if it was), and the view scrolled there.
    /// </summary>
    public void Rewind()
    {
        Seek(0);
        _wave.SetView(0, _wave.VisibleLength());
    }

    /// <summary>
    /// The playhead pressed (ruler, waveform or a track's line): it follows the pointer while it's held, the audio
    /// playing on where it was until it's let go, then jumping there.
    /// </summary>
    public void BeginScrub(double t)
    {
        _scrubbing = true;
        ScrubTo(t);
    }

    public void ScrubTo(double t)
    {
        if (!_scrubbing)
            return;
        _pos = Math.Clamp(t, 0, Length);
        UpdateCursor();
    }

    public void EndScrub(double t)
    {
        if (!_scrubbing)
            return;
        _scrubbing = false;
        Seek(t);
    }

    public void Seek(double t)
    {
        _pos = Math.Clamp(t, 0, Length);
        if (_player.IsPlaying)
            _player.Seek(_pos);
        UpdateCursor();
    }

    void StopPlayback()
    {
        _player.Stop();
        _wasPlaying = false;
        _pos = 0;
    }

    void UpdateTransport()
    {
        _play.Glyph = IsPlaying ? TransportGlyph.Pause : TransportGlyph.Play;
        _play.Disabled = !CanPlay;
        _reset.Disabled = !CanPlay;
        if (IsLoading && _wave.Analysis is null)
            _playNote = "";
        var parts = new[] { _waveNote, _playNote }.Where(n => n != "");
        _status.Text = string.Join("  ·  ", parts);
        UpdateCursor();
    }

    void UpdateCursor()
    {
        var on = CanPlay && _wave.Analysis is not null;
        var t = _scrubbing ? _pos : PlaybackPosition;
        foreach (var w in new[] { _wave, _overview })
        {
            w.ShowCursor = on;
            w.SongPos = t;
        }
        Timelines.SongPos = t;
    }

    /// <summary>
    /// Takes in what's finished in the background, moves the playhead, and syncs the controls: each frame.
    /// </summary>
    public void Update()
    {
        Timelines.Update();
        while (_posted.TryDequeue(out var action))
            action();
        if (_loading is not null)
            _overview.Progress = Math.Max(_progress, 0);
        if (_player.IsPlaying)
        {
            if (!_scrubbing)
            {
                var before = _wave.SongPos;
                UpdateCursor();
                FollowPlayhead(before, _wave.SongPos);
            }
        }
        else if (_wasPlaying)
        {
            // Played to the end: back to the start, ready to play again.
            _wasPlaying = false;
            Rewind();
        }
        UpdateTransport();
        // The structure and its buttons.
        var a = Timelines.Structure;
        if (_structure.Analysis != a)
            _structure.SetAnalysis(a);
        _structure.Placeholder = Timelines.StructurePlaceholder != "" ? Timelines.StructurePlaceholder : "analyse a song to see its structure";
        _structure.Editable = !Timelines.StructureLocked;
        _structure.SetView(_wave.ViewStart, _wave.Analysis is not null ? _wave.VisibleLength() : 0);
        _lock.Glyph = Timelines.StructureLocked ? TransportGlyph.Locked : TransportGlyph.Unlocked;
        _lock.Tooltip = Timelines.StructureLocked ? "Song structure locked: click to edit it" : "Song structure editable: click to lock it";
        _lock.Disabled = a is null;
        _generate.Disabled = a is null;
        if (_picking != "" && _picker is { Visible: false })
            PickerClosed();
        if (_tracksStale)
            RebuildTracks();
    }

    /// <summary>
    /// Pages the view along when the playhead runs off its right edge (only then).
    /// </summary>
    void FollowPlayhead(double before, double now)
    {
        var end = _wave.ViewStart + _wave.VisibleLength();
        if (before <= end && now > end)
            _wave.SetView(now, _wave.VisibleLength());
    }

    void BoxFollow(double start, double length)
    {
        _box.SetView(start, length);
        _structure.SetView(start, length);
    }

    /// <summary>
    /// The view's been hidden (another tab, say): playback pauses and the song's timelines are saved.
    /// </summary>
    public void Hidden()
    {
        Pause();
        Timelines.Flush();
        _panX = -1;
    }

    // ---------------------------------------------------------------- tracks --

    void RebuildTracks()
    {
        _tracksStale = false;
        _fixtureList.Children.Clear();
        _tracks.Clear();
        _plates.Clear();
        foreach (var tr in Timelines.FixtureTracks)
        {
            var fill = PlateColour?.Invoke(TimelineTrack.FixtureOf(tr.Key)) ?? Theme.PlateFill;
            var view = new TrackView(_fonts.Small, _fonts.Small, this, tr) { PlateFill = fill };
            var plate = new NamePlate(_fonts.Body, _fonts.Small, Timelines, tr) { Fill = fill };
            var row = new Row(GAP);
            row.Add(view);
            row.Add(plate);
            _fixtureList.Add(row);
            _tracks.Add(view);
            _plates.Add(plate);
        }
        if (_tracks.Count == 0)
            _fixtureList.Add(_fixtureEmpty);
    }

    /// <summary>
    /// The track view showing <paramref name="track"/>.
    /// </summary>
    public TrackView? ViewOf(TimelineTrack track) => track == Timelines.Master ? _master : _tracks.FirstOrDefault(v => v.Track == track);

    /// <summary>
    /// The row (of <see cref="SongTimelines.RowList"/>) at screen y: the one there, or the nearest. Only what's on
    /// screen counts: a fixture's row scrolled out of its panel can lie right over the master below.
    /// </summary>
    public int RowIndexAt(float y)
    {
        var rows = Timelines.RowList();
        var best = 0;
        var bestD = float.PositiveInfinity;
        var clipTop = _fixtureScroll.ScreenPosition.Y;
        var clipBottom = clipTop + _fixtureScroll.Size.Y;
        for (var i = 0; i < rows.Count; i++)
        {
            if (ViewOf(rows[i].Track) is not { } view)
                continue;
            var (ry, rh) = view.RowRect(rows[i].Row);
            var top = view.ScreenPosition.Y + ry;
            var bottom = top + rh;
            if (view != _master)
            {
                top = Math.Max(top, clipTop);
                bottom = Math.Min(bottom, clipBottom);
            }
            if (bottom <= top)
                continue;
            var d = y >= top && y < bottom ? 0 : Math.Min(Math.Abs(y - top), Math.Abs(y - bottom));
            if (d < bestD)
            {
                best = i;
                bestD = d;
            }
        }
        return best;
    }

    public double TimeAt(float x)
    {
        var w = Math.Max(_wave.Size.X, 1);
        var t = _wave.ViewStart + (x - _wave.ScreenPosition.X) / w * _wave.VisibleLength();
        return Math.Clamp(t, 0, _wave.Duration());
    }

    /// <summary>
    /// A screen point on <paramref name="row"/> of <paramref name="track"/> at time <paramref name="t"/>.
    /// </summary>
    public float2 PointAt(TimelineTrack track, TimelineRow row, double t)
    {
        var view = ViewOf(track)!;
        var (ry, rh) = view.RowRect(row);
        var x = _wave.ScreenPosition.X + (float)((t - _wave.ViewStart) / _wave.VisibleLength() * _wave.Size.X);
        return new float2(x, view.ScreenPosition.Y + ry + rh / 2);
    }

    public void PlayheadDrag(int phase, double t)
    {
        switch (phase)
        {
            case 0:
                BeginScrub(t);
                break;
            case 1:
                ScrubTo(t);
                break;
            default:
                EndScrub(t);
                break;
        }
    }

    public float4? PositionColour(string id) => Positions?.Invoke().FirstOrDefault(p => p.Id == id)?.Colour;

    // --------------------------------------------------------- context menu --

    /// <summary>
    /// A right-click on a track's row: the actions that fit where it was.
    /// </summary>
    public void Context(TrackView track, TimelineRow row, float2 at, double t)
    {
        if (_wave.Analysis is null)
            return;
        var onSelection = Timelines.Selection.Has(track.Track.Key, row, t);
        _contextAt = at;
        _menu.Show(at, ContextItems(onSelection, track.Track, t));
    }

    /// <summary>
    /// The menu for a right-click on (<paramref name="onSelection"/>) or off the selection: Delete, Undo, Redo, Cut,
    /// Copy and Paste on it, with a Dimmer, Colour, Strobe, Position and Movement submenu for the kinds of row selected;
    /// off it, Undo, Redo and Paste Here (into <paramref name="track"/> at <paramref name="t"/>); then Select All and
    /// Deselect.
    /// </summary>
    public List<MenuItem> ContextItems(bool onSelection, TimelineTrack? track = null, double t = 0)
    {
        var tl = Timelines;
        var items = new List<MenuItem>();
        if (onSelection)
            items.Add(new MenuItem("Delete", () => tl.DeleteSelection()));
        if (tl.CanUndo)
            items.Add(new MenuItem("Undo", () => tl.Undo()));
        if (tl.CanRedo)
            items.Add(new MenuItem("Redo", () => tl.Redo()));
        if (onSelection)
        {
            items.Add(new MenuItem("Cut", () => tl.CutSelection()));
            items.Add(new MenuItem("Copy", () => tl.CopySelection()));
        }
        if (tl.Clipboard is not null)
        {
            if (onSelection)
                items.Add(new MenuItem("Paste", () => tl.Paste()));
            else if (track is not null)
                items.Add(new MenuItem("Paste Here", () => tl.Paste(track, t)));
        }
        if (onSelection)
        {
            var kinds = tl.Selection.Blocks.SelectMany(b => b.Rows).Select(id => (TimelineRow)int.Parse(id.Split('|')[1])).ToHashSet();
            if (kinds.Contains(TimelineRow.Dimmer))
                items.Add(new MenuItem("Dimmer", DimmerMenu()));
            if (kinds.Contains(TimelineRow.Colour))
                items.Add(new MenuItem("Colour", ColourMenu()));
            if (kinds.Contains(TimelineRow.Strobe))
                items.Add(new MenuItem("Strobe", StrobeMenu()));
            if (kinds.Contains(TimelineRow.Position))
                items.Add(new MenuItem("Position", PositionMenu()));
            if (kinds.Contains(TimelineRow.Movement))
                items.Add(new MenuItem("Movement", MovementMenu()));
        }
        items.Add(new MenuItem("Select All", () => tl.SelectAll()));
        if (!tl.Selection.IsEmpty)
            items.Add(new MenuItem("Deselect", () => tl.Selection.Clear()));
        return items;
    }

    public List<MenuItem> DimmerMenu() =>
        [
            .. DimmerItems.Select(d => new MenuItem(
                d.Name,
                () => Timelines.ApplyDimmer(d.Shape),
                Enabled: d.Shape is not (DimShape.BeatPulses or DimShape.BarPulses) || Timelines.HasBeatGrid
            )),
        ];

    public List<MenuItem> ColourMenu()
    {
        var items = new List<MenuItem>
        {
            new("Static Colour…", () => PickColour("static")),
            new("Gradient…", () => PickColour("from")),
            new("Rainbow", () => Timelines.ApplyRainbow()),
        };
        foreach (var (name, c) in ColourPresets)
            items.Add(new MenuItem(name, () => Timelines.ApplyStaticColour(c)));
        items.Add(new MenuItem("Clear Colour", () => Timelines.ApplyColourSegments((_, _) => [])));
        return items;
    }

    public List<MenuItem> StrobeMenu() =>
        [
            .. new[] { StrobeSpeed.Slow, StrobeSpeed.Medium, StrobeSpeed.Fast }.Select(sp => new MenuItem("Strobe " + TimelineTrack.StrobeNames[sp], () => Timelines.ApplyStrobe(sp))),
            new MenuItem("Strobe Off", () => Timelines.ApplyStrobe(null)),
        ];

    public List<MenuItem> PositionMenu()
    {
        var items = new List<MenuItem>();
        foreach (var p in Positions?.Invoke() ?? [])
            items.Add(new MenuItem(p.Name, () => Timelines.ApplyPosition(p.Id)));
        if (items.Count == 0)
            items.Add(new MenuItem("(no positions — add some under Positions)", () => { }, Enabled: false));
        items.Add(new MenuItem("Clear Position", () => Timelines.ApplyPosition("")));
        return items;
    }

    public List<MenuItem> MovementMenu()
    {
        var tl = Timelines;
        tl.SyncMovementOptions();
        var items = MovementSpan.Effects.Select(fx => new MenuItem(MovementSpan.EffectNames[fx], () => tl.ApplyMovement(fx))).ToList();
        for (var i = 0; i < SongTimelines.MovementSizes.Length; i++)
        {
            var k = i;
            items.Add(new MenuItem(SongTimelines.MovementSizes[i].Name, () => tl.SetMovementSize(k), Checked: tl.MovementSize == i));
        }
        for (var i = 0; i < SongTimelines.MovementCycles.Length; i++)
        {
            var k = i;
            items.Add(new MenuItem(SongTimelines.MovementCycles[i].Name, () => tl.SetMovementCycle(k), Checked: tl.MovementCycle == i));
        }
        items.Add(new MenuItem("Clear Movement", () => tl.ApplyMovement("")));
        return items;
    }

    /// <summary>
    /// Opens the colour picker for Static Colour… ("static") or a gradient's start ("from") and then its end ("to"),
    /// the selected colour spans following it live, as one undo step.
    /// </summary>
    void PickColour(string what)
    {
        if (_picker is null)
            return;
        _picking = what;
        Timelines.BeginEditGroup();
        var start = what == "to" ? _to : _from;
        _picker.Show(_contextAt, start, c =>
        {
            if (_picking == "static")
                Timelines.ApplyStaticColour(c);
            else if (_picking == "from")
            {
                _from = c;
                Timelines.ApplyGradient(_from, _to);
            }
            else
            {
                _to = c;
                Timelines.ApplyGradient(_from, _to);
            }
        });
    }

    void PickerClosed()
    {
        var was = _picking;
        _picking = "";
        Timelines.EndEditGroup();
        if (was == "from")
            PickColour("to");
    }

    // ------------------------------------------------------------------- keys --

    /// <summary>
    /// A key the rest of the app didn't take, while the timeline's showing. Returns whether it used it.
    /// </summary>
    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        var ctrl = (modifiers & Modifiers.Control) != 0;
        var shift = (modifiers & Modifiers.Shift) != 0;
        var tl = Timelines;
        switch (key)
        {
            case Key.Space when !ctrl:
                TogglePlay();
                return true;
            case Key.Escape when !tl.Selection.IsEmpty:
            case Key.D when ctrl && !tl.Selection.IsEmpty:
                tl.Selection.Clear();
                return true;
            case Key.Delete when !tl.Selection.IsEmpty:
                tl.DeleteSelection();
                return true;
            case Key.Z when ctrl:
                if (shift)
                    tl.Redo();
                else
                    tl.Undo();
                return true;
            case Key.Y when ctrl:
                tl.Redo();
                return true;
            case Key.A when ctrl:
                tl.SelectAll();
                return true;
            case Key.C when ctrl:
                tl.CopySelection();
                return true;
            case Key.X when ctrl:
                tl.CutSelection();
                return true;
            case Key.V when ctrl:
                tl.Paste();
                return true;
        }
        return false;
    }

    // ------------------------------------------------------- middle-drag pan --

    /// <summary>
    /// Where a middle-button drag pans the view: the waveform's width, from the top of the fixtures' timelines down
    /// to the waveform's bottom. (min, max) on screen.
    /// </summary>
    public (float2 Min, float2 Max) PanArea() =>
        (new float2(_wave.ScreenPosition.X, _fixtureScroll.ScreenPosition.Y), _wave.ScreenPosition + _wave.Size);

    public bool IsPanning => _panX >= 0;

    bool IPointerHook.Button(MouseButton button, bool down, float2 at, Modifiers modifiers)
    {
        if (!Visible || _wave.Analysis is null || button != MouseButton.Middle)
            return false;
        var (min, max) = PanArea();
        if (down && at.X >= min.X && at.X < max.X && at.Y >= min.Y && at.Y < max.Y)
        {
            _panX = at.X;
            _panFrom = _wave.ViewStart;
            return true;
        }
        if (!down && IsPanning)
        {
            _panX = -1;
            return true;
        }
        return false;
    }

    bool IPointerHook.Move(float2 at)
    {
        if (!IsPanning)
            return false;
        var perPx = _wave.VisibleLength() / Math.Max(_wave.Size.X, 1);
        _wave.SetView(_panFrom - (at.X - _panX) * perPx, _wave.VisibleLength());
        return true;
    }

    Cursor? IPointerHook.Cursor => IsPanning ? Cursor.Grab : null;

    // ----------------------------------------------------------------- layout --

    protected override void MeasureContent()
    {
        var (w, h) = (Available.X, Available.Y);
        var waveW = Math.Max(w - BUTTON_WIDTH - GAP, 10);
        _status.Visible = true;
        _status.Measure();
        foreach (var v in _tracks)
            v.Width = waveW;
        foreach (var p in _plates)
            p.Want = new float2(Math.Max(BUTTON_WIDTH - _fixtureScroll.ScrollbarWidth - 2, 8), 0);
        _master.Width = waveW;
        _structure.Width = waveW;
        _ruler.Width = waveW;
        _wave.Want = new float2(waveW, WAVE_HEIGHT);
        _overview.Want = new float2(waveW, OVERVIEW_HEIGHT);
        _box.Want = _overview.Want;
        _master.Measure();
        _structure.Measure();
        _ruler.Measure();
        _wave.Measure();
        _overview.Measure();
        _box.Measure();
        _masterPlate.Want = new float2(BUTTON_WIDTH, _master.Size.Y);
        _masterPlate.Measure();
        foreach (var b in new[] { _reset, _play })
        {
            b.Want = new float2(BUTTON_WIDTH, BUTTON_WIDTH);
            b.Measure();
        }
        foreach (var b in new[] { _lock, _generate })
        {
            b.Want = new float2(BarRuler.HEIGHT, BarRuler.HEIGHT);
            b.Measure();
        }
        // The plates as tall as their tracks.
        for (var i = 0; i < _tracks.Count; i++)
        {
            var th = _tracks[i].Track.Collapsed ? TrackView.COLLAPSED_HEIGHT : _tracks[i].FullHeight;
            _plates[i].Want = _plates[i].Want with { Y = th };
            _plates[i].Visible = !_tracks[i].Track.Collapsed;
            if (_tracks[i].Track.Collapsed)
                _tracks[i].Width = waveW + GAP + _plates[i].Want.X;
        }
        var waveRow = _master.Size.Y + _structure.Size.Y + _ruler.Size.Y + _wave.Size.Y;
        var top = _status.Text == "" ? 0 : _status.Size.Y + GAP;
        var scrollH = Math.Max(h - top - waveRow - OVERVIEW_HEIGHT - 2 * GAP - GAP, 20);
        _fixtureScroll.MaxWidth = w;
        _fixtureScroll.MaxHeight = scrollH;
        _fixtureScroll.MinSize = new float2(w, scrollH);
        _fixtureScroll.Measure();
        _status.Visible = _status.Text != "";
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        var (w, h) = (Size.X, Size.Y);
        var waveW = _wave.Size.X;
        var y = position.Y;
        if (_status.Visible)
        {
            _status.Arrange(new float2(position.X, y));
            y += _status.Size.Y + GAP;
        }
        _fixtureScroll.Arrange(new float2(position.X, y));
        var overviewY = position.Y + h - OVERVIEW_HEIGHT;
        var waveBottom = overviewY - GAP;
        var waveY = waveBottom - _wave.Size.Y;
        var rulerY = waveY - _ruler.Size.Y;
        var structY = rulerY - _structure.Size.Y;
        var masterY = structY - _master.Size.Y;
        _master.Arrange(new float2(position.X, masterY));
        _structure.Arrange(new float2(position.X, structY));
        _ruler.Arrange(new float2(position.X, rulerY));
        _wave.Arrange(new float2(position.X, waveY));
        var col = position.X + waveW + GAP;
        _masterPlate.Arrange(new float2(col, masterY));
        _play.Arrange(new float2(col, waveBottom - BUTTON_WIDTH));
        _reset.Arrange(new float2(col, waveBottom - 2 * BUTTON_WIDTH - BUTTON_GAP));
        // The lock and Generate squares level with the structure bar, side by side.
        var sq = BarRuler.HEIGHT;
        var x0 = col + (BUTTON_WIDTH - 2 * sq - BUTTON_GAP) / 2;
        _lock.Arrange(new float2(x0, structY));
        _generate.Arrange(new float2(x0 + sq + BUTTON_GAP, structY));
        _overview.Arrange(new float2(position.X, overviewY));
        _box.Arrange(new float2(position.X, overviewY));
        var clipMin = _fixtureScroll.ScreenPosition;
        var clip = (clipMin, clipMin + _fixtureScroll.Size);
        foreach (var t in _tracks)
            t.Clip = clip;
        _ = w;
    }

    public override void Draw(Batch batch)
    {
        foreach (var child in Children)
            if (child.Visible)
                child.Draw(batch);
    }
}

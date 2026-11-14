using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Dj;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.Timelines;

/// <summary>
/// A dimmer shape the timeline's Dimmer menu puts across the selected spans (<see cref="SongTimelines.DimmerShape"/>).
/// </summary>
enum DimShape
{
    Black,
    Half,
    Full,
    FadeIn,
    FadeOut,
    PulseIn,
    PulseOut,
    Smooth,
    BeatPulses,
    BarPulses,
}

/// <summary>
/// What selections and dragged edges snap to: whole bars, beats, or nothing.
/// </summary>
enum SnapMode
{
    Bar,
    Beat,
    None,
}

/// <summary>
/// The timelines for one fixture or head, before they're made (<see cref="SongTimelines.FixtureTrackSpecs"/>).
/// </summary>
sealed record TrackSpec(string Key, string Title, TimelineTarget Target, List<TimelineRow> Rows);

/// <summary>
/// What <see cref="TimelineGenerator.Generate"/> takes for a song's show, gathered so it can be run on another thread:
/// nothing in it is shared. <see cref="Keys"/>: every track's key, generated or not.
/// </summary>
sealed record GenerateJob(
    List<SongSection> Sections,
    float[] Beats,
    int PerBar,
    double Duration,
    List<GeneratorTrack> Tracks,
    List<GeneratorPosition> Positions,
    string Seed,
    int Variation,
    int BarPhase,
    SongEnvelope? Env,
    List<string> Keys
);

/// <summary>
/// A generated show: every track's key, each generated track's rows, and those rows as saved.
/// </summary>
sealed record GeneratedShow(List<string> Keys, Dictionary<string, GeneratedRows> Show, Dictionary<string, JsonObject> Rows);

/// <summary>
/// DJ Edit Mode's timelines for the open song, without the screen: the master's and each patched fixture's (and its
/// heads'), what's selected on them, and every edit the timeline makes (Delete, Cut, Copy, Paste, dragging a
/// selection or its edges, the Dimmer, Colour, Strobe, Position and Movement menus, Generate), with undo and redo per
/// song. A song's timelines are kept in the show's <see cref="TimelineStore"/> and saved when the song's left
/// (<see cref="Flush"/>). Its structure is analysed in the background when it opens, and kept for the session.
/// <para>
/// The screen tells it the song's length once its waveform is read (<see cref="SetDuration"/>) and the playhead
/// (<see cref="SongPos"/>), drives selections by row and time, and calls <see cref="Update"/> each frame to take in
/// finished analyses and saves. sLight's <c>MusicTimeline</c>, less its drawing and input.
/// </para>
/// </summary>
sealed class SongTimelines : ISongTimelines
{
    public const int BEATS_PER_BAR = 4;
    public const int UNDO_LIMIT = 100;

    /// <summary>
    /// The Movement menu's sizes and cycles (in bars).
    /// </summary>
    public static readonly (string Name, double Value)[] MovementSizes = [("Small", 0.02), ("Medium", 0.04), ("Large", 0.08)];
    public static readonly (string Name, double Value)[] MovementCycles = [("1 bar", 1.0), ("2 bars", 2.0), ("4 bars", 4.0)];

    /// <summary>
    /// The size and cycle new movements get (indexes into <see cref="MovementSizes"/> and
    /// <see cref="MovementCycles"/>).
    /// </summary>
    public int MovementSize { get; private set; } = 1;
    public int MovementCycle { get; private set; } = 1;

    /// <summary>
    /// A song's timelines were saved to the store.
    /// </summary>
    public event Action<string>? TimelinesSaved;

    /// <summary>
    /// The show's timelines folder changed.
    /// </summary>
    public event Action? TimelineStoreChanged;

    /// <summary>
    /// The fixtures' tracks were made afresh (the patch or the song changed).
    /// </summary>
    public event Action? TracksRebuilt;

    /// <summary>
    /// The structure shown changed (or was cleared).
    /// </summary>
    public event Action? StructureChanged;

    /// <summary>
    /// The patched fixtures, in order.
    /// </summary>
    public Func<IReadOnlyList<PatchedFixture>>? Fixtures { get; set; }

    /// <summary>
    /// The named positions, as the generator takes them.
    /// </summary>
    public Func<IReadOnlyList<GeneratorPosition>>? PositionIds { get; set; }

    /// <summary>
    /// How a song's structure is analysed (on a worker thread): <see cref="SongAnalyzer.Analyse"/> unless a test says
    /// otherwise.
    /// </summary>
    public Func<string, CancellationToken, SongAnalysis> AnalyseStructure { get; set; } = (path, cancel) => SongAnalyzer.Analyse(path, null, cancel);

    public TimelineStore? Store { get; private set; }
    public TimelineSelection Selection { get; } = new();
    public TimelineTrack Master { get; } = new();
    public List<TimelineTrack> FixtureTracks { get; } = [];

    /// <summary>
    /// The open song ("" for none) and its VirtualDJ entry.
    /// </summary>
    public string Path { get; private set; } = "";
    public DjSong? Song { get; private set; }

    /// <summary>
    /// The song's length in seconds once its waveform's read (0 until then).
    /// </summary>
    public double Duration { get; private set; }

    public bool HasWave => Duration > 0;

    /// <summary>
    /// VirtualDJ's beat grid for the song (empty without one), and which beat starts a bar.
    /// </summary>
    public float[] GridBeats { get; private set; } = [];
    public int BarPhase { get; private set; }
    public int BeatsPerBar { get; private set; } = BEATS_PER_BAR;

    /// <summary>
    /// The playhead, where Paste goes with nothing selected.
    /// </summary>
    public double SongPos { get; set; }

    public SnapMode Snap { get; set; } = SnapMode.Bar;

    /// <summary>
    /// The structure shown (the open song's, once analysed), and what the structure bar says while there's none.
    /// </summary>
    public SongAnalysis? Structure { get; private set; }
    public string StructurePlaceholder { get; private set; } = "";

    /// <summary>
    /// Locked, the structure bar only seeks; a song's structure starts locked.
    /// </summary>
    public bool StructureLocked { get; set; } = true;

    public TimelineClip? Clipboard { get; private set; }

    /// <summary>
    /// Whether the open song's timelines have changed since they were saved.
    /// </summary>
    public bool HasUnsavedTimelines { get; private set; }

    sealed class History
    {
        public List<JsonObject> Undo { get; } = [];
        public List<JsonObject> Redo { get; } = [];
        public JsonObject? State { get; set; }
    }

    readonly Dictionary<string, JsonObject> _songs = [];
    readonly Dictionary<string, History> _history = [];
    readonly Dictionary<string, SongAnalysis> _structCache = [];
    readonly Dictionary<string, int> _generated = [];
    readonly HashSet<string> _regenerate = [];
    readonly Dictionary<string, CancellationTokenSource> _analysing = [];
    readonly ConcurrentQueue<Action> _posted = new();
    readonly List<Task> _saves = [];
    bool _batching;

    public SongTimelines()
    {
        Master.Target = TimelineTarget.Master;
        Master.Title = "Master";
        Master.Key = "master";
    }

    /// <summary>
    /// The master then the fixtures' tracks.
    /// </summary>
    public IEnumerable<TimelineTrack> AllTracks => [Master, .. FixtureTracks];

    /// <summary>
    /// The tracks top to bottom on screen: the fixtures', then the master's.
    /// </summary>
    public IEnumerable<TimelineTrack> TopDown => [.. FixtureTracks, Master];

    public TimelineTrack? TrackFor(string key) => AllTracks.FirstOrDefault(t => t.Key == key);

    // ------------------------------------------------------------- opening --

    /// <summary>
    /// Opens song <paramref name="path"/>: the last one's timelines saved, its own shown, and its structure analysed
    /// (or taken from this session's).
    /// </summary>
    public void Open(string path, DjSong? song = null)
    {
        if (path != Path)
        {
            Flush();
            Selection.Clear();
            StructureLocked = true;
        }
        Path = path;
        Song = song;
        Duration = 0;
        GridBeats = [];
        BarPhase = 0;
        SetEnds();
        ShowSongData();
        LoadStructure(path);
    }

    /// <summary>
    /// The song's length, once its waveform's read: VirtualDJ's beat grid laid over it, and a master with no dimmer
    /// line given one. 0 for none.
    /// </summary>
    public void SetDuration(double duration)
    {
        Duration = Math.Max(duration, 0);
        GridBeats = [];
        BarPhase = 0;
        if (Song is not null && Duration > 0)
        {
            var beats = Song.BeatTimes(Duration);
            SetBeatGrid([.. beats], BEATS_PER_BAR, Song.FirstBeatIndex(beats));
        }
        SetEnds();
        DefaultMasterLine();
    }

    /// <summary>
    /// The beat grid laid over the song (VirtualDJ's): its beats, beats a bar, and which beat starts one.
    /// </summary>
    public void SetBeatGrid(float[] beats, int perBar = BEATS_PER_BAR, int barPhase = 0)
    {
        GridBeats = beats;
        BeatsPerBar = Math.Max(perBar, 1);
        BarPhase = barPhase;
    }

    void SetEnds()
    {
        foreach (var tr in AllTracks)
            tr.End = HasWave ? Duration : double.PositiveInfinity;
    }

    /// <summary>
    /// Takes in what's finished in the background (structure analyses, saves): each frame, on the UI thread.
    /// </summary>
    public void Update()
    {
        while (_posted.TryDequeue(out var action))
            action();
        _saves.RemoveAll(t => t.IsCompleted);
    }

    // ---------------------------------------------------------- the tracks --

    /// <summary>
    /// The timelines for one fixture: its own (rows for every channel it has), then, when it has more than one head,
    /// one per head with rows for that head's channels, titled with the fixture's name and "Head N" on a second line.
    /// </summary>
    public static List<TrackSpec> FixtureTrackSpecs(string key, string fixtureName, FixtureProfile profile, int mode)
    {
        var chans = profile.Channels(mode);
        var output = new List<TrackSpec> { new(key, fixtureName, TimelineTarget.Fixture, TimelineTrack.RowsForRoles([.. chans.Select(c => c.Role)])) };
        var heads = profile.HeadGroups(mode);
        if (heads.Count > 1)
        {
            for (var hi = 0; hi < heads.Count; hi++)
            {
                var g = heads[hi];
                var roles = new List<string>();
                foreach (var k in new[] { g.R, g.G, g.B }.Concat(g.Extra))
                    if (k >= 0 && k < chans.Count)
                        roles.Add(chans[k].Role);
                output.Add(new($"{key}/h{hi}", $"{fixtureName}\nHead {hi + 1}", TimelineTarget.Head, TimelineTrack.RowsForRoles(roles)));
            }
        }
        return output;
    }

    /// <summary>
    /// Makes the fixtures' tracks afresh from <see cref="Fixtures"/> (call when the patch changes). A track that's
    /// still there keeps what's on it.
    /// </summary>
    public void RefreshFixtures()
    {
        Collect();
        RebuildFixtureTracks();
    }

    void RebuildFixtureTracks()
    {
        var saved = SongData().Obj("tracks");
        FixtureTracks.Clear();
        foreach (var fx in Fixtures?.Invoke() ?? [])
        {
            foreach (var spec in FixtureTrackSpecs(fx.Uid, fx.Name, fx.Profile, fx.Mode))
            {
                var tr = new TimelineTrack
                {
                    Key = spec.Key,
                    Title = spec.Title,
                    Target = spec.Target,
                    Rows = spec.Rows,
                    End = HasWave ? Duration : double.PositiveInfinity,
                };
                TimelineFormat.ReadTrack(tr, saved.Get(spec.Key));
                FixtureTracks.Add(tr);
            }
        }
        UpdateSilenced();
        TracksRebuilt?.Invoke();
    }

    /// <summary>
    /// The tracks by key, edits and all, for playing the open song live. Empty with no song open.
    /// </summary>
    public Dictionary<string, TimelineTrack> LiveTracks() => Path == "" ? [] : AllTracks.ToDictionary(t => t.Key);

    /// <summary>
    /// Collapses or expands a track (a fixture's heads go with it). Collapsed, its rows leave the selection. Saved
    /// with the timelines, but not an edit.
    /// </summary>
    public void SetCollapsed(TimelineTrack tr, bool collapsed)
    {
        if (tr == Master)
            return; // the master's always open
        tr.Collapsed = collapsed;
        var gone = new HashSet<string>();
        foreach (var other in FixtureTracks)
        {
            if (other == tr || tr.Target == TimelineTarget.Fixture && other.Key.StartsWith(tr.Key + "/h", StringComparison.Ordinal))
            {
                other.Collapsed = collapsed;
                if (collapsed)
                    gone.Add(other.Key);
            }
        }
        if (Path != "")
            HasUnsavedTimelines = true;
        if (gone.Count == 0)
            return;
        var blocks = new List<SelectionBlock>();
        var dropped = false;
        foreach (var b in Selection.Blocks)
        {
            var keep = b.Rows.Where(id => !gone.Contains(id.Split('|')[0])).ToList();
            dropped |= keep.Count < b.Rows.Count;
            if (keep.Count > 0)
                blocks.Add(SelectionBlock.Of(keep, b.T0, b.T1));
        }
        if (dropped)
            Selection.SetBlocks(blocks);
    }

    /// <summary>
    /// Mutes or solos a track: saved with the timelines, but not an edit.
    /// </summary>
    public void SetMute(TimelineTrack tr, bool mute)
    {
        tr.Mute = mute;
        MixChanged();
    }

    public void SetSolo(TimelineTrack tr, bool solo)
    {
        tr.Solo = solo;
        MixChanged();
    }

    void MixChanged()
    {
        if (Path != "")
            HasUnsavedTimelines = true;
        UpdateSilenced();
    }

    void UpdateSilenced()
    {
        var flags = AllTracks.ToDictionary(t => t.Key, t => (t.Mute, t.Solo));
        var mix = TimelineTrack.MixStateOf(flags);
        foreach (var tr in AllTracks)
            tr.Silenced = !TimelineTrack.Plays(flags, mix, tr.Key);
    }

    // ---------------------------------------------------- saved timelines --

    /// <summary>
    /// The open song's timelines: {tracks: {key: {dimmer: [[t, level], ...], ...}}, structure?: {sections}}, from the
    /// store the first time, then kept. Empty with no song open.
    /// </summary>
    public JsonObject SongData() => Path != "" ? DataFor(Path) : [];

    JsonObject DataFor(string path)
    {
        if (!_songs.TryGetValue(path, out var d))
        {
            d = Store?.LoadSong(path) ?? [];
            if (d["tracks"] is not JsonObject)
                d["tracks"] = new JsonObject();
            _songs[path] = d;
        }
        return d;
    }

    /// <summary>
    /// Saves the open song's timelines to the store if they've changed: as the user's own (no longer temporary),
    /// unless <paramref name="temporary"/>. Returns "" or what went wrong.
    /// </summary>
    public string Flush(bool temporary = false)
    {
        if (!HasUnsavedTimelines || Path == "" || Store is null)
            return "";
        Collect();
        SetTemporary(SongData(), temporary);
        var err = Store.SaveSong(Path, SongData());
        if (err == "")
        {
            HasUnsavedTimelines = false;
            TimelinesSaved?.Invoke(Path);
        }
        else
            Trace.TraceWarning($"Couldn't save the timelines for {Gd.GetFile(Path)}: {err}");
        return err;
    }

    /// <summary>
    /// Deletes song <paramref name="path"/>'s saved timelines and forgets them (and their undo history). If it's the
    /// open song, its tracks go back to empty. Returns whether there were any.
    /// </summary>
    public bool RemoveSongTimelines(string path)
    {
        var had = Store is not null && Store.RemoveSong(path);
        _songs.Remove(path);
        _history.Remove(path);
        if (path == Path)
        {
            HasUnsavedTimelines = false;
            ShowSongData();
        }
        return had;
    }

    /// <summary>
    /// Switches to another show's timelines folder. <paramref name="reload"/>: forgets what's in memory and shows the
    /// open song's timelines from the new folder (loading another show: <see cref="Flush"/> the old one first);
    /// otherwise keeps it, edits and all (Save Show As… onto a copy of the folder).
    /// </summary>
    public void SetTimelineStore(TimelineStore? store, bool reload = true)
    {
        Store = store;
        TimelineStoreChanged?.Invoke();
        if (!reload)
            return;
        _songs.Clear();
        _history.Clear();
        HasUnsavedTimelines = false;
        ShowSongData();
    }

    /// <summary>
    /// The tracks as they stand into the open song's data.
    /// </summary>
    public void Collect()
    {
        if (Path == "")
            return;
        var tracks = (JsonObject)SongData()["tracks"]!;
        foreach (var tr in AllTracks)
        {
            var entry = TimelineFormat.WriteTrack(tr, tracks[tr.Key] as JsonObject);
            if (entry is null)
                tracks.Remove(tr.Key);
            else
                tracks[tr.Key] = entry;
        }
    }

    void ShowSongData()
    {
        var tracks = SongData().Obj("tracks");
        TimelineFormat.ReadTrack(Master, tracks.Get("master"));
        Master.Solo = false;
        RebuildFixtureTracks();
        if (Path != "" && _structCache.TryGetValue(Path, out var a))
        {
            ApplySavedStructure(Path, a);
            SetStructure(a);
        }
        DefaultMasterLine();
        if (Path != "" && Hist().State is null)
            Hist().State = Snapshot();
    }

    /// <summary>
    /// A song whose master has no dimmer line yet gets one at 0 % from its start to its end, so nothing comes on
    /// until it's raised or a fixture has values of its own. Not an edit: it's where the song starts.
    /// </summary>
    void DefaultMasterLine()
    {
        if (Path == "" || !HasWave || Master.DimmerNodes.Count > 0)
            return;
        Master.DimmerNodes = [new Vector2(0, 0), new Vector2((float)Duration, 0)];
        Collect();
        var h = Hist();
        if (h.State is not null)
            h.State = Snapshot();
    }

    /// <summary>
    /// An edit's been made to the tracks (the screen calls this after a drag on the dimmer line): the song's timelines
    /// are unsaved, and the state before it goes on the undo list.
    /// </summary>
    public void MarkEdited()
    {
        HasUnsavedTimelines = true;
        if (_batching || _groupDepth > 0)
        {
            _groupEdited |= _groupDepth > 0;
            return;
        }
        RecordEdit();
    }

    int _groupDepth;
    bool _groupEdited;

    /// <summary>
    /// Starts a run of edits that undo as one (a colour picked live, changing as it's dragged): until
    /// <see cref="EndEditGroup"/>, edits aren't recorded one by one.
    /// </summary>
    public void BeginEditGroup() => _groupDepth++;

    public void EndEditGroup()
    {
        if (_groupDepth == 0 || --_groupDepth > 0)
            return;
        if (_groupEdited)
            RecordEdit();
        _groupEdited = false;
    }

    /// <summary>
    /// The structure bar's edited the open song's sections: saved with its timelines, and an undo step.
    /// </summary>
    public void StructureEdited()
    {
        if (!_structCache.TryGetValue(Path, out var a))
            return;
        SongData()["structure"] = new JsonObject { ["sections"] = SongAnalysis.SectionsTo(a.Sections) };
        MarkEdited();
    }

    static void SetTemporary(JsonObject d, bool temporary)
    {
        if (temporary)
            d["temporary"] = true;
        else
            d.Remove("temporary");
    }

    /// <summary>
    /// Whether a song's saved tracks have anything on their rows.
    /// </summary>
    public static bool HasRows(JsonObject? tracks)
    {
        foreach (var (_, entry) in tracks ?? [])
            foreach (var row in new[] { "dimmer", "colour", "strobe", "position", "movement" })
                if (entry.Arr(row).Count > 0)
                    return true;
        return false;
    }

    // ----------------------------------------------------------- undo/redo --

    /// <summary>
    /// The open song's timelines as they stand (less mute, solo and collapsed, which aren't edits).
    /// </summary>
    JsonObject Snapshot()
    {
        Collect();
        return SnapshotOf(SongData());
    }

    static JsonObject SnapshotOf(JsonObject d)
    {
        var tracks = (JsonObject)(d["tracks"]?.DeepClone() ?? new JsonObject());
        foreach (var key in tracks.Select(kv => kv.Key).ToList())
        {
            if (tracks[key] is not JsonObject entry)
                continue;
            entry.Remove("mute");
            entry.Remove("solo");
            entry.Remove("collapsed");
            if (entry.Count == 0)
                tracks.Remove(key);
        }
        var snap = new JsonObject { ["tracks"] = tracks };
        if (d["structure"] is JsonObject s)
            snap["structure"] = s.DeepClone();
        return snap;
    }

    History Hist()
    {
        if (!_history.TryGetValue(Path, out var h))
            _history[Path] = h = new History();
        return h;
    }

    void RecordEdit()
    {
        if (Path == "")
            return;
        Hist();
        Record(Path, Snapshot());
    }

    void Record(string path, JsonObject now)
    {
        var h = _history[path];
        if (h.State is not null && !JsonNode.DeepEquals(now, h.State))
        {
            h.Undo.Add(h.State);
            if (h.Undo.Count > UNDO_LIMIT)
                h.Undo.RemoveAt(0);
            h.Redo.Clear();
        }
        h.State = now;
    }

    public bool CanUndo => Path != "" && Hist().Undo.Count > 0;
    public bool CanRedo => Path != "" && Hist().Redo.Count > 0;

    /// <summary>
    /// Undoes the open song's last edit. Returns whether there was one.
    /// </summary>
    public bool Undo()
    {
        if (!CanUndo)
            return false;
        var h = Hist();
        h.Redo.Add(h.State!);
        h.State = h.Undo[^1];
        h.Undo.RemoveAt(h.Undo.Count - 1);
        Restore(h.State);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo)
            return false;
        var h = Hist();
        h.Undo.Add(h.State!);
        h.State = h.Redo[^1];
        h.Redo.RemoveAt(h.Redo.Count - 1);
        Restore(h.State);
        return true;
    }

    void Restore(JsonObject snap)
    {
        var d = SongData();
        var flags = AllTracks.ToDictionary(t => t.Key, t => (t.Mute, t.Solo, t.Collapsed));
        d["tracks"] = snap["tracks"]!.DeepClone();
        if (snap["structure"] is JsonObject s)
            d["structure"] = s.DeepClone();
        else
            d.Remove("structure");
        ShowTracks((JsonObject)d["tracks"]!);
        foreach (var tr in AllTracks)
            (tr.Mute, tr.Solo, tr.Collapsed) = flags[tr.Key];
        Collect();
        if (_structCache.TryGetValue(Path, out var a))
        {
            ApplySavedStructure(Path, a);
            SetStructure(a);
        }
        HasUnsavedTimelines = true;
    }

    /// <summary>
    /// Saved tracks' rows onto the tracks as they are (their mute, solo and collapsed left alone).
    /// </summary>
    void ShowTracks(JsonObject tracks)
    {
        foreach (var tr in AllTracks)
        {
            var (mute, solo, collapsed) = (tr.Mute, tr.Solo, tr.Collapsed);
            TimelineFormat.ReadTrack(tr, tracks[tr.Key]);
            (tr.Mute, tr.Solo, tr.Collapsed) = (mute, solo, collapsed);
        }
    }

    // ------------------------------------------------------------ selection --

    /// <summary>
    /// Every row, top to bottom on screen: the fixtures' tracks (each's rows top down: dimmer first) then the master's.
    /// Collapsed tracks have none.
    /// </summary>
    public List<(TimelineTrack Track, TimelineRow Row)> RowList()
    {
        var output = new List<(TimelineTrack, TimelineRow)>();
        foreach (var tr in TopDown)
            if (!tr.Collapsed)
                foreach (var r in tr.Rows.Reverse())
                    output.Add((tr, r));
        return output;
    }

    List<string> RowIds(List<(TimelineTrack Track, TimelineRow Row)> rows, int lo, int hi) =>
        [.. rows.Skip(lo).Take(hi - lo + 1).Select(r => TimelineSelection.RowId(r.Track.Key, r.Row))];

    /// <summary>
    /// The boundaries a selection snaps to: bar or beat starts (from the beat grid, else the structure's), or none.
    /// </summary>
    public IReadOnlyList<float> SnapGrid()
    {
        if (Snap == SnapMode.None)
            return [];
        if (GridBeats.Length > 0)
            return Snap == SnapMode.Beat ? GridBeats : [.. GridBeats.Where((_, i) => Gd.PosMod(i - BarPhase, BeatsPerBar) == 0)];
        if (_structCache.TryGetValue(Path, out var a))
            return Snap == SnapMode.Beat ? a.BeatTimes : a.Downbeats;
        return [];
    }

    /// <summary>
    /// The span from <paramref name="ta"/> to <paramref name="tb"/> widened to whole grid cells (bars or beats), as is
    /// with no grid.
    /// </summary>
    public (double T0, double T1) SnapSpan(double ta, double tb)
    {
        double lo = Math.Min(ta, tb), hi = Math.Max(ta, tb);
        var g = SnapGrid();
        if (g.Count == 0)
            return (lo, hi);
        var i = Gd.BSearch(g, lo, false) - 1;
        var j = Gd.BSearch(g, hi, false);
        return (i >= 0 ? g[i] : 0.0, j < g.Count ? g[j] : Duration);
    }

    /// <summary>
    /// The nearest snapping boundary to <paramref name="t"/>, or <paramref name="t"/> with no snapping.
    /// </summary>
    public double SnapNearest(double t) => Nearest(SnapGrid(), t);

    /// <summary>
    /// The nearest of <paramref name="grid"/>'s times to <paramref name="t"/> (<paramref name="t"/> for none).
    /// </summary>
    public static double Nearest(IReadOnlyList<float> grid, double t)
    {
        if (grid.Count == 0)
            return t;
        var i = Math.Clamp(Gd.BSearch(grid, t), 0, grid.Count - 1);
        if (i > 0 && Math.Abs(grid[i - 1] - t) < Math.Abs(grid[i] - t))
            i--;
        return grid[i];
    }

    /// <summary>
    /// Selects every row of every timeline over the whole song (Ctrl+A). Returns whether there was a song to select in.
    /// </summary>
    public bool SelectAll()
    {
        if (!HasWave)
            return false;
        var rows = RowList();
        if (rows.Count == 0)
            return false;
        Selection.SetBlocks([SelectionBlock.Of(RowIds(rows, 0, rows.Count - 1), 0, Duration)]);
        return true;
    }

    /// <summary>
    /// A drag under way on the timelines: selecting an area, dragging a selected area's edge, or moving the selection.
    /// </summary>
    abstract record Drag;

    sealed record SelectingDrag(int Row, double T, List<SelectionBlock> Keep, SelectionBlock? Extend) : Drag;

    sealed record EdgeDrag(int Block, bool Start, double Lo, double Hi, List<(TimelineTrack Track, List<Vector2> Nodes, List<int> Idx)> Moving, List<Stretch> Stretching)
        : Drag;

    sealed record MoveDrag(double From, List<SelectionBlock> Blocks, int Row, double T, TimelineClip Clip) : Drag;

    /// <summary>
    /// A row's spans with ends on a dragged edge: which ends (index, start or end), and the spans as they were.
    /// </summary>
    sealed record Stretch(TimelineTrack Track, TimelineRow Row, List<(double T0, double T1)> Spans, List<(int Index, bool Start)> Ends);

    Drag? _drag;

    public bool IsDragging => _drag is not null;

    /// <summary>
    /// Whether a press at <paramref name="t"/> on row <paramref name="rowIndex"/> (of <see cref="RowList"/>) would
    /// grab a selected area's edge: within <paramref name="grab"/> seconds of one, on a row the area covers.
    /// </summary>
    public bool OnEdge(int rowIndex, double t, double grab) => EdgeAt(rowIndex, t, grab) is not null;

    /// <summary>
    /// Whether <paramref name="t"/> on row <paramref name="rowIndex"/> is inside the selection (a press there moves it).
    /// </summary>
    public bool InSelection(int rowIndex, double t)
    {
        var rows = RowList();
        return rowIndex >= 0 && rowIndex < rows.Count && Selection.Has(rows[rowIndex].Track.Key, rows[rowIndex].Row, t);
    }

    /// <summary>
    /// A press on the timelines at <paramref name="t"/> on row <paramref name="rowIndex"/>: on a selected area's edge
    /// (within <paramref name="grab"/> seconds) it starts dragging that edge; inside the selection, moving it; else a
    /// new area (Ctrl: added to the selection; Shift: extending its last area).
    /// </summary>
    public void SelectPress(int rowIndex, double t, bool ctrl = false, bool shift = false, double grab = 0)
    {
        _drag = null;
        if (!HasWave)
            return;
        var rows = RowList();
        if (rows.Count == 0)
            return;
        rowIndex = Math.Clamp(rowIndex, 0, rows.Count - 1);
        t = Math.Clamp(t, 0, Duration);
        if (!ctrl && !shift)
        {
            if (EdgeAt(rowIndex, t, grab) is { } edge)
            {
                _drag = edge;
                return;
            }
            if (Selection.Has(rows[rowIndex].Track.Key, rows[rowIndex].Row, t))
            {
                _drag = new MoveDrag(t, [.. Selection.Blocks], rowIndex, t, CopyClip());
                return;
            }
        }
        List<SelectionBlock> keep = [];
        SelectionBlock? extend = null;
        if (shift && !Selection.IsEmpty)
        {
            keep = [.. Selection.Blocks.Take(Selection.Blocks.Count - 1)];
            extend = Selection.Blocks[^1];
        }
        else if (ctrl)
            keep = [.. Selection.Blocks];
        _drag = new SelectingDrag(rowIndex, t, keep, extend);
        SelectTo(rowIndex, t);
    }

    /// <summary>
    /// The pointer's moved, pressed, to <paramref name="t"/> on row <paramref name="rowIndex"/>.
    /// </summary>
    public void SelectMove(int rowIndex, double t) => DragTo(rowIndex, t, false);

    /// <summary>
    /// The press is let go at <paramref name="t"/> on row <paramref name="rowIndex"/>.
    /// </summary>
    public void SelectRelease(int rowIndex, double t) => DragTo(rowIndex, t, true);

    void DragTo(int rowIndex, double t, bool release)
    {
        var rows = RowList();
        if (_drag is null || rows.Count == 0)
            return;
        rowIndex = Math.Clamp(rowIndex, 0, rows.Count - 1);
        t = Math.Clamp(t, 0, Duration);
        switch (_drag)
        {
            case EdgeDrag e:
                DragEdge(e, t, release);
                break;
            case MoveDrag m:
                DragMove(m, t, release);
                break;
            case SelectingDrag:
                SelectTo(rowIndex, t);
                if (release)
                    _drag = null;
                break;
        }
    }

    void SelectTo(int rowIndex, double t)
    {
        if (_drag is not SelectingDrag d)
            return;
        var rows = RowList();
        var span = SnapSpan(d.T, t);
        int lo = Math.Min(d.Row, rowIndex), hi = Math.Max(d.Row, rowIndex);
        if (d.Extend is { } extend)
        {
            // Shift: from the last area through the new one, every row and all the time between them.
            span = (Math.Min(span.T0, extend.T0), Math.Max(span.T1, extend.T1));
            var ids = RowIds(rows, 0, rows.Count - 1);
            for (var k = 0; k < rows.Count; k++)
            {
                if (extend.Rows.Contains(ids[k]))
                {
                    lo = Math.Min(lo, k);
                    hi = Math.Max(hi, k);
                }
            }
        }
        var blocks = d.Keep.ToList();
        if (span.T1 > span.T0)
            blocks.Add(SelectionBlock.Of(RowIds(rows, lo, hi), span.T0, span.T1));
        Selection.SetBlocks(blocks);
    }

    EdgeDrag? EdgeAt(int rowIndex, double t, double grab)
    {
        var rows = RowList();
        if (rowIndex < 0 || rowIndex >= rows.Count || grab <= 0)
            return null;
        var id = TimelineSelection.RowId(rows[rowIndex].Track.Key, rows[rowIndex].Row);
        for (var bi = 0; bi < Selection.Blocks.Count; bi++)
        {
            var b = Selection.Blocks[bi];
            if (!b.Rows.Contains(id))
                continue;
            foreach (var start in new[] { true, false })
            {
                var et = start ? b.T0 : b.T1;
                if (Math.Abs(t - et) > grab)
                    continue;
                var lo = start ? 0.0 : b.T0;
                var hi = start ? b.T1 : Duration;
                var moving = new List<(TimelineTrack, List<Vector2>, List<int>)>();
                var stretching = new List<Stretch>();
                foreach (var rid in b.Rows)
                {
                    var parts = rid.Split('|');
                    var kind = (TimelineRow)int.Parse(parts[1]);
                    if (TrackFor(parts[0]) is not { } tr)
                        continue;
                    if (kind == TimelineRow.Dimmer)
                    {
                        var idx = new List<int>();
                        for (var k = 0; k < tr.DimmerNodes.Count; k++)
                        {
                            double x = tr.DimmerNodes[k].X;
                            if (Math.Abs(x - et) < 0.001)
                                idx.Add(k);
                            else if (x < et)
                                lo = Math.Max(lo, x);
                            else
                                hi = Math.Min(hi, x);
                        }
                        if (idx.Count > 0)
                            moving.Add((tr, [.. tr.DimmerNodes], idx));
                        continue;
                    }
                    // Span boundaries on the edge move with it; the spans' other ends (and their neighbours) limit it.
                    var spans = SpansOf(tr, kind);
                    var ends = new List<(int, bool)>();
                    for (var k = 0; k < spans.Count; k++)
                    {
                        var (s0, s1) = spans[k];
                        if (Math.Abs(s0 - et) < 0.001)
                        {
                            ends.Add((k, true));
                            hi = Math.Min(hi, s1 - 0.001);
                        }
                        else if (Math.Abs(s1 - et) < 0.001)
                        {
                            ends.Add((k, false));
                            lo = Math.Max(lo, s0 + 0.001);
                        }
                        else if (s1 < et)
                            lo = Math.Max(lo, s1);
                        else if (s0 > et)
                            hi = Math.Min(hi, s0);
                    }
                    if (ends.Count > 0)
                        stretching.Add(new Stretch(tr, kind, spans, ends));
                }
                return new EdgeDrag(bi, start, lo, hi, moving, stretching);
            }
        }
        return null;
    }

    static List<(double T0, double T1)> SpansOf(TimelineTrack tr, TimelineRow row) =>
        row switch
        {
            TimelineRow.Colour => [.. tr.ColourSegments.Select(s => (s.T0, s.T1))],
            TimelineRow.Strobe => [.. tr.StrobeSegments.Select(s => (s.T0, s.T1))],
            TimelineRow.Position => [.. tr.PositionSegments.Select(s => (s.T0, s.T1))],
            TimelineRow.Movement => [.. tr.MovementSegments.Select(s => (s.T0, s.T1))],
            _ => [],
        };

    /// <summary>
    /// A row's spans with new times (the same number, in order), their content kept.
    /// </summary>
    static void SetSpanTimes(TimelineTrack tr, TimelineRow row, List<(double T0, double T1)> times)
    {
        switch (row)
        {
            case TimelineRow.Colour:
                tr.ColourSegments = [.. tr.ColourSegments.Select((s, k) => s with { T0 = times[k].T0, T1 = times[k].T1 })];
                break;
            case TimelineRow.Strobe:
                tr.StrobeSegments = [.. tr.StrobeSegments.Select((s, k) => s with { T0 = times[k].T0, T1 = times[k].T1 })];
                break;
            case TimelineRow.Position:
                tr.PositionSegments = [.. tr.PositionSegments.Select((s, k) => s with { T0 = times[k].T0, T1 = times[k].T1 })];
                break;
            case TimelineRow.Movement:
                tr.MovementSegments = [.. tr.MovementSegments.Select((s, k) => s with { T0 = times[k].T0, T1 = times[k].T1 })];
                break;
        }
    }

    /// <summary>
    /// Dragging a selected area's edge: it follows, snapped and kept inside its limits; the dimmer nodes and span
    /// boundaries on it follow. One undo step when released, if any of those moved.
    /// </summary>
    void DragEdge(EdgeDrag d, double at, bool release)
    {
        var t = Math.Clamp(SnapNearest(at), d.Lo, d.Hi);
        var blocks = Selection.Blocks.ToList();
        var b = blocks[d.Block];
        if (d.Start)
        {
            t = Math.Min(t, b.T1 - 0.001);
            blocks[d.Block] = b with { T0 = t };
        }
        else
        {
            t = Math.Max(t, b.T0 + 0.001);
            blocks[d.Block] = b with { T1 = t };
        }
        Selection.SetBlocks(blocks);
        var moved = false;
        foreach (var (tr, nodes, idx) in d.Moving)
        {
            var now = nodes.ToList();
            foreach (var k in idx)
                now[k] = now[k] with { X = (float)t };
            tr.DimmerNodes = now;
            moved |= !now.SequenceEqual(nodes);
        }
        foreach (var s in d.Stretching)
        {
            var times = s.Spans.ToList();
            foreach (var (k, start) in s.Ends)
                times[k] = start ? (t, times[k].T1) : (times[k].T0, t);
            SetSpanTimes(s.Track, s.Row, times);
            moved |= !times.SequenceEqual(s.Spans);
        }
        if (release)
        {
            _drag = null;
            if (moved)
                MarkEdited();
        }
    }

    /// <summary>
    /// Dragging the whole selection: the areas follow (their start snapped, kept in the song); on release the content
    /// is cut from where it was and pasted where it's dropped, overwriting what's there, as one undo step. Let go
    /// without moving: a plain click there.
    /// </summary>
    void DragMove(MoveDrag d, double at, bool release)
    {
        var start = d.Blocks.Min(b => b.T0);
        var finish = d.Blocks.Max(b => b.T1);
        var newStart = SnapNearest(start + at - d.From);
        newStart = Math.Clamp(newStart, 0, Math.Max(Duration - (finish - start), 0));
        var delta = newStart - start;
        if (!release)
        {
            Selection.SetBlocks(Shifted(d.Blocks, delta));
            ShowGhosts(Gd.ApproxEqual(delta, 0) ? null : d.Clip, start, newStart);
            return;
        }
        _drag = null;
        ShowGhosts(null, 0, 0);
        if (Gd.ApproxEqual(delta, 0))
        {
            Selection.Clear();
            SelectPress(d.Row, d.T);
            SelectRelease(d.Row, d.T);
            return;
        }
        MoveSelection(d.Blocks, delta);
    }

    void ShowGhosts(TimelineClip? clip, double from, double at)
    {
        foreach (var tr in AllTracks)
        {
            var src = clip?.Tracks.FirstOrDefault(c => c.Key == tr.Key);
            tr.Ghost = src is null ? null : new TrackGhost(src.Rows, at, from);
        }
    }

    /// <summary>
    /// Moves the content of the areas <paramref name="blocks"/> by <paramref name="delta"/> seconds into the same rows
    /// (cut, then pasted over what's there), and selects the moved areas. One undo step. Returns whether anything
    /// changed.
    /// </summary>
    public bool MoveSelection(IReadOnlyList<SelectionBlock> blocks, double delta)
    {
        Selection.SetBlocks(blocks);
        var clip = CopyClip();
        var start = blocks.Min(b => b.T0);
        _batching = true;
        var cleared = DeleteSelection();
        var pairs = clip.Tracks.Select(src => (Track: TrackFor(src.Key), Src: src)).Where(p => p.Track is not null).Select(p => (p.Track!, p.Src)).ToList();
        var (_, changed) = PasteInto(pairs, start + delta);
        _batching = false;
        Selection.SetBlocks(Shifted(blocks, delta));
        if (cleared || changed)
        {
            MarkEdited();
            return true;
        }
        return false;
    }

    static List<SelectionBlock> Shifted(IEnumerable<SelectionBlock> blocks, double delta) => [.. blocks.Select(b => SelectionBlock.Of(b.Rows, b.T0 + delta, b.T1 + delta))];

    /// <summary>
    /// Spans sorted, with overlapping and touching ones joined.
    /// </summary>
    public static List<(double T0, double T1)> Merged(IEnumerable<(double T0, double T1)> spans)
    {
        var output = new List<(double T0, double T1)>();
        foreach (var sp in spans.OrderBy(s => s.T0))
        {
            if (output.Count > 0 && sp.T0 <= output[^1].T1)
                output[^1] = (output[^1].T0, Math.Max(output[^1].T1, sp.T1));
            else
                output.Add(sp);
        }
        return output;
    }

    List<(double T0, double T1)> Spans(TimelineTrack tr, TimelineRow row) => Merged(Selection.SpansFor(tr.Key, row));

    // ------------------------------------------------- delete, copy, paste --

    /// <summary>
    /// Clears what's in the selected areas (Delete): the dimmer blacked out across each span, the other rows' spans
    /// removed. The selection stays. Returns whether anything changed.
    /// </summary>
    public bool DeleteSelection()
    {
        var changed = false;
        foreach (var tr in AllTracks)
        {
            foreach (var row in tr.Rows)
            {
                foreach (var (t0, t1) in Spans(tr, row))
                {
                    changed |= row switch
                    {
                        TimelineRow.Dimmer => tr.ClearDimmer(t0, t1),
                        TimelineRow.Colour => tr.ReplaceColour(t0, t1, []),
                        TimelineRow.Position => tr.ReplacePosition(t0, t1, []),
                        TimelineRow.Movement => tr.ReplaceMovement(t0, t1, []),
                        TimelineRow.Strobe => tr.ReplaceStrobe(t0, t1, []),
                        _ => false,
                    };
                }
            }
        }
        if (changed)
            MarkEdited();
        return changed;
    }

    /// <summary>
    /// Copies what's in the selected areas (Ctrl+C). Returns whether there was anything to copy.
    /// </summary>
    public bool CopySelection()
    {
        if (Selection.IsEmpty)
            return false;
        Clipboard = CopyClip();
        return true;
    }

    /// <summary>
    /// The selected areas' content, times relative to the earliest area's start.
    /// </summary>
    TimelineClip CopyClip()
    {
        var start = Selection.Blocks.Count > 0 ? Selection.Blocks.Min(b => b.T0) : 0;
        var finish = Selection.Blocks.Count > 0 ? Selection.Blocks.Max(b => b.T1) : 0;
        var tracks = new List<ClipTrack>();
        foreach (var tr in TopDown)
        {
            var rows = new Dictionary<TimelineRow, List<ClipPart>>();
            foreach (var row in tr.Rows)
            {
                var parts = new List<ClipPart>();
                foreach (var (t0, t1) in Spans(tr, row))
                {
                    var part = new ClipPart(t0 - start, t1 - start);
                    switch (row)
                    {
                        case TimelineRow.Dimmer:
                            // Only where the line has values (a fixture or head may have none there: nothing to copy).
                            if (tr.DimmerDefinedSpan(t0, t1) is { } d)
                                part = new ClipPart(d.T0 - start, d.T1 - start, Nodes: tr.DimmerSegment(d.T0, d.T1));
                            break;
                        case TimelineRow.Colour:
                            part = part with { Colours = tr.ColourSegment(t0, t1) };
                            break;
                        case TimelineRow.Strobe:
                            part = part with { Strobes = tr.StrobeSegment(t0, t1) };
                            break;
                        case TimelineRow.Position:
                            part = part with { Positions = tr.PositionSegment(t0, t1) };
                            break;
                        case TimelineRow.Movement:
                            part = part with { Movements = tr.MovementSegment(t0, t1) };
                            break;
                    }
                    parts.Add(part);
                }
                if (parts.Count > 0)
                    rows[row] = parts;
            }
            if (rows.Count > 0)
                tracks.Add(new ClipTrack(tr.Key, rows));
        }
        return new TimelineClip(finish - start, tracks);
    }

    /// <summary>
    /// Cuts the selected areas (Ctrl+X): copies them, then clears them as Delete does. Returns whether there was
    /// anything to cut.
    /// </summary>
    public bool CutSelection()
    {
        if (!CopySelection())
            return false;
        DeleteSelection();
        return true;
    }

    /// <summary>
    /// Pastes what was copied (Ctrl+V): at the start of the selection, into the tracks it touches (one copied track
    /// into each; several pair off in order, repeating); with nothing selected, at the playhead, back into the tracks
    /// it came from; with <paramref name="hereTrack"/> (Paste Here), at <paramref name="hereTime"/> into that track.
    /// Each row goes into the same kind of row. The pasted areas become the selection. Returns whether anything was
    /// pasted.
    /// </summary>
    public bool Paste(TimelineTrack? hereTrack = null, double hereTime = 0)
    {
        if (Clipboard is not { Tracks.Count: > 0 } clip || !HasWave)
            return false;
        var sources = clip.Tracks;
        var pairs = new List<(TimelineTrack, ClipTrack)>();
        double at;
        if (hereTrack is not null)
        {
            at = hereTime;
            pairs.Add((hereTrack, sources[0]));
        }
        else if (!Selection.IsEmpty)
        {
            at = Selection.Blocks.Min(b => b.T0);
            var keys = Selection.Blocks.SelectMany(b => b.Rows).Select(id => id.Split('|')[0]).ToHashSet();
            var targets = TopDown.Where(tr => keys.Contains(tr.Key)).ToList();
            for (var i = 0; i < targets.Count; i++)
                pairs.Add((targets[i], sources[i % sources.Count]));
        }
        else
        {
            at = SongPos;
            foreach (var src in sources)
                if (TrackFor(src.Key) is { } tr)
                    pairs.Add((tr, src));
        }
        var (blocks, changed) = PasteInto(pairs, at);
        if (blocks.Count == 0)
            return false;
        Selection.SetBlocks(blocks);
        if (changed)
            MarkEdited();
        return true;
    }

    (List<SelectionBlock> Blocks, bool Changed) PasteInto(List<(TimelineTrack Track, ClipTrack Src)> pairs, double at)
    {
        var blocks = new List<SelectionBlock>();
        var changed = false;
        foreach (var (tr, src) in pairs)
        {
            foreach (var (row, parts) in src.Rows.OrderBy(kv => Array.IndexOf(TimelineTrack.RowOrder, kv.Key)))
            {
                if (!tr.HasRow(row))
                    continue;
                foreach (var part in parts)
                {
                    var t0 = at + part.T0;
                    var t1 = Math.Min(at + part.T1, Duration);
                    if (t1 <= t0)
                        continue;
                    double a = at + part.T0, b = at + part.T1;
                    if (row == TimelineRow.Dimmer && part.Nodes is { } nodes)
                        changed |= tr.ReplaceDimmer(a, b, nodes);
                    else if (row == TimelineRow.Colour && part.Colours is { } colours)
                        changed |= tr.ReplaceColour(a, b, colours);
                    else if (row == TimelineRow.Strobe && part.Strobes is { } strobes)
                        changed |= tr.ReplaceStrobe(a, b, strobes);
                    else if (row == TimelineRow.Position && part.Positions is { } positions)
                        changed |= tr.ReplacePosition(a, b, positions);
                    else if (row == TimelineRow.Movement && part.Movements is { } movements)
                        changed |= tr.ReplaceMovement(a, b, movements);
                    blocks.Add(SelectionBlock.Of([TimelineSelection.RowId(tr.Key, row)], t0, t1));
                }
            }
        }
        return (blocks, changed);
    }

    // ---------------------------------------------- the rows' menus' edits --

    /// <summary>
    /// Gives every selected dimmer span a shape (<see cref="DimmerShape"/>). Returns whether anything changed.
    /// </summary>
    public bool ApplyDimmer(DimShape kind)
    {
        var changed = false;
        foreach (var tr in AllTracks)
        {
            if (!tr.HasRow(TimelineRow.Dimmer))
                continue;
            foreach (var (t0, t1) in Spans(tr, TimelineRow.Dimmer))
            {
                var shape = DimmerShape(kind, t0, t1, EffectiveDimmer(tr, t0, false), EffectiveDimmer(tr, t1, true), BeatGridFor(kind));
                if (shape.Count > 0 && tr.ReplaceDimmer(t0, t1, shape))
                    changed = true;
            }
        }
        if (changed)
            MarkEdited();
        return changed;
    }

    /// <summary>
    /// The dimmer level that applies on <paramref name="tr"/> at <paramref name="t"/> (just after it, with
    /// <paramref name="after"/>): its own line where it has values, else what it inherits (a head its fixture's, a
    /// fixture the master's).
    /// </summary>
    public double EffectiveDimmer(TimelineTrack tr, double t, bool after)
    {
        if (tr.DimmerDefinedAt(t) || !tr.Inherits)
            return after ? tr.DimmerAfter(t) : tr.DimmerAt(t);
        var parent = tr.Key.Contains('/') ? TrackFor(tr.Key.Split('/')[0]) : Master;
        if (parent is null || parent == tr)
            return 1;
        return EffectiveDimmer(parent, t, after);
    }

    /// <summary>
    /// Whether the beat grid's there for the beat and bar pulses.
    /// </summary>
    public bool HasBeatGrid => GridBeats.Length > 0;

    float[] BeatGridFor(DimShape kind) =>
        kind switch
        {
            DimShape.BeatPulses => GridBeats,
            DimShape.BarPulses => [.. GridBeats.Where((_, i) => Gd.PosMod(i - BarPhase, BeatsPerBar) == 0)],
            _ => [],
        };

    /// <summary>
    /// The dimmer line for a shape across <paramref name="t0"/> to <paramref name="t1"/> (nodes relative to t0).
    /// <paramref name="left"/> and <paramref name="right"/>: the level just outside the span (pulses start and end
    /// there, Smooth ramps between them); <paramref name="grid"/>: the beat or bar times (pulses up to 100 % on each,
    /// down to 0 % by the next).
    /// </summary>
    public static List<Vector2> DimmerShape(DimShape kind, double t0, double t1, double left, double right, IReadOnlyList<float>? grid = null)
    {
        var n = (float)(t1 - t0);
        float l = (float)left, r = (float)right;
        switch (kind)
        {
            case DimShape.Black:
                return [new(0, 0), new(n, 0)];
            case DimShape.Half:
                return [new(0, 0.5f), new(n, 0.5f)];
            case DimShape.Full:
                return [new(0, 1), new(n, 1)];
            case DimShape.FadeIn:
                return [new(0, 0), new(n, 1)];
            case DimShape.FadeOut:
                return [new(0, 1), new(n, 0)];
            case DimShape.PulseIn:
                return [new(0, l), new((float)((t1 - t0) * 0.5), 1), new(n, r)];
            case DimShape.PulseOut:
                return [new(0, l), new((float)((t1 - t0) * 0.5), 0), new(n, r)];
            case DimShape.Smooth:
                return [new(0, l), new(n, r)];
            case DimShape.BeatPulses or DimShape.BarPulses:
            {
                var hits = (grid ?? []).Where(b => b >= t0 && b < t1).ToList();
                if (hits.Count == 0)
                    return [];
                var output = new List<Vector2>();
                if (hits[0] > t0)
                    output.Add(new(0, 0));
                for (var i = 0; i < hits.Count; i++)
                {
                    var at = (float)(hits[i] - t0);
                    var next = (float)((i + 1 < hits.Count ? hits[i + 1] : t1) - t0);
                    if (output.Count > 0)
                        output.Add(new(at, 0));
                    output.Add(new(at, 1));
                    output.Add(new(next, 0));
                }
                return output;
            }
        }
        return [];
    }

    /// <summary>
    /// Runs <paramref name="make"/>(t0, t1), giving spans relative to t0, over every selected colour span, replacing
    /// what's there. Returns whether anything changed.
    /// </summary>
    public bool ApplyColourSegments(Func<double, double, List<ColourSpan>> make)
    {
        var changed = false;
        foreach (var tr in AllTracks)
        {
            if (!tr.HasRow(TimelineRow.Colour))
                continue;
            foreach (var (t0, t1) in Spans(tr, TimelineRow.Colour))
                changed |= tr.ReplaceColour(t0, t1, make(t0, t1));
        }
        if (changed)
            MarkEdited();
        return changed;
    }

    public bool ApplyStaticColour(Rgb c) => ApplyColourSegments((t0, t1) => [new ColourSpan(0, t1 - t0, c, c)]);

    public bool ApplyGradient(Rgb a, Rgb b) => ApplyColourSegments((t0, t1) => [new ColourSpan(0, t1 - t0, a, b)]);

    public bool ApplyRainbow() => ApplyColourSegments((t0, t1) => RainbowSegments(t1 - t0));

    /// <summary>
    /// A rainbow over <paramref name="length"/> seconds: red round the hue wheel back to red, in six spans.
    /// </summary>
    public static List<ColourSpan> RainbowSegments(double length) =>
        [.. Enumerable.Range(0, 6).Select(i => new ColourSpan(length * i / 6.0, length * (i + 1) / 6.0, Rgb.FromHsv(i / 6.0, 1, 1), Rgb.FromHsv((i + 1) / 6.0 % 1.0, 1, 1)))];

    /// <summary>
    /// Puts named position <paramref name="id"/> across every selected position span ("" clears them). Returns
    /// whether anything changed.
    /// </summary>
    public bool ApplyPosition(string id)
    {
        var changed = false;
        foreach (var tr in AllTracks)
        {
            if (!tr.HasRow(TimelineRow.Position))
                continue;
            foreach (var (t0, t1) in Spans(tr, TimelineRow.Position))
                changed |= tr.ReplacePosition(t0, t1, id == "" ? [] : [new PositionSpan(0, t1 - t0, id)]);
        }
        if (changed)
            MarkEdited();
        return changed;
    }

    /// <summary>
    /// Turns the strobe on at <paramref name="speed"/>, or off (null), across every selected strobe span. Returns
    /// whether anything changed.
    /// </summary>
    public bool ApplyStrobe(StrobeSpeed? speed)
    {
        var changed = false;
        foreach (var tr in AllTracks)
        {
            if (!tr.HasRow(TimelineRow.Strobe))
                continue;
            foreach (var (t0, t1) in Spans(tr, TimelineRow.Strobe))
                changed |= tr.ReplaceStrobe(t0, t1, speed is { } s ? [new StrobeSpan(0, t1 - t0, s)] : []);
        }
        if (changed)
            MarkEdited();
        return changed;
    }

    /// <summary>
    /// Takes the size and cycle the Movement menu shows from the first movement in the selection, if there is one.
    /// </summary>
    public void SyncMovementOptions()
    {
        foreach (var tr in AllTracks)
        {
            foreach (var (t0, t1) in Spans(tr, TimelineRow.Movement))
            {
                foreach (var sg in tr.MovementSegment(t0, t1))
                {
                    if (sg.Effect == "none")
                        continue;
                    MovementSize = NearestIndex(MovementSizes, sg.Size);
                    MovementCycle = NearestIndex(MovementCycles, sg.Period / BarLengthAt(t0 + sg.T0));
                    return;
                }
            }
        }
    }

    static int NearestIndex((string, double Value)[] options, double v)
    {
        var best = 0;
        for (var i = 0; i < options.Length; i++)
            if (Math.Abs(options[i].Value - v) < Math.Abs(options[best].Value - v))
                best = i;
        return best;
    }

    /// <summary>
    /// Puts movement <paramref name="effect"/> ("" clears) across every selected movement span, at the chosen size
    /// and cycle (in bars from the beat grid where the span starts). Returns whether anything changed.
    /// </summary>
    public bool ApplyMovement(string effect)
    {
        var changed = false;
        foreach (var tr in AllTracks)
        {
            if (!tr.HasRow(TimelineRow.Movement))
                continue;
            foreach (var (t0, t1) in Spans(tr, TimelineRow.Movement))
            {
                List<MovementSpan> segs =
                    effect == "" ? [] : [new MovementSpan(0, t1 - t0, effect, BarLengthAt(t0) * MovementCycles[MovementCycle].Value, MovementSizes[MovementSize].Value)];
                changed |= tr.ReplaceMovement(t0, t1, segs);
            }
        }
        if (changed)
            MarkEdited();
        return changed;
    }

    /// <summary>
    /// Size <paramref name="i"/> for new movements, and for the selected ones.
    /// </summary>
    public bool SetMovementSize(int i)
    {
        MovementSize = Math.Clamp(i, 0, MovementSizes.Length - 1);
        var size = MovementSizes[MovementSize].Value;
        return RestyleMovements((sg, _) => sg with { Size = size });
    }

    /// <summary>
    /// Cycle <paramref name="i"/> for new movements, and for the selected ones (in bars at each one's start).
    /// </summary>
    public bool SetMovementCycle(int i)
    {
        MovementCycle = Math.Clamp(i, 0, MovementCycles.Length - 1);
        var bars = MovementCycles[MovementCycle].Value;
        return RestyleMovements((sg, at) => sg with { Period = BarLengthAt(at) * bars });
    }

    bool RestyleMovements(Func<MovementSpan, double, MovementSpan> change)
    {
        var changed = false;
        foreach (var tr in AllTracks)
        {
            if (!tr.HasRow(TimelineRow.Movement))
                continue;
            foreach (var (t0, t1) in Spans(tr, TimelineRow.Movement))
            {
                var segs = tr.MovementSegment(t0, t1);
                // Keep the gaps between them: replace only where they are.
                foreach (var sg in segs)
                {
                    var now = sg.Effect != "none" ? change(sg, t0 + sg.T0) : sg;
                    changed |= tr.ReplaceMovement(t0 + sg.T0, t0 + sg.T1, [now with { T0 = 0, T1 = sg.T1 - sg.T0 }]);
                }
            }
        }
        if (changed)
            MarkEdited();
        return changed;
    }

    /// <summary>
    /// How long a bar is at <paramref name="t"/>, from the beat grid (<see cref="MovementSpan.PERIOD"/> without one).
    /// </summary>
    public double BarLengthAt(double t)
    {
        var beats = GridBeats;
        if (beats.Length < 2)
            return MovementSpan.PERIOD;
        var i = Math.Clamp(Gd.BSearch(beats, t), 1, beats.Length - 1);
        return ((double)beats[i] - beats[i - 1]) * Math.Max(BeatsPerBar, 1);
    }

    // ------------------------------------------------------------ structure --

    /// <summary>
    /// Song <paramref name="path"/>'s structure analysed this session (its edits on it), or null.
    /// </summary>
    public SongAnalysis? CachedStructure(string path) => _structCache.GetValueOrDefault(path);

    /// <summary>
    /// Takes song <paramref name="path"/>'s freshly analysed structure (analysed away from the timeline, for
    /// <see cref="GenerateInto"/>): VirtualDJ's grid on it, its saved edits too, kept as if the song had been opened.
    /// </summary>
    public void AddStructure(string path, SongAnalysis analysis, DjSong? song)
    {
        if (song is not null)
            ApplyVdjGrid(analysis, song);
        DataFor(path);
        ApplySavedStructure(path, analysis);
        _structCache[path] = analysis;
        if (path == Path)
            SetStructure(analysis);
    }

    void SetStructure(SongAnalysis? a)
    {
        Structure = a;
        StructureChanged?.Invoke();
    }

    /// <summary>
    /// Puts the song's saved (edited) sections on its analysis, or, with none saved, the analyser's own (kept aside
    /// the first time).
    /// </summary>
    void ApplySavedStructure(string path, SongAnalysis a)
    {
        a.AnalysedSections ??= [.. a.Sections];
        var saved = _songs.GetValueOrDefault(path)?.Obj("structure")?.Get("sections");
        a.Sections = saved is JsonArray arr ? SongAnalysis.SectionsFrom(arr) : [.. a.AnalysedSections];
    }

    void LoadStructure(string path, bool fresh = false)
    {
        if (_structCache.TryGetValue(path, out var cached) && !fresh)
        {
            SetStructure(cached);
            return;
        }
        SetStructure(null);
        if (!File.Exists(path))
        {
            StructurePlaceholder = "";
            return;
        }
        StructurePlaceholder = "analysing the song structure...";
        if (_analysing.Remove(path, out var old))
            old.Cancel();
        var cancel = new CancellationTokenSource();
        _analysing[path] = cancel;
        var song = Song;
        var analyse = AnalyseStructure;
        Task.Run(() =>
        {
            try
            {
                var a = analyse(path, cancel.Token);
                _posted.Enqueue(() =>
                {
                    if (_analysing.GetValueOrDefault(path) == cancel)
                        _analysing.Remove(path);
                    StructureReady(path, a, song);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _posted.Enqueue(() =>
                {
                    if (_analysing.GetValueOrDefault(path) == cancel)
                        _analysing.Remove(path);
                    StructureFailed(path, e.Message);
                });
            }
        });
    }

    /// <summary>
    /// Whether the open song's structure is being analysed.
    /// </summary>
    public bool IsAnalysing => _analysing.ContainsKey(Path);

    void StructureFailed(string path, string message)
    {
        _regenerate.Remove(path);
        if (path != Path)
            return;
        StructurePlaceholder = "couldn't analyse the structure: " + message.TrimEnd('.');
        if (_structCache.TryGetValue(path, out var a))
            SetStructure(a); // re-analysing: keep the old one
        else
            StructureChanged?.Invoke();
    }

    /// <summary>
    /// A song's structure's been analysed: VirtualDJ's grid on it, its saved edits (unless it was re-analysed for
    /// Shift+Generate, which drops them and generates from it), kept for the session.
    /// </summary>
    internal void StructureReady(string path, SongAnalysis a, DjSong? song)
    {
        if (song is not null)
            ApplyVdjGrid(a, song);
        var regenerate = _regenerate.Remove(path) && path == Path;
        if (regenerate)
            SongData().Remove("structure");
        ApplySavedStructure(path, a);
        _structCache[path] = a;
        if (path == Path)
            SetStructure(a);
        if (regenerate && !GenerateShow())
            MarkEdited();
    }

    /// <summary>
    /// Shift+Generate: analyses the song's structure afresh (dropping its edits), then generates from it once it's in;
    /// the new structure and the show one undo step. The old structure stays if the analysis fails. Returns whether
    /// it started.
    /// </summary>
    public bool ReanalyseAndGenerate()
    {
        if (Path == "" || !_structCache.ContainsKey(Path) || !File.Exists(Path))
            return false;
        _regenerate.Add(Path);
        LoadStructure(Path, true);
        return true;
    }

    /// <summary>
    /// Uses VirtualDJ's beat grid in place of the analyser's: its beats, bars (four beats from its first) and tempo;
    /// and moves each section border to the nearest of its bars, so the structure lines up with the bar ruler.
    /// </summary>
    public static void ApplyVdjGrid(SongAnalysis a, DjSong song)
    {
        var beats = song.BeatTimes(a.Duration);
        if (beats.Count < 2)
            return;
        var phase = song.FirstBeatIndex(beats);
        var bars = beats.Where((_, i) => Gd.PosMod(i - phase, BEATS_PER_BAR) == 0).ToArray();
        a.BeatTimes = [.. beats];
        a.Downbeats = bars;
        if (song.BestBpm > 0)
            a.Bpm = song.BestBpm;
        if (bars.Length == 0)
            return;
        var secs = a.Sections;
        for (var i = 1; i < secs.Count; i++)
        {
            var t = secs[i].Start;
            var j = Math.Clamp(Gd.BSearch(bars, t), 0, bars.Length - 1);
            if (j > 0 && Math.Abs(bars[j - 1] - t) < Math.Abs(bars[j] - t))
                j--;
            secs[i] = secs[i] with { Start = bars[j] };
            secs[i - 1] = secs[i - 1] with { End = bars[j] };
        }
        // Snapping can collapse a very short section to nothing: drop it.
        for (var i = secs.Count - 1; i >= 0; i--)
        {
            if (secs[i].End - secs[i].Start < 0.01 && secs.Count > 1)
            {
                if (i > 0)
                    secs[i - 1] = secs[i - 1] with { End = secs[i].End };
                else
                    secs[i + 1] = secs[i + 1] with { Start = secs[i].Start };
                secs.RemoveAt(i);
            }
        }
    }

    // ------------------------------------------------------------- generate --

    /// <summary>
    /// Generates a light show into the open song's timelines (the Generate button): from its structure, beat grid or
    /// tempo, the fixtures' rows and the named positions. Each press makes another variation. Replaces what the
    /// timelines had, as one undo step; with areas selected, only what's in them. Returns whether it ran.
    /// </summary>
    public bool GenerateShow()
    {
        var a = Structure;
        if (a is null || !HasWave || Path == "")
            return false;
        var beats = GridBeats;
        var onGrid = beats.Length > 0;
        if (!onGrid)
            beats = a.BeatTimes.Length > 0 ? a.BeatTimes : TimelineGenerator.EvenBeats(a.Bpm, Duration);
        var barPhase = onGrid ? BarPhase : FirstBar(a, beats);
        var parts = PatchParts();
        var tracks = AllTracks.Where(tr => !LeftOut(parts, tr.Key)).Select(tr => GenTrack(parts, tr.Key, tr.Target, tr.Rows)).ToList();
        var show = RunJob(MakeJob(Path, a, beats, barPhase, BeatsPerBar, Duration, tracks, [])).Show;
        if (!Selection.IsEmpty)
        {
            var skip = AllTracks.Where(tr => LeftOut(parts, tr.Key)).Select(tr => tr.Key).ToHashSet();
            if (SpliceShow(show, skip))
                MarkEdited();
            return true;
        }
        foreach (var tr in AllTracks)
        {
            var c = show.GetValueOrDefault(tr.Key) ?? new GeneratedRows();
            tr.DimmerNodes = c.Dimmer;
            tr.ColourSegments = c.Colour;
            tr.StrobeSegments = c.Strobe;
            tr.PositionSegments = c.Position;
            tr.MovementSegments = c.Movement;
        }
        MarkEdited();
        return true;
    }

    Dictionary<string, PatchedFixture> PatchParts()
    {
        var parts = new Dictionary<string, PatchedFixture>();
        foreach (var fx in Fixtures?.Invoke() ?? [])
            parts[fx.Uid] = fx;
        return parts;
    }

    /// <summary>
    /// Whether track <paramref name="key"/> belongs to a fixture left out of generation (its timelines are cleared).
    /// </summary>
    static bool LeftOut(Dictionary<string, PatchedFixture> parts, string key) => parts.TryGetValue(key.Split('/')[0], out var fx) && fx.ExcludeGenerate;

    static GeneratorTrack GenTrack(Dictionary<string, PatchedFixture> parts, string key, TimelineTarget target, IReadOnlyList<TimelineRow> rows)
    {
        if (!parts.TryGetValue(key, out var fx))
            return new GeneratorTrack(key, target, [.. rows]);
        var wheel = fx.Profile.WheelColours(fx.Mode);
        return new GeneratorTrack(key, target, [.. rows], fx.Category, fx.CategoryGroup, wheel.Count > 0 ? wheel : null);
    }

    /// <summary>
    /// Which of <paramref name="beats"/> starts a bar: the analysis's first downbeat's.
    /// </summary>
    static int FirstBar(SongAnalysis a, float[] beats)
    {
        if (a.Downbeats.Length == 0 || beats.Length == 0)
            return 0;
        return Math.Clamp(Gd.BSearch(beats, a.Downbeats[0] - 0.01), 0, beats.Length - 1);
    }

    /// <summary>
    /// What the generator takes for song <paramref name="path"/>'s show: the next variation for it, around the named
    /// positions.
    /// </summary>
    GenerateJob MakeJob(string path, SongAnalysis a, float[] beats, int barPhase, int perBar, double duration, List<GeneratorTrack> tracks, List<string> keys)
    {
        var variation = _generated.GetValueOrDefault(path);
        _generated[path] = variation + 1;
        return new GenerateJob(
            [.. a.Sections],
            beats,
            perBar,
            duration,
            tracks,
            [.. PositionIds?.Invoke() ?? []],
            Gd.GetFile(path),
            variation,
            barPhase,
            new SongEnvelope(a.WavePeak, a.WaveLo, a.WaveHi),
            keys
        );
    }

    /// <summary>
    /// Generates a job's show, on any thread.
    /// </summary>
    public static GeneratedShow RunJob(GenerateJob job)
    {
        var show = TimelineGenerator.Generate(job.Sections, job.Beats, job.PerBar, job.Duration, job.Tracks, job.Positions, job.Seed, job.Variation, job.BarPhase, job.Env);
        var rows = job.Keys.ToDictionary(k => k, k => RowsOf(show.GetValueOrDefault(k)));
        return new GeneratedShow(job.Keys, show, rows);
    }

    /// <summary>
    /// A generated track's rows as saved: each row there, empty or not.
    /// </summary>
    static JsonObject RowsOf(GeneratedRows? c)
    {
        c ??= new GeneratedRows();
        return new JsonObject
        {
            ["dimmer"] = TimelineFormat.NodesTo(c.Dimmer),
            ["colour"] = TimelineFormat.ColoursTo(c.Colour),
            ["strobe"] = TimelineFormat.StrobesTo(c.Strobe),
            ["position"] = TimelineFormat.PositionsTo(c.Position),
            ["movement"] = TimelineFormat.MovementsTo(c.Movement),
        };
    }

    /// <summary>
    /// Generates a show into song <paramref name="path"/>'s saved timelines without its screen (the song list's
    /// Regenerate Lighting Timelines): as Generate does, but always every track. Saved at once, and an undo step for
    /// the song. Returns "" or what went wrong.
    /// </summary>
    public string GenerateInto(string path, DjSong? song)
    {
        if (Store is null)
            return "save the show first";
        var gen = GenerateFor(path, song);
        if (gen is null)
            return "its structure hasn't been analysed";
        return PutGenerated(path, gen);
    }

    /// <summary>
    /// Song <paramref name="path"/>'s show as <see cref="GenerateInto"/> makes it, without saving it; null if its
    /// structure hasn't been analysed. A new variation each time.
    /// </summary>
    public GeneratedShow? GenerateFor(string path, DjSong? song = null) => GenerateJobFor(path, song) is { } job ? RunJob(job) : null;

    /// <summary>
    /// <see cref="GenerateFor"/>'s show as a job, to run on another thread; null if the structure hasn't been
    /// analysed.
    /// </summary>
    public GenerateJob? GenerateJobFor(string path, DjSong? song = null)
    {
        if (!_structCache.TryGetValue(path, out var a))
            return null;
        DataFor(path);
        if (path == Path)
            Collect();
        ApplySavedStructure(path, a);
        float[] beats = song is not null ? [.. song.BeatTimes(a.Duration)] : [];
        var barPhase = beats.Length >= 2 ? song!.FirstBeatIndex(beats) : 0;
        if (beats.Length < 2)
        {
            beats = a.BeatTimes.Length > 0 ? a.BeatTimes : TimelineGenerator.EvenBeats(a.Bpm, a.Duration);
            barPhase = FirstBar(a, beats);
        }
        var (keys, tracks) = AllGenTracks();
        return MakeJob(path, a, beats, barPhase, BEATS_PER_BAR, a.Duration, tracks, keys);
    }

    /// <summary>
    /// A stand-in show for a song before its structure's known (DJ Perform Mode, while it's analysed): one even
    /// section on its beat grid (VirtualDJ's, else <paramref name="bpm"/> from <paramref name="firstBeat"/>), so it
    /// keeps time without following the song's shape. As long as VirtualDJ says the song is (else 10 minutes).
    /// </summary>
    public GeneratedShow PlaceholderFor(string path, DjSong? song = null, double bpm = 0, double firstBeat = 0) => RunJob(PlaceholderJob(path, song, bpm, firstBeat));

    public GenerateJob PlaceholderJob(string path, DjSong? song = null, double bpm = 0, double firstBeat = 0)
    {
        var duration = song is { Length: > 0 } ? song.Length : 600.0;
        float[] beats = song is not null ? [.. song.BeatTimes(duration)] : [];
        var barPhase = beats.Length >= 2 ? song!.FirstBeatIndex(beats) : 0;
        if (beats.Length < 2)
        {
            var first = Math.Max(firstBeat, 0);
            beats = [.. TimelineGenerator.EvenBeats(bpm > 0 ? bpm : 120, duration - first).Select(b => (float)(b + first))];
            barPhase = 0;
        }
        var (keys, tracks) = AllGenTracks();
        return new GenerateJob([], beats, BEATS_PER_BAR, duration, tracks, [.. PositionIds?.Invoke() ?? []], Gd.GetFile(path), 0, barPhase, null, keys);
    }

    /// <summary>
    /// Every track of the patch as generation takes them: every track's key (the master's, each fixture's and
    /// head's), and the tracks not left out.
    /// </summary>
    (List<string> Keys, List<GeneratorTrack> Tracks) AllGenTracks()
    {
        var parts = PatchParts();
        var keys = new List<string> { "master" };
        var tracks = new List<GeneratorTrack> { GenTrack(parts, "master", TimelineTarget.Master, Master.Rows) };
        foreach (var fx in parts.Values)
        {
            foreach (var spec in FixtureTrackSpecs(fx.Uid, fx.Name, fx.Profile, fx.Mode))
            {
                keys.Add(spec.Key);
                if (!LeftOut(parts, spec.Key))
                    tracks.Add(GenTrack(parts, spec.Key, spec.Target, spec.Rows));
            }
        }
        return (keys, tracks);
    }

    /// <summary>
    /// Saves a generated show as song <paramref name="path"/>'s timelines, replacing what its tracks had.
    /// <paramref name="temporary"/>: as temporary ones (made in DJ Perform Mode, not by the user; refused if it has
    /// timelines of its own; written in the background, and no undo step unless it's the open song). Otherwise an
    /// undo step for the song. Returns "" or what went wrong.
    /// </summary>
    public string PutGenerated(string path, GeneratedShow gen, bool temporary = false)
    {
        if (Store is null)
            return "save the show first";
        var d = DataFor(path);
        if (path == Path)
            Collect();
        if (temporary && !d.Bool("temporary", false) && HasRows(d.Obj("tracks")))
            return "it has timelines of its own";
        var before = temporary ? null : SnapshotOf(d);
        var saved = (JsonObject)d["tracks"]!;
        foreach (var key in gen.Keys)
        {
            var entry = saved[key] as JsonObject ?? [];
            var rows = gen.Rows.TryGetValue(key, out var r) ? r : RowsOf(gen.Show.GetValueOrDefault(key));
            foreach (var (row, value) in rows)
            {
                if (value is JsonArray { Count: > 0 })
                    entry[row] = value.DeepClone();
                else
                    entry.Remove(row);
            }
            entry.Parent?.AsObject().Remove(key);
            if (entry.Count == 0)
                saved.Remove(key);
            else
                saved[key] = entry;
        }
        if (path == Path)
        {
            ShowTracks(saved);
            MarkEdited();
            return Flush(temporary);
        }
        if (!temporary)
        {
            if (!_history.TryGetValue(path, out var h))
                _history[path] = h = new History();
            h.State ??= before;
            Record(path, SnapshotOf(d));
        }
        SetTemporary(d, temporary);
        if (temporary)
            return SaveInBackground(path, d);
        var err = Store.SaveSong(path, d);
        if (err == "")
            TimelinesSaved?.Invoke(path);
        return err;
    }

    /// <summary>
    /// Saves song <paramref name="path"/>'s timelines with the file written on a worker thread (DJ Perform Mode
    /// mustn't stall the lights while a big show's written); <see cref="TimelinesSaved"/> once it is. Returns "" or
    /// what went wrong before then.
    /// </summary>
    string SaveInBackground(string path, JsonObject d)
    {
        var store = Store!;
        var job = store.SaveJob(path, d);
        if (job.Error is { } error)
            return error;
        _saves.Add(
            Task.Run(() =>
            {
                var err = TimelineStore.WriteJob(job);
                _posted.Enqueue(() =>
                {
                    if (err != "")
                        Trace.TraceWarning($"Couldn't save the timelines for {Gd.GetFile(path)}: {err}");
                    else if (store == Store)
                        TimelinesSaved?.Invoke(path);
                });
            })
        );
        return "";
    }

    /// <summary>
    /// Whether a song's timelines are still being written in the background.
    /// </summary>
    public bool IsSaving => _saves.Any(t => !t.IsCompleted);

    /// <summary>
    /// Puts a generated show into the selected areas only, row by row, leaving the timelines outside them as they
    /// were. The tracks in <paramref name="skip"/> (left out of generation) are left alone. Returns whether anything
    /// changed.
    /// </summary>
    internal bool SpliceShow(Dictionary<string, GeneratedRows> show, IReadOnlySet<string>? skip = null)
    {
        var changed = false;
        foreach (var tr in AllTracks)
        {
            if (skip?.Contains(tr.Key) == true)
                continue;
            var c = show.GetValueOrDefault(tr.Key) ?? new GeneratedRows();
            var src = new TimelineTrack
            {
                Target = tr.Target,
                DimmerNodes = c.Dimmer,
                ColourSegments = c.Colour,
                StrobeSegments = c.Strobe,
                PositionSegments = c.Position,
                MovementSegments = c.Movement,
            };
            foreach (var row in tr.Rows)
            {
                foreach (var (t0, t1) in Spans(tr, row))
                {
                    switch (row)
                    {
                        case TimelineRow.Dimmer:
                            // Only where the generated line has values (a fixture or head may have none there).
                            if (src.DimmerDefinedSpan(t0, t1) is { } d)
                                changed |= tr.ReplaceDimmer(d.T0, d.T1, src.DimmerSegment(d.T0, d.T1));
                            break;
                        case TimelineRow.Colour:
                            changed |= tr.ReplaceColour(t0, t1, src.ColourSegment(t0, t1));
                            break;
                        case TimelineRow.Strobe:
                            changed |= tr.ReplaceStrobe(t0, t1, src.StrobeSegment(t0, t1));
                            break;
                        case TimelineRow.Position:
                            changed |= tr.ReplacePosition(t0, t1, src.PositionSegment(t0, t1));
                            break;
                        case TimelineRow.Movement:
                            changed |= tr.ReplaceMovement(t0, t1, src.MovementSegment(t0, t1));
                            break;
                    }
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// The show's named positions as Generate takes them, each with how widely it spreads the beams. Movement needs a
    /// position to move around: with none named yet, a "Centre" one is made (no points of its own: every fixture
    /// centred).
    /// </summary>
    public static Func<IReadOnlyList<GeneratorPosition>> PositionsFrom(PositionLibrary library) =>
        () =>
        {
            if (library.Positions.Count == 0)
                library.Rename(library.Add().Id, "Centre");
            return [.. library.Positions.Select(p => new GeneratorPosition(p.Id, Spread(p.Points.Values)))];
        };

    /// <summary>
    /// How far apart a position's points are: their mean distance from their middle (0 with fewer than two).
    /// </summary>
    public static double Spread(IReadOnlyCollection<Vector2> points)
    {
        if (points.Count < 2)
            return 0;
        var mid = Vector2.Zero;
        foreach (var v in points)
            mid += v;
        mid /= points.Count;
        double d = 0;
        foreach (var v in points)
            d += Vector2.Distance(v, mid);
        return d / points.Count;
    }
}

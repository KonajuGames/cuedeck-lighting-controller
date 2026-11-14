using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Dj;
using Cuedeck.Dmx;
using Cuedeck.Os2l;
using Cuedeck.Show;
using Cuedeck.Timelines;

namespace Cuedeck.Perform;

/// <summary>
/// Where a deck's timelines are.
/// </summary>
enum LoadState
{
    None,
    Loading,
    Loaded,
    NoTimelines,
    NoFolder,
}

/// <summary>
/// One of VirtualDJ's decks as DJ Perform Mode follows it: the song on it, whether it's playing, its volume fader, where
/// it is, and the song's timelines.
/// </summary>
sealed class Deck
{
    public int Num { get; set; }

    /// <summary>
    /// Not in VirtualDJ's skin: not followed.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// The song (forward slashes); "" for none.
    /// </summary>
    public string Path { get; set; } = "";

    public bool Playing { get; set; }

    /// <summary>
    /// The volume fader, 0 to 1; null until reported.
    /// </summary>
    public double? Level { get; set; }

    /// <summary>
    /// Seconds into the song at <see cref="Ticks"/> (milliseconds).
    /// </summary>
    public double Time { get; set; }

    public long Ticks { get; set; }

    /// <summary>
    /// Song seconds per real second, learned from VirtualDJ's reports (a pitched-up deck runs fast).
    /// </summary>
    public double Rate { get; set; } = 1;

    public LoadState LoadState { get; set; }

    /// <summary>
    /// Its song's timelines by key ("master", a fixture's uid, "&lt;uid&gt;/h&lt;n&gt;"): only those with something on
    /// them, or muted or soloed.
    /// </summary>
    public Dictionary<string, TimelineTrack> Tracks { get; set; } = [];

    /// <summary>
    /// Counts the loads begun, so a finished one that's been overtaken is dropped.
    /// </summary>
    public int LoadId { get; set; }

    /// <summary>
    /// The tracks are generated ones, not the user's.
    /// </summary>
    public bool Temporary { get; set; }

    /// <summary>
    /// They're a stand-in on the beat while the song's analysed.
    /// </summary>
    public bool Analysing { get; set; }

    /// <summary>
    /// How far the analysis has got, 0 to 1.
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// Why they're only a stand-in, or weren't saved.
    /// </summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// Seconds into the song at <paramref name="now"/> (milliseconds): while it plays, running on from the last report
    /// at its rate.
    /// </summary>
    public double Position(long now) => Playing ? Time + (now - Ticks) / 1000.0 * Rate : Time;

    /// <summary>
    /// Whether it drives the lights: playing, with timelines, its fader up.
    /// </summary>
    public bool Live => Playing && LoadState == LoadState.Loaded && (Level is null || Level > DjPerformer.SILENT_LEVEL);
}

/// <summary>
/// DJ Perform Mode's engine: listens for VirtualDJ (or Cuepoint) over OS2L and plays the lighting timelines of the
/// songs on its decks into the DMX output.
/// <para>
/// For each deck it follows what VirtualDJ reports through the server's subscriptions: the song loaded, whether it's
/// playing, its volume fader and where it is (milliseconds, every 100 ms or so; between reports the position runs on
/// at the rate they've been advancing). A new song's timelines are read from the show's <see cref="TimelineStore"/> on
/// a worker thread, so a deck change never stalls the app. Each frame (<see cref="Layer"/>) every deck that's playing
/// with its fader up and has timelines plays them at its position (<see cref="SongOutput.Of"/>), and the decks are
/// mixed (<see cref="SongOutput.Mix"/>: highest takes precedence, but movement averaged) into a layer of the DMX
/// output under the chases and effects.
/// </para>
/// <para>
/// A song without timelines gets temporary ones from DJ Edit Mode's <see cref="Timeline"/>: at once a stand-in on its
/// beat grid (VirtualDJ's, or the tempo and first beat it reports) while its structure's analysed in the background;
/// then a show generated from it as Generate would, saved with the show flagged temporary, or only kept here when the
/// show has no timelines folder. A song analysed this session goes straight to its show. Everything handed back from
/// the workers, and the server's messages, are dealt with in <see cref="Update"/>, called each frame. sLight's
/// <c>DjPerformer</c>.
/// </para>
/// </summary>
sealed class DjPerformer
{
    /// <summary>
    /// Decks followed (VirtualDJ has up to four).
    /// </summary>
    public const int DECKS = 4;

    /// <summary>
    /// Decks followed until VirtualDJ reports its skin's (get_decks): its default skin's two. Behind a 2-deck skin
    /// decks 3 and 4 still hold whatever was last loaded on them, so they're only followed once the skin shows them.
    /// </summary>
    public const int DEFAULT_SKIN_DECKS = 2;

    /// <summary>
    /// A deck whose volume fader is at or below this doesn't drive the lights (it's being cued in the headphones).
    /// </summary>
    public const double SILENT_LEVEL = 0.02;

    readonly DmxOutput _dmx;
    readonly LayerProvider _layer;
    readonly ConcurrentQueue<Action> _posted = new();
    TimelineBatch? _batch;
    int _pending; // shows being generated on worker threads

    public DjPerformer(DmxOutput dmx)
    {
        _dmx = dmx;
        _layer = Layer;
        for (var i = 1; i <= DECKS; i++)
            Decks.Add(new Deck { Num = i, Hidden = i > SkinDecks });
    }

    /// <summary>
    /// Something the panel shows changed (the connection, a deck's song, state or timelines).
    /// </summary>
    public event Action? StateChanged;

    public int Port { get; set; } = Os2lServer.DEFAULT_PORT;
    public string BindAddress { get; set; } = "*";

    /// <summary>
    /// Advertise over mDNS so VirtualDJ finds the server by itself.
    /// </summary>
    public bool Advertise { get; set; } = true;

    public Func<IReadOnlyList<PerformFixture>> FixturesProvider { get; set; } = () => [];

    /// <summary>
    /// The show's timelines folder, or null when it has none (a show not saved yet).
    /// </summary>
    public Func<TimelineStore?> StoreProvider { get; set; } = () => null;

    /// <summary>
    /// The show's named positions, which position rows refer to.
    /// </summary>
    public PositionLibrary? Positions { get; set; }

    /// <summary>
    /// DJ Edit Mode's timelines, which generate temporary ones for a song without any (null: none are).
    /// </summary>
    public SongTimelines? Timeline { get; set; }

    /// <summary>
    /// A song's library entry (its beat grid), or null.
    /// </summary>
    public Func<string, DjSong?>? SongLookup { get; set; }

    /// <summary>
    /// Analyses a song's structure for its temporary timelines (null: the analyser's own).
    /// </summary>
    public Func<string, Action<float>, CancellationToken, SongAnalysis>? Analyse { get; set; }

    /// <summary>
    /// The tempo clock VirtualDJ's beats drive (beat-synced chases and effects).
    /// </summary>
    public TempoClock? Tempo { get; set; }

    /// <summary>
    /// The time in milliseconds that decks' positions run on by.
    /// </summary>
    public Func<long> Now { get; set; } = () => Environment.TickCount64;

    public Os2lServer? Os2l { get; private set; }
    public List<Deck> Decks { get; } = [];

    /// <summary>
    /// How many decks VirtualDJ's skin shows; the rest are hidden.
    /// </summary>
    public int SkinDecks { get; private set; } = DEFAULT_SKIN_DECKS;

    /// <summary>
    /// The OS2L server's state ("listening on :9960", "off", what went wrong).
    /// </summary>
    public string Status { get; private set; } = "off";

    public bool IsActive => Os2l is not null;
    public bool IsListening => Os2l?.IsListening == true;
    public int ClientCount => Os2l?.ClientCount ?? 0;
    public string AdvertisingStatus => Os2l?.AdvertisingStatus ?? "";

    public Deck Deck(int num) => Decks[num - 1];

    public double Position(Deck d) => d.Position(Now());

    /// <summary>
    /// Starts listening for VirtualDJ and playing timelines. Returns "" or why it couldn't listen (the decks play
    /// regardless, once something connects).
    /// </summary>
    public string Start()
    {
        Stop();
        var os2l = new Os2lServer { Subscriptions = Os2lServer.DefaultSubscriptions(DECKS), Advertise = Advertise };
        os2l.StateChanged += (_, detail) =>
        {
            Status = detail;
            StateChanged?.Invoke();
        };
        os2l.ClientConnected += _ => StateChanged?.Invoke();
        os2l.ClientDisconnected += _ => StateChanged?.Invoke();
        os2l.AdvertisingChanged += (_, _) => StateChanged?.Invoke();
        os2l.ValueChanged += OnValue;
        // VirtualDJ's beats drive the tempo clock.
        os2l.Beat += (_, _, bpm, _) => Tempo?.ExternalBeat(bpm, TempoSource.Os2l);
        Os2l = os2l;
        var err = os2l.Start(Port, BindAddress);
        if (!_dmx.LayerProviders.Contains(_layer))
            _dmx.LayerProviders.Add(_layer);
        StateChanged?.Invoke();
        return err;
    }

    /// <summary>
    /// Stops listening and playing timelines (the decks are forgotten).
    /// </summary>
    public void Stop()
    {
        _dmx.LayerProviders.Remove(_layer);
        if (Os2l is not null)
        {
            Os2l.Dispose();
            Os2l = null;
        }
        Status = "off";
        _batch?.Cancel();
        SkinDecks = DEFAULT_SKIN_DECKS;
        foreach (var d in Decks.ToList())
            Forget(d.Num);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Takes the server's connections and messages, and what the workers have handed back: each frame.
    /// </summary>
    public void Update()
    {
        Os2l?.Poll();
        _batch?.Update();
        while (_posted.TryDequeue(out var act))
            act();
    }

    /// <summary>
    /// Empties deck <paramref name="num"/> (hidden if the skin doesn't show it).
    /// </summary>
    void Forget(int num) =>
        Decks[num - 1] = new Deck
        {
            Num = num,
            Hidden = num > SkinDecks,
            LoadId = Decks[num - 1].LoadId + 1, // drops any load still under way
        };

    /// <summary>
    /// Reads every loaded deck's timelines again (the show changed).
    /// </summary>
    public void ReloadAll()
    {
        foreach (var d in Decks)
            if (d.Path != "")
                Load(d);
    }

    // ----------------------------------------------------------------- decks --

    void OnValue(string trigger, JsonNode? value)
    {
        if (trigger == "get_decks")
        {
            SetSkinDecks(ToInt(value));
            return;
        }
        // "deck N <query>"
        if (!trigger.StartsWith("deck "))
            return;
        var parts = trigger.Split(' ');
        var num = ToInt(parts[1]);
        if (num < 1 || num > DECKS)
            return;
        var query = trigger[$"deck {num} ".Length..];
        var d = Decks[num - 1];
        if (d.Hidden)
            return;
        switch (query)
        {
            case "get_filepath":
                var p = Json.AsString(value).Replace('\\', '/');
                if (p != d.Path)
                {
                    d.Path = p;
                    d.Time = 0;
                    d.Ticks = Now();
                    d.Rate = 1;
                    Load(d);
                }
                break;
            case "play":
                var on = Os2lServer.Truthy(value);
                if (on != d.Playing)
                {
                    d.Time = d.Position(Now()); // frozen, or resumed from where it is
                    d.Ticks = Now();
                    d.Playing = on;
                    StateChanged?.Invoke();
                }
                break;
            case "level":
                d.Level = Json.AsDouble(value);
                break;
            case "get_time elapsed absolute":
                ReportTime(d, Json.AsDouble(value) / 1000.0, Now());
                break;
        }
    }

    /// <summary>
    /// Godot's <c>str(value).to_int()</c>: a number's whole part, or the digits in a string.
    /// </summary>
    static int ToInt(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (int)Json.AsDouble(value) : ToInt(Json.AsString(value));

    static int ToInt(string s)
    {
        var digits = new string([.. s.Where(char.IsAsciiDigit)]);
        var n = digits == "" ? 0 : int.TryParse(digits, out var x) ? x : 0;
        return s.TrimStart().StartsWith('-') ? -n : n;
    }

    /// <summary>
    /// VirtualDJ's skin shows <paramref name="n"/> decks: the ones past it are emptied and no longer followed; ones it
    /// shows again pick up what VirtualDJ last reported for them.
    /// </summary>
    public void SetSkinDecks(int n)
    {
        n = Math.Clamp(n, 1, DECKS);
        if (n == SkinDecks)
            return;
        SkinDecks = n;
        foreach (var num in Enumerable.Range(1, DECKS))
        {
            var hide = num > n;
            if (hide == Decks[num - 1].Hidden)
                continue;
            Forget(num);
            if (!hide && Os2l is not null)
            {
                var prefix = $"deck {num} ";
                if (Os2l.Values.TryGetValue(prefix + "get_filepath", out var song) && song is not null)
                    OnValue(prefix + "get_filepath", song); // first: it resets the position
                foreach (var (t, v) in Os2l.Values.ToList())
                    if (t.StartsWith(prefix) && !t.EndsWith("get_filepath"))
                        OnValue(t, v);
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// A position report for deck <paramref name="d"/>: <paramref name="t"/> seconds into the song at
    /// <paramref name="now"/> milliseconds. While it plays, the rate is learned from how far reports advance, and the
    /// position snaps to the report.
    /// </summary>
    public static void ReportTime(Deck d, double t, long now)
    {
        if (d.Playing && now > d.Ticks)
        {
            var dt = (now - d.Ticks) / 1000.0;
            var observed = (t - d.Time) / dt;
            if (dt > 0.02 && observed > 0.25 && observed < 4.0)
                d.Rate = Gd.Lerp(d.Rate, observed, 0.3);
        }
        d.Time = t;
        d.Ticks = now;
    }

    void Load(Deck d)
    {
        d.LoadId++;
        d.Tracks = [];
        d.Temporary = false;
        d.Analysing = false;
        d.Note = "";
        var store = StoreProvider();
        if (d.Path == "")
            d.LoadState = LoadState.None;
        else if (store is null && Timeline is not null)
        {
            // Nothing to read: temporary ones, made next frame.
            d.LoadState = LoadState.Loading;
            d.Temporary = true;
            var (num, id) = (d.Num, d.LoadId);
            _posted.Enqueue(() => Generate(num, id));
        }
        else if (store is null)
            d.LoadState = LoadState.NoFolder;
        else
        {
            d.LoadState = LoadState.Loading;
            var (dir, path, num, id) = (store.Dir, d.Path, d.Num, d.LoadId);
            // A store of its own per read: nothing shared with the UI thread. The tracks are made there too (a big
            // show's take a frame or more, which would stall the lights).
            Task.Run(() =>
            {
                try
                {
                    var data = new TimelineStore(dir).LoadSong(path);
                    var tracks = TracksFromSaved(data);
                    var temporary = data.Bool("temporary", false);
                    _posted.Enqueue(() => Loaded(num, id, temporary, tracks));
                }
                catch (Exception e)
                {
                    Trace.TraceWarning($"Couldn't read the timelines for {Gd.GetFile(path)}: {e.Message}");
                    _posted.Enqueue(() => Loaded(num, id, false, []));
                }
            });
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// A song's saved timelines (<see cref="TimelineStore.LoadSong"/>) as a deck's tracks: only those with something
    /// on them, or muted or soloed. On any thread.
    /// </summary>
    public static Dictionary<string, TimelineTrack> TracksFromSaved(JsonObject data)
    {
        var tracks = new Dictionary<string, TimelineTrack>();
        if (data.Obj("tracks") is not { } saved)
            return tracks;
        foreach (var (key, entry) in saved)
        {
            if (entry is not JsonObject)
                continue;
            var track = new TimelineTrack { Key = key };
            TimelineFormat.ReadTrack(track, entry);
            if (HasRows(track) || track.Mute || track.Solo)
                tracks[key] = track;
        }
        return tracks;
    }

    static bool HasRows(TimelineTrack t) =>
        t.DimmerNodes.Count > 0 || t.ColourSegments.Count > 0 || t.StrobeSegments.Count > 0 || t.PositionSegments.Count > 0 || t.MovementSegments.Count > 0;

    /// <summary>
    /// A load finished: whether the saved timelines are temporary ones, and the tracks made from them.
    /// </summary>
    internal void Loaded(int num, int id, bool temporary, Dictionary<string, TimelineTrack> tracks)
    {
        var d = Decks[num - 1];
        if (id != d.LoadId)
            return; // overtaken
        d.Tracks = tracks;
        d.Temporary = temporary && tracks.Count > 0;
        d.LoadState = tracks.Count > 0 ? LoadState.Loaded : LoadState.NoTimelines;
        if (tracks.Count == 0 && Timeline is not null)
        {
            Generate(num, id);
            return;
        }
        StateChanged?.Invoke();
    }

    // --------------------------------------------------- temporary timelines --

    /// <summary>
    /// Gives deck <paramref name="num"/>'s song (still load <paramref name="id"/>) temporary timelines: a stand-in at
    /// once and its structure analysed for the real thing; or, analysed already this session, the real thing.
    /// </summary>
    void Generate(int num, int id)
    {
        var d = Decks[num - 1];
        if (id != d.LoadId || d.Path == "" || Timeline is null)
            return; // overtaken
        var song = SongLookup?.Invoke(d.Path);
        d.Temporary = true;
        d.LoadState = LoadState.Loading;
        if (Timeline.CachedStructure(d.Path) is null)
        {
            var bpm = Os2l is not null ? Json.AsDouble(Os2l.DeckValue(num, "get_bpm")) : 0;
            var first = Os2l is not null ? Json.AsDouble(Os2l.DeckValue(num, "get_firstbeat")) / 1000.0 : 0;
            d.Analysing = true;
            d.Progress = 0;
            RunJob(
                Timeline.PlaceholderJob(d.Path, song, bpm, first),
                gen =>
                {
                    // Not overtaken, nor the real thing there first.
                    if (id == d.LoadId && d.Tracks.Count == 0)
                    {
                        d.Tracks = TracksFromShow(gen);
                        d.LoadState = LoadState.Loaded;
                        StateChanged?.Invoke();
                    }
                }
            );
        }
        if (_batch is null)
        {
            _batch = new TimelineBatch(Timeline) { SongLookup = SongLookup, Generator = Generated };
            if (Analyse is not null)
                _batch.Analyse = Analyse;
            _batch.Progress += (path, f) =>
            {
                foreach (var other in Decks)
                    if (other.Path == path && other.Analysing)
                        other.Progress = f;
            };
            _batch.Done += AnalysisDone;
        }
        _batch.Add([d.Path]);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Song <paramref name="path"/>'s structure is known: a show generated from it for the decks waiting on it (if any
    /// still are), saved as its temporary timelines where the show has a folder, generated on a worker thread. Returns
    /// "" or what went wrong.
    /// </summary>
    string Generated(string path, DjSong? song)
    {
        if (Waiting(path).Count == 0 || Timeline is not { } timeline)
            return "";
        if (timeline.GenerateJobFor(path, song) is not { } job)
            return "its structure hasn't been analysed";
        RunJob(
            job,
            gen =>
            {
                var waiting = Waiting(path);
                if (waiting.Count == 0)
                    return; // every deck's moved on
                var tracks = TracksFromShow(gen);
                var store = StoreProvider();
                var note = "not saved: the show hasn't been saved";
                if (store is not null && ReferenceEquals(store, timeline.Store))
                {
                    var err = timeline.PutGenerated(path, gen, temporary: true);
                    note = err == "" ? "" : "not saved: " + err;
                }
                foreach (var d in waiting)
                {
                    d.Tracks = tracks;
                    d.Analysing = false;
                    d.LoadState = LoadState.Loaded;
                    d.Note = note;
                }
                StateChanged?.Invoke();
            }
        );
        return "";
    }

    /// <summary>
    /// The decks with song <paramref name="path"/> still waiting for its generated show.
    /// </summary>
    List<Deck> Waiting(string path) => [.. Decks.Where(d => d.Path == path && d.Temporary && (d.Analysing || d.LoadState == LoadState.Loading))];

    /// <summary>
    /// Runs a generating job on a worker thread, then <paramref name="done"/> with its show back in
    /// <see cref="Update"/>.
    /// </summary>
    void RunJob(GenerateJob job, Action<GeneratedShow> done)
    {
        _pending++;
        Task.Run(() =>
        {
            try
            {
                var gen = SongTimelines.RunJob(job);
                _posted.Enqueue(() =>
                {
                    _pending--;
                    done(gen);
                });
            }
            catch (Exception e)
            {
                Trace.TraceWarning($"Couldn't generate temporary timelines: {e.Message}");
                _posted.Enqueue(() => _pending--);
            }
        });
    }

    /// <summary>
    /// Whether temporary timelines are still being made (analysed, generated or saved).
    /// </summary>
    public bool IsGenerating => _pending > 0 || _batch?.IsBusy == true || Timeline?.IsSaving == true;

    /// <summary>
    /// The analysis (or generating) of song <paramref name="path"/> finished; with an error, the decks waiting on it
    /// keep their stand-in.
    /// </summary>
    void AnalysisDone(string path, string error)
    {
        if (error == "")
            return;
        foreach (var d in Decks)
            if (d.Path == path && d.Temporary && (d.Analysing || d.LoadState == LoadState.Loading))
            {
                if (d.LoadState == LoadState.Loading && !d.Analysing)
                    d.LoadState = LoadState.NoTimelines; // (a stand-in still on its way: loaded when it's here)
                d.Analysing = false;
                d.Note = error;
            }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// A generated show as a deck's tracks: only those with something on them.
    /// </summary>
    public static Dictionary<string, TimelineTrack> TracksFromShow(GeneratedShow gen)
    {
        var tracks = new Dictionary<string, TimelineTrack>();
        foreach (var (key, c) in gen.Show)
        {
            var track = new TimelineTrack
            {
                Key = key,
                DimmerNodes = [.. c.Dimmer],
                ColourSegments = [.. c.Colour],
                StrobeSegments = [.. c.Strobe],
                PositionSegments = [.. c.Position],
                MovementSegments = [.. c.Movement],
            };
            if (HasRows(track))
                tracks[key] = track;
        }
        return tracks;
    }

    /// <summary>
    /// Whether every deck's timelines have finished loading (temporary ones made, the stand-ins at least).
    /// </summary>
    public bool LoadsDone() => Decks.All(d => d.LoadState != LoadState.Loading);

    // ---------------------------------------------------------------- output --

    /// <summary>
    /// The DMX layer: the live decks' timelines, mixed.
    /// </summary>
    public IReadOnlyList<Dictionary<int, int>> Layer(int n)
    {
        var fixtures = FixturesProvider();
        var bases = Bases(_dmx, n);
        var now = Now();
        var outputs = new List<IReadOnlyList<Dictionary<int, int>>>();
        foreach (var d in Decks)
            if (d.Live)
                outputs.Add(SongOutput.Of(d.Tracks, d.Position(now), fixtures, bases, n, Positions));
        return outputs.Count == 0 ? SongOutput.Empty(n) : SongOutput.Mix(outputs, SongOutput.AverageChannels(fixtures, n), n);
    }

    /// <summary>
    /// Each universe's levels as the console has them, under the playback layers.
    /// </summary>
    internal static List<byte[]> Bases(DmxOutput dmx, int n) => [.. Enumerable.Range(0, n).Select(i => dmx.GetUniverse(i)?.Data ?? [])];
}

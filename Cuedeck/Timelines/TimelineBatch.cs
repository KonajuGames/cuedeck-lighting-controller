using System.Collections.Concurrent;
using Cuedeck.Audio;
using Cuedeck.Dj;

namespace Cuedeck.Timelines;

/// <summary>
/// What <see cref="TimelineBatch"/> needs of the timelines it fills: DJ Edit Mode's structure cache and its Generate.
/// </summary>
interface ISongTimelines
{
    /// <summary>
    /// Song <paramref name="path"/>'s structure analysed this session (its edits on it), or null.
    /// </summary>
    SongAnalysis? CachedStructure(string path);

    /// <summary>
    /// Takes song <paramref name="path"/>'s freshly analysed structure (VirtualDJ's grid on it, with
    /// <paramref name="song"/>), kept as if the song had been opened.
    /// </summary>
    void AddStructure(string path, SongAnalysis analysis, DjSong? song);

    /// <summary>
    /// Generates a show into song <paramref name="path"/>'s saved timelines, as Generate would (a new variation each
    /// time). Returns "" or what went wrong.
    /// </summary>
    string GenerateInto(string path, DjSong? song);
}

/// <summary>
/// Regenerates the lighting timelines of a list of songs (the song list's Regenerate Lighting Timelines), a few at a
/// time: each song's structure analysed on a worker thread of its own (a song already analysed this session goes
/// straight through), then a show generated into its saved timelines (<see cref="ISongTimelines.GenerateInto"/>).
/// <para>
/// The analyses run in the background; everything else, the events included, happens in <see cref="Update"/>, which
/// the UI calls each frame. <see cref="Progress"/> (path, 0 to 1) as each song goes (0 while it waits its turn),
/// <see cref="Done"/> (path, error: "" when saved) when it's finished, <see cref="AllDone"/> when the last has.
/// sLight's <c>TimelineBatch</c>.
/// </para>
/// </summary>
sealed class TimelineBatch(ISongTimelines timelines)
{
    /// <summary>
    /// How much of a song's bar its analysis takes (the generating, the rest).
    /// </summary>
    public const double ANALYSIS_SHARE = 0.9;

    public event Action<string, double>? Progress;
    public event Action<string, string>? Done;
    public event Action? AllDone;

    /// <summary>
    /// A song's VirtualDJ entry (its beat grid) by path, or null.
    /// </summary>
    public Func<string, DjSong?>? SongLookup { get; set; }

    /// <summary>
    /// What's done with each song once its structure's known: (path, song) → "" or what went wrong. Unset, a show is
    /// generated into its saved timelines.
    /// </summary>
    public Func<string, DjSong?, string>? Generator { get; set; }

    /// <summary>
    /// How a song is analysed (on a worker thread): <see cref="SongAnalyzer.Analyse"/> unless a test says otherwise.
    /// </summary>
    public Func<string, Action<float>, CancellationToken, SongAnalysis> Analyse { get; set; } = (path, progress, cancel) => SongAnalyzer.Analyse(path, progress, cancel);

    /// <summary>
    /// Songs analysed at once, one per worker thread: a few, leaving cores for the app.
    /// </summary>
    public int Workers { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>
    /// A song under way: its analysis's cancel switch, or null while it's being generated.
    /// </summary>
    sealed class Job(DjSong? song)
    {
        public DjSong? Song { get; } = song;
        public CancellationTokenSource? Cancel { get; set; }
    }

    readonly List<string> _queue = [];
    readonly Dictionary<string, Job> _running = [];

    /// <summary>
    /// What the workers have to say, for <see cref="Update"/> to hand on: progress, a finished analysis, a failure.
    /// </summary>
    readonly ConcurrentQueue<(string Path, Job Job, float Progress, SongAnalysis? Analysis, string? Error)> _posted = new();

    /// <summary>
    /// Songs ready to generate (or to finish with an error) on the next <see cref="Update"/>.
    /// </summary>
    readonly List<(string Path, string? Error)> _deferred = [];

    /// <summary>
    /// Queues <paramref name="paths"/> (those not already queued or under way), and starts.
    /// </summary>
    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_queue.Contains(path) || _running.ContainsKey(path))
                continue;
            _queue.Add(path);
            Progress?.Invoke(path, 0);
        }
        Pump();
    }

    /// <summary>
    /// Whether <paramref name="path"/> is queued or under way.
    /// </summary>
    public bool Has(string path) => _queue.Contains(path) || _running.ContainsKey(path);

    public bool IsBusy => _queue.Count > 0 || _running.Count > 0;

    /// <summary>
    /// Songs being analysed or generated just now.
    /// </summary>
    public int Running => _running.Count;

    /// <summary>
    /// Stops: nothing more is generated (an analysis under way is told to stop, and its result isn't used).
    /// </summary>
    public void Cancel()
    {
        _queue.Clear();
        foreach (var job in _running.Values)
            job.Cancel?.Cancel();
        _running.Clear();
        _deferred.Clear();
    }

    /// <summary>
    /// Hands on what the workers have done, and generates the songs that are ready: each frame, on the UI thread.
    /// </summary>
    public void Update()
    {
        while (_posted.TryDequeue(out var msg))
        {
            if (!_running.TryGetValue(msg.Path, out var job) || job != msg.Job)
                continue; // cancelled
            if (msg.Error is not null)
                Finish(msg.Path, msg.Error);
            else if (msg.Analysis is not null)
            {
                timelines.AddStructure(msg.Path, msg.Analysis, job.Song);
                job.Cancel = null;
                Generate(msg.Path);
            }
            else
                Progress?.Invoke(msg.Path, msg.Progress * ANALYSIS_SHARE);
        }
        var ready = _deferred.ToList();
        _deferred.Clear();
        foreach (var (path, error) in ready)
        {
            if (error is not null)
                Finish(path, error);
            else
                Generate(path);
        }
    }

    void Pump()
    {
        while (_running.Count < Workers && _queue.Count > 0)
        {
            var path = _queue[0];
            _queue.RemoveAt(0);
            Start(path);
        }
    }

    void Start(string path)
    {
        var job = new Job(SongLookup?.Invoke(path));
        _running[path] = job;
        if (timelines.CachedStructure(path) is not null)
        {
            _deferred.Add((path, null));
            return;
        }
        if (!File.Exists(path))
        {
            _deferred.Add((path, "file not found"));
            return;
        }
        var cancel = new CancellationTokenSource();
        job.Cancel = cancel;
        var analyse = Analyse;
        Task.Run(() =>
        {
            try
            {
                var a = analyse(path, f => _posted.Enqueue((path, job, f, null, null)), cancel.Token);
                _posted.Enqueue((path, job, 1, a, null));
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _posted.Enqueue((path, job, 0, null, e.Message.TrimEnd('.')));
            }
        });
    }

    void Generate(string path)
    {
        if (!_running.TryGetValue(path, out var job))
            return; // cancelled
        Progress?.Invoke(path, ANALYSIS_SHARE);
        Finish(path, Generator is { } gen ? gen(path, job.Song) : timelines.GenerateInto(path, job.Song));
    }

    void Finish(string path, string error)
    {
        if (!_running.Remove(path))
            return;
        if (error == "")
            Progress?.Invoke(path, 1);
        Done?.Invoke(path, error);
        Pump();
        if (!IsBusy)
            AllDone?.Invoke();
    }
}

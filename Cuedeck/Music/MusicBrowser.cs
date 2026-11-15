using System.Globalization;
using Cuedeck.Common;
using Cuedeck.Dj;
using Cuedeck.Timelines;

namespace Cuedeck.Music;

/// <summary>
/// The DJ software's song library and virtual folders (VirtualDJ's or Cuepoint's, as the app's settings say), loaded
/// the first time they're wanted.
/// </summary>
sealed class DjLibrary
{
    readonly Func<string, DjSong?> _find;

    public DjLibrary(Func<string, DjSong?> find, List<DjList> lists, string softwareName)
    {
        _find = find;
        Lists = lists;
        SoftwareName = softwareName;
    }

    /// <summary>
    /// Reads <paramref name="software"/>'s library and lists from this machine.
    /// </summary>
    public static DjLibrary Load(DjSoftware software)
    {
        if (software == DjSoftware.Cuepoint)
        {
            var cuepoint = new CuepointLibrary();
            cuepoint.LoadAll();
            return new DjLibrary(cuepoint.FindSong, CuepointLibrary.LoadLists(), AppSettings.NameOf(software));
        }
        var vdj = new VirtualDjDatabase();
        vdj.LoadAll();
        return new DjLibrary(vdj.FindSong, DjList.LoadAll(), AppSettings.NameOf(software));
    }

    /// <summary>
    /// None: no songs, no lists.
    /// </summary>
    public static DjLibrary Empty(string softwareName = "VirtualDJ") => new(_ => null, [], softwareName);

    public DjSong? FindSong(string path) => _find(path);

    public List<DjList> Lists { get; }

    /// <summary>
    /// "VirtualDJ" or "Cuepoint".
    /// </summary>
    public string SoftwareName { get; }
}

/// <summary>
/// A track listed in DJ Edit Mode: its place in the folder or list, its file, and its details, from the DJ software's
/// library where it knows the file (the values its decks use), else the list's cached copy, else the file's name.
/// </summary>
sealed record MusicRow(int Num, string Path, string File, string Title, string Artist, double Bpm, string Key, double Length, string Album, string Genre, bool Missing)
{
    /// <summary>
    /// Its title — artist.
    /// </summary>
    public string SongTitle => Artist != "" ? $"{Title} — {Artist}" : Title;

    /// <summary>
    /// Its BPM · key · length, those that are known.
    /// </summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Bpm > 0)
                parts.Add(Bpm.ToString("F1", CultureInfo.InvariantCulture) + " BPM");
            if (Key != "")
                parts.Add(Key);
            if (Length > 0)
                parts.Add(MusicBrowser.FormatTime(Length));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The heading over its timeline: its title — artist, then its BPM, key and length where they're known.
    /// </summary>
    public string Heading => Details != "" ? $"{SongTitle} · {Details}" : SongTitle;
}

/// <summary>
/// The columns of DJ Edit Mode's track list, in order.
/// </summary>
enum MusicColumn
{
    Num,
    Title,
    Artist,
    Bpm,
    Key,
    Length,
    Album,
    Genre,
    File,
}

/// <summary>
/// DJ Edit Mode's track list without the screen: the music in a folder or one of the DJ software's lists, its heading,
/// sorting, which tracks have lighting timelines in the show (most told from the timelines' index; a song that might
/// have been moved there is fingerprinted a few a frame, in <see cref="Update"/>), and regenerating or removing their
/// timelines. sLight's <c>MusicBrowserPanel</c>'s list.
/// </summary>
sealed class MusicBrowser
{
    /// <summary>
    /// Time a frame spent fingerprinting songs for their marks, milliseconds.
    /// </summary>
    const int CHECK_BUDGET_MS = 6;

    readonly SongTimelines _timelines;
    readonly Dictionary<string, bool> _hasTimelines = [];
    readonly List<string> _toCheck = [];
    readonly Dictionary<string, double> _progress = [];
    readonly Dictionary<string, string> _errors = [];
    DjLibrary? _library;

    public MusicBrowser(SongTimelines timelines, Func<DjLibrary> loadLibrary)
    {
        _timelines = timelines;
        LoadLibrary = loadLibrary;
        Batch = new TimelineBatch(timelines) { SongLookup = path => Library.FindSong(path) };
        Batch.Progress += (path, f) => _progress[path] = f;
        Batch.Done += Regenerated;
        timelines.TimelinesSaved += path =>
        {
            _hasTimelines[path] = true;
            _toCheck.Remove(path);
        };
        timelines.TimelineStoreChanged += () =>
        {
            Batch.Cancel();
            _progress.Clear();
            RefreshMarks();
        };
    }

    /// <summary>
    /// How the DJ software's library is read (the first time it's wanted).
    /// </summary>
    public Func<DjLibrary> LoadLibrary { get; set; }

    public DjLibrary Library => _library ??= LoadLibrary();

    /// <summary>
    /// How a folder's path reads in its heading (the screenshot tool's demo folder, by name only).
    /// </summary>
    public Func<string, string> ShownPath { get; set; } = path => path;

    /// <summary>
    /// Whether the library's been read yet.
    /// </summary>
    public bool IsLoaded => _library is not null;

    /// <summary>
    /// Forgets the library (the DJ software setting changed): it's read again when next wanted, and what was listed
    /// from the other one goes.
    /// </summary>
    public void ReloadLibrary()
    {
        _library = null;
        Rows = [];
        ListHeading = "";
    }

    public TimelineBatch Batch { get; }

    public List<MusicRow> Rows { get; private set; } = [];

    /// <summary>
    /// The folder or list's heading: what it is, its tracks, their total length, and how many are missing; "" before
    /// one's shown.
    /// </summary>
    public string ListHeading { get; private set; } = "";

    public MusicColumn SortColumn { get; private set; } = MusicColumn.Num;
    public bool SortAscending { get; private set; } = true;

    /// <summary>
    /// The rows changed (another folder or list, or a sort).
    /// </summary>
    public event Action? RowsChanged;

    // ----------------------------------------------------------------- lists --

    /// <summary>
    /// Lists the music in a folder on disk (not its subfolders).
    /// </summary>
    public void ShowFolder(string path)
    {
        var files = MusicFolders.AudioFiles(path);
        Rows = [.. files.Select((f, i) => Row(i + 1, Gd.PathJoin(path, f), f, null))];
        SetHeading(ShownPath(path));
        SortColumn = MusicColumn.Num;
        SortAscending = true;
        Changed();
    }

    /// <summary>
    /// Lists a virtual folder's tracks, in its order.
    /// </summary>
    public void ShowList(DjList list)
    {
        Rows = [.. list.Entries.Select((e, i) => Row(i + 1, e.Path, e.Path, e))];
        SetHeading($"{list.Name}  ({Library.SoftwareName} list)");
        SortColumn = MusicColumn.Num;
        SortAscending = true;
        Changed();
    }

    MusicRow Row(int num, string path, string file, DjListEntry? entry)
    {
        var missing = !System.IO.File.Exists(path);
        string title = "", artist = "", key = "", album = "", genre = "";
        double bpm = 0, length = 0;
        if (Library.FindSong(path) is { } song)
        {
            (title, artist, bpm, key, length, album, genre) = (song.Title, song.Artist, song.BestBpm, song.BestKey, song.Length, song.Album, song.Genre);
        }
        else if (entry is not null)
        {
            (title, artist, bpm, key, length) = (entry.Title, entry.Artist, entry.Bpm, entry.Key, entry.Length);
        }
        if (title == "")
            title = Gd.BaseName(Gd.GetFile(path));
        return new MusicRow(num, path, file, title, artist, bpm, key, length, album, genre, missing);
    }

    void SetHeading(string what)
    {
        var total = Rows.Sum(r => r.Length);
        var missing = Rows.Count(r => r.Missing);
        var text = $"{what} — {Rows.Count} track{(Rows.Count == 1 ? "" : "s")}";
        if (total > 0)
            text += " · " + FormatDuration(total);
        if (missing > 0)
            text += $" · {missing} missing";
        ListHeading = text;
    }

    void Changed()
    {
        _errors.Clear();
        RefreshMarks();
        RowsChanged?.Invoke();
    }

    public MusicRow? RowFor(string path) => Rows.FirstOrDefault(r => r.Path == path);

    /// <summary>
    /// Sorts the rows by a column; ties keep their list order.
    /// </summary>
    public void SortBy(MusicColumn column, bool ascending)
    {
        SortColumn = column;
        SortAscending = ascending;
        IComparable Key(MusicRow r) =>
            column switch
            {
                MusicColumn.Num => r.Num,
                MusicColumn.Title => r.Title,
                MusicColumn.Artist => r.Artist,
                MusicColumn.Bpm => r.Bpm,
                MusicColumn.Key => r.Key,
                MusicColumn.Length => r.Length,
                MusicColumn.Album => r.Album,
                MusicColumn.Genre => r.Genre,
                _ => r.File,
            };
        int Compare(MusicRow a, MusicRow b)
        {
            var (ka, kb) = (Key(a), Key(b));
            var c = ka is string sa && kb is string sb ? Gd.NaturalCompare(sa, sb) : ka.CompareTo(kb);
            if (c != 0)
                return ascending ? c : -c;
            return a.Num.CompareTo(b.Num);
        }
        Rows.Sort(Compare);
        RowsChanged?.Invoke();
    }

    // ------------------------------------------------------- timeline marks --

    /// <summary>
    /// Works out again which listed tracks have saved timelines in the show.
    /// </summary>
    public void RefreshMarks()
    {
        _hasTimelines.Clear();
        _toCheck.Clear();
        var store = _timelines.Store;
        foreach (var r in Rows)
        {
            var has = store is not null && !r.Missing ? store.HasSongQuick(r.Path) : HasTimelines.No;
            if (has == HasTimelines.Maybe)
                _toCheck.Add(r.Path);
            else
                _hasTimelines[r.Path] = has == HasTimelines.Yes;
        }
    }

    /// <summary>
    /// Whether the track at <paramref name="path"/> is marked as having timelines (false while it's still checked).
    /// </summary>
    public bool HasTimelinesMark(string path) => _hasTimelines.GetValueOrDefault(path);

    /// <summary>
    /// Whether its timelines are temporary ones (generated in DJ Perform Mode, not edited since).
    /// </summary>
    public bool IsTemporaryMark(string path) => HasTimelinesMark(path) && _timelines.Store?.IsTemporary(path) == true;

    public bool MarksDone => _toCheck.Count == 0;

    /// <summary>
    /// What went wrong regenerating a track's timelines, if it failed.
    /// </summary>
    public string? RegenerateError(string path) => _errors.GetValueOrDefault(path);

    /// <summary>
    /// A track's regeneration's progress, 0 to 1 (0 while it waits), or null if it's not being regenerated.
    /// </summary>
    public double? Progress(string path) => _progress.TryGetValue(path, out var f) ? f : null;

    /// <summary>
    /// Fingerprints songs for their marks, a few milliseconds' worth, and takes in the batch's progress: each frame.
    /// </summary>
    public void Update()
    {
        var store = _timelines.Store;
        var start = Environment.TickCount64;
        while (_toCheck.Count > 0 && Environment.TickCount64 - start < CHECK_BUDGET_MS)
        {
            var path = _toCheck[0];
            _toCheck.RemoveAt(0);
            _hasTimelines[path] = store is not null && store.HasSong(path);
        }
        Batch.Update();
    }

    // ------------------------------------------------------------ actions --

    /// <summary>
    /// Those of <paramref name="paths"/> whose files are there.
    /// </summary>
    public List<string> Regenerable(IEnumerable<string> paths) => [.. paths.Where(p => RowFor(p) is { Missing: false })];

    /// <summary>
    /// Regenerates the lighting timelines of the tracks at <paramref name="paths"/> (those whose files are there), a
    /// few at a time. Returns how many were queued; none without a saved show.
    /// </summary>
    public int Regenerate(IEnumerable<string> paths)
    {
        if (_timelines.Store is null)
            return 0;
        var todo = Regenerable(paths);
        Batch.Add(todo);
        return todo.Count;
    }

    void Regenerated(string path, string error)
    {
        _progress.Remove(path);
        if (error == "")
        {
            _hasTimelines[path] = true;
            _toCheck.Remove(path);
            _errors.Remove(path);
            return;
        }
        _errors[path] = error;
    }

    /// <summary>
    /// Deletes a track's lighting timelines from the show and unmarks it.
    /// </summary>
    public void RemoveTimelines(string path)
    {
        _timelines.RemoveSongTimelines(path);
        _toCheck.Remove(path);
        _hasTimelines[path] = false;
    }

    // ------------------------------------------------------------ formatting --

    /// <summary>
    /// A length as "3:45".
    /// </summary>
    public static string FormatTime(double seconds)
    {
        var s = Gd.RoundToInt(seconds);
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>
    /// A total length as "42 min" or "1h 05m".
    /// </summary>
    public static string FormatDuration(double seconds)
    {
        var m = Gd.RoundToInt(seconds / 60.0);
        return m >= 60 ? $"{m / 60}h {m % 60:00}m" : $"{m} min";
    }
}

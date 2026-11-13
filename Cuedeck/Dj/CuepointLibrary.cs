using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Dj;

/// <summary>
/// Reads Cuepoint's library files (<c>library.json</c>: each song's analysis, cue points and hand-fixed beat grid) and
/// its virtual folders (<c>.m3u8</c> playlists), as Cuepoint's docs/FORMAT.md describes them: the Cuepoint counterpart
/// of <see cref="VirtualDjDatabase"/> and <see cref="DjList"/>, giving the same <see cref="DjSong"/> and
/// <see cref="DjList"/>, so the rest of the app takes either program's library. Cuepoint keeps one library in
/// %LOCALAPPDATA%\Cuepoint (the system drive's songs, by full path) and one in \Cuepoint on each other drive with songs
/// (by path from that drive's root, so a removable drive's are found whatever its letter). Virtual folders are in
/// VirtualFolders beside each. sLight's <c>CuepointLibrary</c>.
/// </summary>
sealed class CuepointLibrary
{
    public const string APP_DIR = "Cuepoint";
    public const string LIB_FILE = "library.json";
    public const string FOLDERS_DIR = "VirtualFolders";
    public const int FORMAT_VERSION = 1;
    public const string LIST_EXT = "m3u8";

    /// <summary>
    /// Cuepoint's key tonics, as it writes them, by pitch class.
    /// </summary>
    public static readonly string[] Tonics = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    readonly Dictionary<string, DjSong> _songs = [];

    public IEnumerable<DjSong> Songs => _songs.Values;
    public int SongCount => _songs.Count;
    public List<string> Sources { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>
    /// The Cuepoint folders here that exist: the system one (in %LOCALAPPDATA%, root ""), then one per other drive
    /// (root "F:/").
    /// </summary>
    public static List<(string Dir, string Root)> FindAppDirs()
    {
        var output = new List<(string, string)>();
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        if (local != "")
        {
            var d = Gd.PathJoin(local.Replace('\\', '/'), APP_DIR);
            if (Directory.Exists(d))
                output.Add((d, ""));
        }
        var system = (Environment.GetEnvironmentVariable("SystemDrive") ?? "").ToLowerInvariant();
        foreach (var root in VirtualDjDatabase.DriveRoots())
        {
            // The system drive's songs are in the %LOCALAPPDATA% one.
            if (system != "" && root.ToLowerInvariant().StartsWith(system, StringComparison.Ordinal))
                continue;
            var d = root + APP_DIR;
            if (Directory.Exists(d))
                output.Add((d, root));
        }
        return output;
    }

    /// <summary>
    /// Finds and loads every library here: the system one, then each drive's (whose entries win). Returns how many
    /// songs there are.
    /// </summary>
    public int LoadAll()
    {
        _songs.Clear();
        Sources.Clear();
        Errors.Clear();
        foreach (var (dir, root) in FindAppDirs())
        {
            var p = Gd.PathJoin(dir, LIB_FILE);
            if (File.Exists(p))
                LoadFile(p, root);
        }
        return _songs.Count;
    }

    /// <summary>
    /// Loads one library.json, merged in (replacing songs already there). Relative keys are under
    /// <paramref name="driveRoot"/> ("F:/"); with none, only full paths are taken. Returns why it didn't load, or null.
    /// </summary>
    public string? LoadFile(string filePath, string driveRoot = "")
    {
        string? why = null;
        JsonNode? parsed = null;
        if (!File.Exists(filePath))
            why = "not found";
        else
        {
            try
            {
                parsed = Json.Parse(File.ReadAllText(filePath));
                if (parsed is not JsonObject)
                    why = "not valid JSON";
            }
            catch (System.Text.Json.JsonException)
            {
                why = "not valid JSON";
            }
            catch (IOException e)
            {
                why = $"can't open ({e.Message})";
            }
        }
        if (why is null && parsed.Int("version", 0) != FORMAT_VERSION)
            why = $"not a Cuepoint library (version {Json.Stringify(parsed.Get("version"))})";
        if (why is not null)
        {
            Errors.Add($"{filePath}: {why}");
            return why;
        }

        // The three maps are separate: each song's entries are gathered.
        var byKey = new Dictionary<string, (string Path, JsonNode? Track, JsonNode? Hotcues, JsonNode? Grid)>();
        foreach (var part in new[] { "tracks", "hotcues", "grids" })
        {
            if (parsed.Get(part) is not JsonObject m)
                continue;
            foreach (var (key, value) in m)
            {
                var path = ResolveKey(key, driveRoot);
                if (path == "")
                    continue;
                var k = VirtualDjDatabase.PathKey(path);
                var e = byKey.GetValueOrDefault(k, (path, null, null, null));
                e = part switch
                {
                    "tracks" => e with { Track = value },
                    "hotcues" => e with { Hotcues = value },
                    _ => e with { Grid = value },
                };
                byKey[k] = e;
            }
        }
        foreach (var (k, e) in byKey)
        {
            if (MakeSong(e.Path, e.Track, e.Hotcues, e.Grid) is { } s)
            {
                s.Source = filePath;
                _songs[k] = s;
            }
        }
        Sources.Add(filePath);
        return null;
    }

    public DjSong? FindSong(string filePath) => _songs.GetValueOrDefault(VirtualDjDatabase.PathKey(filePath));

    /// <summary>
    /// A song from its library entries (any may be null): the analysis (with the title, artist and album from the
    /// song's tags), the cue points and the hand-fixed grid, which replaces the analysed one. Null if none has
    /// anything to use.
    /// </summary>
    public static DjSong? MakeSong(string path, JsonNode? track, JsonNode? hotcues, JsonNode? fixedGrid)
    {
        var s = new DjSong { Path = path };
        List<(double Time, double Bpm)> markers = [];
        var downbeat = 0;
        if (track is JsonObject && !track.Has("error"))
        {
            var key = ParseKey(track.Str("key", ""));
            var grid = Markers(track.Get("grid"));
            if (key is { } k && grid.Count > 0)
            {
                markers = grid;
                downbeat = track.Int("downbeat", 0);
                s.Scanned = true;
                s.ScanKey = ShortKey(k);
                s.ScanBpm = track.Num("bpm", 0);
                s.Volume = AutoGain(track.Num("loudness", 0), track.Num("peak", 0));
            }
        }
        if (track is JsonObject)
        {
            s.FileSize = (long)track.Num("size", 0);
            s.Length = track.Num("duration", 0);
            // Tags are kept even where the analysis failed.
            s.Title = track.Str("title", "");
            s.Artist = track.Str("artist", "");
            s.Album = track.Str("album", "");
        }
        var isFixed = false;
        if (fixedGrid is JsonObject && Markers(fixedGrid.Get("markers")) is { Count: > 0 } m)
        {
            markers = m;
            downbeat = fixedGrid.Int("downbeat", 0);
            isFixed = true;
        }
        if (hotcues is JsonArray cues)
        {
            for (var i = 0; i < cues.Count; i++)
                if (cues[i] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                    s.Cues.Add(new HotCue(i + 1, Json.AsDouble(v), ""));
        }
        if (markers.Count == 0 && s.Cues.Count == 0 && s.Title == "" && s.Artist == "" && s.Album == "")
            return null;
        if (markers.Count > 0)
        {
            SetGrid(s, markers, Gd.PosMod(downbeat, 4));
            if (isFixed || s.ScanBpm <= 0)
                s.ScanBpm = MainTempo(markers, s.Length);
        }
        return s;
    }

    /// <summary>
    /// Grid markers that are fit to use (times rising, tempos above 0), or none.
    /// </summary>
    static List<(double Time, double Bpm)> Markers(JsonNode? v)
    {
        var output = new List<(double, double)>();
        if (v is not JsonArray a)
            return output;
        foreach (var m in a)
        {
            if (m is not JsonObject)
                return [];
            var t = m.Num("time", 0);
            var bpm = m.Num("bpm", 0);
            if (bpm <= 0 || output.Count > 0 && t <= output[^1].Item1)
                return [];
            output.Add((t, bpm));
        }
        return output;
    }

    /// <summary>
    /// Cuepoint's markers as grid runs, one a marker with the last running on to the end, and a run of the first
    /// marker's tempo in front back towards 0 s (Cuepoint's grid runs on backwards before its first marker). The
    /// downbeat moves with it.
    /// </summary>
    static void SetGrid(DjSong s, List<(double Time, double Bpm)> markers, int downbeat)
    {
        var segments = new List<GridSegment>();
        var first = markers[0];
        var spb0 = 60 / first.Bpm;
        var lead = (int)Math.Floor(first.Time / spb0 + 1e-6);
        if (lead > 0)
            segments.Add(new GridSegment(first.Time - lead * spb0, lead, spb0));
        for (var i = 0; i < markers.Count; i++)
        {
            var spb = 60 / markers[i].Bpm;
            var n = i < markers.Count - 1 ? Math.Max(Gd.RoundToInt((markers[i + 1].Time - markers[i].Time) / spb), 1) : 0;
            segments.Add(new GridSegment(markers[i].Time, n, spb));
        }
        s.GridMode = 0;
        s.GridSegments = segments;
        s.Downbeat = Gd.PosMod(downbeat + lead, 4);
    }

    /// <summary>
    /// The song's main tempo (FORMAT.md 1.4): one marker's, or for several, the median over time (each marker's
    /// tempo weighted by how long it lasts, from 0 for the first to <paramref name="duration"/> for the last).
    /// </summary>
    public static double MainTempo(List<(double Time, double Bpm)> markers, double duration)
    {
        if (markers.Count == 0)
            return 0;
        if (markers.Count == 1)
            return markers[0].Bpm;
        var spans = new List<(double Bpm, double Weight)>();
        var total = 0.0;
        for (var i = 0; i < markers.Count; i++)
        {
            var a = i == 0 ? 0 : markers[i].Time;
            var b = i == markers.Count - 1 ? duration : markers[i + 1].Time;
            var w = Math.Max(b - a, 0);
            spans.Add((markers[i].Bpm, w));
            total += w;
        }
        if (total <= 0)
            return markers[0].Bpm;
        var run = 0.0;
        foreach (var sp in spans.OrderBy(x => x.Bpm))
        {
            run += sp.Weight;
            if (run >= total * 0.5)
                return sp.Bpm;
        }
        return spans.Max(x => x.Bpm);
    }

    /// <summary>
    /// Cuepoint's automatic gain (FORMAT.md 1.3) as a factor: towards -14 LUFS, a boost no more than the peak allows.
    /// </summary>
    public static double AutoGain(double loudness, double peak)
    {
        var gainDb = Math.Clamp(-14 - loudness, -24, 12);
        if (gainDb > 0 && peak > 0)
            gainDb = Math.Max(0, Math.Min(gainDb, -20 * Math.Log10(peak)));
        return Math.Pow(10, gainDb / 20);
    }

    /// <summary>
    /// A Cuepoint key ("Eb minor") as a root (0 to 11) and "major" or "minor"; null if it isn't one.
    /// </summary>
    public static (int Root, string Mode)? ParseKey(string text)
    {
        var parts = text.Split(' ');
        if (parts.Length != 2 || parts[1] is not ("major" or "minor"))
            return null;
        var root = Array.IndexOf(Tonics, parts[0]);
        return root < 0 ? null : (root, parts[1]);
    }

    /// <summary>
    /// A key's short name, VirtualDJ's notation too: the tonic, and "m" for minor ("Ebm", "F").
    /// </summary>
    public static string ShortKey((int Root, string Mode) key) => Tonics[key.Root] + (key.Mode == "minor" ? "m" : "");

    /// <summary>
    /// Every virtual folder here, as lists of paths, by name. Folders of one name on several drives get the drive
    /// after it, as Cuepoint shows them ("Warm up (F:)"). Ones that can't be read are left out, with why added to
    /// <paramref name="errors"/>.
    /// </summary>
    public static List<DjList> LoadLists(List<string>? errors = null)
    {
        errors ??= [];
        var lists = new List<DjList>();
        foreach (var (dir, _) in FindAppDirs())
        {
            var folder = Gd.PathJoin(dir, FOLDERS_DIR);
            if (!Directory.Exists(folder))
                continue;
            foreach (var f in Directory.GetFiles(folder).Order(StringComparer.Ordinal))
                if (Path.GetExtension(f).Equals("." + LIST_EXT, StringComparison.OrdinalIgnoreCase) && LoadM3u8(f.Replace('\\', '/'), errors) is { } list)
                    lists.Add(list);
        }
        var count = lists.GroupBy(l => l.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
        foreach (var l in lists)
            if (count[l.Name.ToLowerInvariant()] > 1)
                l.Name += l.Path.Length >= 2 && l.Path[1] == ':' ? $" ({l.Path[..2]})" : "";
        return [.. lists.OrderBy(l => l.Name, Comparer<string>.Create(Gd.NaturalCompare))];
    }

    /// <summary>
    /// Reads one <c>.m3u8</c> virtual folder (FORMAT.md 2.2): a path a line, comments and blanks skipped, relative ones
    /// from the file's folder, each song once (in any case), in order. Songs not on disk stay listed. Null (and why in
    /// <paramref name="errors"/>) if it can't be read.
    /// </summary>
    public static DjList? LoadM3u8(string filePath, List<string>? errors = null)
    {
        errors ??= [];
        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{filePath}: can't open ({e.Message})");
            return null;
        }
        var list = new DjList { Path = filePath, Name = Gd.BaseName(filePath) };
        var folder = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
        var seen = new HashSet<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF').Trim();
            if (line == "" || line.StartsWith('#'))
                continue;
            var p = line.Replace('\\', '/');
            if (!IsAbsolute(p))
                p = Gd.PathJoin(folder, p);
            p = Gd.SimplifyPath(p);
            if (!seen.Add(VirtualDjDatabase.PathKey(p)))
                continue;
            list.Entries.Add(new DjListEntry { Path = p, Index = list.Entries.Count });
        }
        return list;
    }

    /// <summary>
    /// A library key as a full path: a full one as it is, a relative one under <paramref name="driveRoot"/>; "" for a
    /// relative one with no root.
    /// </summary>
    public static string ResolveKey(string key, string driveRoot)
    {
        var p = key.Replace('\\', '/');
        if (IsAbsolute(p))
            return Gd.SimplifyPath(p);
        return driveRoot == "" ? "" : Gd.SimplifyPath(Gd.PathJoin(driveRoot.Replace('\\', '/'), p));
    }

    static bool IsAbsolute(string p) => p.StartsWith('/') || p.Length >= 2 && p[1] == ':';
}

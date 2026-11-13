using System.Xml;
using Cuedeck.Common;

namespace Cuedeck.Dj;

/// <summary>
/// A cue point: its number (from 1), its place in seconds, and its name.
/// </summary>
sealed record HotCue(int Number, double Position, string Name);

/// <summary>
/// A named point in a song (VirtualDJ's remix points, such as "Break 1").
/// </summary>
sealed record RemixPoint(string Name, double Position);

/// <summary>
/// A run of evenly spaced beats in a variable-tempo grid: its first beat in seconds, how many beats (0 for the last,
/// which runs on to the end of the song), and seconds a beat.
/// </summary>
sealed record GridSegment(double Start, int Beats, double SecondsPerBeat);

/// <summary>
/// One song as a DJ program knows it: its tags, its analysis (tempo, key, beat grid, gain) and its cue points. Times
/// are in seconds from the start of the file. sLight's <c>VirtualDjDatabase.Song</c>, which Cuepoint's library fills
/// too.
/// </summary>
sealed class DjSong
{
    /// <summary>
    /// BeatGrid segment starts are stored to about five significant digits (±0.05 s for times of 1000 s and up), so
    /// <see cref="BpmAt"/> lets a time this far before a segment's start still count as inside it.
    /// </summary>
    public const double GRID_START_SLACK = 0.05;

    /// <summary>
    /// The &lt;Song Flag&gt; bit VirtualDJ sets on video files.
    /// </summary>
    public const int SONG_FLAG_VIDEO = 64;

    /// <summary>
    /// The file, with forward slashes.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The database or library file it came from.
    /// </summary>
    public string Source { get; set; } = "";
    public long FileSize { get; set; }
    public int Flags { get; set; }

    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Album { get; set; } = "";
    public string Composer { get; set; } = "";
    public string Remix { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>
    /// As stored, such as "1/14".
    /// </summary>
    public string TrackNumber { get; set; } = "";
    public string Year { get; set; } = "";

    /// <summary>
    /// The key tagged, in VirtualDJ's notation ("C#m", "Eb").
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// The tempo tagged, in beats a minute (0 for unknown).
    /// </summary>
    public double Bpm { get; set; }
    public int Rating { get; set; }
    public string Comment { get; set; } = "";

    public double Length { get; set; }
    public int Bitrate { get; set; }
    public int PlayCount { get; set; }

    /// <summary>
    /// Unix times.
    /// </summary>
    public long FirstSeen { get; set; }
    public long LastModified { get; set; }
    public long LastPlay { get; set; }

    /// <summary>
    /// Whether the DJ program has analysed it (the tags above may be edits).
    /// </summary>
    public bool Scanned { get; set; }
    public double ScanBpm { get; set; }

    /// <summary>
    /// The half- or double-time alternative.
    /// </summary>
    public double ScanAltBpm { get; set; }

    /// <summary>
    /// A beat's place in seconds (not always the first: see <see cref="FirstBeat"/>).
    /// </summary>
    public double Phase { get; set; }

    /// <summary>
    /// The automatic gain, as a factor.
    /// </summary>
    public double Volume { get; set; } = 1;
    public string ScanKey { get; set; } = "";

    /// <summary>
    /// The BeatGrid's prefix (seen: 1, 9644; 0 for Cuepoint's), or -1 for none.
    /// </summary>
    public int GridMode { get; set; } = -1;

    /// <summary>
    /// The variable-tempo grid, when there's one (long mixes, tracks with tempo drift).
    /// </summary>
    public List<GridSegment> GridSegments { get; set; } = [];

    /// <summary>
    /// &lt;Scan Rigid&gt;, seconds a beat; what it means isn't known.
    /// </summary>
    public double Rigid { get; set; }

    /// <summary>
    /// Which beat of the grid (its first as 0) starts a bar: VirtualDJ's grids start on one (0), Cuepoint's say.
    /// </summary>
    public int Downbeat { get; set; }

    /// <summary>
    /// Beat anchors' places.
    /// </summary>
    public List<double> BeatGrid { get; } = [];
    public List<HotCue> Cues { get; set; } = [];

    /// <summary>
    /// Automix points by name ("realStart", "fadeEnd", …).
    /// </summary>
    public Dictionary<string, double> Automix { get; } = [];
    public List<RemixPoint> RemixPoints { get; set; } = [];

    public bool IsVideo => (Flags & SONG_FLAG_VIDEO) != 0;

    /// <summary>
    /// The best-known tempo: the analysis's (what the decks play at), else the tag's. For a variable-tempo grid,
    /// <see cref="BpmAt"/> gives it at a given point.
    /// </summary>
    public double BestBpm => ScanBpm > 0 ? ScanBpm : Bpm;

    /// <summary>
    /// The best-known key, in VirtualDJ's notation.
    /// </summary>
    public string BestKey => Key != "" ? Key : ScanKey;

    public bool HasBeatGrid => GridSegments.Count > 0;

    /// <summary>
    /// The first beat in seconds: the grid's downbeat when there's a grid (its phase can be far in), else the first
    /// beat anchor, else the phase. What VirtualDJ reports live as <c>get_firstbeat</c> (in milliseconds).
    /// </summary>
    public double FirstBeat
    {
        get
        {
            if (HasBeatGrid)
            {
                // The downbeat-th beat of the grid, which may be in a later segment.
                var b = Downbeat;
                for (var i = 0; i < GridSegments.Count; i++)
                {
                    var seg = GridSegments[i];
                    if (b < seg.Beats || seg.Beats == 0 || i == GridSegments.Count - 1)
                        return seg.Start + b * seg.SecondsPerBeat;
                    b -= seg.Beats;
                }
            }
            return BeatGrid.Count > 0 ? BeatGrid[0] : Phase;
        }
    }

    /// <summary>
    /// Where <see cref="FirstBeat"/> falls in <paramref name="beats"/> (a <see cref="BeatTimes"/> list): the nearest
    /// beat's index, or negative when it's before the first listed (VirtualDJ can put it just before 0 s). Bars count
    /// from this beat.
    /// </summary>
    public int FirstBeatIndex(IReadOnlyList<float> beats)
    {
        if (beats.Count == 0)
            return 0;
        var fb = (float)FirstBeat;
        if (fb < beats[0] && beats.Count > 1)
            return -Gd.RoundToInt((beats[0] - fb) / (beats[1] - beats[0]));
        var i = Math.Clamp(LowerBound(beats, fb), 0, beats.Count - 1);
        if (i > 0 && Math.Abs(beats[i - 1] - fb) < Math.Abs(beats[i] - fb))
            i--;
        return i;
    }

    /// <summary>
    /// Godot's <c>bsearch()</c>: where <paramref name="value"/> would go in the sorted list, before any equal.
    /// </summary>
    static int LowerBound(IReadOnlyList<float> list, float value)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// The tempo at <paramref name="t"/> seconds: with a variable-tempo grid, the declared tempo of the run playing
    /// then (what VirtualDJ reports for the deck), not one from the gap between two beats, which the grid's rounded
    /// starts make jittery. Otherwise <see cref="BestBpm"/>. 0 for unknown.
    /// </summary>
    public double BpmAt(double t)
    {
        if (!HasBeatGrid)
            return BestBpm;
        var spb = GridSegments[0].SecondsPerBeat;
        foreach (var seg in GridSegments)
        {
            // Starts are rounded to about five significant digits, so a beat can land just before its run's start.
            if (seg.Start > t + GRID_START_SLACK)
                break;
            spb = seg.SecondsPerBeat;
        }
        return spb > 0 ? 60 / spb : 0;
    }

    /// <summary>
    /// Every beat in seconds, ascending (as 32-bit floats, as sLight kept them): the variable-tempo grid's when
    /// there's one, else a steady one at <see cref="BestBpm"/> locked to <see cref="FirstBeat"/> (reaching back
    /// towards 0 as well as on), up to <paramref name="until"/> seconds (the song's length by default). None when
    /// the tempo's unknown.
    /// </summary>
    public List<float> BeatTimes(double until = -1)
    {
        var output = new List<float>();
        var end = until >= 0 ? until : Length;
        if (HasBeatGrid)
        {
            var last = GridSegments.Count - 1;
            for (var i = 0; i < GridSegments.Count; i++)
            {
                var seg = GridSegments[i];
                var n = seg.Beats;
                // The last run (count 0) runs on to the end of the song.
                if (i == last && n == 0 && seg.SecondsPerBeat > 0)
                    n = Math.Max((int)Math.Floor((end - seg.Start) / seg.SecondsPerBeat) + 1, 1);
                for (var b = 0; b < n; b++)
                {
                    var t = seg.Start + b * seg.SecondsPerBeat;
                    if (end > 0 && t > end)
                        break;
                    output.Add((float)t);
                }
            }
            return output;
        }
        var bpm = BestBpm;
        if (bpm <= 0 || end <= 0)
            return output;
        var step = 60 / bpm;
        for (var t = Gd.FPosMod(FirstBeat, step); t <= end; t += step)
            output.Add((float)t);
        return output;
    }

    /// <summary>
    /// "Artist - Title", the title alone, or the file's name.
    /// </summary>
    public string DisplayName => Artist != "" && Title != "" ? $"{Artist} - {Title}" : Title != "" ? Title : Gd.BaseName(Path);
}

/// <summary>
/// Reads VirtualDJ's song databases (<c>database.xml</c>): each track's tags, VirtualDJ's own tempo, key and beat
/// analysis, cue points, automix points and named remix sections, so a track VirtualDJ has analysed can be lit without
/// analysing it again. VirtualDJ keeps one in Documents\VirtualDJ and one in \VirtualDJ on every drive with music (each
/// listing that drive's songs); <see cref="LoadAll"/> finds and merges them all by file path. sLight's
/// <c>VirtualDjDatabase</c>.
/// </summary>
sealed class VirtualDjDatabase
{
    public const string DB_DIR = "VirtualDJ";
    public const string DB_FILE = "database.xml";

    readonly Dictionary<string, DjSong> _songs = [];

    /// <summary>
    /// Every song loaded, in load order.
    /// </summary>
    public IEnumerable<DjSong> Songs => _songs.Values;

    public int SongCount => _songs.Count;

    /// <summary>
    /// The databases that loaded, in order.
    /// </summary>
    public List<string> Sources { get; } = [];

    /// <summary>
    /// What went wrong with the last load, to show.
    /// </summary>
    public List<string> Errors { get; } = [];

    /// <summary>
    /// Every VirtualDJ database on this machine that exists: the Documents one, then one per drive.
    /// </summary>
    public static List<string> FindDatabaseFiles()
    {
        var candidates = new List<string>();
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (docs != "")
            candidates.Add(Gd.PathJoin(Gd.PathJoin(docs.Replace('\\', '/'), DB_DIR), DB_FILE));
        foreach (var root in DriveRoots())
            candidates.Add(root + DB_DIR + "/" + DB_FILE);
        var output = new List<string>();
        var seen = new HashSet<string>();
        foreach (var c in candidates)
        {
            var p = Gd.SimplifyPath(c);
            if (seen.Add(PathKey(p)) && File.Exists(p))
                output.Add(p);
        }
        return output;
    }

    /// <summary>
    /// Every drive's root, as "C:/".
    /// </summary>
    public static IEnumerable<string> DriveRoots()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }
        foreach (var d in drives)
        {
            var root = d.Name.Replace('\\', '/');
            yield return root.EndsWith('/') ? root : root + "/";
        }
    }

    /// <summary>
    /// Finds and loads every database here. Returns how many songs there are once merged.
    /// </summary>
    public int LoadAll()
    {
        _songs.Clear();
        Sources.Clear();
        Errors.Clear();
        foreach (var p in FindDatabaseFiles())
            LoadFile(p);
        return _songs.Count;
    }

    /// <summary>
    /// Loads one database.xml, merged in. Where two list the same file, the one on the song's own drive wins (that's
    /// the one VirtualDJ writes to); otherwise the first loaded stays. False if it couldn't be read.
    /// </summary>
    public bool LoadFile(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            Errors.Add($"{dbPath}: not found");
            return false;
        }
        var sawRoot = false;
        try
        {
            using var reader = XmlReader.Create(dbPath, new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true });
            DjSong? song = null;
            var inComment = false;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var tag = reader.Name;
                        if (tag == "VirtualDJ_Database")
                            sawRoot = true;
                        else if (tag == "Song")
                        {
                            song = ReadSong(reader, dbPath);
                            if (reader.IsEmptyElement)
                            {
                                Add(song);
                                song = null;
                            }
                        }
                        else if (song is not null)
                        {
                            switch (tag)
                            {
                                case "Tags":
                                    ReadTags(reader, song);
                                    break;
                                case "Infos":
                                    ReadInfos(reader, song);
                                    break;
                                case "Scan":
                                    ReadScan(reader, song);
                                    break;
                                case "Poi":
                                    ReadPoi(reader, song);
                                    break;
                                case "Comment":
                                    inComment = !reader.IsEmptyElement;
                                    break;
                            }
                        }
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        if (inComment && song is not null)
                            song.Comment += reader.Value;
                        break;
                    case XmlNodeType.EndElement:
                        if (reader.Name == "Comment")
                        {
                            inComment = false;
                            if (song is not null)
                                song.Comment = song.Comment.Trim();
                        }
                        else if (reader.Name == "Song")
                        {
                            if (song is not null)
                                Add(song);
                            song = null;
                        }
                        break;
                }
            }
        }
        catch (XmlException e)
        {
            Errors.Add($"{dbPath}: XML parse error near line {e.LineNumber}");
        }
        catch (IOException e)
        {
            Errors.Add($"{dbPath}: can't open ({e.Message})");
            return false;
        }
        if (!sawRoot)
        {
            Errors.Add($"{dbPath}: not a VirtualDJ database");
            return false;
        }
        Sources.Add(dbPath);
        return true;
    }

    /// <summary>
    /// A music file's entry, or null. Slashes and (on Windows) letter case don't matter.
    /// </summary>
    public DjSong? FindSong(string filePath) => _songs.GetValueOrDefault(PathKey(filePath));

    static string Attr(XmlReader r, string name) => r.GetAttribute(name) ?? "";

    static DjSong ReadSong(XmlReader r, string dbPath) =>
        new()
        {
            Source = dbPath,
            Path = Attr(r, "FilePath").Replace('\\', '/'),
            FileSize = Gd.ToInt(Attr(r, "FileSize")),
            Flags = Gd.ToInt(Attr(r, "Flag")),
        };

    static void ReadTags(XmlReader r, DjSong s)
    {
        s.Artist = Attr(r, "Author");
        s.Title = Attr(r, "Title");
        s.Genre = Attr(r, "Genre");
        s.Album = Attr(r, "Album");
        s.Composer = Attr(r, "Composer");
        s.Remix = Attr(r, "Remix");
        s.Label = Attr(r, "Label");
        s.TrackNumber = Attr(r, "TrackNumber");
        s.Year = Attr(r, "Year");
        s.Key = Attr(r, "Key");
        s.Bpm = BpmOf(Attr(r, "Bpm"));
        s.Rating = Gd.ToInt(Attr(r, "Stars"));
    }

    static void ReadInfos(XmlReader r, DjSong s)
    {
        s.Length = Gd.ToFloat(Attr(r, "SongLength"));
        s.Bitrate = Gd.ToInt(Attr(r, "Bitrate"));
        s.PlayCount = Gd.ToInt(Attr(r, "PlayCount"));
        s.FirstSeen = Gd.ToInt(Attr(r, "FirstSeen"));
        s.LastModified = Gd.ToInt(Attr(r, "LastModified"));
        s.LastPlay = Gd.ToInt(Attr(r, "LastPlay"));
    }

    static void ReadScan(XmlReader r, DjSong s)
    {
        s.Scanned = true;
        s.ScanBpm = BpmOf(Attr(r, "Bpm"));
        s.ScanAltBpm = BpmOf(Attr(r, "AltBpm"));
        s.Phase = Gd.ToFloat(Attr(r, "Phase"));
        if (Attr(r, "Volume") is { Length: > 0 } volume)
            s.Volume = Gd.ToFloat(volume);
        s.ScanKey = Attr(r, "Key");
        s.Rigid = Gd.ToFloat(Attr(r, "Rigid"));
        if (ParseBeatGrid(Attr(r, "BeatGrid")) is { } grid)
        {
            s.GridMode = grid.Mode;
            s.GridSegments = grid.Segments;
        }
    }

    static void ReadPoi(XmlReader r, DjSong s)
    {
        // VirtualDJ leaves Pos out when it's 0.
        var pos = Gd.ToFloat(Attr(r, "Pos"));
        switch (Attr(r, "Type"))
        {
            case "beatgrid":
                s.BeatGrid.Add(pos);
                break;
            case "cue":
                s.Cues.Add(new HotCue(Gd.ToInt(Attr(r, "Num")), pos, Attr(r, "Name")));
                break;
            case "automix":
                if (Attr(r, "Point") is { Length: > 0 } point)
                    s.Automix[point] = pos;
                break;
            case "remix":
                s.RemixPoints.Add(new RemixPoint(Attr(r, "Name"), pos));
                break;
        }
    }

    /// <summary>
    /// Adds a song, unless one from its own drive's database is there already.
    /// </summary>
    public void Add(DjSong s)
    {
        if (s.Path == "")
            return;
        var k = PathKey(s.Path);
        if (_songs.TryGetValue(k, out var existing) && !(OnSameDrive(s.Source, s.Path) && !OnSameDrive(existing.Source, existing.Path)))
            return;
        s.Cues = [.. s.Cues.OrderBy(c => c.Number)];
        s.RemixPoints = [.. s.RemixPoints.OrderBy(p => p.Position)];
        _songs[k] = s;
    }

    /// <summary>
    /// VirtualDJ keeps tempo as seconds a beat (0.5 is 120 BPM).
    /// </summary>
    static double BpmOf(string secondsPerBeat)
    {
        var spb = Gd.ToFloat(secondsPerBeat);
        return spb > 0 ? 60 / spb : 0;
    }

    /// <summary>
    /// A path to look songs up by: forward slashes, and lower case on Windows, whose files don't mind case.
    /// </summary>
    public static string PathKey(string p)
    {
        var k = p.Replace('\\', '/');
        return OperatingSystem.IsWindows() ? k.ToLowerInvariant() : k;
    }

    /// <summary>
    /// Whether a database is on the same Windows drive as a song.
    /// </summary>
    static bool OnSameDrive(string dbPath, string songPath) =>
        dbPath.Length >= 2 && songPath.Length >= 2 && dbPath[1] == ':' && songPath[1] == ':' && char.ToLowerInvariant(dbPath[0]) == char.ToLowerInvariant(songPath[0]);

    /// <summary>
    /// Reads a &lt;Scan BeatGrid&gt; value, VirtualDJ's undocumented variable-tempo grid, as
    /// <c>1[8.8033:125,0.58252][81.622:138,0.58236][204.61:261]…[2052:0,0.517256,0.51726]</c>: a number (seen: 1, 9644;
    /// meaning unknown), then one [start:count,secondsPerBeat] a run of evenly spaced beats. A run without seconds a
    /// beat has the last one's; the last run has count 0 (it goes on to the end) and sometimes a third value, ignored.
    /// Null for an empty or broken one.
    /// </summary>
    public static (int Mode, List<GridSegment> Segments)? ParseBeatGrid(string text)
    {
        var open = text.IndexOf('[');
        if (open <= 0 || !text.EndsWith(']'))
            return null;
        var prefix = text[..open];
        if (!Gd.IsValidInt(prefix))
            return null;
        var segments = new List<GridSegment>();
        var spb = 0.0;
        foreach (var chunk in text[(open + 1)..^1].Split("]["))
        {
            var colon = chunk.IndexOf(':');
            if (colon <= 0)
                return null;
            var startText = chunk[..colon];
            var fields = chunk[(colon + 1)..].Split(',');
            if (!Gd.IsValidFloat(startText) || !Gd.IsValidInt(fields[0]))
                return null;
            if (fields.Length > 1)
            {
                if (!Gd.IsValidFloat(fields[1]))
                    return null;
                spb = Gd.ToFloat(fields[1]);
            }
            var beats = Gd.ToInt(fields[0]);
            if (spb <= 0 || beats < 0)
                return null;
            segments.Add(new GridSegment(Gd.ToFloat(startText), beats, spb));
        }
        return (Gd.ToInt(prefix), segments);
    }

    static readonly Dictionary<char, int> Pitch = new()
    {
        ['C'] = 0,
        ['D'] = 2,
        ['E'] = 4,
        ['F'] = 5,
        ['G'] = 7,
        ['A'] = 9,
        ['B'] = 11,
    };

    /// <summary>
    /// A key in VirtualDJ's notation ("C#m", "Ebm", "G#", "F") as a root (0 to 11, C = 0) and "major" or "minor";
    /// null if it can't be read (Camelot's "8A", say).
    /// </summary>
    public static (int Root, string Mode)? ParseKey(string key)
    {
        var k = key.Trim();
        if (k == "" || !Pitch.TryGetValue(char.ToUpperInvariant(k[0]), out var root))
            return null;
        var i = 1;
        if (i < k.Length && k[i] is '#' or '♯')
        {
            root++;
            i++;
        }
        else if (i < k.Length && k[i] is 'b' or '♭')
        {
            root--;
            i++;
        }
        var rest = k[i..].Trim().ToLowerInvariant();
        var mode = rest.StartsWith('m') && !rest.StartsWith("maj", StringComparison.Ordinal) ? "minor" : "major";
        return (Gd.PosMod(root, 12), mode);
    }
}

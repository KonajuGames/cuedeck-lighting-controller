using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Timelines;

/// <summary>
/// Whether a folder has a song's timelines, as far as can be told without reading the song.
/// </summary>
enum HasTimelines
{
    No,
    Yes,

    /// <summary>
    /// It must be fingerprinted to tell: it may have been moved or renamed.
    /// </summary>
    Maybe,
}

/// <summary>
/// Where a show keeps its songs' lighting timelines: a folder beside the show file ("MyShow.slight" →
/// "MyShow.timelines") holding a JSON file a song, named by the song's fingerprint, and an index:
/// <code>
/// MyShow.timelines/
///     index.json        { fingerprint: {path, title, size, temporary?}, ... }
///     &lt;fingerprint&gt;.json
/// </code>
/// A song's file is <c>{ format, version, track: {path, fingerprint}, structure: {sections: [...]} (once edited),
/// temporary: true (generated in DJ Perform Mode), tracks: {"master": {...}, "&lt;uid&gt;": {...},
/// "&lt;uid&gt;/h&lt;n&gt;": {...}} }</c> (see <see cref="TimelineFormat"/>). Songs are found by fingerprint (their size
/// and a hash of their start and end), so a song that's been moved or renamed keeps its timelines. sLight's
/// <c>TimelineStore</c>, sharing its folders and fingerprints.
/// </summary>
sealed class TimelineStore(string dir = "")
{
    public const string FORMAT = "sLight timelines";
    public const int VERSION = 1;
    const int SAMPLE = 65536;

    readonly Dictionary<string, string> _fingerprints = [];
    JsonObject? _index;

    /// <summary>
    /// The folder ("" for none).
    /// </summary>
    public string Dir { get; } = dir;

    /// <summary>
    /// The timelines folder for a show file: beside it, named after it.
    /// </summary>
    public static string FolderForShow(string showPath)
    {
        var p = showPath.Replace('\\', '/');
        var slash = p.LastIndexOf('/');
        var dot = p.LastIndexOf('.');
        return (dot > slash ? p[..dot] : p) + ".timelines";
    }

    /// <summary>
    /// A song file's identity wherever it is: a SHA-256 of its size (as Godot's <c>var_to_bytes</c> wrote an int) and
    /// its first and last 64 KB, the first 16 hex digits. "" if it can't be read.
    /// </summary>
    public static string FingerprintOf(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var n = f.Length;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(VarToBytes(n));
            var buffer = new byte[SAMPLE];
            sha.AppendData(buffer, 0, ReadUpTo(f, buffer, (int)Math.Min(n, SAMPLE)));
            if (n > SAMPLE)
            {
                f.Seek(Math.Max(n - SAMPLE, SAMPLE), SeekOrigin.Begin);
                sha.AppendData(buffer, 0, ReadUpTo(f, buffer, SAMPLE));
            }
            return Convert.ToHexStringLower(sha.GetHashAndReset())[..16];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    static int ReadUpTo(Stream s, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = s.Read(buffer, total, count - total);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    /// <summary>
    /// Godot's <c>var_to_bytes()</c> of an int: its type (2) as a little-endian word, with a flag for 64 bits when it
    /// doesn't fit in 32, then the number.
    /// </summary>
    static byte[] VarToBytes(long n)
    {
        var wide = n > int.MaxValue || n < int.MinValue;
        var bytes = new byte[wide ? 12 : 8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2u | (wide ? 1u << 16 : 0));
        if (wide)
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), n);
        else
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), (int)n);
        return bytes;
    }

    public string Fingerprint(string path)
    {
        if (!_fingerprints.TryGetValue(path, out var fp))
            _fingerprints[path] = fp = FingerprintOf(path);
        return fp;
    }

    string SongFile(string fp) => Gd.PathJoin(Dir, fp + ".json");

    string IndexFile => Gd.PathJoin(Dir, "index.json");

    /// <summary>
    /// A file's JSON, or null. Opened letting a save replace it meanwhile (<see cref="WriteJson"/>), as decks read
    /// songs on other threads.
    /// </summary>
    static JsonNode? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Json.Parse(new StreamReader(stream).ReadToEnd());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a file whole or not at all: into a temporary file beside it, then swapped in, so a reader (or a crash)
    /// never sees half of it. A swap refused while something has the file open is tried a few more times.
    /// </summary>
    static bool WriteJson(string path, JsonNode data)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, Json.StringifyIndented(data));
            for (var tries = 1; ; tries++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return true;
                }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && tries < 10)
                {
                    Thread.Sleep(Math.Min(20 * tries, 100)); // about 0.7 s in all
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>
    /// The song at <paramref name="path"/>'s saved data (empty if there's none, or it's from a newer version).
    /// </summary>
    public JsonObject LoadSong(string path)
    {
        if (Dir == "")
            return [];
        var fp = Fingerprint(path);
        if (fp == "")
            return [];
        return ReadJson(SongFile(fp)) is JsonObject d && d.Int("version", 1) <= VERSION ? d : [];
    }

    /// <summary>
    /// Saves <paramref name="data"/> ({structure?, tracks, temporary?}) as the song's timelines. Returns what went
    /// wrong, or "".
    /// </summary>
    public string SaveSong(string path, JsonObject data)
    {
        var job = SaveJob(path, data);
        return job.Error ?? WriteJob(job);
    }

    /// <summary>
    /// A song's file to write, from <see cref="SaveJob"/>: where, and what (or why not).
    /// </summary>
    public sealed record Job(string File, JsonObject Data, string Dir, string? Error = null);

    /// <summary>
    /// <see cref="SaveSong"/> in two halves, so the slow one (<see cref="WriteJob"/>, writing the song's file) can go on
    /// another thread. This one updates and writes the index, and copies the data (it can change meanwhile).
    /// </summary>
    public Job SaveJob(string path, JsonObject data)
    {
        if (Dir == "")
            return new Job("", [], "", "no timelines folder");
        var fp = Fingerprint(path);
        if (fp == "")
            return new Job("", [], "", $"can't read {Path.GetFileName(path)}");
        Directory.CreateDirectory(Dir);
        var output = (JsonObject)data.DeepClone();
        output["format"] = FORMAT;
        output["version"] = VERSION;
        output["track"] = new JsonObject { ["path"] = path, ["fingerprint"] = fp };
        var idx = Index();
        var entry = new JsonObject
        {
            ["path"] = path,
            ["title"] = Gd.BaseName(path),
            ["size"] = SizeOf(path),
        };
        if (output.Bool("temporary", false))
            entry["temporary"] = true;
        idx[fp] = entry;
        WriteJson(IndexFile, idx);
        return new Job(SongFile(fp), output, Dir);
    }

    public static string WriteJob(Job job) => WriteJson(job.File, job.Data) ? "" : $"can't write to {job.Dir}";

    /// <summary>
    /// Deletes the song's timelines (its file and index entry), found by fingerprint or, if the song can't be read,
    /// by its indexed path. Returns whether there were any.
    /// </summary>
    public bool RemoveSong(string path)
    {
        if (Dir == "")
            return false;
        var idx = Index();
        var fp = Fingerprint(path);
        if (fp == "")
            fp = idx.FirstOrDefault(kv => kv.Value.Str("path") == path).Key ?? "";
        if (fp == "")
            return false;
        var had = File.Exists(SongFile(fp));
        if (had)
            File.Delete(SongFile(fp));
        if (idx.Remove(fp))
        {
            WriteJson(IndexFile, idx);
            had = true;
        }
        return had;
    }

    /// <summary>
    /// The songs this folder has timelines for: {fingerprint: {path, title, size}}, read once.
    /// </summary>
    public JsonObject Index() => _index ??= ReadJson(IndexFile) as JsonObject ?? [];

    /// <summary>
    /// Whether the song has saved timelines here, as far as can be told without reading it: Yes (the index lists it at
    /// this path), No (nothing indexed is its size, or nothing's indexed), or Maybe (<see cref="HasSong"/> must
    /// fingerprint it).
    /// </summary>
    public HasTimelines HasSongQuick(string path)
    {
        var idx = Index();
        if (idx.Count == 0)
            return HasTimelines.No;
        var size = SizeOf(path);
        var maybe = false;
        foreach (var (fp, e) in idx)
        {
            if (e is not JsonObject)
                continue;
            if (e.Str("path") == path && File.Exists(SongFile(fp)))
                return HasTimelines.Yes;
            if (!e.Has("size") || (long)e.Num("size") == size)
                maybe = true;
        }
        return maybe && size >= 0 ? HasTimelines.Maybe : HasTimelines.No;
    }

    /// <summary>
    /// Whether the song's saved timelines here are temporary ones (generated in DJ Perform Mode), as the index says:
    /// found by its indexed path, or its fingerprint once that's known.
    /// </summary>
    public bool IsTemporary(string path)
    {
        var idx = Index();
        if (_fingerprints.TryGetValue(path, out var fp) && fp != "" && idx.Get(fp) is JsonObject byFp)
            return byFp.Bool("temporary", false);
        foreach (var (_, e) in idx)
            if (e is JsonObject && e.Str("path") == path)
                return e.Bool("temporary", false);
        return false;
    }

    /// <summary>
    /// Whether the song has saved timelines here (reading part of it for its fingerprint unless
    /// <see cref="HasSongQuick"/> can tell).
    /// </summary>
    public bool HasSong(string path) =>
        HasSongQuick(path) switch
        {
            HasTimelines.Yes => true,
            HasTimelines.No => false,
            _ => Fingerprint(path) is { Length: > 0 } fp && File.Exists(SongFile(fp)),
        };

    static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Copies every file here to <paramref name="toDir"/> (Save Show As). Returns the store for the copy.
    /// </summary>
    public TimelineStore CopyTo(string toDir)
    {
        var copy = new TimelineStore(toDir);
        foreach (var (k, v) in _fingerprints)
            copy._fingerprints[k] = v;
        if (Dir == "" || toDir == Dir || !Directory.Exists(Dir))
            return copy;
        Directory.CreateDirectory(toDir);
        foreach (var file in Directory.GetFiles(Dir, "*.json"))
            File.Copy(file, Gd.PathJoin(toDir, Path.GetFileName(file)), overwrite: true);
        return copy;
    }
}

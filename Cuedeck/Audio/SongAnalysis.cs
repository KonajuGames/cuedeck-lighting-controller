using System.Globalization;
using System.Text.Json.Nodes;
using Cuedeck.Common;

namespace Cuedeck.Audio;

/// <summary>
/// What analysing a song gives: its length, tempo, key, beats and bars, its structure, and a whole-song waveform (one
/// value a column, 0 to 1: the level, and the balance of low, mid and high there). Made by <see cref="SongAnalyzer"/>,
/// used by DJ Edit Mode. sLight's <c>SongAnalysis</c>, saved the same way.
/// </summary>
sealed class SongAnalysis
{
    static readonly string[] KeyNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public string Path { get; set; } = "";

    /// <summary>
    /// Seconds.
    /// </summary>
    public double Duration { get; set; }
    public double Bpm { get; set; } = 120;

    /// <summary>
    /// The key's root pitch class (0 = C to 11 = B), and "major" or "minor".
    /// </summary>
    public int KeyRoot { get; set; }
    public string KeyMode { get; set; } = "major";

    public float[] BeatTimes { get; set; } = [];

    /// <summary>
    /// Every bar's first beat.
    /// </summary>
    public float[] Downbeats { get; set; } = [];
    public List<SongSection> Sections { get; set; } = [];

    public float[] WaveLo { get; set; } = [];
    public float[] WaveMid { get; set; } = [];
    public float[] WaveHi { get; set; } = [];
    public float[] WavePeak { get; set; } = [];

    public bool HasWave => WavePeak.Length > 0;

    /// <summary>
    /// The sections as the analyser found them, kept aside the first time a song's saved (edited) structure is put on
    /// its analysis, for when there's no edit to show. Not saved.
    /// </summary>
    public List<SongSection>? AnalysedSections { get; set; }

    /// <summary>
    /// Sections as sLight saves them: [{start, end, label, energy, cluster}].
    /// </summary>
    public static JsonArray SectionsTo(IEnumerable<SongSection> sections) =>
        new([
            .. sections.Select(s =>
                (JsonNode)
                    new JsonObject
                    {
                        ["start"] = s.Start,
                        ["end"] = s.End,
                        ["label"] = s.Label,
                        ["energy"] = s.Energy,
                        ["cluster"] = s.Cluster,
                    }
            ),
        ]);

    public static List<SongSection> SectionsFrom(JsonNode? arr) =>
        [
            .. (arr as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(s => new SongSection(s.Num("start", 0), s.Num("end", 0), s.Str("label", "Section"), Math.Clamp(s.Num("energy", 0.5), 0, 1), s.Int("cluster", 0))),
        ];

    /// <summary>
    /// The section playing at <paramref name="t"/>: the last after them all, -1 with none.
    /// </summary>
    public int SectionAt(double t)
    {
        for (var i = 0; i < Sections.Count; i++)
            if (t >= Sections[i].Start && t < Sections[i].End)
                return i;
        return Sections.Count - 1;
    }

    /// <summary>
    /// "A Minor".
    /// </summary>
    public string KeyName => $"{KeyNames[Math.Clamp(KeyRoot, 0, 11)]} {Gd.Capitalize(KeyMode)}";

    public string Summary =>
        $"{Bpm.ToString("F0", CultureInfo.InvariantCulture)} BPM · {KeyName} · {BeatTimes.Length} beats · {Downbeats.Length} bars · {Sections.Count} sections";

    public JsonObject ToJson() =>
        new()
        {
            ["path"] = Path,
            ["duration"] = Duration,
            ["bpm"] = Bpm,
            ["key_root"] = KeyRoot,
            ["key_mode"] = KeyMode,
            ["beat_times"] = new JsonArray([.. BeatTimes.Select(t => (JsonNode)(double)t)]),
            ["downbeats"] = new JsonArray([.. Downbeats.Select(t => (JsonNode)(double)t)]),
            ["sections"] = SectionsTo(Sections),
            ["wave_cols"] = WavePeak.Length,
            ["wave"] = HasWave ? Convert.ToBase64String(WaveBytes()) : "",
        };

    /// <summary>
    /// The four wave arrays a byte a value, laid out [lo…][mid…][hi…][peak…].
    /// </summary>
    byte[] WaveBytes()
    {
        var n = WavePeak.Length;
        var b = new byte[n * 4];
        static byte Q(float v) => (byte)(int)(Math.Clamp(v, 0f, 1f) * 255.0);
        for (var i = 0; i < n; i++)
        {
            b[i] = Q(WaveLo[i]);
            b[n + i] = Q(WaveMid[i]);
            b[n * 2 + i] = Q(WaveHi[i]);
            b[n * 3 + i] = Q(WavePeak[i]);
        }
        return b;
    }

    public static SongAnalysis FromJson(JsonNode? d)
    {
        var a = new SongAnalysis
        {
            Path = d.Str("path", ""),
            Duration = d.Num("duration", 0),
            Bpm = d.Num("bpm", 120),
            KeyRoot = Math.Clamp(d.Int("key_root", 0), 0, 11),
            KeyMode = d.Str("key_mode", "major"),
            BeatTimes = [.. d.Arr("beat_times").Select(t => (float)Json.AsDouble(t))],
            Downbeats = [.. d.Arr("downbeats").Select(t => (float)Json.AsDouble(t))],
        };
        a.Sections = SectionsFrom(d.Get("sections"));
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(d.Str("wave", ""));
        }
        catch (FormatException)
        {
            bytes = [];
        }
        var n = d.Int("wave_cols", 0);
        if (n > 0 && bytes.Length >= n * 4)
        {
            float[] Part(int k) => [.. Enumerable.Range(0, n).Select(i => bytes[k * n + i] / 255f)];
            (a.WaveLo, a.WaveMid, a.WaveHi, a.WavePeak) = (Part(0), Part(1), Part(2), Part(3));
        }
        return a;
    }
}

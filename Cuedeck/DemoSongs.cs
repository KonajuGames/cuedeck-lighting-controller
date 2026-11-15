using Cuedeck.Dj;

namespace Cuedeck;

/// <summary>
/// Made-up songs for the screenshot tool's DJ Edit Mode shots: WAV files synthesised with a clear structure (an intro,
/// verses, a build, choruses, a breakdown and an outro, on a steady beat) for the song analyser to find, and the
/// details a DJ library would know about them.
/// </summary>
static class DemoSongs
{
    const int RATE = 22050;

    public sealed record Song(string File, string Title, double Bpm, string Key, int Root);

    public static readonly Song[] All =
    [
        new("01 Neon Skyline.wav", "Neon Skyline", 124, "Am", 57),
        new("02 Afterglow.wav", "Afterglow", 126, "Fm", 53),
        new("03 Night Drive.wav", "Night Drive", 122, "Dm", 50),
        new("04 Heatwave.wav", "Heatwave", 128, "Gm", 55),
    ];

    /// <summary>
    /// The sections, in bars, and how loud each part plays in them: kick, bass, hats, pad, lead, the build's riser.
    /// </summary>
    static readonly (string Name, int Bars, double Kick, double Bass, double Hats, double Pad, double Lead, double Rise)[] Plan =
    [
        ("Intro", 16, 0, 0, 0.5, 0.6, 0, 0),
        ("Verse", 16, 1, 0.8, 0.7, 0.3, 0, 0),
        ("Build", 8, 0.6, 0.6, 0.8, 0.2, 0, 1),
        ("Chorus", 16, 1, 1, 1, 0.5, 1, 0),
        ("Breakdown", 8, 0, 0, 0, 0.9, 0.3, 0),
        ("Build", 8, 0.6, 0.6, 0.8, 0.2, 0, 1),
        ("Chorus", 16, 1, 1, 1, 0.5, 1, 0),
        ("Outro", 8, 0.8, 0.4, 0.5, 0.4, 0, 0),
    ];

    /// <summary>
    /// Writes every song into <paramref name="folder"/>, and returns what a DJ library would say of each, by path.
    /// </summary>
    public static Dictionary<string, DjSong> Write(string folder)
    {
        Directory.CreateDirectory(folder);
        var library = new Dictionary<string, DjSong>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in All)
        {
            var path = Path.Combine(folder, song.File);
            var length = WriteWav(path, song);
            library[Key(path)] = new DjSong
            {
                Path = path,
                Title = song.Title,
                Artist = "The Demo Band",
                Album = "Cuedeck Sessions",
                Genre = "House",
                Bpm = song.Bpm,
                Key = song.Key,
                Length = length,
            };
        }
        return library;
    }

    /// <summary>
    /// A path as the library's looked up by: forward slashes.
    /// </summary>
    public static string Key(string path) => path.Replace('\\', '/');

    static double Freq(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);

    /// <summary>
    /// Synthesises <paramref name="song"/> as 16-bit mono PCM; returns its length in seconds.
    /// </summary>
    static double WriteWav(string path, Song song)
    {
        var beat = 60 / song.Bpm;
        var bars = Plan.Sum(p => p.Bars);
        var total = (int)(bars * 4 * beat * RATE);
        var pcm = new short[total];
        // A minor-key progression: i, VI, III, VII.
        int[] chords = [0, -4, 3, -2];
        var noise = 12345u;
        double Noise()
        {
            noise = noise * 1664525 + 1013904223;
            return noise / (double)uint.MaxValue * 2 - 1;
        }
        var start = 0.0;
        foreach (var part in Plan)
        {
            var partLength = part.Bars * 4 * beat;
            var from = (int)(start * RATE);
            var to = Math.Min((int)((start + partLength) * RATE), total);
            for (var i = from; i < to; i++)
            {
                var t = i / (double)RATE;
                var inPart = t - start;
                var beatPos = inPart / beat;
                var b = (int)beatPos;
                var bt = (beatPos - b) * beat; // seconds since the beat
                var bar = b / 4;
                var root = song.Root + chords[bar % 4];
                var s = 0.0;
                // Kick on every beat: a falling sine.
                if (part.Kick > 0)
                    s += part.Kick * 0.9 * Math.Sin(2 * Math.PI * (50 * bt + 70 * 0.05 * (1 - Math.Exp(-bt / 0.05)))) * Math.Exp(-bt / 0.12);
                // Hats on the off-beats.
                var off = (beatPos + 0.5) % 1 * beat;
                if (part.Hats > 0)
                    s += part.Hats * 0.18 * Noise() * Math.Exp(-off / 0.03);
                // Bass: eighth notes on the chord's root.
                if (part.Bass > 0)
                {
                    var eighth = (beatPos * 2 % 1) * beat / 2;
                    var f = Freq(root - 24);
                    s += part.Bass * 0.35 * (2 * (t * f % 1) - 1) * Math.Exp(-eighth / 0.15);
                }
                // Pad: the chord, held.
                if (part.Pad > 0)
                    foreach (var n in new[] { 0, 3, 7 })
                        s += part.Pad * 0.08 * Math.Sin(2 * Math.PI * Freq(root + n) * t);
                // Lead: a sixteenth-note arpeggio over the chord.
                if (part.Lead > 0)
                {
                    var step = (int)(beatPos * 4);
                    var note = root + 12 + new[] { 0, 3, 7, 12 }[step % 4];
                    var sixteenth = (beatPos * 4 % 1) * beat / 4;
                    s += part.Lead * 0.16 * Math.Sign(Math.Sin(2 * Math.PI * Freq(note) * t)) * Math.Exp(-sixteenth / 0.08);
                }
                // The build: noise rising over the section, a snare roll speeding up.
                if (part.Rise > 0)
                {
                    var p = inPart / partLength;
                    var hits = p < 0.5 ? 1 : p < 0.75 ? 2 : 4;
                    var roll = (beatPos * hits % 1) * beat / hits;
                    s += part.Rise * (0.25 * p * Noise() + 0.3 * Noise() * Math.Exp(-roll / 0.04));
                }
                // A fade in and out at the ends.
                var fade = Math.Min(1, Math.Min(t / 2, (total / (double)RATE - t) / 4));
                pcm[i] = (short)Math.Clamp(s * 0.6 * fade * 32767, -32768, 32767);
            }
            start += partLength;
        }

        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + total * 2);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)1);
        file.Write(RATE);
        file.Write(RATE * 2);
        file.Write((short)2);
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(total * 2);
        foreach (var v in pcm)
            file.Write(v);
        return total / (double)RATE;
    }
}

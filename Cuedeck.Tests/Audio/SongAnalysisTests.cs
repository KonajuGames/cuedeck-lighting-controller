using System.Buffers.Binary;
using Cuedeck.Audio;

namespace Cuedeck.Tests.Audio;

/// <summary>
/// Song analysis: structure_detection_test.gd's three made-up songs (its real-file pass isn't ported, as tests don't
/// depend on what's on the machine), and the tempo, key, downbeat, WAV and saving parts on their own.
/// </summary>
public sealed class SongAnalysisTests : IDisposable
{
    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>
    /// A click track (a kick on the beat, a hat off it, at 120 BPM) and a triad, each part at its own loudness and on
    /// C major or F major: <paramref name="layout"/> is (from, to, amplitude, chord 0 or 1) over the whole song. As
    /// sLight's test made them (its hat noise seeded here, so the song's the same each run).
    /// </summary>
    string Synth(string name, int sr, (double T0, double T1, double Amp, int Chord)[] layout)
    {
        var total = layout[^1].T1;
        var n = (int)(sr * total);
        var buf = new float[n];
        var beat = sr * 0.5;
        double[] chordA = [261.63, 329.63, 392.0];
        double[] chordB = [349.23, 440.0, 523.25];
        var random = new Random(7);
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / sr;
            var (amp, chord) = (0.4, 0);
            foreach (var seg in layout)
            {
                if (t >= seg.T0 && t < seg.T1)
                {
                    (amp, chord) = (seg.Amp, seg.Chord);
                    break;
                }
            }
            double s = 0;
            var ph = i % beat;
            if (ph < sr * 0.10)
                s += 0.7 * amp * Math.Exp(-ph / (sr * 0.03)) * Math.Sin(Math.Tau * 60.0 * (ph / sr));
            var ph2 = (i + beat * 0.5) % beat;
            if (ph2 < sr * 0.03)
                s += 0.15 * amp * Math.Exp(-ph2 / (sr * 0.008)) * (random.NextDouble() * 2 - 1);
            var freqs = chord == 1 ? chordB : chordA;
            s += 0.22 * amp * (freqs.Sum(f => Math.Sin(Math.Tau * f * t)) / freqs.Length);
            buf[i] = (float)Math.Clamp(s, -1, 1);
        }
        return WriteWav(name, sr, 1, 16, buf);
    }

    string WriteWav(string name, int rate, int channels, int bits, float[] samples)
    {
        var path = _dir.File(name);
        var bytes = bits / 8;
        var data = new byte[samples.Length * bytes];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = Math.Clamp(samples[i], -1f, 1f);
            var at = data.AsSpan(i * bytes);
            switch (bits)
            {
                case 8:
                    at[0] = (byte)(v * 127 + 128);
                    break;
                case 16:
                    BinaryPrimitives.WriteInt16LittleEndian(at, (short)(v * 32767));
                    break;
                case 24:
                    var x = (int)(v * 8388607);
                    (at[0], at[1], at[2]) = ((byte)x, (byte)(x >> 8), (byte)(x >> 16));
                    break;
                case 32:
                    BinaryPrimitives.WriteSingleLittleEndian(at, v);
                    break;
            }
        }
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)(bits == 32 ? 3 : 1));
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * bytes);
        w.Write((short)(channels * bytes));
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        return path;
    }

    static Dictionary<string, int> Labels(SongAnalysis a) => a.Sections.GroupBy(s => s.Label).ToDictionary(g => g.Key, g => g.Count());

    static int Choruses(SongAnalysis a) => a.Sections.Count(s => s.Label is "Chorus" or "Drop");

    // ------------------------------------------- structure_detection_test.gd --

    /// <summary>
    /// The bug's trigger: parts alternating quiet and loud on the same chord, so only loudness can tell verse from
    /// chorus. Before sLight's fix every part read "Chorus".
    /// </summary>
    [Fact]
    public void The_same_chord_alternating_quiet_and_loud()
    {
        var a = SongAnalyzer.Analyse(Synth("1.wav", 22050, [(0, 24, 0.4, 0), (24, 40, 1.0, 0), (40, 56, 0.4, 0), (56, 72, 1.0, 0), (72, 80, 0.3, 0)]));
        Assert.True(a.Sections.Count >= 3, $"{a.Sections.Count} sections");
        Assert.True(Choruses(a) < a.Sections.Count, "not every part's a chorus");
        Assert.True(Choruses(a) > 0, "still a chorus");
        var loudLo = 1.0;
        var quietHi = 0.0;
        foreach (var s in a.Sections)
        {
            var mid = (s.Start + s.End) / 2;
            if (mid is > 24 and < 40 or > 56 and < 72)
                loudLo = Math.Min(loudLo, s.Energy);
            else
                quietHi = Math.Max(quietHi, s.Energy);
        }
        Assert.True(loudLo > quietHi, "loud parts score more energy than quiet ones");
    }

    /// <summary>
    /// Real repeats never have quite the same loudness: a little jitter between "the same" part must still group.
    /// </summary>
    [Fact]
    public void Noisy_repeats_still_group()
    {
        var a = SongAnalyzer.Analyse(Synth("2.wav", 22050, [(0, 16, 0.42, 0), (16, 32, 0.95, 0), (32, 48, 0.48, 0), (48, 64, 1.0, 0), (64, 80, 0.40, 0)]));
        Assert.True(a.Sections.Count >= 4, $"{a.Sections.Count} sections");
        var distinct = Labels(a).Count;
        Assert.True(distinct <= 3, $"{distinct} labels for {a.Sections.Count} parts");
        Assert.True(distinct >= 2, "quiet told from loud");
    }

    /// <summary>
    /// A fuller layout: intro, two verses, three choruses, a bridge on another chord, outro.
    /// </summary>
    [Fact]
    public void A_realistic_layout()
    {
        var a = SongAnalyzer.Analyse(
            Synth("3.wav", 22050, [(0, 8, 0.35, 0), (8, 24, 0.55, 0), (24, 40, 0.95, 0), (40, 56, 0.5, 0), (56, 72, 1.0, 0), (72, 88, 0.6, 1), (88, 104, 0.92, 0), (104, 112, 0.3, 0)])
        );
        Assert.True(a.Sections.Count >= 5, $"{a.Sections.Count} sections");
        Assert.True(Choruses(a) <= a.Sections.Count / 2, $"{Choruses(a)} of {a.Sections.Count} choruses");
        Assert.True(Choruses(a) >= 2, "the recurring choruses found");
    }

    [Fact]
    public void A_click_track_is_120_bpm_in_c_with_its_beats_bars_and_waveform()
    {
        var a = SongAnalyzer.Analyse(Synth("click.wav", 22050, [(0, 40, 0.8, 0)]));
        Assert.Equal(120, a.Bpm, 1.0);
        Assert.Equal(40, a.Duration, 0.01);
        Assert.Equal((0, "major"), (a.KeyRoot, a.KeyMode)); // C major
        Assert.InRange(a.BeatTimes.Length, 76, 82);
        Assert.InRange(a.Downbeats.Length, a.BeatTimes.Length / 4 - 1, a.BeatTimes.Length / 4 + 1);
        Assert.Equal((0.0, 40.0), (a.Sections[0].Start, a.Sections[^1].End));
        Assert.Equal(SongAnalyzer.WAVE_COLS, a.WavePeak.Length);
        Assert.Equal(1, a.WavePeak.Max(), 6);
        Assert.StartsWith("120 BPM · C Major", a.Summary);
    }

    [Fact]
    public void Songs_it_cant_analyse_say_why()
    {
        var shortOne = WriteWav("short.wav", 22050, 1, 16, new float[22050 * 2]);
        Assert.Equal("Couldn't analyse this track: track is too short.", Assert.Throws<InvalidDataException>(() => SongAnalyzer.Analyse(shortOne)).Message);
        Assert.Equal("Couldn't analyse this track: file not found.", Assert.Throws<InvalidDataException>(() => SongAnalyzer.Analyse(_dir.File("none.wav"))).Message);
        File.WriteAllText(_dir.File("bad.wav"), "not a wav at all, but long enough to be read as one ..........");
        Assert.Equal("Couldn't analyse this track: not a WAV file.", Assert.Throws<InvalidDataException>(() => SongAnalyzer.Analyse(_dir.File("bad.wav"))).Message);
    }

    // -------------------------------------------------------- the parts alone --

    [Fact]
    public void Tempo_from_an_onset_envelope()
    {
        // Pulses every half second at 100 Hz: 120 BPM; every 0.45 s: 133. (Perfect pulses at 150 read as 75, as in
        // sLight: the octave correction's prior around 120 takes the half when it fits as well.)
        foreach (var (spacing, want) in new[] { (50, 120.0), (45, 133.33) })
        {
            var onset = new float[3000];
            for (var i = 0; i < onset.Length; i += spacing)
                onset[i] = 1;
            Assert.Equal(want, SongDetect.EstimateTempo(onset, 100), 1.0);
            var beats = SongDetect.DpBeats(onset, 100, want);
            Assert.True(beats.Length > 20);
            Assert.All(beats.Skip(1).Zip(beats), p => Assert.Equal(spacing / 100.0, p.First - p.Second, 2));
        }
    }

    static float[] Chord(params int[] pitches)
    {
        var c = new float[12];
        foreach (var p in pitches)
            c[p % 12] = 1f / pitches.Length;
        return c;
    }

    [Fact]
    public void Key_and_downbeat_from_chroma()
    {
        // A minor's triad most, with C and E major around: A minor.
        var song = Enumerable.Repeat(Chord(9, 0, 4), 8).Concat(Enumerable.Repeat(Chord(0, 4, 7), 2)).Concat(Enumerable.Repeat(Chord(4, 8, 11), 2)).ToList();
        Assert.Equal((9, "minor"), SongDetect.DetectKey(song));
        Assert.Equal((7, "major"), SongDetect.DetectKey([Chord(7, 11, 2), Chord(0, 4, 7), Chord(2, 6, 9), Chord(7, 11, 2)])); // G

        // The chord changes on the second beat of each four: that's beat 1.
        var beats = new List<float[]>();
        for (var bar = 0; bar < 6; bar++)
            for (var b = 0; b < 4; b++)
                beats.Add(b == 1 ? Chord(bar * 5, bar * 5 + 4, bar * 5 + 7) : beats.Count > 0 ? beats[^1] : Chord(0, 4, 7));
        Assert.Equal(1, SongDetect.DownbeatPhase(beats));
        Assert.Equal(0, SongDetect.DownbeatPhase([.. beats.Take(7)])); // too few to tell
    }

    [Fact]
    public void Too_few_beats_is_one_verse()
    {
        var parts = SongDetect.Segment([.. Enumerable.Repeat(new float[15], 10)], new float[10], new float[10], 0, 30);
        Assert.Equal([new SongSection(0, 30, "Verse", 0.6, 0)], parts);
    }

    [Fact]
    public void Wavs_of_every_kind_are_read()
    {
        var ramp = Enumerable.Range(0, 8).Select(i => i / 8f - 0.5f).ToArray();
        foreach (var bits in new[] { 8, 16, 24, 32 })
        {
            var pcm = SongDecoder.DecodeWav(File.ReadAllBytes(WriteWav($"r{bits}.wav", 8000, 2, bits, ramp)));
            Assert.Equal((2, 8000, 4L), (pcm.Channels, pcm.SampleRate, pcm.Frames));
            Assert.All(pcm.Samples.Zip(ramp), p => Assert.Equal(p.Second, p.First, bits == 8 ? 0.01 : 0.0002));
        }
    }

    [Fact]
    public void An_analysis_is_saved_as_sLight_saved_it()
    {
        var a = new SongAnalysis
        {
            Path = "D:/x.mp3",
            Duration = 100,
            Bpm = 128,
            KeyRoot = 9,
            KeyMode = "minor",
            BeatTimes = [0.5f, 1, 1.5f],
            Downbeats = [0.5f],
            Sections = [new SongSection(0, 50, "Intro", 0.3, 0), new SongSection(50, 100, "Chorus", 1, 1)],
            WaveLo = [0, 0.5f],
            WaveMid = [1, 0.25f],
            WaveHi = [0.1f, 0.2f],
            WavePeak = [1, 0.75f],
        };
        var d = a.ToJson();
        Assert.Equal(2, d["wave_cols"]!.GetValue<int>());
        var b = SongAnalysis.FromJson(d);
        Assert.Equal(("D:/x.mp3", 100.0, 128.0, "A Minor"), (b.Path, b.Duration, b.Bpm, b.KeyName));
        Assert.Equal(a.BeatTimes, b.BeatTimes);
        Assert.Equal(a.Sections, b.Sections);
        Assert.Equal([0, 127 / 255f], b.WaveLo); // a byte a value, cut down
        Assert.Equal(1, b.SectionAt(75));
        Assert.Equal(1, b.SectionAt(500)); // after them all: the last
        Assert.Equal(-1, new SongAnalysis().SectionAt(1));
        Assert.False(SongAnalysis.FromJson(new System.Text.Json.Nodes.JsonObject()).HasWave);
    }
}

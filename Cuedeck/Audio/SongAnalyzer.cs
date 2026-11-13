using Cuedeck.Common;

namespace Cuedeck.Audio;

/// <summary>
/// Analyses a song file into a <see cref="SongAnalysis"/>: decodes it (<see cref="SongDecoder"/>), works out its
/// frames (<see cref="SongFeatures"/>), then its tempo and beats (<see cref="SongDetect.EstimateTempo"/>,
/// <see cref="SongDetect.DpBeats"/>), the chroma, loudness and timbre each beat, the key and downbeat, and the
/// structure (<see cref="SongDetect.Segment"/>). sLight's <c>SongAnalyzer</c> by its fast, native path, which it
/// used whenever that was built; its slow path (playing the song muted to capture it) isn't needed here.
/// </summary>
static class SongAnalyzer
{
    public const int N_FFT = 2048;
    public const int HOP = 1024;

    /// <summary>
    /// Columns in the whole-song waveform.
    /// </summary>
    public const int WAVE_COLS = 1024;

    /// <summary>
    /// Analyses <paramref name="path"/>, telling <paramref name="progress"/> how far it's got (0 to 1). Throws
    /// <see cref="InvalidDataException"/> with "Couldn't analyse this track: …" when it can't.
    /// </summary>
    public static SongAnalysis Analyse(string path, Action<float>? progress = null, CancellationToken cancel = default)
    {
        PcmAudio audio;
        try
        {
            progress?.Invoke(0.02f);
            audio = SongDecoder.Decode(path);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidDataException($"Couldn't analyse this track: {Why(e)}.", e);
        }
        cancel.ThrowIfCancellationRequested();
        SongFrames frames;
        try
        {
            frames = SongFeatures.Analyze(audio, N_FFT, HOP, WAVE_COLS, progress, cancel);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"Couldn't analyse this track: {e.Message}.", e);
        }
        return DetectFromFrames(path, frames);
    }

    /// <summary>
    /// <see cref="Analyse"/> on a worker thread.
    /// </summary>
    public static Task<SongAnalysis> AnalyseAsync(string path, Action<float>? progress = null, CancellationToken cancel = default) =>
        Task.Run(() => Analyse(path, progress, cancel), cancel);

    static string Why(Exception e) => e is FileNotFoundException ? "file not found" : e.Message.TrimEnd('.');

    /// <summary>
    /// A song's frames made into its analysis.
    /// </summary>
    public static SongAnalysis DetectFromFrames(string path, SongFrames d)
    {
        var a = new SongAnalysis
        {
            Path = path,
            Duration = d.Duration,
            WaveLo = d.WaveLo,
            WaveMid = d.WaveMid,
            WaveHi = d.WaveHi,
            WavePeak = d.WavePeak,
        };
        var chroma = new float[d.FrameCount][];
        for (var f = 0; f < d.FrameCount; f++)
            chroma[f] = d.Chroma.AsSpan(f * 12, 12).ToArray();

        var bpm = SongDetect.EstimateTempo(d.Onset, d.OnsetHz, 120);
        var beats = SongDetect.DpBeats(d.Onset, d.OnsetHz, bpm);
        if (beats.Length < 8)
        {
            var steady = new List<float>();
            for (var t = 0.0; t < a.Duration; t += 60 / bpm)
                steady.Add((float)t);
            beats = [.. steady];
        }

        // Each beat's chroma, loudness and timbre.
        var frameHz = d.Rate / HOP;
        var bchroma = new List<float[]>();
        var benergy = new float[beats.Length];
        var cen = new float[beats.Length];
        var flx = new float[beats.Length];
        for (var i = 0; i < beats.Length; i++)
        {
            double t0 = beats[i];
            var t1 = i + 1 < beats.Length ? beats[i + 1] : t0 + 60.0 / bpm;
            var f0 = (int)(t0 * frameHz);
            var f1 = Math.Max(f0 + 1, (int)(t1 * frameHz));
            bchroma.Add(AvgRows(chroma, f0, f1));
            benergy[i] = (float)Avg(d.Rms, f0, f1);
            cen[i] = (float)Avg(d.Centroid, f0, f1);
            flx[i] = (float)Avg(d.Flux, f0, f1);
        }

        (a.KeyRoot, a.KeyMode) = SongDetect.DetectKey(bchroma);
        Normalise(benergy);
        Normalise(cen);
        Normalise(flx);
        var downbeatPhase = SongDetect.DownbeatPhase(bchroma);

        var feats = new List<float[]>();
        for (var i = 0; i < beats.Length; i++)
        {
            var v = new float[15];
            for (var k = 0; k < 12; k++)
                v[k] = (float)(bchroma[i][k] * 1.6);
            v[12] = (float)(cen[i] * 0.5);
            v[13] = (float)(flx[i] * 0.5);
            v[14] = (float)(benergy[i] * 0.8);
            feats.Add(v);
        }
        var segs = SongDetect.Segment(feats, benergy, beats, downbeatPhase, a.Duration);

        a.BeatTimes = beats;
        a.Downbeats = [.. beats.Where((_, i) => i % 4 == downbeatPhase)];
        a.Sections = [.. segs.Select(s => s with { Start = Math.Clamp(s.Start, 0, a.Duration), End = Math.Clamp(s.End, 0, a.Duration) })];
        if (a.Sections.Count > 0)
        {
            a.Sections[0] = a.Sections[0] with { Start = 0 };
            a.Sections[^1] = a.Sections[^1] with { End = a.Duration };
        }

        // The tempo from the beats found: the median gap.
        var gaps = new List<double>();
        for (var i = 1; i < beats.Length; i++)
            gaps.Add((double)beats[i] - beats[i - 1]);
        gaps.Sort();
        a.Bpm = gaps.Count > 0 ? Gd.Snapped(60.0 / Math.Max(gaps[gaps.Count / 2], 0.01), 0.1) : bpm;
        return a;
    }

    static double Avg(float[] a, int lo, int hi)
    {
        lo = Math.Max(lo, 0);
        hi = Math.Min(hi, a.Length);
        if (hi <= lo)
            return 0;
        double s = 0;
        for (var i = lo; i < hi; i++)
            s += a[i];
        return s / (hi - lo);
    }

    static float[] AvgRows(float[][] rows, int lo, int hi)
    {
        lo = Math.Max(lo, 0);
        hi = Math.Min(hi, rows.Length);
        var output = new float[12];
        if (hi <= lo)
        {
            output[0] = 1;
            return output;
        }
        for (var i = lo; i < hi; i++)
            for (var k = 0; k < 12; k++)
                output[k] += rows[i][k];
        for (var k = 0; k < 12; k++)
            output[k] /= hi - lo;
        return output;
    }

    static void Normalise(float[] a)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var v in a)
        {
            lo = Math.Min(lo, v);
            hi = Math.Max(hi, v);
        }
        var span = hi - lo;
        if (span < 1e-6)
            return;
        for (var i = 0; i < a.Length; i++)
            a[i] = (float)((a[i] - lo) / span);
    }
}

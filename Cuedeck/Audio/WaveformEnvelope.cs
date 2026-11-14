namespace Cuedeck.Audio;

/// <summary>
/// A song's waveform for DJ Edit Mode's timeline, in columns of a fixed length (100 a second, so zooming in shows
/// real detail): each column's loudness and the loudness of its bass (below about 250 Hz), air (above about 2.5 kHz)
/// and what's between, split with two one-pole filters. sLight's native <c>SongFeatures.waveform_file</c>, ported.
/// </summary>
static class WaveformEnvelope
{
    /// <summary>
    /// Columns a second the timeline reads songs at.
    /// </summary>
    public const int COLS_PER_SEC = 100;

    /// <summary>
    /// The waveform of <paramref name="audio"/> as a <see cref="SongAnalysis"/> (its length and four wave arrays,
    /// each scaled so its loudest column is 1, the bands together). Throws <see cref="InvalidDataException"/> for
    /// audio with nothing in it.
    /// </summary>
    public static SongAnalysis Compute(PcmAudio audio, int colsPerSec = COLS_PER_SEC, Action<float>? progress = null, CancellationToken cancel = default)
    {
        var cps = Math.Clamp(colsPerSec, 1, 1000);
        var rate = audio.SampleRate;
        var channels = audio.Channels;
        if (rate < 1 || channels < 1 || audio.Frames == 0)
            throw new InvalidDataException("no audio in the file");
        var aLo = 1.0 - Math.Exp(-Math.Tau * 250.0 / rate);
        var aHi = 1.0 - Math.Exp(-Math.Tau * 2500.0 / rate);
        double lp1 = 0, lp2 = 0, sAll = 0, sLo = 0, sMid = 0, sHi = 0;
        long n = 0;
        var colEnd = (long)Math.Round((double)rate / cps, MidpointRounding.AwayFromZero);
        List<float> lo = [], mid = [], hi = [], pk = [];
        void Flush()
        {
            var inv = n > 0 ? 1.0 / n : 0.0;
            pk.Add((float)Math.Sqrt(sAll * inv));
            lo.Add((float)Math.Sqrt(sLo * inv));
            mid.Add((float)Math.Sqrt(sMid * inv));
            hi.Add((float)Math.Sqrt(sHi * inv));
            sAll = sLo = sMid = sHi = 0;
            n = 0;
            colEnd = (long)Math.Round((double)(pk.Count + 1) * rate / cps, MidpointRounding.AwayFromZero);
        }
        var samples = audio.Samples;
        var frames = audio.Frames;
        var invCh = 1.0 / channels;
        for (long f = 0; f < frames; f++)
        {
            double x = 0;
            var at = f * channels;
            for (var c = 0; c < channels; c++)
                x += samples[at + c];
            x *= invCh;
            lp1 += aLo * (x - lp1);
            lp2 += aHi * (x - lp2);
            double l = lp1, m = lp2 - lp1, h = x - lp2;
            sAll += x * x;
            sLo += l * l;
            sMid += m * m;
            sHi += h * h;
            n++;
            if (f + 1 >= colEnd)
                Flush();
            if ((f & 0xFFFF) == 0)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Invoke(Math.Min((float)f / frames, 0.99f));
            }
        }
        if (n > 0)
            Flush();
        float pkMax = 1e-9f, bandMax = 1e-9f;
        for (var i = 0; i < pk.Count; i++)
        {
            pkMax = Math.Max(pkMax, pk[i]);
            bandMax = Math.Max(bandMax, Math.Max(lo[i], Math.Max(mid[i], hi[i])));
        }
        for (var i = 0; i < pk.Count; i++)
        {
            pk[i] /= pkMax;
            lo[i] /= bandMax;
            mid[i] /= bandMax;
            hi[i] /= bandMax;
        }
        progress?.Invoke(1);
        return new SongAnalysis
        {
            Duration = (double)frames / rate,
            WaveLo = [.. lo],
            WaveMid = [.. mid],
            WaveHi = [.. hi],
            WavePeak = [.. pk],
        };
    }
}

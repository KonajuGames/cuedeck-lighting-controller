namespace Cuedeck.Audio;

/// <summary>
/// A song's analysis frames: per short-time FFT frame its chroma (12 pitch classes, summing to 1), loudness, spectral
/// centroid and flux; an onset envelope; and a whole-song waveform. What <see cref="SongAnalyzer"/> works the
/// structure out from.
/// </summary>
sealed record SongFrames(
    double Rate,
    double Duration,
    int FrameCount,
    float[] Chroma,
    float[] Rms,
    float[] Centroid,
    float[] Flux,
    float[] Onset,
    double OnsetHz,
    float[] WaveLo,
    float[] WaveMid,
    float[] WaveHi,
    float[] WavePeak
);

/// <summary>
/// Turns decoded audio into <see cref="SongFrames"/>: downmixed to mono, brought down to about 11 kHz, then a
/// short-time FFT, an onset envelope and the waveform's columns. A port of sLight's native <c>SongFeatures</c>
/// (song_features.cpp), which its analysis uses whenever it's built.
/// </summary>
static class SongFeatures
{
    /// <summary>
    /// The onset envelope's hop, in working samples.
    /// </summary>
    public const int ONSET_DECIM = 128;

    /// <summary>
    /// The working rate aimed for (44100 / 4).
    /// </summary>
    const double WORK_RATE = 11025.0;

    public const double MAX_SECONDS = 3600.0;

    /// <summary>
    /// Works out <paramref name="audio"/>'s frames, telling <paramref name="progress"/> how far it's got (0 to 1).
    /// Throws <see cref="InvalidDataException"/> with why, for a song too short or long.
    /// </summary>
    public static SongFrames Analyze(PcmAudio audio, int nFft, int hop, int waveCols, Action<float>? progress = null, CancellationToken cancel = default)
    {
        if (nFft < 256 || (nFft & (nFft - 1)) != 0 || hop < 1 || waveCols < 16)
            throw new ArgumentException("bad analysis parameters");
        var framesNative = audio.Frames;
        var rateNative = audio.SampleRate;
        var duration = (double)framesNative / rateNative;
        if (duration < 5.0)
            throw new InvalidDataException("track is too short");
        if (duration > MAX_SECONDS)
            throw new InvalidDataException("track is too long");
        progress?.Invoke(0.28f);

        // Downmixed to mono.
        var c = audio.Channels;
        var mono = new float[framesNative];
        if (c == 1)
            Array.Copy(audio.Samples, mono, framesNative);
        else
        {
            var inv = 1.0 / c;
            for (long i = 0; i < framesNative; i++)
            {
                double acc = 0;
                for (var ch = 0; ch < c; ch++)
                    acc += audio.Samples[i * c + ch];
                mono[i] = (float)(acc * inv);
            }
        }

        // Down to the working rate.
        var factor = Math.Max(1, (int)Math.Round(rateNative / WORK_RATE, MidpointRounding.AwayFromZero));
        var workRate = (double)rateNative / factor;
        var w = HannDecimate(mono, factor);
        progress?.Invoke(0.35f);
        var l = w.Length;
        if (l < workRate * 3.0)
            throw new InvalidDataException("not enough audio after decode");

        // The short-time FFT.
        var half = nFft / 2 + 1;
        var binPc = new int[half];
        var binBand = new int[half];
        for (var k = 0; k < half; k++)
        {
            var f = (double)k * workRate / nFft;
            if (f < 55.0 || f > 2000.0)
                binPc[k] = -1;
            else
            {
                var midi = (int)Math.Floor(69.0 + 12.0 * Math.Log2(f / 440.0) + 0.5);
                binPc[k] = (midi % 12 + 12) % 12;
            }
            binBand[k] = f < 250.0 ? 0 : f < 2500.0 ? 1 : 2;
        }
        var nframes = Math.Max(1, (l - nFft) / hop);
        var chroma = new float[nframes * 12];
        var rms = new float[nframes];
        var centroid = new float[nframes];
        var flux = new float[nframes];
        var blo = new float[nframes];
        var bmid = new float[nframes];
        var bhi = new float[nframes];
        var re = new double[nFft];
        var im = new double[nFft];
        var win = new float[nFft];
        var invn = 1.0 / (nFft - 1);
        for (var i = 0; i < nFft; i++)
            win[i] = (float)(0.5 - 0.5 * Math.Cos(Math.Tau * i * invn));
        var prev = new float[half];
        var eb = new double[3]; // energy in the low, mid and high bands
        var havePrev = false;

        for (var fr = 0; fr < nframes; fr++)
        {
            cancel.ThrowIfCancellationRequested();
            var off = (long)fr * hop;
            for (var i = 0; i < nFft; i++)
            {
                var idx = off + i;
                double s = idx >= 0 && idx < l ? w[idx] : 0.0;
                re[i] = s * win[i];
                im[i] = 0;
            }
            Transform(re, im);

            double e = 0, cnum = 0, cden = 0, fx = 0;
            Array.Clear(eb);
            var crow = chroma.AsSpan(fr * 12, 12);
            for (var k = 0; k < half; k++)
            {
                var m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                e += m * m;
                cnum += k * m;
                cden += m;
                eb[binBand[k]] += m * m;
                if (binPc[k] >= 0)
                    crow[binPc[k]] += (float)m;
                if (havePrev)
                {
                    var d = m - prev[k];
                    if (d > 0)
                        fx += d;
                }
                prev[k] = (float)m;
            }
            havePrev = true;

            double cs = 0;
            for (var i = 0; i < 12; i++)
                cs += crow[i];
            if (cs > 0.0001)
                for (var i = 0; i < 12; i++)
                    crow[i] /= (float)cs;
            rms[fr] = (float)Math.Sqrt(e / half);
            centroid[fr] = (float)(cden > 0 ? cnum / cden : 0);
            flux[fr] = (float)fx;
            blo[fr] = (float)Math.Sqrt(eb[0]);
            bmid[fr] = (float)Math.Sqrt(eb[1]);
            bhi[fr] = (float)Math.Sqrt(eb[2]);
            if ((fr & 63) == 0)
                progress?.Invoke(0.35f + 0.53f * fr / nframes);
        }
        progress?.Invoke(0.88f);

        // The onset envelope: rises in a low and a high band.
        var outN = l / ONSET_DECIM;
        var olo = new float[outN];
        var ohi = new float[outN];
        {
            double lp = 0, hpPrev = 0, xPrev = 0, accLo = 0, accHi = 0;
            var cnt = 0;
            var oi = 0;
            for (var i = 0; i < l; i++)
            {
                double x = w[i];
                lp += 0.15 * (x - lp);
                var h = 0.85 * (hpPrev + x - xPrev);
                hpPrev = h;
                xPrev = x;
                accLo += lp * lp;
                accHi += h * h;
                if (++cnt >= ONSET_DECIM)
                {
                    if (oi < outN)
                    {
                        olo[oi] = (float)Math.Sqrt(accLo / cnt);
                        ohi[oi] = (float)Math.Sqrt(accHi / cnt);
                    }
                    oi++;
                    accLo = accHi = 0;
                    cnt = 0;
                }
            }
        }
        Normalise01(olo);
        Normalise01(ohi);
        var env = new float[outN];
        for (var i = 1; i < outN; i++)
        {
            var dl = olo[i] - olo[i - 1];
            var dh = ohi[i] - ohi[i - 1];
            env[i] = (dl > 0 ? dl : 0) + 0.35f * (dh > 0 ? dh : 0);
        }
        var onset = new float[outN];
        for (var i = 0; i < outN; i++)
        {
            double s = 0;
            var cw = 0;
            for (var j = i > 0 ? i - 1 : 0; j < (i + 2 < outN ? i + 2 : outN); j++)
            {
                s += env[j];
                cw++;
            }
            onset[i] = (float)(cw > 0 ? s / cw : 0);
        }
        progress?.Invoke(0.95f);

        // The whole song's waveform.
        var wl = new float[waveCols];
        var wm = new float[waveCols];
        var wh = new float[waveCols];
        var wp = new float[waveCols];
        double pkMax = 1e-9, bndMax = 1e-9;
        for (var col = 0; col < waveCols; col++)
        {
            var f0 = (long)col * nframes / waveCols;
            var f1 = Math.Max(((long)col + 1) * nframes / waveCols, f0 + 1);
            var fe = Math.Min(f1, nframes);
            double sl = 0, sm = 0, sh = 0, pk = 0;
            for (var i = f0; i < fe; i++)
            {
                sl += blo[i];
                sm += bmid[i];
                sh += bhi[i];
                if (rms[i] > pk)
                    pk = rms[i];
            }
            double dn = Math.Max(fe - f0, 1);
            sl /= dn;
            sm /= dn;
            sh /= dn;
            wl[col] = (float)sl;
            wm[col] = (float)sm;
            wh[col] = (float)sh;
            wp[col] = (float)pk;
            pkMax = Math.Max(pk, pkMax);
            bndMax = Math.Max(Math.Max(sl, Math.Max(sm, sh)), bndMax);
        }
        for (var col = 0; col < waveCols; col++)
        {
            wl[col] = (float)(wl[col] / bndMax);
            wm[col] = (float)(wm[col] / bndMax);
            wh[col] = (float)(wh[col] / bndMax);
            wp[col] = (float)(wp[col] / pkMax);
        }
        progress?.Invoke(1);
        return new SongFrames(workRate, duration, nframes, chroma, rms, centroid, flux, onset, workRate / ONSET_DECIM, wl, wm, wh, wp);
    }

    /// <summary>
    /// The radix-2 FFT in doubles, as the native code had it (<see cref="Fft"/> is sLight's GDScript one, in floats).
    /// </summary>
    static void Transform(double[] re, double[] im)
    {
        var n = re.Length;
        var j = 0;
        for (var i = 1; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var seg = 2; seg <= n; seg <<= 1)
        {
            var ang = -Math.Tau / seg;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            var half = seg >> 1;
            for (var i = 0; i < n; i += seg)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < half; k++)
                {
                    var a = i + k;
                    var b = a + half;
                    var tr = cr * re[b] - ci * im[b];
                    var ti = cr * im[b] + ci * re[b];
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    static void Normalise01(float[] a)
    {
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        foreach (var v in a)
        {
            lo = Math.Min(v, lo);
            hi = Math.Max(v, hi);
        }
        var span = hi - lo;
        if (span < 1e-6f)
            return;
        for (var i = 0; i < a.Length; i++)
            a[i] = (a[i] - lo) / span;
    }

    /// <summary>
    /// Brings audio down by a whole factor through a Hann-windowed sinc, so nothing above the new rate's limit folds
    /// back.
    /// </summary>
    static float[] HannDecimate(float[] input, int factor)
    {
        if (factor <= 1)
            return (float[])input.Clone();
        var r = 2 * factor;
        var fc = 0.45 / factor;
        var k = new double[2 * r + 1];
        double ksum = 0;
        for (var i = -r; i <= r; i++)
        {
            var s = i == 0 ? 2.0 * fc : Math.Sin(2.0 * Math.PI * fc * i) / (Math.PI * i);
            var win = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * (i + r) / (2.0 * r));
            k[i + r] = s * win;
            ksum += s * win;
        }
        for (var i = 0; i < k.Length; i++)
            k[i] /= ksum;
        var n = input.Length;
        var output = new float[n / factor];
        for (var m = 0; m < output.Length; m++)
        {
            var centre = (long)m * factor;
            double acc = 0;
            for (var j = -r; j <= r; j++)
            {
                var idx = Math.Clamp(centre + j, 0, n - 1);
                acc += k[j + r] * input[idx];
            }
            output[m] = (float)acc;
        }
        return output;
    }
}

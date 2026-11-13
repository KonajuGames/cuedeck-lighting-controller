using Cuedeck.Common;

namespace Cuedeck.Audio;

/// <summary>
/// One part of a song's structure: where it starts and ends, what it is (Intro, Verse, Chorus, Bridge, Build, Drop or
/// Outro), how loud it is (0 to 1), and which group of like parts it's in.
/// </summary>
sealed record SongSection(double Start, double End, string Label, double Energy, int Cluster);

/// <summary>
/// The detection maths behind <see cref="SongAnalyzer"/>, apart so it can be tested on made-up features without
/// audio: tempo from an onset envelope, beat tracking, the downbeat, the key, and the structure. sLight's
/// <c>SongDetect</c>; arrays sLight kept as 32-bit floats are floats here too.
/// </summary>
static class SongDetect
{
    // ------------------------------------------------------------------ tempo --

    /// <summary>
    /// The tempo (BPM) from the autocorrelation of an onset envelope sampled at <paramref name="envHz"/>, weighted
    /// towards <paramref name="priorBpm"/>, with octave correction and a parabola through the peak.
    /// </summary>
    public static double EstimateTempo(float[] onset, double envHz, double priorBpm = 120)
    {
        var loLag = Math.Max(1, (int)(60.0 / (priorBpm * 3.2) * envHz));
        var hiLag = (int)(60.0 / (priorBpm / 2.6) * envHz);
        var raw = new Dictionary<int, double>();
        var bestLag = loLag;
        var best = -1.0;
        for (var lag = loLag; lag <= hiLag; lag++)
        {
            double s = 0;
            for (var i = 0; i + lag < onset.Length; i++)
                s += (double)onset[i] * onset[i + lag];
            raw[lag] = s;
            var bpm = 60.0 * envHz / lag;
            var w = Math.Exp(-0.5 * Math.Pow(Math.Log(bpm / priorBpm) / 0.55, 2));
            if (s * w > best)
            {
                best = s * w;
                bestLag = lag;
            }
        }

        // Octave correction towards the prior.
        var dbl = Gd.RoundToInt(bestLag / 2.0);
        if (raw.TryGetValue(dbl, out var rd) && rd > raw[bestLag] * 0.55 && 60.0 * envHz / dbl <= priorBpm * 1.7)
            bestLag = dbl;
        var hlf = bestLag * 2;
        if (raw.TryGetValue(hlf, out var rh) && rh > raw[bestLag] * 0.75 && 60.0 * envHz / hlf >= priorBpm * 0.6)
            bestLag = hlf;

        double lagF = bestLag;
        if (raw.TryGetValue(bestLag - 1, out var y0) && raw.TryGetValue(bestLag + 1, out var y2))
        {
            var y1 = raw[bestLag];
            var den = y0 - 2.0 * y1 + y2;
            if (Math.Abs(den) > 0.0001)
                lagF += Math.Clamp(0.5 * (y0 - y2) / den, -1, 1);
        }
        var output = 60.0 * envHz / Math.Max(lagF, 1);
        while (output > priorBpm * 2)
            output *= 0.5;
        while (output < priorBpm * 0.5)
            output *= 2;
        return Gd.Snapped(output, 0.05);
    }

    // ------------------------------------------------------------------ beats --

    /// <summary>
    /// Dynamic-programming beat tracking (Ellis 2007): beats on onset peaks, staying close to the period. Beat times
    /// in seconds. A second pass at the period the first measured corrects a rough tempo.
    /// </summary>
    public static float[] DpBeats(float[] onset, double envHz, double bpm)
    {
        var beats = DpPass(onset, envHz, 60.0 / bpm * envHz);
        if (beats.Length > 16)
        {
            var ivs = new List<double>();
            for (var i = 1; i < beats.Length; i++)
                ivs.Add(beats[i] - beats[i - 1]);
            ivs.Sort();
            var med = ivs[ivs.Count / 2] * envHz;
            if (med > 2.0 && Math.Abs(med / (60.0 / bpm * envHz) - 1.0) > 0.03)
                beats = DpPass(onset, envHz, med);
        }
        return beats;
    }

    static float[] DpPass(float[] onset, double envHz, double period)
    {
        var n = onset.Length;
        if (n < period * 4.0 || period < 2.0)
            return [];
        var o = LocalNorm(onset, (int)(period * 2.0));
        var cum = new float[n];
        var back = new int[n];
        const double TIGHT = 12.0;
        var lo = (int)(period * 0.5);
        var hi = (int)(period * 2.0);
        for (var t = 0; t < n; t++)
        {
            var best = double.NegativeInfinity;
            var bi = -1;
            var a = Math.Max(0, t - hi);
            var z = t - lo;
            for (var tau = a; tau <= z; tau++)
            {
                if (tau < 0)
                    continue;
                var ratio = (t - tau) / period;
                var pen = ratio > 0 ? Math.Log(ratio) : -10.0;
                var score = cum[tau] - TIGHT * pen * pen;
                if (score > best)
                {
                    best = score;
                    bi = tau;
                }
            }
            cum[t] = (float)(o[t] + (bi >= 0 ? best : 0.0));
            back[t] = bi;
        }

        // The best end within the last beat and a bit.
        var e = n - 1;
        for (var t = Math.Max(0, n - (int)period); t < n; t++)
            if (cum[t] > cum[e])
                e = t;
        var rev = new List<int>();
        while (e >= 0)
        {
            rev.Add(e);
            e = back[e];
        }
        rev.Reverse();
        return [.. rev.Select(idx => (float)(idx / envHz))];
    }

    static float[] LocalNorm(float[] a, int win)
    {
        var output = new float[a.Length];
        win = Math.Max(win, 4);
        for (var i = 0; i < a.Length; i++)
        {
            var lo = Math.Max(0, i - win);
            var hi = Math.Min(a.Length, i + win);
            var mx = 0.0001;
            for (var j = lo; j < hi; j++)
                mx = Math.Max(mx, a[j]);
            output[i] = (float)(a[i] / mx);
        }
        return output;
    }

    // -------------------------------------------------------------- downbeats --

    /// <summary>
    /// Which of the four beat positions (0 to 3) is beat 1: the one whose beats carry the most chord change.
    /// </summary>
    public static int DownbeatPhase(IReadOnlyList<float[]> beatChroma)
    {
        if (beatChroma.Count < 8)
            return 0;
        var score = new double[4];
        for (var i = 1; i < beatChroma.Count; i++)
            score[i % 4] += ChromaDistance(beatChroma[i], beatChroma[i - 1]);
        var best = 0;
        for (var p = 1; p < 4; p++)
            if (score[p] > score[best])
                best = p;
        return best;
    }

    static double ChromaDistance(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            s += Math.Abs((double)a[i] - b[i]);
        return s;
    }

    // -------------------------------------------------------------------- key --

    /// <summary>
    /// Krumhansl-Kessler key profiles: how well each scale degree (0 the tonic) fits a major or minor key.
    /// </summary>
    static readonly double[] KkMajor = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    static readonly double[] KkMinor = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    /// <summary>
    /// The key: the song's overall pitch-class profile against all 24 turns of the major and minor profiles (the
    /// Krumhansl-Schmuckler method), the best match. Root 0 to 11 (C to B).
    /// </summary>
    public static (int Root, string Mode) DetectKey(IReadOnlyList<float[]> beatChroma)
    {
        var profile = new float[12];
        foreach (var row in beatChroma)
            for (var k = 0; k < 12; k++)
                profile[k] += row[k];
        var bestRoot = 0;
        var bestMode = "major";
        var bestScore = double.NegativeInfinity;
        for (var root = 0; root < 12; root++)
        {
            foreach (var mode in new[] { "major", "minor" })
            {
                var kk = mode == "major" ? KkMajor : KkMinor;
                var rotated = new float[12];
                for (var pc = 0; pc < 12; pc++)
                    rotated[pc] = (float)kk[(pc - root + 12) % 12];
                var score = Correlate(profile, rotated);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestRoot = root;
                    bestMode = mode;
                }
            }
        }
        return (bestRoot, bestMode);
    }

    static double Correlate(float[] x, float[] y)
    {
        var n = Math.Min(x.Length, y.Length);
        if (n == 0)
            return 0;
        double mx = 0, my = 0;
        for (var i = 0; i < n; i++)
        {
            mx += x[i];
            my += y[i];
        }
        mx /= n;
        my /= n;
        double num = 0, dx = 0, dy = 0;
        for (var i = 0; i < n; i++)
        {
            var a = x[i] - mx;
            var b = y[i] - my;
            num += a * b;
            dx += a * a;
            dy += b * b;
        }
        var den = Math.Sqrt(dx * dy);
        return den > 0.0001 ? num / den : 0;
    }

    // -------------------------------------------------------------- structure --

    sealed class Part
    {
        public int S;
        public int E;
        public float[] Feat = [];
        public double En;
        public double Rise;
    }

    /// <summary>
    /// The song's structure. <paramref name="feats"/> are per-beat feature vectors (chroma and timbre, already
    /// scaled), <paramref name="energy"/> per-beat loudness 0 to 1, <paramref name="beatTimes"/> in seconds.
    /// </summary>
    public static List<SongSection> Segment(IReadOnlyList<float[]> feats, float[] energy, float[] beatTimes, int downbeatPhase, double duration)
    {
        var nb = feats.Count;
        if (nb < 12)
            return [new SongSection(0, duration, "Verse", 0.6, 0)];

        var ssm = Ssm(feats);
        var nov = Novelty(ssm, Math.Min(16, nb / 4));

        // Boundaries: novelty peaks, at least about 4 bars apart, snapped to the downbeats.
        const int MIN_GAP_BEATS = 16;
        var thr = Percentile(nov, 0.70);
        var bounds = new List<int> { 0 };
        var last = -MIN_GAP_BEATS;
        for (var i = 4; i < nb - 4; i++)
        {
            if (i - last < MIN_GAP_BEATS)
                continue;
            if (nov[i] >= thr && nov[i] >= nov[i - 1] && nov[i] > nov[i + 1])
            {
                bounds.Add(SnapDownbeat(i, downbeatPhase));
                last = i;
            }
        }
        if (bounds[^1] < nb - 8)
            bounds.Add(nb);
        else
            bounds[^1] = nb;

        // Segments under about 6 bars merged into the one before.
        var segs = new List<Part>();
        for (var i = 0; i < bounds.Count - 1; i++)
        {
            int s = bounds[i], e = bounds[i + 1];
            if (e - s < 16 && segs.Count > 0)
                segs[^1].E = e;
            else
                segs.Add(new Part { S = s, E = e });
        }
        foreach (var seg in segs)
        {
            seg.Feat = MeanVec(feats, seg.S, seg.E);
            seg.En = MeanArr(energy, seg.S, seg.E);
            seg.Rise = MeanArr(energy, seg.E - 8, seg.E) - MeanArr(energy, seg.S, seg.S + 8);
        }
        var elo = segs.Min(s => s.En);
        var ehi = segs.Max(s => s.En);
        var espan = Math.Max(ehi - elo, 0.0001);

        // Grouped by likeness (union-find over like pairs), but only within a loudness range of each other, a part
        // of this song's own range: chroma's 12 of the vector's 15 dimensions, so a verse and its chorus over the
        // same chords would read as the same part otherwise.
        var tol = Math.Clamp(0.16 * espan, 0.03, 0.16);
        var parent = Enumerable.Range(0, segs.Count).ToArray();
        for (var i = 0; i < segs.Count; i++)
            for (var j = i + 1; j < segs.Count; j++)
                if (Cos(segs[i].Feat, segs[j].Feat) > 0.86 && Math.Abs(segs[i].En - segs[j].En) < tol)
                    Union(parent, i, j);
        var clusterOf = new int[segs.Count];
        var clusterIds = new Dictionary<int, int>();
        for (var i = 0; i < segs.Count; i++)
        {
            var root = Find(parent, i);
            if (!clusterIds.ContainsKey(root))
                clusterIds[root] = clusterIds.Count;
            clusterOf[i] = clusterIds[root];
        }

        // A long track can still chain quiet to loud a small step at a time: a group whose own loudness spans more
        // than the tolerance is split, walking its members by loudness.
        var byCluster = new Dictionary<int, List<int>>();
        for (var i = 0; i < segs.Count; i++)
        {
            if (!byCluster.TryGetValue(clusterOf[i], out var list))
                byCluster[clusterOf[i]] = list = [];
            list.Add(i);
        }
        var nextId = clusterIds.Count;
        foreach (var (key, members) in byCluster)
        {
            if (members.Count < 2)
                continue;
            var ordered = members.OrderBy(m => segs[m].En).ToList();
            var groupLo = segs[ordered[0]].En;
            var curId = key;
            foreach (var idx in ordered)
            {
                var e = segs[idx].En;
                if (e - groupLo > tol)
                {
                    nextId++;
                    curId = nextId;
                    groupLo = e;
                }
                clusterOf[idx] = curId;
            }
        }

        var clCount = new Dictionary<int, int>();
        var clEnergy = new Dictionary<int, double>();
        for (var i = 0; i < segs.Count; i++)
        {
            var c = clusterOf[i];
            clCount[c] = clCount.GetValueOrDefault(c) + 1;
            clEnergy[c] = clEnergy.GetValueOrDefault(c) + segs[i].En;
        }
        foreach (var c in clEnergy.Keys.ToList())
            clEnergy[c] /= clCount[c];

        // The loudest group that recurs is the chorus; the next, the verse.
        var recurring = clCount.Where(kv => kv.Value >= 2).Select(kv => kv.Key).OrderByDescending(c => clEnergy[c]).ToList();
        var chorusCl = recurring.Count > 0 ? recurring[0] : -1;
        var verseCl = recurring.Count > 1 ? recurring[1] : -1;

        var output = new List<SongSection>();
        for (var i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            var c = clusterOf[i];
            var enN = Math.Clamp((seg.En - elo) / espan, 0, 1);
            var label = "Verse";
            if (c == chorusCl)
                label = "Chorus";
            else if (c == verseCl || clCount.GetValueOrDefault(c) >= 2)
                label = "Verse";
            else if (i == 0)
                label = enN < 0.55 ? "Intro" : "Verse";
            else if (i == segs.Count - 1)
                label = enN < 0.55 ? "Outro" : "Chorus";
            else if (enN <= 0.32)
                label = "Bridge";
            else if (enN >= 0.78)
                label = "Bridge"; // loud but never again: a climax, not a chorus
            output.Add(new SongSection(BeatTime(beatTimes, seg.S), BeatTime(beatTimes, seg.E), label, Math.Clamp(enN, 0.06, 1), c));
        }

        // A rise into a loud chorus: a build and a drop.
        for (var i = 0; i < output.Count - 1; i++)
        {
            if (segs[i].Rise > 0.14 && output[i].Energy < 0.7 && output[i + 1].Label == "Chorus" && output[i + 1].Energy >= 0.8)
            {
                output[i] = output[i] with { Label = "Build" };
                output[i + 1] = output[i + 1] with { Label = "Drop" };
            }
        }
        output[0] = output[0] with { Start = 0 };
        output[^1] = output[^1] with { End = duration };
        return output;
    }

    static float[][] Ssm(IReadOnlyList<float[]> feats)
    {
        var n = feats.Count;
        var s = new float[n][];
        for (var i = 0; i < n; i++)
            s[i] = new float[n];
        for (var i = 0; i < n; i++)
        {
            s[i][i] = 1;
            for (var j = i + 1; j < n; j++)
                s[i][j] = s[j][i] = (float)Cos(feats[i], feats[j]);
        }
        return s;
    }

    /// <summary>
    /// Foote's checkerboard novelty along the self-similarity matrix's diagonal.
    /// </summary>
    static float[] Novelty(float[][] ssm, int k)
    {
        var n = ssm.Length;
        var nov = new float[n];
        k = Math.Max(k, 2);
        var kern = new float[2 * k, 2 * k];
        for (var a = -k; a < k; a++)
        {
            for (var b = -k; b < k; b++)
            {
                var g = Math.Exp(-(a * a + b * b) / (double)(k * k) * 2.0);
                kern[a + k, b + k] = (float)((a * b >= 0 ? 1.0 : -1.0) * g);
            }
        }
        for (var i = 0; i < n; i++)
        {
            if (i - k < 0 || i + k >= n)
                continue;
            double acc = 0;
            for (var a = -k; a < k; a++)
                for (var b = -k; b < k; b++)
                    acc += (double)kern[a + k, b + k] * ssm[i + a][i + b];
            nov[i] = (float)Math.Max(0, acc);
        }
        return nov;
    }

    static int SnapDownbeat(int beatIndex, int phase)
    {
        var off = ((beatIndex - phase) % 4 + 4) % 4;
        if (off == 0)
            return beatIndex;
        return off <= 2 ? beatIndex - off : beatIndex + (4 - off);
    }

    static double BeatTime(float[] beatTimes, int idx) => beatTimes.Length == 0 ? 0 : beatTimes[Math.Clamp(idx, 0, beatTimes.Length - 1)];

    static float[] MeanVec(IReadOnlyList<float[]> vecs, int lo, int hi)
    {
        lo = Math.Max(lo, 0);
        hi = Math.Min(hi, vecs.Count);
        var dim = vecs.Count > 0 ? vecs[0].Length : 0;
        var output = new float[dim];
        if (hi <= lo)
            return output;
        for (var i = lo; i < hi; i++)
            for (var d = 0; d < dim; d++)
                output[d] += vecs[i][d];
        for (var d = 0; d < dim; d++)
            output[d] /= hi - lo;
        return output;
    }

    static double MeanArr(float[] a, int lo, int hi)
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

    public static double Cos(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na < 1e-9 || nb < 1e-9 ? 0 : dot / Math.Sqrt(na * nb);
    }

    static double Percentile(float[] a, double p)
    {
        if (a.Length == 0)
            return 0;
        var c = a.Order().ToArray();
        return c[Math.Clamp((int)(p * c.Length), 0, c.Length - 1)];
    }

    static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }

    static void Union(int[] parent, int a, int b) => parent[Find(parent, a)] = Find(parent, b);
}

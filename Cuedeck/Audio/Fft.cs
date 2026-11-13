namespace Cuedeck.Audio;

/// <summary>
/// An in-place iterative radix-2 Cooley–Tukey FFT for power-of-two sizes, as sLight's <c>FFT</c>.
/// </summary>
static class Fft
{
    /// <summary>
    /// The Hann-windowed magnitude spectrum of <paramref name="n"/> samples from <paramref name="offset"/> (zeros past
    /// either end): n/2 + 1 bins, DC to Nyquist. sLight's <c>FFT.magnitude()</c>, for song analysis.
    /// </summary>
    public static float[] Magnitude(ReadOnlySpan<float> samples, int offset, int n)
    {
        var re = new float[n];
        var im = new float[n];
        var inv = 1.0 / (n - 1);
        for (var i = 0; i < n; i++)
        {
            var idx = offset + i;
            var s = idx >= 0 && idx < samples.Length ? samples[idx] : 0f;
            re[i] = (float)(s * (0.5 - 0.5 * Math.Cos(Math.Tau * i * inv)));
        }
        Transform(re, im);
        var output = new float[n / 2 + 1];
        for (var i = 0; i < output.Length; i++)
            output[i] = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
        return output;
    }

    /// <summary>
    /// The FFT of <paramref name="re"/> and <paramref name="im"/> (a power of two long), in place.
    /// </summary>
    public static void Transform(Span<float> re, Span<float> im)
    {
        var n = re.Length;
        // Bit-reversal permutation.
        var j = 0;
        for (var i = 1; i < n; i++)
        {
            var bit = n >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }
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
                    var tr = (float)(cr * re[b] - ci * im[b]);
                    var ti = (float)(cr * im[b] + ci * re[b]);
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
}

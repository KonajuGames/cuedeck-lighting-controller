namespace Cuedeck.Visualizer;

/// <summary>
/// Paces a recording's frames: how many are due after each screen frame, at a fixed rate, catching up at most
/// <see cref="MAX_CATCH_UP"/> at a time so a stall doesn't spiral (sLight's recorder's pacing).
/// </summary>
sealed class FramePacer(int fps = FramePacer.FPS)
{
    public const int FPS = 30;
    public const int MAX_CATCH_UP = 3;

    double _owed;

    public int Fps { get; } = fps;

    /// <summary>
    /// The frames due after <paramref name="delta"/> more seconds.
    /// </summary>
    public int Due(double delta)
    {
        _owed += Math.Max(delta, 0);
        var step = 1.0 / Fps;
        var n = 0;
        while (_owed >= step && n < MAX_CATCH_UP)
        {
            _owed -= step;
            n++;
        }
        return n;
    }
}

/// <summary>
/// Pictures turned into what files want.
/// </summary>
static class Pictures
{
    /// <summary>
    /// The size a video's encoded at: the picture's, each side cut down to a multiple of 16 (H.264's blocks).
    /// </summary>
    public static (int Width, int Height) VideoSize(int width, int height) => (width - width % 16, height - height % 16);

    /// <summary>
    /// The top-left <paramref name="toWidth"/> by <paramref name="toHeight"/> of an RGBA picture, as BGRA (what
    /// Windows' video wants); what the picture doesn't cover is black.
    /// </summary>
    public static byte[] ToBgra(ReadOnlySpan<byte> rgba, int width, int height, int toWidth, int toHeight)
    {
        var output = new byte[toWidth * toHeight * 4];
        var rows = Math.Min(height, toHeight);
        var cols = Math.Min(width, toWidth);
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++)
            {
                var s = (y * width + x) * 4;
                var d = (y * toWidth + x) * 4;
                output[d] = rgba[s + 2];
                output[d + 1] = rgba[s + 1];
                output[d + 2] = rgba[s];
                output[d + 3] = 255;
            }
        return output;
    }

    /// <summary>
    /// Interleaved float samples (-1 to 1) as 16-bit.
    /// </summary>
    public static short[] ToPcm16(ReadOnlySpan<float> samples)
    {
        var output = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            output[i] = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
        return output;
    }

    /// <summary>
    /// A file name stamped with when it's made, as sLight's ("shot_2026-11-15_21-04-05.png").
    /// </summary>
    public static string Stamped(string prefix, string extension, DateTime when) => $"{prefix}_{when:yyyy-MM-dd_HH-mm-ss}.{extension}";
}

namespace Cuedeck.Audio;

/// <summary>
/// A live audio input (a microphone or line-in) to listen to: what's there to choose from, and the latest samples
/// heard. <see cref="WasapiInput"/> is Windows' own; tests give their own.
/// </summary>
interface IAudioCapture : IDisposable
{
    /// <summary>
    /// The inputs there are, by name.
    /// </summary>
    IReadOnlyList<string> Devices();

    /// <summary>
    /// Starts listening to <paramref name="device"/> ("" for the system's default), stopping any other first. False
    /// if it couldn't; then see <see cref="Error"/>.
    /// </summary>
    bool Start(string device);

    void Stop();

    /// <summary>
    /// Samples a second, of the input listening now.
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// Why it isn't listening, or "".
    /// </summary>
    string Error { get; }

    /// <summary>
    /// Copies the latest samples heard, left and right, oldest first, as many as the spans hold (silence before the
    /// first).
    /// </summary>
    void Latest(Span<float> left, Span<float> right);
}

/// <summary>
/// The last second or so of a stereo input, filled from the capture's own thread and read from the show's.
/// </summary>
sealed class CaptureBuffer(int capacity = 1 << 16)
{
    readonly float[] _left = new float[capacity];
    readonly float[] _right = new float[capacity];
    readonly object _lock = new();
    int _write;

    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        lock (_lock)
        {
            for (var i = 0; i < left.Length; i++)
            {
                _left[_write] = left[i];
                _right[_write] = right[i];
                _write = (_write + 1) % _left.Length;
            }
        }
    }

    public void Latest(Span<float> left, Span<float> right)
    {
        lock (_lock)
        {
            var n = left.Length;
            var start = (_write - n % _left.Length + _left.Length) % _left.Length;
            for (var i = 0; i < n; i++)
            {
                var at = (start + i) % _left.Length;
                left[i] = _left[at];
                right[i] = _right[at];
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_left);
            Array.Clear(_right);
            _write = 0;
        }
    }
}

/// <summary>
/// No input: nothing to choose and nothing heard. What a show listens to until it's given one.
/// </summary>
sealed class NoCapture : IAudioCapture
{
    public IReadOnlyList<string> Devices() => [];

    public bool Start(string device) => false;

    public void Stop() { }

    public int SampleRate => 48000;

    public string Error => "no audio input";

    public void Latest(Span<float> left, Span<float> right)
    {
        left.Clear();
        right.Clear();
    }

    public void Dispose() { }
}

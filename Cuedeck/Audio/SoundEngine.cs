using Cuedeck.Common;
using Cuedeck.Show;

namespace Cuedeck.Audio;

/// <summary>
/// Listens to an audio input for the Sound Reactive run mode: smoothed bass, mid, treble and overall levels, each 0
/// to 1, and the beats in the bass. Sound reactors and beat-synced chases follow it; the Sound tab shows it. sLight's
/// <c>Sound</c> autoload, with Godot's spectrum analyser (a 2048-point FFT of the latest samples, magnitudes over the
/// FFT size, the loudest bin in each range) done here.
/// <para>
/// Idle (not <see cref="Active"/>) it doesn't listen, and every level falls away to 0, so it's always safe to read.
/// </para>
/// </summary>
sealed class SoundEngine : ISoundLevels, IDisposable
{
    const int FFT_SIZE = 2048;
    const double MIN_DB = -60;
    const int HISTORY = 50;

    static readonly (double Lo, double Hi) BassHz = (20, 250);
    static readonly (double Lo, double Hi) MidHz = (250, 2000);
    static readonly (double Lo, double Hi) TrebleHz = (2000, 16000);

    readonly SoundSettings _settings;
    readonly IAudioCapture _capture;
    readonly double[] _bassHistory = new double[HISTORY];
    readonly float[] _left = new float[FFT_SIZE];
    readonly float[] _right = new float[FFT_SIZE];
    readonly float[] _im = new float[FFT_SIZE];
    readonly float[] _magLeft = new float[FFT_SIZE / 2];
    readonly float[] _magRight = new float[FFT_SIZE / 2];
    int _historyAt;
    double _beatCooldown;
    bool _active;
    bool _listening;
    string _openDevice = "";

    public SoundEngine(SoundSettings settings, IAudioCapture capture)
    {
        _settings = settings;
        _capture = capture;
    }

    public double Level { get; private set; }
    public double Bass { get; private set; }
    public double Mid { get; private set; }
    public double Treble { get; private set; }

    /// <summary>
    /// 1 on a beat, falling back to 0.
    /// </summary>
    public double BeatPulse { get; private set; }

    /// <summary>
    /// Why the input isn't being heard while active, or "".
    /// </summary>
    public string Error { get; private set; } = "";

    /// <summary>
    /// Heard on each beat in the bass, while active.
    /// </summary>
    public event Action? Beat;

    /// <summary>
    /// Whether it's listening: on in Sound Reactive.
    /// </summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (value == _active)
                return;
            _active = value;
            if (_active)
                Start();
            else
                Stop();
        }
    }

    /// <summary>
    /// The inputs to choose from.
    /// </summary>
    public IReadOnlyList<string> Devices() => _capture.Devices();

    void Start()
    {
        _openDevice = _settings.Device;
        _listening = _capture.Start(_settings.Device);
        Error = _listening ? "" : _capture.Error;
    }

    void Stop()
    {
        _capture.Stop();
        _listening = false;
        Level = Bass = Mid = Treble = BeatPulse = 0;
        Array.Clear(_bassHistory);
    }

    /// <summary>
    /// Moves on by <paramref name="delta"/> seconds: reads the latest samples and updates the levels. True on a beat
    /// (and <see cref="Beat"/> is raised).
    /// </summary>
    public bool Update(double delta)
    {
        BeatPulse = Math.Max(0, BeatPulse - delta * 3.5);
        _beatCooldown = Math.Max(0, _beatCooldown - delta);

        // The input's changed (in the Sound tab, or with another show): listen to the new one.
        if (_active && _settings.Device != _openDevice)
        {
            Stop();
            Start();
        }
        if (!_active || !_listening)
        {
            var d = Math.Clamp(delta * 6, 0, 1);
            Level = Gd.Lerp(Level, 0, d);
            Bass = Gd.Lerp(Bass, 0, d);
            Mid = Gd.Lerp(Mid, 0, d);
            Treble = Gd.Lerp(Treble, 0, d);
            return false;
        }

        Analyse();
        var b = Band(BassHz);
        var m = Band(MidHz);
        var t = Band(TrebleHz);
        var l = Math.Clamp(Math.Max(b, Math.Max(m, t)) * 0.6 + (b + m + t) / 3 * 0.7, 0, 1);

        // Asymmetric smoothing: rise fast, fall slow, as light chasing sound looks.
        var response = _settings.Response;
        var up = Math.Clamp(Gd.Lerp(0.35, 0.95, response) * delta * 60, 0, 1);
        var down = Math.Clamp(Gd.Lerp(0.04, 0.30, response) * delta * 60, 0, 1);
        Bass = Slew(Bass, b, up, down);
        Mid = Slew(Mid, m, up, down);
        Treble = Slew(Treble, t, up, down);
        Level = Slew(Level, l, up, down);

        return DetectBeat(b);
    }

    static double Slew(double current, double target, double up, double down) => Gd.Lerp(current, target, target > current ? up : down);

    /// <summary>
    /// The latest FFT_SIZE samples' spectra, each channel's, Hann-windowed, as magnitudes over the FFT size.
    /// </summary>
    void Analyse()
    {
        _capture.Latest(_left, _right);
        Spectrum(_left, _magLeft);
        Spectrum(_right, _magRight);
    }

    void Spectrum(float[] samples, float[] output)
    {
        for (var i = 0; i < FFT_SIZE; i++)
        {
            samples[i] *= (float)(-0.5 * Math.Cos(Math.Tau * i / FFT_SIZE) + 0.5);
            _im[i] = 0;
        }
        Fft.Transform(samples, _im);
        for (var i = 0; i < output.Length; i++)
            output[i] = MathF.Sqrt(samples[i] * samples[i] + _im[i] * _im[i]) / FFT_SIZE;
    }

    /// <summary>
    /// A range's loudest bin, averaged over the two channels, as -60 to 0 dB mapped to 0 to 1, times the gain.
    /// </summary>
    double Band((double Lo, double Hi) hz)
    {
        if (!_listening)
            return 0;
        var perBin = (double)_capture.SampleRate / FFT_SIZE;
        var last = _magLeft.Length - 1;
        var begin = Math.Clamp((int)(hz.Lo / perBin), 0, last);
        var end = Math.Clamp((int)(hz.Hi / perBin), 0, last);
        if (begin > end)
            (begin, end) = (end, begin);
        float left = 0, right = 0;
        for (var i = begin; i <= end; i++)
        {
            left = Math.Max(left, _magLeft[i]);
            right = Math.Max(right, _magRight[i]);
        }
        var lin = (left + right) * 0.5;
        if (lin <= 0.00001)
            return 0;
        var db = 20 * Math.Log10(lin) + 6; // +6: line level rarely reaches 0 dBFS
        return Math.Clamp((db - MIN_DB) / -MIN_DB, 0, 1) * _settings.Gain;
    }

    /// <summary>
    /// <paramref name="n"/> log-spaced bands' levels (0 to 1, or more with gain) from 40 Hz to 16 kHz, for the
    /// spectrogram; zeros while it's not listening.
    /// </summary>
    public float[] Spectrum(int n)
    {
        var output = new float[n];
        if (!_active || !_listening)
            return output;
        const double LO = 40, HI = 16000;
        var ratio = Math.Pow(HI / LO, 1.0 / n);
        var f = LO;
        for (var i = 0; i < n; i++)
        {
            var f2 = f * ratio;
            output[i] = (float)Band((f, f2));
            f = f2;
        }
        return output;
    }

    /// <summary>
    /// A beat when the bass jumps well over its average of the last 50 updates, at most about nine a second.
    /// </summary>
    bool DetectBeat(double bass)
    {
        var average = _bassHistory.Sum() / HISTORY;
        _bassHistory[_historyAt] = bass;
        _historyAt = (_historyAt + 1) % HISTORY;
        if (_beatCooldown <= 0 && bass > 0.12 && bass > average * _settings.BeatSensitivity && bass > average + 0.04)
        {
            BeatPulse = 1;
            _beatCooldown = 0.11;
            Beat?.Invoke();
            return true;
        }
        return false;
    }

    public void Dispose() => _capture.Dispose();
}

using System.Diagnostics;

namespace Cuedeck.Show;

/// <summary>
/// Where the tempo clock's beat comes from.
/// </summary>
enum TempoSource
{
    None,
    Tap,
    MidiClock,
    Os2l,
}

/// <summary>
/// The console's tempo clock: a steady beat that beat-sync chases step on and tempo-synced effects run at, in any run
/// mode. Off until something sets the tempo:
/// <list type="bullet">
/// <item><see cref="Tap"/>: Tap Tempo (the top bar's Tap button, the T key, a MIDI or OSC binding). Each tap lands a
/// beat; two or more within <see cref="TapReset"/> seconds of each other set the tempo from their average spacing.</item>
/// <item><see cref="ClockPulse"/>: a MIDI input's timing clock, 24 pulses a quarter note. <see cref="ClockStart"/>
/// re-zeroes the beat count.</item>
/// <item><see cref="ExternalBeat"/>: a beat from another program, such as VirtualDJ's or Cuepoint's OS2L beats.</item>
/// </list>
/// MIDI clock and OS2L are external sources: while one keeps arriving it owns the clock and taps are ignored. When it
/// stops, the clock runs on at the last tempo, as if tapped, until <see cref="Stop"/>. Between beats the clock runs
/// freely from the last one, so a source that drops a beat or two is bridged.
/// </summary>
sealed class TempoClock
{
    public const double MinBpm = 30;
    public const double MaxBpm = 300;

    /// <summary>
    /// Taps further apart than this start a new count.
    /// </summary>
    public const double TapReset = 2;

    /// <summary>
    /// The tempo is the average of up to this many taps' spacing.
    /// </summary>
    public const int TapHistory = 8;

    public const int ClockPpqn = 24;

    /// <summary>
    /// MIDI clock pulses averaged for the tempo: four beats.
    /// </summary>
    public const int ClockWindow = 96;

    /// <summary>
    /// A MIDI clock is lost after this long without a pulse.
    /// </summary>
    public const double ClockTimeout = 0.5;

    /// <summary>
    /// An OS2L source is lost after this many beats without one.
    /// </summary>
    public const double ExternalTimeoutBeats = 2.5;

    public static readonly string[] SourceNames = ["Off", "Tap", "MIDI clock", "VirtualDJ"];

    double _nextBeat;
    readonly List<double> _taps = [];
    readonly List<double> _clockTimes = [];
    int _clockCount;
    double _lastExternal = double.NegativeInfinity;
    TempoSource _external = TempoSource.None;

    public bool Running { get; private set; }
    public double Bpm { get; private set; } = 120;
    public TempoSource Source { get; private set; } = TempoSource.None;

    /// <summary>
    /// 1 on a beat, falling to 0, for meters and indicators.
    /// </summary>
    public double BeatPulse { get; private set; }

    /// <summary>
    /// Whether MIDI clock on the MIDI inputs is listened for.
    /// </summary>
    public bool MidiClockEnabled { get; set; } = true;

    /// <summary>
    /// The seconds the clock runs by. Replace it to step the clock exactly in tests.
    /// </summary>
    public Func<double> Now { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>
    /// A beat of the running clock.
    /// </summary>
    public event Action? Beat;

    /// <summary>
    /// Whether it's running, its tempo or its source changed.
    /// </summary>
    public event Action? Changed;

    public double Period => 60.0 / Bpm;

    /// <summary>
    /// How far into the current beat the clock is, 0 to 1 (0 when stopped).
    /// </summary>
    public double Phase(double? now = null)
    {
        if (!Running)
            return 0;
        return Math.Clamp(1 - (_nextBeat - (now ?? Now())) / Period, 0, 1);
    }

    /// <summary>
    /// Whether a MIDI clock or OS2L source is driving the clock now.
    /// </summary>
    public bool ExternalLive(double? now = null)
    {
        if (_external == TempoSource.None)
            return false;
        var timeout = _external == TempoSource.MidiClock ? ClockTimeout : Period * ExternalTimeoutBeats;
        return (now ?? Now()) - _lastExternal <= timeout;
    }

    /// <summary>
    /// A tap: lands a beat now and, with the taps before it, sets the tempo. Ignored (false) while an external source
    /// owns the clock.
    /// </summary>
    public bool Tap(double? at = null)
    {
        var now = at ?? Now();
        if (ExternalLive(now))
            return false;
        if (_taps.Count > 0 && now - _taps[^1] > TapReset)
            _taps.Clear();
        _taps.Add(now);
        while (_taps.Count > TapHistory)
            _taps.RemoveAt(0);
        if (_taps.Count >= 2)
        {
            var spacing = (_taps[^1] - _taps[0]) / (_taps.Count - 1);
            Bpm = Math.Clamp(60.0 / spacing, MinBpm, MaxBpm);
        }
        _external = TempoSource.None;
        Source = TempoSource.Tap;
        Running = true;
        LandBeat(now);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Stops the clock: chases go back to their own rate.
    /// </summary>
    public void Stop()
    {
        if (!Running && Source == TempoSource.None)
            return;
        Running = false;
        Source = TempoSource.None;
        _external = TempoSource.None;
        _taps.Clear();
        _clockTimes.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// A beat from another program, with its tempo (0 to keep the clock's).
    /// </summary>
    public void ExternalBeat(double bpm, TempoSource from = TempoSource.Os2l, double? at = null)
    {
        var now = at ?? Now();
        if (bpm > 0)
            Bpm = Math.Clamp(bpm, MinBpm, MaxBpm);
        Take(from, now);
        LandBeat(now);
    }

    /// <summary>
    /// One MIDI timing-clock pulse (0xF8).
    /// </summary>
    public void ClockPulse(double? at = null)
    {
        var now = at ?? Now();
        if (_clockTimes.Count > 0 && now - _clockTimes[^1] > ClockTimeout)
        {
            // The clock was stopped: start counting again.
            _clockTimes.Clear();
            _clockCount = 0;
        }
        _clockTimes.Add(now);
        while (_clockTimes.Count > ClockWindow + 1)
            _clockTimes.RemoveAt(0);
        if (_clockTimes.Count >= ClockPpqn + 1)
        {
            var span = _clockTimes[^1] - _clockTimes[0];
            if (span > 0)
                Bpm = Math.Clamp(60.0 / (span / (_clockTimes.Count - 1) * ClockPpqn), MinBpm, MaxBpm);
        }
        var onBeat = _clockCount % ClockPpqn == 0;
        _clockCount++;
        if (_clockTimes.Count < ClockPpqn + 1)
            return; // no tempo yet
        Take(TempoSource.MidiClock, now);
        if (onBeat)
            LandBeat(now);
    }

    /// <summary>
    /// MIDI Start (0xFA): the next pulse is beat 1.
    /// </summary>
    public void ClockStart() => _clockCount = 0;

    void Take(TempoSource from, double now)
    {
        _lastExternal = now;
        if (_external != from || Source != from || !Running)
        {
            _external = from;
            Source = from;
            Running = true;
            Changed?.Invoke();
        }
    }

    void LandBeat(double now)
    {
        _nextBeat = now + Period;
        BeatPulse = 1;
        Beat?.Invoke();
    }

    /// <summary>
    /// Once a frame: lets <see cref="BeatPulse"/> fall and runs the clock up to now.
    /// </summary>
    public void Update(double delta)
    {
        BeatPulse = Math.Max(0, BeatPulse - delta * 4);
        Advance(Now());
    }

    /// <summary>
    /// Runs the clock up to <paramref name="now"/>: a free-running beat when one's due (a quarter beat late while an
    /// external source is live, so it only fills in a missed one), and an external source given up when it's gone
    /// quiet.
    /// </summary>
    public void Advance(double now)
    {
        if (!Running)
            return;
        var live = ExternalLive(now);
        if (_external != TempoSource.None && !live)
        {
            _external = TempoSource.None;
            Source = TempoSource.Tap; // runs on at the last tempo
            Changed?.Invoke();
        }
        var due = _nextBeat + (live ? Period * 0.25 : 0);
        if (now >= due)
        {
            // Several beats late (the app stalled): just the one, back in step.
            while (_nextBeat + Period <= now)
                _nextBeat += Period;
            _nextBeat += Period;
            BeatPulse = 1;
            Beat?.Invoke();
        }
    }

    /// <summary>
    /// The top bar's read-out: "128.0 BPM · Tap", or "— BPM" when off.
    /// </summary>
    public string Describe() => Running ? $"{Bpm:F1} BPM · {SourceNames[(int)Source]}" : "— BPM";
}

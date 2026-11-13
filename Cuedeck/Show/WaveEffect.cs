using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Fixtures;

namespace Cuedeck.Show;

enum Waveform
{
    Sine,
    Triangle,
    Sawtooth,
    Square,
    Random,
}

enum WaveBase
{
    /// <summary>
    /// Swings around <see cref="WaveEffect.Center"/>.
    /// </summary>
    Absolute,

    /// <summary>
    /// Swings around the channel's live level.
    /// </summary>
    Pickup,
}

/// <summary>
/// A channel the effects drive: universe slot and channel (0-based).
/// </summary>
readonly record struct ChannelTarget(int Universe, int Channel);

/// <summary>
/// A waveform on one channel role, spread across the patched fixtures that have it: a dimmer pulse, a pan and tilt
/// circle (a pan sine and a tilt sine a quarter cycle apart), a colour swell. Its channels come from the patch
/// (<see cref="SetTargets"/>); set them again when the patch changes.
/// </summary>
sealed class WaveEffect
{
    public static readonly string[] Waveforms = ["Sine", "Triangle", "Sawtooth", "Square", "Random"];
    public static readonly string[] BaseModes = ["Absolute", "Pickup"];

    /// <summary>
    /// Roles moved by a motor: pan, tilt and zoom can't follow a fast waveform, so their rate is capped.
    /// </summary>
    public static readonly string[] MotorRoles = [Roles.Pan, Roles.PanFine, Roles.Tilt, Roles.TiltFine, Roles.Zoom];
    public const double MotorMaxBpm = 60;

    /// <summary>
    /// The tempo-beat choices and their names.
    /// </summary>
    public static readonly double[] TempoBeatChoices = [0, 0.5, 1, 2, 4, 8];
    public static readonly string[] TempoBeatNames = ["Own rate", "½ beat", "1 beat", "2 beats", "4 beats", "8 beats"];

    string _role = Roles.Dimmer;
    double _bpm = 60;
    double _phase;
    List<ChannelTarget> _targets = [];
    readonly Random _random = new();
    double[] _randomValues = [];
    int _randomStep = -1;

    public string Name { get; set; } = "Effect";

    public string Role
    {
        get => _role;
        set
        {
            _role = value;
            Bpm = _bpm; // clamped again for the new role
        }
    }

    /// <summary>
    /// The universe slot it runs on, or -1 for every one. Ignored when <see cref="Group"/> is set.
    /// </summary>
    public int Universe { get; set; } = -1;

    /// <summary>
    /// The fixture group it runs on, or "" to go by <see cref="Universe"/>.
    /// </summary>
    public string Group { get; set; } = "";

    public Waveform Waveform { get; set; } = Waveform.Sine;
    public WaveBase BaseMode { get; set; } = WaveBase.Absolute;

    /// <summary>
    /// Cycles a minute.
    /// </summary>
    public double Bpm
    {
        get => _bpm;
        set => _bpm = Math.Clamp(value, 0, MaxBpm);
    }

    /// <summary>
    /// The swing, peak to peak.
    /// </summary>
    public double Size { get; set; } = 128;

    /// <summary>
    /// The level it swings around, in <see cref="WaveBase.Absolute"/>.
    /// </summary>
    public double Center { get; set; } = 128;

    /// <summary>
    /// The phase spread across its fixtures, in degrees.
    /// </summary>
    public double FanDeg { get; set; }

    public double PhaseDeg { get; set; }

    /// <summary>
    /// Beats a cycle when following the tempo clock: while it's running, the effect runs at its tempo instead of
    /// <see cref="Bpm"/>, kept in phase with its beats. 0 always runs at <see cref="Bpm"/>.
    /// </summary>
    public double TempoBeats { get; set; }

    public bool Running { get; set; }

    /// <summary>
    /// Its strength, 0 to 1, set by a playback fader that runs it. An intensity role's output is scaled; any other
    /// role's swing is (a pan circle shrinks). Not saved.
    /// </summary>
    public double Level { get; set; } = 1;

    /// <summary>
    /// The rate it can actually run at: capped for motor roles.
    /// </summary>
    public double MaxBpm => MotorRoles.Contains(_role) ? MotorMaxBpm : 1200;

    public IReadOnlyList<ChannelTarget> Targets => _targets;

    public void SetTargets(IEnumerable<ChannelTarget> targets)
    {
        _targets = [.. targets];
        Array.Resize(ref _randomValues, _targets.Count);
        _randomStep = -1;
    }

    /// <summary>
    /// The cycles a minute it runs at: <see cref="Bpm"/>, or with <paramref name="tempo"/> (the tempo clock's, 0 when
    /// it's off) and <see cref="TempoBeats"/> set, one cycle per <see cref="TempoBeats"/> beats, capped like
    /// <see cref="Bpm"/>.
    /// </summary>
    public double Rate(double tempo = 0) => TempoBeats > 0 && tempo > 0 ? Math.Min(tempo / TempoBeats, MaxBpm) : Bpm;

    public void Advance(double delta, double tempo = 0) => _phase += delta * (Rate(tempo) / 60.0);

    /// <summary>
    /// A beat of the tempo clock (running at <paramref name="tempo"/>): a tempo-synced effect snaps to the nearest
    /// beat's point in its cycle, so it stays in phase with the music.
    /// </summary>
    public void OnBeat(double tempo)
    {
        if (TempoBeats <= 0 || tempo <= 0)
            return;
        if (Rate(tempo) < tempo / TempoBeats)
            return; // capped: it can't keep up
        _phase = Gd.Round(_phase * TempoBeats) / TempoBeats;
    }

    double Wave(double x)
    {
        var f = x - Math.Floor(x);
        return Waveform switch
        {
            Waveform.Triangle => 1 - 4 * Math.Abs(f - 0.5),
            Waveform.Sawtooth => 2 * f - 1,
            Waveform.Square => f < 0.5 ? 1 : -1,
            _ => Math.Sin(x * Math.Tau),
        };
    }

    /// <summary>
    /// Merges the effect's output into <paramref name="layers"/>, highest taking precedence. In
    /// <see cref="WaveBase.Pickup"/> the swing is around the channel's level in <paramref name="bases"/>.
    /// </summary>
    public void WriteInto(IReadOnlyList<Dictionary<int, int>> layers, IReadOnlyList<byte[]>? bases = null)
    {
        if (_targets.Count == 0)
            return;
        var level = Math.Clamp(Level, 0, 1);
        var intensity = Roles.Intensity.Contains(_role);
        var swing = Size * 0.5 * (intensity ? 1 : level);

        if (Waveform == Waveform.Random)
        {
            var step = (int)Math.Floor(_phase);
            if (step != _randomStep)
            {
                _randomStep = step;
                for (var i = 0; i < _targets.Count; i++)
                    _randomValues[i] = _random.NextDouble() * 2 - 1;
            }
        }

        for (var k = 0; k < _targets.Count; k++)
        {
            var (u, c) = _targets[k];
            if (u < 0 || u >= layers.Count)
                continue;
            var w = Waveform == Waveform.Random
                ? (k < _randomValues.Length ? _randomValues[k] : 0)
                : Wave(_phase + PhaseDeg / 360.0 + FanDeg / 360.0 * k);

            var mid = Center;
            if (BaseMode == WaveBase.Pickup && bases is not null && u < bases.Count)
                mid = c >= 0 && c < bases[u].Length ? bases[u][c] : 0;

            var output = mid + swing * w;
            if (intensity)
                output *= level;
            var v = Gd.Byte(output);
            layers[u][c] = Math.Max(layers[u].GetValueOrDefault(c), v);
        }
    }

    public JsonObject ToJson() =>
        new()
        {
            ["name"] = Name,
            ["role"] = _role,
            ["universe"] = Universe,
            ["group"] = Group,
            ["waveform"] = (int)Waveform,
            ["base_mode"] = (int)BaseMode,
            ["bpm"] = Bpm,
            ["size"] = Size,
            ["center"] = Center,
            ["fan_deg"] = FanDeg,
            ["phase_deg"] = PhaseDeg,
            ["tempo_beats"] = TempoBeats,
        };

    public static WaveEffect FromJson(JsonNode? d)
    {
        var e = new WaveEffect
        {
            Name = d.Str("name", "Effect"),
            Role = d.Str("role", Roles.Dimmer),
            Universe = d.Int("universe", -1),
            Group = d.Str("group", ""),
            Waveform = (Waveform)Math.Clamp(d.Int("waveform", 0), 0, Waveforms.Length - 1),
            BaseMode = (WaveBase)Math.Clamp(d.Int("base_mode", 0), 0, BaseModes.Length - 1),
            Size = Math.Clamp(d.Num("size", 128), 0, 255),
            Center = Math.Clamp(d.Num("center", 128), 0, 255),
            FanDeg = d.Num("fan_deg", 0),
            PhaseDeg = d.Num("phase_deg", 0),
            TempoBeats = Math.Max(d.Num("tempo_beats", 0), 0),
        };
        e.Bpm = d.Num("bpm", 60);
        return e;
    }
}

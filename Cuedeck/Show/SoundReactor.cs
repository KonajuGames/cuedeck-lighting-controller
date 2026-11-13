using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Fixtures;

namespace Cuedeck.Show;

/// <summary>
/// How loud the live audio is, smoothed, each band 0 to 1: what sound reactors follow. The audio input supplies it;
/// <see cref="Silence"/> stands in until there is one.
/// </summary>
interface ISoundLevels
{
    double Level { get; }
    double Bass { get; }
    double Mid { get; }
    double Treble { get; }
}

/// <summary>
/// No audio.
/// </summary>
sealed class Silence : ISoundLevels
{
    public static readonly Silence Instance = new();

    public double Level => 0;
    public double Bass => 0;
    public double Mid => 0;
    public double Treble => 0;
}

enum SoundBand
{
    Level,
    Bass,
    Mid,
    Treble,
}

enum ReactorMode
{
    /// <summary>
    /// The output follows the band continuously.
    /// </summary>
    Follow,

    /// <summary>
    /// Each beat snaps the output to <see cref="SoundReactor.High"/>, then it falls away.
    /// </summary>
    Pulse,
}

/// <summary>
/// An audio band (or the beat) driving a channel role across the patched fixtures that have it: the Sound Reactive
/// counterpart of a <see cref="WaveEffect"/>, laid over the stage while the console's in Sound Reactive run mode.
/// </summary>
sealed class SoundReactor
{
    public static readonly string[] Bands = ["Level", "Bass", "Mid", "Treble"];
    public static readonly string[] Modes = ["Follow", "Pulse"];

    double _value;
    List<ChannelTarget> _targets = [];

    public string Name { get; set; } = "Reactor";
    public SoundBand Band { get; set; } = SoundBand.Bass;
    public string Role { get; set; } = Roles.Dimmer;

    /// <summary>
    /// The universe slot it runs on, or -1 for every one. Ignored when <see cref="Group"/> is set.
    /// </summary>
    public int Universe { get; set; } = -1;
    public string Group { get; set; } = "";
    public ReactorMode Mode { get; set; } = ReactorMode.Follow;

    /// <summary>
    /// The DMX value when the band reads 0.
    /// </summary>
    public double Low { get; set; }

    /// <summary>
    /// The DMX value when the band reads 1.
    /// </summary>
    public double High { get; set; } = 255;

    /// <summary>
    /// 0 to 1: how quickly it rises to a louder band.
    /// </summary>
    public double Attack { get; set; } = 0.7;

    /// <summary>
    /// 0 to 1: how quickly it falls to a quieter band.
    /// </summary>
    public double Release { get; set; } = 0.12;

    /// <summary>
    /// 0 to 1: spreads the response along its fixtures, so they don't move as one block.
    /// </summary>
    public double Fan { get; set; }

    public bool Running { get; set; }

    public IReadOnlyList<ChannelTarget> Targets => _targets;

    public void SetTargets(IEnumerable<ChannelTarget> targets) => _targets = [.. targets];

    double Raw(ISoundLevels sound) =>
        Band switch
        {
            SoundBand.Bass => sound.Bass,
            SoundBand.Mid => sound.Mid,
            SoundBand.Treble => sound.Treble,
            _ => sound.Level,
        };

    /// <summary>
    /// Moves the smoothed drive on by <paramref name="delta"/> seconds; <paramref name="beat"/> on a beat.
    /// </summary>
    public void Advance(double delta, bool beat, ISoundLevels sound)
    {
        var target = Raw(sound);
        if (Mode == ReactorMode.Pulse)
        {
            if (beat)
                _value = 1;
            _value = Math.Max(0, _value - delta * Gd.Lerp(0.6, 4, Release));
            return;
        }
        var k = Math.Clamp((target > _value ? Attack : Release) * delta * 60, 0, 1);
        _value = Gd.Lerp(_value, target, k);
    }

    public void WriteInto(IReadOnlyList<Dictionary<int, int>> layers)
    {
        for (var i = 0; i < _targets.Count; i++)
        {
            var (u, c) = _targets[i];
            if (u < 0 || u >= layers.Count)
                continue;
            var drive = _value;
            if (Fan > 0 && _targets.Count > 1)
            {
                // Ripple the drive along the list.
                var ph = (double)i / (_targets.Count - 1);
                drive = Math.Clamp(_value - Fan * ph * (1 - _value), 0, 1);
            }
            var v = Gd.Byte(Gd.Lerp(Low, High, drive));
            layers[u][c] = Math.Max(layers[u].GetValueOrDefault(c), v);
        }
    }

    public JsonObject ToJson() =>
        new()
        {
            ["name"] = Name,
            ["band"] = (int)Band,
            ["role"] = Role,
            ["universe"] = Universe,
            ["group"] = Group,
            ["mode"] = (int)Mode,
            ["low"] = Low,
            ["high"] = High,
            ["attack"] = Attack,
            ["release"] = Release,
            ["fan"] = Fan,
        };

    public static SoundReactor FromJson(JsonNode? d) =>
        new()
        {
            Name = d.Str("name", "Reactor"),
            Band = (SoundBand)Math.Clamp(d.Int("band", (int)SoundBand.Bass), 0, Bands.Length - 1),
            Role = d.Str("role", Roles.Dimmer),
            Universe = d.Int("universe", -1),
            Group = d.Str("group", ""),
            Mode = (ReactorMode)Math.Clamp(d.Int("mode", 0), 0, Modes.Length - 1),
            Low = Math.Clamp(d.Num("low", 0), 0, 255),
            High = Math.Clamp(d.Num("high", 255), 0, 255),
            Attack = Math.Clamp(d.Num("attack", 0.7), 0, 1),
            Release = Math.Clamp(d.Num("release", 0.12), 0, 1),
            Fan = Math.Clamp(d.Num("fan", 0), 0, 1),
        };

    /// <summary>
    /// A starter set, so Sound Reactive does something the moment a fixture's patched: the three bands on roles most
    /// fixtures have (dimmer and RGB), and a strobe on the beat, left off as it's a lot more intense.
    /// </summary>
    public static List<SoundReactor> Defaults() =>
        [
            new() { Name = "Bass Pulse", Band = SoundBand.Bass, Role = Roles.Dimmer, Mode = ReactorMode.Pulse, Low = 20, High = 255, Release = 0.18, Running = true },
            new() { Name = "Mid Glow", Band = SoundBand.Mid, Role = Roles.Red, Mode = ReactorMode.Follow, Low = 0, High = 220, Attack = 0.5, Release = 0.25, Running = true },
            new() { Name = "Treble Sparkle", Band = SoundBand.Treble, Role = Roles.Blue, Mode = ReactorMode.Follow, Low = 0, High = 200, Attack = 0.6, Release = 0.2, Fan = 0.4, Running = true },
            new() { Name = "Beat Strobe", Band = SoundBand.Bass, Role = Roles.Strobe, Mode = ReactorMode.Pulse, Low = 0, High = 180, Release = 0.4, Running = false },
        ];
}

using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;

namespace Cuedeck.Show;

enum ChaseDirection
{
    Forward,
    Backward,
    Bounce,
}

/// <summary>
/// A chase: looks (its steps) cycled at a tempo. Unlike a cue, it runs on and on, and its output goes over the
/// operator's levels as a layer rather than replacing them.
/// </summary>
sealed class Chase
{
    public string Name { get; set; } = "Chase";
    public double Bpm { get; set; } = 120;

    /// <summary>
    /// The part of each step (0 to 1) spent crossfading in from the step before; 0 snaps.
    /// </summary>
    public double Crossfade { get; set; }

    public ChaseDirection Direction { get; set; } = ChaseDirection.Forward;

    /// <summary>
    /// Steps once a beat instead of by <see cref="Bpm"/>: the tempo clock's while it's running, else the beats heard
    /// in the audio in Sound Reactive run mode.
    /// </summary>
    public bool BeatSync { get; set; }

    public bool Running { get; set; }

    /// <summary>
    /// Its intensity, 0 to 1, set by a playback fader that runs it; scales its intensity channels. Not saved.
    /// </summary>
    public double Level { get; set; } = 1;

    public List<Look> Steps { get; } = [];

    int _pos;
    int _from;
    double _elapsed;
    int _bounce = 1;

    public double StepTime => 60.0 / Math.Max(Bpm, 1);

    /// <summary>
    /// The step showing now.
    /// </summary>
    public int Position => _pos;

    /// <summary>
    /// Adds the live levels as a new step.
    /// </summary>
    public void CaptureStep(DmxOutput dmx) => Steps.Add(Look.Capture(dmx));

    /// <summary>
    /// Replaces step <paramref name="index"/> with the live levels.
    /// </summary>
    public void UpdateStep(int index, DmxOutput dmx)
    {
        if (index < 0 || index >= Steps.Count)
            return;
        Steps[index] = Look.Capture(dmx);
        Reset();
    }

    /// <summary>
    /// Step <paramref name="index"/>'s look as a full buffer per universe, for loading into the fixture controls.
    /// </summary>
    public List<byte[]> StepBuffers(int index, int universes) =>
        (index >= 0 && index < Steps.Count ? Steps[index] : new Look()).Buffers(universes);

    public void Reset()
    {
        _pos = 0;
        _from = 0;
        _elapsed = 0;
        _bounce = 1;
    }

    public void Advance(double delta)
    {
        if (Steps.Count < 2)
            return;
        _elapsed += delta;
        var st = StepTime;
        while (_elapsed >= st)
        {
            _elapsed -= st;
            _from = _pos;
            AdvancePosition();
        }
    }

    /// <summary>
    /// Goes to the next step now, on a beat; a crossfade starts from here.
    /// </summary>
    public void BeatStep()
    {
        if (Steps.Count < 2)
            return;
        _from = _pos;
        _elapsed = 0;
        AdvancePosition();
    }

    void AdvancePosition()
    {
        switch (Direction)
        {
            case ChaseDirection.Backward:
                _pos = (_pos - 1 + Steps.Count) % Steps.Count;
                break;
            case ChaseDirection.Bounce:
                _pos += _bounce;
                if (_pos >= Steps.Count - 1)
                {
                    _pos = Steps.Count - 1;
                    _bounce = -1;
                }
                else if (_pos <= 0)
                {
                    _pos = 0;
                    _bounce = 1;
                }
                break;
            default:
                _pos = (_pos + 1) % Steps.Count;
                break;
        }
    }

    /// <summary>
    /// Merges the chase's output into <paramref name="layers"/>, highest taking precedence, with its
    /// <paramref name="intensity"/> channels (per universe) scaled by <see cref="Level"/>.
    /// </summary>
    public void WriteInto(IReadOnlyList<Dictionary<int, int>> layers, IReadOnlyList<IReadOnlySet<int>>? intensity = null)
    {
        if (Steps.Count == 0)
            return;
        var cur = Steps[Math.Clamp(_pos, 0, Steps.Count - 1)];
        Look? prev = null;
        var blend = 1.0;
        if (Crossfade > 0 && Steps.Count >= 2)
        {
            var fade = StepTime * Crossfade;
            if (fade > 0 && _elapsed < fade)
            {
                prev = Steps[Math.Clamp(_from, 0, Steps.Count - 1)];
                blend = _elapsed / fade;
            }
        }

        for (var u = 0; u < layers.Count; u++)
        {
            var touched = new List<int>();
            if (u < cur.Universes.Count)
                touched.AddRange(cur.Universes[u].Keys);
            if (prev is not null && u < prev.Universes.Count)
                foreach (var k in prev.Universes[u].Keys)
                    if (!touched.Contains(k))
                        touched.Add(k);
            foreach (var c in touched)
            {
                var b = cur.Level(u, c);
                var a = prev is not null ? prev.Level(u, c) : b;
                var v = Gd.Byte(a + (b - a) * blend);
                if (Level < 1 && intensity is not null && u < intensity.Count && intensity[u].Contains(c))
                    v = Gd.RoundToInt(v * Level);
                layers[u][c] = Math.Max(layers[u].GetValueOrDefault(c), v);
            }
        }
    }

    public JsonObject ToJson() =>
        new()
        {
            ["name"] = Name,
            ["bpm"] = Bpm,
            ["crossfade"] = Crossfade,
            ["direction"] = (int)Direction,
            ["beat_sync"] = BeatSync,
            ["steps"] = new JsonArray([.. Steps.Select(s => (JsonNode)s.ToJson())]),
        };

    public static Chase FromJson(JsonNode? d)
    {
        var c = new Chase
        {
            Name = d.Str("name", "Chase"),
            Bpm = d.Num("bpm", 120),
            Crossfade = Math.Clamp(d.Num("crossfade", 0), 0, 1),
            Direction = (ChaseDirection)Math.Clamp(d.Int("direction", 0), 0, 2),
            BeatSync = d.Bool("beat_sync", false),
        };
        foreach (var s in d.Arr("steps"))
            c.Steps.Add(Look.FromJson(s));
        return c;
    }
}

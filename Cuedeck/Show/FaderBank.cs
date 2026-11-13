using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;
using Cuedeck.Midi;

namespace Cuedeck.Show;

enum FaderKind
{
    Cue,
    Chase,
    Group,
    Effect,
}

/// <summary>
/// One playback fader: what it's on, how far up it is, and the MIDI control it's learned.
/// </summary>
sealed class Fader
{
    public string Name { get; set; } = "Fader";
    public FaderKind Kind { get; set; } = FaderKind.Cue;

    /// <summary>
    /// The cue's number, or the chase's, group's or effect's name.
    /// </summary>
    public string Target { get; set; } = "";

    /// <summary>
    /// 0 to 1.
    /// </summary>
    public double Level { get; set; }

    /// <summary>
    /// 0 to 15, or -1 for any.
    /// </summary>
    public int MidiChannel { get; set; } = -1;

    /// <summary>
    /// The controller it's on, or -1.
    /// </summary>
    public int MidiCc { get; set; } = -1;

    /// <summary>
    /// On its channel's pitch bend instead.
    /// </summary>
    public bool MidiBend { get; set; }

    /// <summary>
    /// ...or on a note (on is full, off is 0), or -1.
    /// </summary>
    public int MidiNote { get; set; } = -1;

    public bool IsMapped => MidiCc >= 0 || MidiBend || MidiNote >= 0;

    public void ForgetMidi()
    {
        MidiCc = -1;
        MidiBend = false;
        MidiNote = -1;
        MidiChannel = -1;
    }

    /// <summary>
    /// "CC 7 · ch 1", "Bend · ch 2", "Note 60 · ch 1", or "" when it isn't mapped.
    /// </summary>
    public string MidiText
    {
        get
        {
            var ch = MidiChannel < 0 ? "any" : (MidiChannel + 1).ToString();
            if (MidiBend)
                return $"Bend · ch {ch}";
            if (MidiNote >= 0)
                return $"Note {MidiNote} · ch {ch}";
            return MidiCc < 0 ? "" : $"CC {MidiCc} · ch {ch}";
        }
    }

    public JsonObject ToJson() =>
        new()
        {
            ["name"] = Name,
            ["kind"] = FaderBank.Kinds[(int)Kind].ToLowerInvariant(),
            ["target"] = Target,
            ["level"] = Level,
            ["midi_channel"] = MidiChannel,
            ["midi_cc"] = MidiCc,
            ["midi_bend"] = MidiBend,
            ["midi_note"] = MidiNote,
        };

    public static Fader FromJson(JsonNode? d) =>
        new()
        {
            Name = d.Str("name", "Fader"),
            Kind = (FaderKind)Math.Max(Array.FindIndex(FaderBank.Kinds, k => k.ToLowerInvariant() == d.Str("kind", "cue")), 0),
            Target = d.Str("target", ""),
            Level = Math.Clamp(d.Num("level", 0), 0, 1),
            MidiChannel = Math.Clamp(d.Int("midi_channel", -1), -1, 15),
            MidiCc = Math.Clamp(d.Int("midi_cc", -1), -1, 127),
            MidiBend = d.Bool("midi_bend", false),
            MidiNote = Math.Clamp(d.Int("midi_note", -1), -1, 127),
        };
}

/// <summary>
/// The playback faders (the console's Faders tab): each puts a cue, chase, fixture group or effect on a fader, which a
/// MIDI controller's fader or knob (a CC or pitch bend, learned), pad or key (a note) can move.
/// <list type="bullet">
/// <item>Cue: a submaster. The cue's standing look brought up by the fader: its intensity channels scaled by it and
/// merged highest-takes-precedence with the stage, its other channels at the look's values while the fader's up at
/// all. A layer under the chases and effects.</item>
/// <item>Chase: runs the chase while it's up, its intensity channels scaled by the fader; down to 0, the chase stops.</item>
/// <item>Group: a group master, scaling the group's intensity channels whatever drives them, after everything but the
/// live flashes. Up full, it does nothing.</item>
/// <item>Effect: runs the effect while it's up, at the fader's strength; down to 0, it stops.</item>
/// </list>
/// </summary>
sealed class FaderBank
{
    public static readonly string[] Kinds = ["Cue", "Chase", "Group", "Effect"];
    public const int MaxFaders = 24;

    /// <summary>
    /// The share of a MIDI fader's travel at each end that reads as 0 or full.
    /// </summary>
    public const double MidiEndZone = 0.02;

    readonly DmxOutput _dmx;
    readonly EffectsEngine _effects;
    readonly Patch _patch;
    readonly Func<IReadOnlyList<FixtureGroup>> _groups;
    readonly Func<int, Look?> _cueLook;
    int _learning = -1;
    readonly HashSet<Chase> _chaseHeld = [];
    readonly HashSet<WaveEffect> _effectHeld = [];

    public List<Fader> Faders { get; } = [];

    /// <summary>
    /// Starts (with its targets from the patch) or stops an effect by name. Without one, its Running is set directly.
    /// </summary>
    public Action<string, bool>? EffectRunner { get; set; }

    /// <summary>
    /// A fader moved, from MIDI or code.
    /// </summary>
    public event Action? LevelsChanged;

    /// <summary>
    /// Faders were added, removed, re-targeted or loaded.
    /// </summary>
    public event Action? FadersChanged;

    /// <summary>
    /// A MIDI Learn finished, for the fader at this index.
    /// </summary>
    public event Action<int>? Learned;

    /// <summary>
    /// A fader started or stopped a chase.
    /// </summary>
    public event Action? ChasesChanged;

    /// <summary>
    /// A fader started or stopped an effect.
    /// </summary>
    public event Action? EffectsChanged;

    /// <param name="cueLook">A cue's standing look by its number (1-based), or null if there's no such cue.</param>
    public FaderBank(DmxOutput dmx, EffectsEngine effects, Patch patch, Func<IReadOnlyList<FixtureGroup>> groups, Func<int, Look?> cueLook)
    {
        _dmx = dmx;
        _effects = effects;
        _patch = patch;
        _groups = groups;
        _cueLook = cueLook;
        _dmx.LayerProviders.Add(Layer);
        _dmx.ScaleProviders.Add(ScaleLayer);
        _effects.IntensityProvider = n => _patch.IntensityChannels(n);
    }

    public Fader? Add()
    {
        if (Faders.Count >= MaxFaders)
            return null;
        var f = new Fader { Name = $"Fader {Faders.Count + 1}" };
        Faders.Add(f);
        FadersChanged?.Invoke();
        return f;
    }

    public void Remove(int i)
    {
        if (i < 0 || i >= Faders.Count)
            return;
        SetLevel(i, 0); // lets go of its chase or effect
        Faders.RemoveAt(i);
        if (_learning == i)
            _learning = -1;
        FadersChanged?.Invoke();
    }

    /// <summary>
    /// Points fader <paramref name="i"/> at something else, letting go of what it had first.
    /// </summary>
    public void SetTarget(int i, FaderKind kind, string target)
    {
        if (i < 0 || i >= Faders.Count)
            return;
        var level = Faders[i].Level;
        SetLevel(i, 0);
        Faders[i].Kind = kind;
        Faders[i].Target = target;
        SetLevel(i, level);
        FadersChanged?.Invoke();
    }

    public void SetLevel(int i, double v)
    {
        if (i < 0 || i >= Faders.Count)
            return;
        var f = Faders[i];
        f.Level = Math.Clamp(v, 0, 1);
        if (f.Kind == FaderKind.Chase)
            DriveChase(f);
        else if (f.Kind == FaderKind.Effect)
            DriveEffect(f);
        LevelsChanged?.Invoke();
    }

    void DriveChase(Fader f)
    {
        if (_effects.FindChase(f.Target) is not { } c)
            return;
        if (f.Level > 0)
        {
            if (!c.Running)
            {
                c.Reset();
                c.Running = true;
                ChasesChanged?.Invoke();
            }
            _chaseHeld.Add(c);
            c.Level = f.Level;
        }
        else if (_chaseHeld.Remove(c))
        {
            c.Running = false;
            c.Level = 1;
            ChasesChanged?.Invoke();
        }
    }

    void DriveEffect(Fader f)
    {
        if (_effects.FindEffect(f.Target) is not { } e)
            return;
        if (f.Level > 0)
        {
            if (!e.Running)
            {
                if (EffectRunner is not null)
                    EffectRunner(e.Name, true);
                else
                    e.Running = true;
                if (!e.Running)
                    return; // nothing patched for it to run on
                EffectsChanged?.Invoke();
            }
            _effectHeld.Add(e);
            e.Level = f.Level;
        }
        else if (_effectHeld.Remove(e))
        {
            if (EffectRunner is not null)
                EffectRunner(e.Name, false);
            else
                e.Running = false;
            e.Level = 1;
            EffectsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Every cue, chase and effect fader to 0 (group masters only ever take light away, so they stay).
    /// </summary>
    public void PullDownPlayback()
    {
        for (var i = 0; i < Faders.Count; i++)
            if (Faders[i].Kind != FaderKind.Group)
                SetLevel(i, 0);
    }

    /// <summary>
    /// Every fader down and gone, chases and effects let go, before another show loads.
    /// </summary>
    public void Clear()
    {
        for (var i = 0; i < Faders.Count; i++)
            SetLevel(i, 0);
        Faders.Clear();
        _learning = -1;
        _chaseHeld.Clear();
        _effectHeld.Clear();
        FadersChanged?.Invoke();
    }

    // --------------------------------------------------------------- MIDI --

    /// <summary>
    /// Arms MIDI Learn for fader <paramref name="i"/>: the next CC, pitch bend or note-on maps it (-1 cancels).
    /// </summary>
    public void Learn(int i) => _learning = i >= 0 && i < Faders.Count ? i : -1;

    public bool IsLearning(int i) => _learning == i;

    /// <summary>
    /// A CC's or pitch bend's 0 to 1 as a fader level: each end of the travel reads as fully out or in, so a fader that
    /// stops just short of an end still gets there, and the rest is stretched evenly between them.
    /// </summary>
    public static double MidiLevel(double v) => Math.Clamp((v - MidiEndZone) / (1 - 2 * MidiEndZone), 0, 1);

    /// <summary>
    /// A MIDI message: finishes a Learn, or moves the faders mapped to it. Returns whether it was used.
    /// </summary>
    public bool HandleMidi(MidiMessage message)
    {
        var bend = message.Kind == MidiKind.PitchBend;
        var cc = message.Kind == MidiKind.ControlChange;
        var noteOn = message.IsPressed;
        var note = message.IsNote;
        if (!(bend || cc || note))
            return false;
        var value = bend ? MidiLevel(message.BendValue / 16383.0) : cc ? MidiLevel(message.Value / 127.0) : noteOn ? 1 : 0;
        if (_learning >= 0)
        {
            if (note && !noteOn)
                return false; // a note's learned by its press
            var f = Faders[_learning];
            f.MidiBend = bend;
            f.MidiCc = cc ? message.Number : -1;
            f.MidiNote = note ? message.Number : -1;
            f.MidiChannel = message.Channel;
            var i = _learning;
            _learning = -1;
            SetLevel(i, value);
            Learned?.Invoke(i);
            FadersChanged?.Invoke();
            return true;
        }
        var used = false;
        for (var i = 0; i < Faders.Count; i++)
        {
            var f = Faders[i];
            var hit = bend ? f.MidiBend : cc ? f.MidiCc == message.Number && !f.MidiBend && f.MidiNote < 0 : f.MidiNote == message.Number && !f.MidiBend;
            if (hit && (f.MidiChannel < 0 || f.MidiChannel == message.Channel))
            {
                SetLevel(i, value);
                used = true;
            }
        }
        return used;
    }

    // ------------------------------------------------------------- output --

    /// <summary>
    /// The cue faders' layer: <paramref name="n"/> {channel: value} maps.
    /// </summary>
    public IReadOnlyList<Dictionary<int, int>> Layer(int n)
    {
        var looks = new List<(Look Look, double Level)>();
        foreach (var f in Faders)
            if (f.Kind == FaderKind.Cue && f.Level > 0 && Json.IsValidInt(f.Target) && _cueLook(int.Parse(f.Target)) is { } look)
                looks.Add((look, f.Level));
        if (looks.Count == 0)
            return [.. Enumerable.Range(0, n).Select(_ => new Dictionary<int, int>())];
        var bases = Enumerable.Range(0, n).Select(i => _dmx.GetUniverse(i)?.Data ?? []).ToList();
        return CueOutput(looks, _patch.IntensityChannels(n), bases, n);
    }

    /// <summary>
    /// Cue submasters: intensity channels (<paramref name="intensity"/>, per universe) scaled by their fader, the
    /// highest winning over each other and the stage (<paramref name="bases"/>); every other channel a look holds at
    /// its value, the highest fader's where looks share one.
    /// </summary>
    public static List<Dictionary<int, int>> CueOutput(IReadOnlyList<(Look Look, double Level)> looks, IReadOnlyList<IReadOnlySet<int>> intensity, IReadOnlyList<byte[]> bases, int n)
    {
        var output = Enumerable.Range(0, n).Select(_ => new Dictionary<int, int>()).ToList();
        var othersLevel = Enumerable.Range(0, n).Select(_ => new Dictionary<int, double>()).ToList();
        foreach (var (look, level) in looks)
        {
            for (var u = 0; u < Math.Min(n, look.Universes.Count); u++)
            {
                var inten = u < intensity.Count ? intensity[u] : new HashSet<int>();
                var b = u < bases.Count ? bases[u] : [];
                foreach (var (c, raw) in look.Universes[u])
                {
                    var v = Math.Clamp(raw, 0, 255);
                    if (inten.Contains(c))
                    {
                        var scaled = Gd.RoundToInt(v * level);
                        var stage = c < b.Length ? b[c] : 0;
                        output[u][c] = Math.Max(Math.Max(output[u].GetValueOrDefault(c), scaled), stage);
                    }
                    else if (level > othersLevel[u].GetValueOrDefault(c, -1.0))
                    {
                        output[u][c] = v;
                        othersLevel[u][c] = level;
                    }
                }
            }
        }
        return output;
    }

    /// <summary>
    /// The group masters' scale layer: the lowest group fader over each of a group's fixtures' intensity channels.
    /// </summary>
    public IReadOnlyList<Dictionary<int, double>> ScaleLayer(int n)
    {
        var output = Enumerable.Range(0, n).Select(_ => new Dictionary<int, double>()).ToList();
        foreach (var f in Faders)
        {
            if (f.Kind != FaderKind.Group || f.Level >= 1)
                continue;
            var target = f.Target.Trim().ToLowerInvariant();
            var members = _groups().Where(g => g.Name.ToLowerInvariant() == target).SelectMany(g => g.Members).ToHashSet();
            if (members.Count == 0)
                continue;
            var chans = _patch.IntensityChannels(n, members);
            for (var u = 0; u < n; u++)
                foreach (var c in chans[u])
                    output[u][c] = Math.Min(output[u].GetValueOrDefault(c, 1.0), f.Level);
        }
        return output;
    }

    // ---------------------------------------------------------- show file --

    public JsonObject ToJson() => new() { ["faders"] = new JsonArray([.. Faders.Select(f => (JsonNode)f.ToJson())]) };

    public void FromJson(JsonNode? d)
    {
        Clear();
        foreach (var e in d.Arr("faders"))
            if (e is JsonObject && Faders.Count < MaxFaders)
                Faders.Add(Fader.FromJson(e));
        // A chase or effect fader that's up runs its chase.
        for (var i = 0; i < Faders.Count; i++)
            SetLevel(i, Faders[i].Level);
        FadersChanged?.Invoke();
    }
}

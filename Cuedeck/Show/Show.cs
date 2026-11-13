using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Dmx;

namespace Cuedeck.Show;

/// <summary>
/// A named set of fixtures, by uid, for effects, reactors and group masters to work on.
/// </summary>
sealed class FixtureGroup
{
    public string Name { get; set; } = "Group";
    public List<string> Members { get; } = [];
}

enum RunMode
{
    /// <summary>
    /// The cue list drives playback.
    /// </summary>
    Cues,

    /// <summary>
    /// The sound reactors follow the audio input.
    /// </summary>
    SoundReactive,
}

/// <summary>
/// The audio input settings saved with a show, for the Sound tab.
/// </summary>
sealed class SoundSettings
{
    /// <summary>
    /// The input device's name; "" for the system default.
    /// </summary>
    public string Device { get; set; } = "";
    public double Gain { get; set; } = 1;
    public double BeatSensitivity { get; set; } = 1.4;
    public double Response { get; set; } = 0.5;
}

/// <summary>
/// Everything a show is and does, without the screen: the universes and their output, the patch, groups, cues,
/// chases, effects, sound reactors, palettes, positions, playback faders and live flashes, all wired together as
/// sLight's shell wired its panels. The screen shows and edits it; <see cref="ShowFile"/> saves and loads it.
/// </summary>
sealed class LightingShow : IDisposable
{
    public DmxOutput Dmx { get; } = new();
    public TempoClock Tempo { get; } = new();
    public EffectsEngine Effects { get; }
    public Patch Patch { get; }
    public List<FixtureGroup> Groups { get; } = [];
    public PaletteLibrary Palettes { get; } = new();
    public PositionLibrary Positions { get; } = new();
    public CueList Cues { get; }
    public LiveFlash Flash { get; } = new();
    public FaderBank Faders { get; }
    public SoundSettings Sound { get; } = new();

    RunMode _runMode = RunMode.Cues;

    /// <summary>
    /// The parts of a show file not ported yet (triggers, the visualizer's stage), carried through loading and saving
    /// as they were, so nothing in a show is lost.
    /// </summary>
    public Dictionary<string, JsonNode?> Carried { get; } = [];

    /// <summary>
    /// The name of the folder beside the show file holding its songs' lighting timelines, or null.
    /// </summary>
    public string? TimelinesFolder { get; set; }

    public LightingShow()
    {
        Effects = new EffectsEngine(Tempo);
        Patch = new Patch(Dmx);
        Cues = new CueList(Dmx, Palettes, Patch);
        Faders = new FaderBank(Dmx, Effects, Patch, () => Groups, Cues.StandingLook) { EffectRunner = SetEffectRunning };
        Flash.Fixtures = () => Patch.All;
        Dmx.Compose = Effects.Compose;
        Dmx.TopProviders.Add(Flash.Layer);
    }

    public RunMode RunMode
    {
        get => _runMode;
        set
        {
            _runMode = Enum.IsDefined(value) ? value : RunMode.Cues;
            Effects.SoundReactive = _runMode == RunMode.SoundReactive;
        }
    }

    /// <summary>
    /// Runs the show on by <paramref name="delta"/> seconds and works out the frame, sending it with
    /// <paramref name="transmit"/>. Call at the refresh rate.
    /// </summary>
    public void Tick(double delta, bool transmit)
    {
        Tempo.Update(delta);
        Dmx.Advance(delta);
        Cues.Advance(delta);
        Effects.Advance(delta);
        Dmx.Tick(transmit);
    }

    /// <summary>
    /// The uids in group <paramref name="name"/>, or null if there's no such group.
    /// </summary>
    public IReadOnlySet<string>? GroupMembers(string name) => Groups.FirstOrDefault(g => g.Name == name) is { } g ? g.Members.ToHashSet() : null;

    /// <summary>
    /// The channels an effect or reactor on <paramref name="role"/> drives: in group <paramref name="group"/> if
    /// there's one by that name, else in universe slot <paramref name="universe"/> (-1 for every one).
    /// </summary>
    public List<ChannelTarget> Targets(string role, int universe, string group) =>
        Patch.Targets(role, universe, group != "" ? GroupMembers(group) : null);

    /// <summary>
    /// Starts or stops effect <paramref name="name"/> (by name, case-insensitive, or 1-based number). Starting finds
    /// its channels in the patch, and doesn't start it if there are none.
    /// </summary>
    public void SetEffectRunning(string name, bool on)
    {
        var e = Effects.FindEffect(name);
        if (e is null && Json.IsValidInt(name.Trim()) && int.Parse(name.Trim()) is var n && n >= 1 && n <= Effects.Effects.Count)
            e = Effects.Effects[n - 1];
        if (e is null || e.Running == on)
            return;
        if (on)
        {
            e.SetTargets(Targets(e.Role, e.Universe, e.Group));
            if (e.Targets.Count == 0)
                return;
        }
        e.Running = on;
    }

    /// <summary>
    /// Finds every effect's and reactor's channels in the patch again, after it's changed.
    /// </summary>
    public void Retarget()
    {
        foreach (var e in Effects.Effects)
            e.SetTargets(Targets(e.Role, e.Universe, e.Group));
        foreach (var r in Effects.Reactors)
            r.SetTargets(Targets(r.Role, r.Universe, r.Group));
    }

    /// <summary>
    /// Every universe to 0, the fade and anything waiting stopped, chases and effects stopped and the playback faders
    /// down: the console's Blackout.
    /// </summary>
    public void BlackoutAll()
    {
        Cues.Halt(); // its fade, and any delay or follow waiting
        Faders.PullDownPlayback(); // cue, chase and effect faders to 0
        Effects.StopAll();
        foreach (var u in Dmx.Universes)
            u.SetAll(0);
    }

    /// <summary>
    /// Loads a look (a full buffer per universe: a cue's, a chase step's, a palette applied) into the fixture
    /// controls, stopping any fade, so it can be tweaked and recorded again. Virtual dimmers open, as a look holds the
    /// colour as it's stored.
    /// </summary>
    public void LoadLook(IReadOnlyList<byte[]> buffers)
    {
        Dmx.StopFade();
        for (var i = 0; i < Math.Min(buffers.Count, Dmx.UniverseCount); i++)
            Patch.LoadLook(i, buffers[i]);
    }

    /// <summary>
    /// Removes universe slot <paramref name="index"/> (never the last one); its fixtures leave their groups.
    /// </summary>
    public void RemoveUniverse(int index)
    {
        if (index < 0 || index >= Patch.Universes.Count || Patch.Universes.Count <= 1)
            return;
        var gone = Patch.Universes[index].Fixtures.Select(f => f.Uid).ToHashSet();
        foreach (var g in Groups)
            g.Members.RemoveAll(gone.Contains);
        Patch.RemoveUniverse(index);
    }

    /// <summary>
    /// Starts a new show: one universe (sent where universe 1 was), nothing patched, everything dark, no cues, chases,
    /// effects, groups, palettes, positions or faders, and the starter sound reactors.
    /// </summary>
    public void NewShow()
    {
        var connection = Patch.PatchToJson(0);
        connection["fixtures"] = new JsonArray();
        ShowFile.Apply(this, new JsonObject { ["universes"] = new JsonArray(connection), ["live"] = new JsonArray(new JsonObject()) });
    }

    public void Dispose() => Dmx.Dispose();
}

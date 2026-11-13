using System.Text.Json.Nodes;
using Cuedeck.Audio;
using Cuedeck.Midi;
using Cuedeck.Triggers;
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

    /// <summary>
    /// The live audio input, listening in Sound Reactive: what reactors and beat-synced chases follow.
    /// </summary>
    public SoundEngine Audio { get; }

    /// <summary>
    /// The MIDI and OSC triggers: what they fire runs here.
    /// </summary>
    public TriggersEngine Triggers { get; } = new();

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

    /// <param name="capture">The audio input to listen to in Sound Reactive; none if null (as in tests).</param>
    public LightingShow(IAudioCapture? capture = null)
    {
        Audio = new SoundEngine(Sound, capture ?? new NoCapture());
        Effects = new EffectsEngine(Tempo) { Sound = Audio };
        Triggers.Fired += RunTrigger;
        Triggers.Released += ReleaseTrigger;
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
            Audio.Active = _runMode == RunMode.SoundReactive;
        }
    }

    /// <summary>
    /// Runs the show on by <paramref name="delta"/> seconds and works out the frame, sending it with
    /// <paramref name="transmit"/>. Call at the refresh rate.
    /// </summary>
    public void Tick(double delta, bool transmit)
    {
        // In sLight's autoload order: the audio, then the tempo clock, then the effects.
        if (Audio.Update(delta))
            Effects.OnSoundBeat();
        Tempo.Update(delta);
        Triggers.Update();
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

    /// <summary>
    /// A preset: every universe's live levels (the channels that aren't 0), as a file holds them.
    /// </summary>
    public JsonObject PresetToJson()
    {
        var universes = new JsonArray();
        foreach (var u in Dmx.Universes)
        {
            var channels = new Dictionary<int, int>();
            for (var c = 0; c < DmxUniverse.Size; c++)
                if (u.Data[c] != 0)
                    channels[c] = u.Data[c];
            universes.Add(new JsonObject { ["channels"] = Look.ChannelsToJson(channels) });
        }
        return new JsonObject { ["universes"] = universes };
    }

    /// <summary>
    /// Puts a preset's levels on the universes, stopping any fade and every chase, effect and reactor. Universes are
    /// added (never taken away) so a preset wider than the show still loads. An older preset, a bare
    /// {"channel": value} object, is universe 1's. Returns why it couldn't, or null.
    /// </summary>
    public string? ApplyPreset(JsonNode? preset)
    {
        JsonArray universes;
        if (preset is JsonObject o && o.Get("universes") is JsonArray list)
            universes = list;
        else if (preset is JsonObject flat)
            universes = [new JsonObject { ["channels"] = flat.DeepClone() }];
        else
            return "Preset file corrupt.";
        Dmx.StopFade();
        Effects.StopAll();
        var n = Math.Clamp(universes.Count, 1, DmxOutput.MaxUniverses);
        Dmx.SetUniverseCount(Math.Max(Dmx.UniverseCount, n));
        Patch.SyncToUniverses();
        for (var i = 0; i < Math.Min(universes.Count, Dmx.UniverseCount); i++)
        {
            var u = Dmx.Universes[i];
            u.SetAll(0);
            foreach (var (c, v) in Look.ChannelsFromJson(universes[i].Get("channels")))
                u.SetChannel(c, v);
        }
        return null;
    }

    /// <summary>
    /// A MIDI message from any input: the tempo clock takes its clock, the faders and the triggers whatever they're
    /// mapped to or learning. Call on the show's thread.
    /// </summary>
    public void HandleMidi(MidiMessage m)
    {
        if (Tempo.MidiClockEnabled && m.Kind == MidiKind.Clock)
            Tempo.ClockPulse();
        else if (Tempo.MidiClockEnabled && m.Kind == MidiKind.Start)
            Tempo.ClockStart();
        Triggers.HandleMidi(m);
        Faders.HandleMidi(m);
    }

    /// <summary>
    /// Runs the console action a trigger's bound to: sLight's shell's <c>_on_trigger_fired</c>.
    /// </summary>
    public void RunTrigger(TriggerAction action, string target)
    {
        switch (action)
        {
            case TriggerAction.CueGo:
                Cues.Go();
                break;
            case TriggerAction.CueBack:
                Cues.GoBack();
                break;
            case TriggerAction.CueHalt:
                Cues.Halt();
                break;
            case TriggerAction.CueGoTo:
                Cues.GoTo(Json.IsValidInt(target.Trim()) ? int.Parse(target.Trim(), System.Globalization.CultureInfo.InvariantCulture) : 1);
                break;
            case TriggerAction.ChaseToggle:
                if (FindChase(target) is { } c)
                    SetChaseRunning(c, !c.Running);
                break;
            case TriggerAction.EffectToggle:
                if (FindEffect(target) is { } e)
                    SetEffectRunning(e.Name, !e.Running);
                break;
            case TriggerAction.Blackout:
                BlackoutAll();
                break;
            case TriggerAction.TapTempo:
                Tempo.Tap();
                break;
            case TriggerAction.FlashBlackout or TriggerAction.FlashStrobe or TriggerAction.FlashWhite or TriggerAction.FlashColour:
                var kind = (FlashKind)(action - TriggerAction.FlashBlackout);
                if (target == Trigger.FLASH_TOGGLE)
                    Flash.Toggle(kind);
                else
                    Flash.Press(kind);
                break;
        }
    }

    /// <summary>
    /// A flash trigger's pad came back up.
    /// </summary>
    public void ReleaseTrigger(TriggerAction action, string target)
    {
        if (action is >= TriggerAction.FlashBlackout and <= TriggerAction.FlashColour)
            Flash.Release((FlashKind)(action - TriggerAction.FlashBlackout));
    }

    /// <summary>
    /// A chase by name (case-insensitive), or by its 1-based number.
    /// </summary>
    public Chase? FindChase(string key)
    {
        if (Effects.FindChase(key) is { } c)
            return c;
        var low = key.Trim();
        return Json.IsValidInt(low) && int.Parse(low, System.Globalization.CultureInfo.InvariantCulture) is var n && n >= 1 && n <= Effects.Chases.Count ? Effects.Chases[n - 1] : null;
    }

    /// <summary>
    /// An effect by name (case-insensitive), or by its 1-based number.
    /// </summary>
    public WaveEffect? FindEffect(string key)
    {
        if (Effects.FindEffect(key) is { } e)
            return e;
        var low = key.Trim();
        return Json.IsValidInt(low) && int.Parse(low, System.Globalization.CultureInfo.InvariantCulture) is var n && n >= 1 && n <= Effects.Effects.Count ? Effects.Effects[n - 1] : null;
    }

    /// <summary>
    /// Starts a chase from its first step, or stops it.
    /// </summary>
    public void SetChaseRunning(Chase chase, bool on)
    {
        if (chase.Running == on)
            return;
        if (on)
            chase.Reset();
        chase.Running = on;
    }

    public void Dispose()
    {
        Triggers.Dispose();
        Audio.Dispose();
        Dmx.Dispose();
    }
}

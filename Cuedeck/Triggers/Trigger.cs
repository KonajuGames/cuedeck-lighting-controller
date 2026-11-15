using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Midi;
using Cuedeck.Show;

namespace Cuedeck.Triggers;

enum TriggerSource
{
    Midi,
    Osc,
}

enum TriggerMidiKind
{
    Note,
    ControlChange,
    ProgramChange,
    PitchBend,
}

/// <summary>
/// What a trigger does. The numbers are sLight's and are saved in shows, so ones added later go on the end
/// (<see cref="CueClear"/> is Cuedeck's own).
/// </summary>
enum TriggerAction
{
    CueGo,
    CueBack,
    CueHalt,
    CueGoTo,
    ChaseToggle,
    EffectToggle,
    Blackout,
    None,
    TapTempo,
    FlashBlackout,
    FlashStrobe,
    FlashWhite,
    FlashColour,
    CueClear,
}

/// <summary>
/// What a trigger's feedback light follows: its own action's state, or something about the console.
/// </summary>
enum FeedbackWatch
{
    Action,
    Beat,
    Sending,
    AnyRunning,
    CueMode,
    SoundMode,
}

/// <summary>
/// What a Learn heard: a MIDI message's kind, channel and number, or an OSC address.
/// </summary>
sealed record Learned(TriggerSource Source, TriggerMidiKind Kind = TriggerMidiKind.Note, int Channel = -1, int Number = 0, string Address = "");

/// <summary>
/// One binding: an incoming MIDI message or OSC address fires one console action (a cue GO, Back, Halt, Clear or Go To, a
/// chase or effect toggled, Blackout, Tap tempo, or a live flash held). It can also light its pad to follow some
/// state. Kept in <see cref="TriggersEngine.Triggers"/>, edited in the Triggers dialog, saved in the show. sLight's
/// <c>Trigger</c>.
/// </summary>
sealed class Trigger
{
    public static readonly string[] Sources = ["MIDI", "OSC"];
    public static readonly string[] MidiKinds = ["Note", "Control Change", "Program Change", "Pitch Bend"];

    public static readonly string[] Actions =
    [
        "Cue — GO",
        "Cue — Back",
        "Cue — Halt",
        "Cue — Go to #",
        "Chase — toggle",
        "Effect — toggle",
        "Blackout all",
        "(nothing — feedback only)",
        "Tap tempo",
        "Flash — Blackout",
        "Flash — Strobe",
        "Flash — White",
        "Flash — Colour",
        "Cue — Clear",
    ];

    /// <summary>
    /// The actions in the order the Triggers dialog lists them: Clear with the other cue actions, though it's saved
    /// as the newest number.
    /// </summary>
    public static readonly TriggerAction[] MenuOrder =
    [
        TriggerAction.CueGo,
        TriggerAction.CueBack,
        TriggerAction.CueHalt,
        TriggerAction.CueClear,
        TriggerAction.CueGoTo,
        TriggerAction.ChaseToggle,
        TriggerAction.EffectToggle,
        TriggerAction.Blackout,
        TriggerAction.None,
        TriggerAction.TapTempo,
        TriggerAction.FlashBlackout,
        TriggerAction.FlashStrobe,
        TriggerAction.FlashWhite,
        TriggerAction.FlashColour,
    ];

    public static readonly string[] FeedbackWatches =
    [
        "this binding's action",
        "Beat pulse",
        "Sending is on",
        "Any chase / effect running",
        "Run mode: Cue",
        "Run mode: Sound Reactive",
    ];

    /// <summary>
    /// The feedback watches as saved, by name, so the list can change without old shows' choices shifting.
    /// </summary>
    static readonly string[] FeedbackKeys = ["action", "beat", "sending", "fx_any", "mode_cue", "mode_sound"];

    /// <summary>
    /// The numbers shows saved before the names, when the list had Auto Show entries (null: those, now gone).
    /// </summary>
    static readonly FeedbackWatch?[] LegacyWatches =
    [
        FeedbackWatch.Action,
        FeedbackWatch.Beat,
        FeedbackWatch.Sending,
        FeedbackWatch.AnyRunning,
        null,
        FeedbackWatch.CueMode,
        FeedbackWatch.SoundMode,
        null,
    ];

    static readonly TriggerAction[] Flashes = [TriggerAction.FlashBlackout, TriggerAction.FlashStrobe, TriggerAction.FlashWhite, TriggerAction.FlashColour];

    /// <summary>
    /// A pitch bend (0 to 16383, 8192 the centre) counts as pressed from three-quarters of the way up, so a wheel at
    /// rest, or a fader sending bend at the bottom, is up.
    /// </summary>
    public const int BEND_PRESS = 12288;

    /// <summary>
    /// The target a flash fires with when it should toggle (from a Program Change, which has no release).
    /// </summary>
    public const string FLASH_TOGGLE = "toggle";

    public string Name { get; set; } = "Trigger";
    public bool Enabled { get; set; } = true;
    public TriggerSource Source { get; set; } = TriggerSource.Midi;

    public TriggerMidiKind MidiKind { get; set; } = TriggerMidiKind.Note;

    /// <summary>
    /// 0 to 15, or -1 for any channel.
    /// </summary>
    public int MidiChannel { get; set; } = -1;

    /// <summary>
    /// The note, controller or program number (a pitch bend has none).
    /// </summary>
    public int MidiNumber { get; set; } = 60;

    public string OscAddress { get; set; } = "/go";

    public TriggerAction Action { get; set; } = TriggerAction.CueGo;

    /// <summary>
    /// A cue's number as text, or a chase's or effect's name (or number).
    /// </summary>
    public string Target { get; set; } = "";

    public bool FeedbackEnabled { get; set; }
    public FeedbackWatch FeedbackWatch { get; set; } = FeedbackWatch.Action;

    /// <summary>
    /// The note velocity or CC value sent while what it follows is on, and while it's off.
    /// </summary>
    public int FeedbackOn { get; set; } = 127;
    public int FeedbackOff { get; set; }

    public bool NeedsTarget => Action is TriggerAction.CueGoTo or TriggerAction.ChaseToggle or TriggerAction.EffectToggle;

    public bool FiresAction => Action != TriggerAction.None;

    /// <summary>
    /// A live flash, held while the pad's down.
    /// </summary>
    public bool IsFlash => Flashes.Contains(Action);

    /// <summary>
    /// Which flash it works, for a flash action.
    /// </summary>
    public FlashKind? Flash => IsFlash ? (FlashKind)Array.IndexOf(Flashes, Action) : null;

    public static TriggerAction FlashAction(FlashKind kind) => Flashes[(int)kind];

    /// <summary>
    /// Whether its feedback has a steady state to follow: a watch of its own, or an action that has one.
    /// </summary>
    public bool CanFeedback => FeedbackWatch != FeedbackWatch.Action || Action is TriggerAction.CueGoTo or TriggerAction.ChaseToggle or TriggerAction.EffectToggle || IsFlash;

    /// <summary>
    /// What the feedback asks the console about: ("cue", "5"), ("chase", name), ("flash", "2"), ("run_mode", "1"),
    /// ("beat", "") for a pulse, or ("", "") for nothing.
    /// </summary>
    public (string Kind, string Target) FeedbackQuery =>
        FeedbackWatch switch
        {
            FeedbackWatch.Beat => ("beat", ""),
            FeedbackWatch.Sending => ("sending", ""),
            FeedbackWatch.AnyRunning => ("fx_any", ""),
            FeedbackWatch.CueMode => ("run_mode", "0"),
            FeedbackWatch.SoundMode => ("run_mode", "1"),
            _ => Action switch
            {
                TriggerAction.CueGoTo => ("cue", Target),
                TriggerAction.ChaseToggle => ("chase", Target),
                TriggerAction.EffectToggle => ("effect", Target),
                _ => Flash is { } kind ? ("flash", ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture)) : ("", ""),
            },
        };

    /// <summary>
    /// The match uses <see cref="MidiNumber"/> (a pitch bend is the channel's own).
    /// </summary>
    public bool UsesNumber => MidiKind != TriggerMidiKind.PitchBend;

    /// <summary>
    /// What it listens for: "MIDI  CC 21  ch any", "OSC  /go".
    /// </summary>
    public string SourceSummary
    {
        get
        {
            if (Source == TriggerSource.Osc)
                return $"OSC  {OscAddress}";
            var ch = MidiChannel < 0 ? "any" : (MidiChannel + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return MidiKind switch
            {
                TriggerMidiKind.ControlChange => $"MIDI  CC {MidiNumber}  ch {ch}",
                TriggerMidiKind.ProgramChange => $"MIDI  PC {MidiNumber}  ch {ch}",
                TriggerMidiKind.PitchBend => $"MIDI  Pitch Bend  ch {ch}",
                _ => $"MIDI  Note {MidiNumber}  ch {ch}",
            };
        }
    }

    bool OnChannel(MidiMessage m) => MidiChannel < 0 || m.Channel == MidiChannel;

    /// <summary>
    /// Whether a MIDI message presses it: a note-on with velocity, a CC at 64 or more, its program, or a bend past
    /// <see cref="BEND_PRESS"/>.
    /// </summary>
    public bool Matches(MidiMessage m)
    {
        if (Source != TriggerSource.Midi || !OnChannel(m))
            return false;
        return MidiKind switch
        {
            TriggerMidiKind.Note => m.Kind == Midi.MidiKind.NoteOn && m.Number == MidiNumber && m.Value > 0,
            TriggerMidiKind.ControlChange => m.Kind == Midi.MidiKind.ControlChange && m.Number == MidiNumber && m.Value >= 64,
            TriggerMidiKind.ProgramChange => m.Kind == Midi.MidiKind.ProgramChange && m.Number == MidiNumber,
            TriggerMidiKind.PitchBend => m.Kind == Midi.MidiKind.PitchBend && m.BendValue >= BEND_PRESS,
            _ => false,
        };
    }

    /// <summary>
    /// Whether a MIDI message is its pad coming back up: a note-off (or note-on at velocity 0), its CC below 64, its
    /// bend below <see cref="BEND_PRESS"/>. A Program Change has no release.
    /// </summary>
    public bool MatchesRelease(MidiMessage m)
    {
        if (Source != TriggerSource.Midi || !OnChannel(m))
            return false;
        return MidiKind switch
        {
            TriggerMidiKind.Note => m.Number == MidiNumber && (m.Kind == Midi.MidiKind.NoteOff || m.Kind == Midi.MidiKind.NoteOn && m.Value == 0),
            TriggerMidiKind.ControlChange => m.Kind == Midi.MidiKind.ControlChange && m.Number == MidiNumber && m.Value < 64,
            TriggerMidiKind.PitchBend => m.Kind == Midi.MidiKind.PitchBend && m.BendValue < BEND_PRESS,
            _ => false,
        };
    }

    /// <summary>
    /// Whether an OSC message presses it: its address with no arguments, or a first one that's non-zero (or true).
    /// </summary>
    public bool Matches(OscMessage m)
    {
        if (Source != TriggerSource.Osc || m.Address != OscAddress)
            return false;
        if (m.Args.Count == 0)
            return true;
        return m.Args[0] switch
        {
            bool b => b,
            int i => i != 0,
            float f => Math.Abs(f) > 0.0001,
            _ => true,
        };
    }

    /// <summary>
    /// Whether an OSC message releases it: its address with a first argument of 0 (or false).
    /// </summary>
    public bool MatchesRelease(OscMessage m)
    {
        if (Source != TriggerSource.Osc || m.Address != OscAddress || m.Args.Count == 0)
            return false;
        return m.Args[0] switch
        {
            bool b => !b,
            int i => i == 0,
            float f => Math.Abs(f) <= 0.0001,
            _ => false,
        };
    }

    /// <summary>
    /// Takes its match from what a Learn heard.
    /// </summary>
    public void LearnFrom(Learned heard)
    {
        Source = heard.Source;
        if (Source == TriggerSource.Osc)
        {
            OscAddress = heard.Address;
            return;
        }
        MidiKind = heard.Kind;
        MidiChannel = heard.Channel;
        MidiNumber = heard.Number;
    }

    public JsonObject ToJson() =>
        new()
        {
            ["name"] = Name,
            ["enabled"] = Enabled,
            ["source"] = (int)Source,
            ["midi_kind"] = (int)MidiKind,
            ["midi_channel"] = MidiChannel,
            ["midi_number"] = MidiNumber,
            ["osc_address"] = OscAddress,
            ["action"] = (int)Action,
            ["target"] = Target,
            ["fb_enabled"] = FeedbackEnabled,
            ["fb_watch_key"] = FeedbackKeys[(int)FeedbackWatch],
            ["fb_on"] = FeedbackOn,
            ["fb_off"] = FeedbackOff,
        };

    public static Trigger FromJson(JsonNode? d)
    {
        var t = new Trigger
        {
            Name = d.Str("name", "Trigger"),
            Enabled = d.Bool("enabled", true),
            Source = (TriggerSource)Math.Clamp(d.Int("source", 0), 0, Sources.Length - 1),
            MidiKind = (TriggerMidiKind)Math.Clamp(d.Int("midi_kind", 0), 0, MidiKinds.Length - 1),
            MidiChannel = Math.Clamp(d.Int("midi_channel", -1), -1, 15),
            MidiNumber = Math.Clamp(d.Int("midi_number", 60), 0, 127),
            OscAddress = d.Str("osc_address", "/go"),
            Action = d.Int("action", 0) is var a && Enum.IsDefined((TriggerAction)a) ? (TriggerAction)a : TriggerAction.None, // an unknown one does nothing
            Target = d.Str("target", ""),
            FeedbackEnabled = d.Bool("fb_enabled", false),
            FeedbackOn = Math.Clamp(d.Int("fb_on", 127), 0, 127),
            FeedbackOff = Math.Clamp(d.Int("fb_off", 0), 0, 127),
        };
        if (d.Has("fb_watch_key"))
            t.FeedbackWatch = (FeedbackWatch)Math.Max(Array.IndexOf(FeedbackKeys, d.Str("fb_watch_key")), 0);
        else
        {
            var old = LegacyWatches[Math.Clamp(d.Int("fb_watch", 0), 0, LegacyWatches.Length - 1)];
            if (old is null)
                t.FeedbackEnabled = false;
            t.FeedbackWatch = old ?? FeedbackWatch.Action;
        }
        return t;
    }

    public Trigger Clone() => FromJson(ToJson());
}

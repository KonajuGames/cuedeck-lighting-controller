using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Midi;

namespace Cuedeck.Triggers;

/// <summary>
/// Listens for MIDI (handed in by the app from every connected input) and OSC (a UDP listener on
/// <see cref="OscPort"/>) and fires the console actions the <see cref="Triggers"/> are bound to, through
/// <see cref="Fired"/> and <see cref="Released"/>. Learn takes the next message heard as a binding's match instead.
/// sLight's <c>Triggers</c> autoload; its feedback settings are kept and saved here, and sent from slice 3c.
/// </summary>
sealed class TriggersEngine : IDisposable
{
    readonly Dictionary<Trigger, bool> _bendDown = [];
    UdpClient? _osc;
    bool _learning;

    public List<Trigger> Triggers { get; } = [];

    public bool MidiEnabled { get; set; } = true;

    /// <summary>
    /// The MIDI inputs being listened to, by name: the app's, none in tests.
    /// </summary>
    public Func<IReadOnlyList<string>> MidiInputs { get; set; } = () => [];

    /// <summary>
    /// Looks for MIDI inputs plugged in since (the app checks every couple of seconds anyway).
    /// </summary>
    public Action RescanMidi { get; set; } = () => { };
    public bool OscEnabled { get; private set; }
    public int OscPort { get; private set; } = 9000;

    /// <summary>
    /// Where the OSC listener stands: "off", "listening on :9000", "port :9000 unavailable".
    /// </summary>
    public string OscState { get; private set; } = "off";

    // Feedback, sent from slice 3c: saved with the show meanwhile.
    public bool FeedbackEnabled { get; set; }
    public int MidiOutPort { get; set; } = 9010;
    public string MidiOutPortName { get; set; } = "";
    public string OscOutHost { get; set; } = "127.0.0.1";
    public int OscOutPort { get; set; } = 9001;

    /// <summary>
    /// A binding was pressed: its action and target.
    /// </summary>
    public event Action<TriggerAction, string>? Fired;

    /// <summary>
    /// A flash binding's pad came back up (a note-off, a CC below 64, an OSC 0).
    /// </summary>
    public event Action<TriggerAction, string>? Released;

    /// <summary>
    /// The last message heard, to show.
    /// </summary>
    public event Action<string>? Activity;

    /// <summary>
    /// A Learn heard its message.
    /// </summary>
    public event Action<Learned>? Learned;

    public bool Learning => _learning;

    /// <summary>
    /// Takes the next MIDI or OSC message heard as what to learn, rather than matching it.
    /// </summary>
    public void StartLearn() => _learning = true;

    public void CancelLearn() => _learning = false;

    /// <summary>
    /// Turns the OSC listener on or off, on <paramref name="port"/>.
    /// </summary>
    public void SetOsc(bool enabled, int port)
    {
        OscEnabled = enabled;
        OscPort = Math.Clamp(port, 1, 65535);
        _osc?.Dispose();
        _osc = null;
        if (!OscEnabled)
        {
            OscState = "off";
            return;
        }
        try
        {
            _osc = new UdpClient(new IPEndPoint(IPAddress.Any, OscPort));
            OscState = $"listening on :{OscPort}";
        }
        catch (SocketException)
        {
            OscEnabled = false;
            OscState = $"port :{OscPort} unavailable";
        }
    }

    /// <summary>
    /// Reads the OSC packets that have come in. Call once a frame.
    /// </summary>
    public void Update()
    {
        if (_osc is null)
            return;
        try
        {
            while (_osc.Available > 0)
            {
                IPEndPoint? from = null;
                HandleOscPacket(_osc.Receive(ref from));
            }
        }
        catch (SocketException) { }
    }

    public void HandleOscPacket(ReadOnlySpan<byte> packet)
    {
        foreach (var m in OscMessage.Parse(packet))
            HandleOsc(m);
    }

    public void HandleOsc(OscMessage m)
    {
        if (_learning)
        {
            _learning = false;
            Learned?.Invoke(new Learned(TriggerSource.Osc, Address: m.Address));
            return;
        }
        var args = m.Args.Count == 0 ? "" : "  [" + string.Join(", ", m.Args.Select(Show)) + "]";
        Activity?.Invoke($"OSC  {m.Address}{args}");
        foreach (var t in Triggers.ToList())
        {
            if (t.Enabled && t.FiresAction && t.Matches(m))
                Fired?.Invoke(t.Action, t.Target);
            else if (t.Enabled && t.IsFlash && t.MatchesRelease(m))
                Released?.Invoke(t.Action, t.Target);
        }
    }

    static string Show(object? arg) =>
        arg switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToString(arg, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };

    /// <summary>
    /// A MIDI message from any input: finishes a Learn, or fires and releases the bindings it matches. Clock,
    /// aftertouch and the like are left alone.
    /// </summary>
    public void HandleMidi(MidiMessage m)
    {
        if (!MidiEnabled || m.Kind is not (MidiKind.NoteOn or MidiKind.NoteOff or MidiKind.ControlChange or MidiKind.ProgramChange or MidiKind.PitchBend))
            return;

        if (_learning)
        {
            if (m.Kind == MidiKind.NoteOff || m.Kind == MidiKind.NoteOn && m.Value == 0)
                return; // a note's learned by its press
            _learning = false;
            var (kind, number) = m.Kind switch
            {
                MidiKind.ControlChange => (TriggerMidiKind.ControlChange, m.Number),
                MidiKind.ProgramChange => (TriggerMidiKind.ProgramChange, m.Number),
                MidiKind.PitchBend => (TriggerMidiKind.PitchBend, 0),
                _ => (TriggerMidiKind.Note, m.Number),
            };
            Learned?.Invoke(new Learned(TriggerSource.Midi, kind, m.Channel, number));
            return;
        }

        Report(m);
        foreach (var t in Triggers.ToList())
        {
            if (!(t.Enabled && t.FiresAction))
                continue;
            if (t.Source == TriggerSource.Midi && t.MidiKind == TriggerMidiKind.PitchBend && m.Kind == MidiKind.PitchBend && (t.MidiChannel < 0 || m.Channel == t.MidiChannel))
            {
                // A bend streams values as it moves: only crossing BEND_PRESS counts.
                var down = t.Matches(m);
                if (down == _bendDown.GetValueOrDefault(t))
                    continue;
                _bendDown[t] = down;
            }
            if (t.Matches(m))
            {
                // A Program Change has no release: its flash toggles instead.
                var toggles = t.IsFlash && t.MidiKind == TriggerMidiKind.ProgramChange;
                Fired?.Invoke(t.Action, toggles ? Trigger.FLASH_TOGGLE : t.Target);
            }
            else if (t.IsFlash && t.MatchesRelease(m))
                Released?.Invoke(t.Action, t.Target);
        }
    }

    void Report(MidiMessage m)
    {
        var ch = m.Channel + 1;
        var text = m.Kind switch
        {
            MidiKind.ControlChange => $"MIDI  CC {m.Number} = {m.Value}  ch {ch}",
            MidiKind.ProgramChange => $"MIDI  PC {m.Number}  ch {ch}",
            MidiKind.PitchBend => $"MIDI  Pitch Bend {m.BendValue}  ch {ch}",
            MidiKind.NoteOn => $"MIDI  Note {m.Number}  vel {m.Value}  ch {ch}",
            _ => null,
        };
        if (text is not null)
            Activity?.Invoke(text);
    }

    public JsonObject ToJson() =>
        new()
        {
            ["triggers"] = new JsonArray([.. Triggers.Select(t => (JsonNode)t.ToJson())]),
            ["midi_enabled"] = MidiEnabled,
            ["osc_enabled"] = OscEnabled,
            ["osc_port"] = OscPort,
            ["feedback_enabled"] = FeedbackEnabled,
            ["midi_out_port"] = MidiOutPort,
            ["midi_out_port_name"] = MidiOutPortName,
            ["osc_out_host"] = OscOutHost,
            ["osc_out_port"] = OscOutPort,
        };

    /// <summary>
    /// A show's triggers and settings; starts or stops the OSC listener to match.
    /// </summary>
    public void FromJson(JsonNode? d)
    {
        Triggers.Clear();
        _bendDown.Clear();
        foreach (var e in d.Arr("triggers"))
            if (e is JsonObject)
                Triggers.Add(Trigger.FromJson(e));
        MidiEnabled = d.Bool("midi_enabled", true);
        SetOsc(d.Bool("osc_enabled", false), d.Int("osc_port", 9000));
        FeedbackEnabled = d.Bool("feedback_enabled", false);
        MidiOutPort = Math.Clamp(d.Int("midi_out_port", 9010), 1, 65535);
        MidiOutPortName = d.Str("midi_out_port_name", "");
        var host = d.Str("osc_out_host", "127.0.0.1");
        OscOutHost = host.Trim() != "" ? host : "127.0.0.1";
        OscOutPort = Math.Clamp(d.Int("osc_out_port", 9001), 1, 65535);
    }

    public void Dispose() => _osc?.Dispose();
}

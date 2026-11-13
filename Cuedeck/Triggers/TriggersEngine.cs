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
/// <para>
/// It also sends feedback: a binding's pad lights while what it follows is on (its cue live, its chase or effect
/// running, its flash held, or a console state), through the chosen MIDI output (else, as in sLight, over UDP to
/// <c>midi_bridge.py</c>), or as OSC to <see cref="OscOutHost"/>. Every pad's state is sent again every two seconds,
/// for a controller plugged in since. sLight's <c>Triggers</c> autoload.
/// </para>
/// </summary>
sealed class TriggersEngine : IDisposable
{
    /// <summary>
    /// The host the UDP MIDI bridge listens on, as sLight's.
    /// </summary>
    const string MIDI_OUT_HOST = "127.0.0.1";

    readonly Dictionary<Trigger, bool> _bendDown = [];
    readonly Dictionary<Trigger, bool> _feedbackState = [];
    readonly Dictionary<Trigger, double> _pulses = [];
    UdpClient? _osc;
    UdpClient? _out;
    IMidiOutput? _midiOut;
    double _feedbackRefresh;
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

    public bool FeedbackEnabled { get; private set; }

    /// <summary>
    /// The UDP port of sLight's <c>midi_bridge.py</c>, which MIDI feedback goes to when no MIDI output's chosen.
    /// </summary>
    public int MidiOutPort { get; private set; } = 9010;

    /// <summary>
    /// The MIDI output feedback goes to: the first whose name contains this, or none for "".
    /// </summary>
    public string MidiOutPortName { get; private set; } = "";

    public string OscOutHost { get; private set; } = "127.0.0.1";
    public int OscOutPort { get; private set; } = 9001;

    /// <summary>
    /// The system's MIDI ports, for feedback's output: the app's; none in tests unless they give some.
    /// </summary>
    public IMidiPorts? MidiPorts { get; set; }

    /// <summary>
    /// Sends a UDP packet (host, port, bytes): OSC feedback, and MIDI to the bridge. Tests catch it.
    /// </summary>
    public Action<string, int, byte[]> SendUdp { get; set; }

    /// <summary>
    /// Whether what a feedback query (see <see cref="Trigger.FeedbackQuery"/>) asks about is on: the show answers.
    /// </summary>
    public Func<string, string, bool> FeedbackState { get; set; } = (_, _) => false;

    public TriggersEngine() => SendUdp = Send;

    /// <summary>
    /// The MIDI outputs there are, by name.
    /// </summary>
    public IReadOnlyList<string> MidiOutputs() => MidiPorts?.OutputNames() ?? [];

    /// <summary>
    /// Whether a MIDI output is open for feedback.
    /// </summary>
    public bool MidiOutputOpen => _midiOut is not null;

    /// <summary>
    /// Turns feedback on or off, with where it goes. The pads are put out first when it goes off.
    /// </summary>
    public void SetFeedback(bool enabled, int midiPort, string oscHost, int oscPort)
    {
        if (FeedbackEnabled && !enabled)
            AllFeedbackOff();
        FeedbackEnabled = enabled;
        MidiOutPort = Math.Clamp(midiPort, 1, 65535);
        OscOutHost = oscHost.Trim() != "" ? oscHost.Trim() : "127.0.0.1";
        OscOutPort = Math.Clamp(oscPort, 1, 65535);
        if (enabled)
            RefreshFeedback();
    }

    /// <summary>
    /// Opens the first MIDI output whose name contains <paramref name="name"/> (any case) for feedback, or closes it
    /// for "".
    /// </summary>
    public void SetMidiOutput(string name)
    {
        MidiOutPortName = name;
        _midiOut?.Dispose();
        _midiOut = null;
        if (name.Trim() != "" && MidiPorts is { } ports)
        {
            var names = ports.OutputNames();
            for (var i = 0; i < names.Count; i++)
            {
                if (names[i].Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    _midiOut = ports.OpenOutput(i);
                    break;
                }
            }
        }
        RefreshFeedback();
    }

    /// <summary>
    /// Sends every pad's state again (after a show loads, or a controller's plugged in).
    /// </summary>
    public void RefreshFeedback()
    {
        _feedbackState.Clear();
        _feedbackRefresh = 0;
    }

    /// <summary>
    /// A beat (the tempo clock's, or else the live audio's): flashes every pad that follows the beat.
    /// </summary>
    public void OnBeat()
    {
        if (!FeedbackEnabled)
            return;
        foreach (var t in Triggers)
        {
            if (t.Enabled && t.FeedbackEnabled && t.FeedbackWatch == FeedbackWatch.Beat)
            {
                SendFeedback(t, t.FeedbackOn);
                _pulses[t] = 0.09;
            }
        }
    }

    /// <summary>
    /// Lights the pads whose state has changed, and puts beat flashes out again. Call once a frame.
    /// </summary>
    public void UpdateFeedback(double delta)
    {
        if (!FeedbackEnabled)
            return;
        foreach (var t in _pulses.Keys.ToList())
        {
            _pulses[t] -= delta;
            if (_pulses[t] <= 0)
            {
                _pulses.Remove(t);
                SendFeedback(t, t.FeedbackOff);
            }
        }
        _feedbackRefresh -= delta;
        if (_feedbackRefresh <= 0)
        {
            _feedbackRefresh = 2;
            _feedbackState.Clear(); // all sent again now and then: for a controller plugged in since
        }
        foreach (var t in Triggers)
        {
            if (!(t.Enabled && t.FeedbackEnabled && t.CanFeedback) || t.FeedbackWatch == FeedbackWatch.Beat)
                continue;
            var (kind, target) = t.FeedbackQuery;
            if (kind == "")
                continue;
            var active = FeedbackState(kind, target);
            if (_feedbackState.TryGetValue(t, out var was) && was == active)
                continue;
            _feedbackState[t] = active;
            SendFeedback(t, active ? t.FeedbackOn : t.FeedbackOff);
        }
    }

    /// <summary>
    /// Puts every feedback pad out (when feedback's turned off, and as the app closes).
    /// </summary>
    public void AllFeedbackOff()
    {
        foreach (var t in Triggers)
            if (t.FeedbackEnabled)
                SendFeedback(t, t.FeedbackOff);
        _feedbackState.Clear();
        _pulses.Clear();
    }

    void SendFeedback(Trigger t, int value)
    {
        if (t.Source == TriggerSource.Osc)
        {
            SendUdp(OscOutHost, OscOutPort, OscMessage.Encode(t.OscAddress, value / 127f));
            return;
        }
        var ch = Math.Max(t.MidiChannel, 0);
        var message = t.MidiKind switch
        {
            // 0 to 127 stretched over the bend's 14 bits: a motor fader goes there.
            TriggerMidiKind.PitchBend => MidiMessage.Bend(ch, Math.Clamp((int)Math.Round(value / 127.0 * 16383, MidpointRounding.AwayFromZero), 0, 16383)),
            TriggerMidiKind.ControlChange => MidiMessage.Control(ch, t.MidiNumber, value),
            _ => MidiMessage.NoteOn(ch, t.MidiNumber, value),
        };
        if (_midiOut is { } output)
            output.Send(message);
        else
            SendUdp(MIDI_OUT_HOST, MidiOutPort, [message.Status, message.Data1, message.Data2]);
    }

    void Send(string host, int port, byte[] bytes)
    {
        try
        {
            _out ??= new UdpClient();
            _out.Send(bytes, bytes.Length, host, port);
        }
        catch (SocketException) { }
    }

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
        SetFeedback(d.Bool("feedback_enabled", false), d.Int("midi_out_port", 9010), d.Str("osc_out_host", "127.0.0.1"), d.Int("osc_out_port", 9001));
        SetMidiOutput(d.Str("midi_out_port_name", ""));
    }

    /// <summary>
    /// Puts the pads out and lets go of the ports.
    /// </summary>
    public void Dispose()
    {
        if (FeedbackEnabled)
            AllFeedbackOff();
        _midiOut?.Dispose();
        _osc?.Dispose();
        _out?.Dispose();
    }
}

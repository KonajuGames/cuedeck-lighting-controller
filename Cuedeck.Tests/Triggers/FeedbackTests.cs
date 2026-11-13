using Cuedeck.Midi;
using Cuedeck.Show;
using Cuedeck.Triggers;

namespace Cuedeck.Tests.Triggers;

/// <summary>
/// Feedback to controllers: pads lit from the console's state, through a MIDI output, the UDP bridge or OSC.
/// </summary>
public class FeedbackTests
{
    const double FRAME = 1.0 / 30;

    sealed class FakeOutputs : IMidiPorts
    {
        public List<MidiMessage> Sent { get; } = [];
        public List<string> Opened { get; } = [];
        public bool Closed { get; private set; }

        public IReadOnlyList<string> InputNames() => [];

        public IReadOnlyList<string> OutputNames() => ["Microsoft GS Wavetable Synth", "APC40 mkII"];

        public IDisposable? OpenInput(int index, Action<MidiMessage> received) => null;

        public IMidiOutput? OpenOutput(int index)
        {
            Opened.Add(OutputNames()[index]);
            return new Output(this);
        }

        sealed class Output(FakeOutputs owner) : IMidiOutput
        {
            public void Send(MidiMessage message) => owner.Sent.Add(message);

            public void Dispose() => owner.Closed = true;
        }
    }

    /// <summary>
    /// A show whose UDP goes to a list rather than the network.
    /// </summary>
    static (LightingShow Show, List<(string Host, int Port, byte[] Bytes)> Udp) Rig()
    {
        var show = new LightingShow();
        var udp = new List<(string, int, byte[])>();
        show.Triggers.SendUdp = (host, port, bytes) => udp.Add((host, port, bytes));
        return (show, udp);
    }

    [Fact]
    public void A_cue_pad_lights_while_its_cue_is_live_through_the_bridge()
    {
        var (show, udp) = Rig();
        using var _ = show;
        show.Patch.Add(0, Profiles.Simple("dim", "Dim", ("Dimmer", "DIMMER")), 0);
        show.Cues.Record(-1, 0);
        show.Cues.Record(-1, 0);
        show.Triggers.Triggers.Add(new Trigger { MidiNumber = 50, MidiChannel = 2, Action = TriggerAction.CueGoTo, Target = "2", FeedbackEnabled = true, FeedbackOn = 100, FeedbackOff = 5 });

        show.Tick(FRAME, false);
        Assert.Empty(udp); // feedback's off
        show.Triggers.SetFeedback(true, 9010, "", 9001);
        show.Tick(FRAME, false);
        var off = Assert.Single(udp);
        Assert.Equal(("127.0.0.1", 9010), (off.Host, off.Port));
        Assert.Equal([0x92, 50, 5], off.Bytes); // a note-on on its channel, at the "off" value

        show.Tick(FRAME, false);
        Assert.Single(udp); // nothing new to say
        show.RunTrigger(TriggerAction.CueGoTo, "2");
        show.Tick(FRAME, false);
        Assert.Equal([0x92, 50, 100], udp[^1].Bytes);

        // Said again every two seconds, for a controller plugged in since.
        udp.Clear();
        for (var i = 0; i < 61; i++)
            show.Tick(FRAME, false);
        Assert.Equal([0x92, 50, 100], Assert.Single(udp).Bytes);

        // Turned off: the pad's put out.
        udp.Clear();
        show.Triggers.SetFeedback(false, 9010, "", 9001);
        Assert.Equal([0x92, 50, 5], Assert.Single(udp).Bytes);
    }

    [Fact]
    public void A_chosen_midi_output_takes_the_feedback()
    {
        var (show, udp) = Rig();
        using var _ = show;
        var ports = new FakeOutputs();
        show.Triggers.MidiPorts = ports;
        Assert.Equal(["Microsoft GS Wavetable Synth", "APC40 mkII"], show.Triggers.MidiOutputs());
        show.Triggers.SetMidiOutput("apc40");
        Assert.Equal(["APC40 mkII"], ports.Opened);
        Assert.True(show.Triggers.MidiOutputOpen);

        var chase = new Chase { Name = "Swirl" };
        show.Effects.Chases.Add(chase);
        show.Triggers.Triggers.Add(new Trigger { MidiKind = TriggerMidiKind.ControlChange, MidiNumber = 16, Action = TriggerAction.ChaseToggle, Target = "swirl", FeedbackEnabled = true });
        show.Triggers.Triggers.Add(new Trigger { MidiKind = TriggerMidiKind.PitchBend, MidiChannel = 1, Action = TriggerAction.None, FeedbackWatch = FeedbackWatch.Sending, FeedbackEnabled = true, FeedbackOn = 64 });
        show.Triggers.SetFeedback(true, 9010, "", 9001);
        show.Tick(FRAME, false);
        Assert.Empty(udp);
        Assert.Equal([MidiMessage.Control(0, 16, 0), MidiMessage.Bend(1, 8256)], ports.Sent); // 64/127 of the way up

        show.SetChaseRunning(chase, true);
        show.Sending = false;
        show.Tick(FRAME, false);
        Assert.Equal([MidiMessage.Control(0, 16, 127), MidiMessage.Bend(1, 0)], ports.Sent[2..]);

        show.Triggers.SetMidiOutput("");
        Assert.True(ports.Closed);
    }

    [Fact]
    public void Beat_pads_flash_on_the_tempo_clocks_beats()
    {
        var (show, udp) = Rig();
        using var _ = show;
        show.Triggers.Triggers.Add(new Trigger { MidiNumber = 60, FeedbackWatch = FeedbackWatch.Beat, FeedbackEnabled = true });
        show.Triggers.SetFeedback(true, 9010, "", 9001);
        show.Tempo.Tap(); // a tap lands a beat
        Assert.Equal([0x90, 60, 127], Assert.Single(udp).Bytes);
        show.Tempo.Stop(); // no more beats, for the test
        for (var i = 0; i < 4; i++)
            show.Tick(FRAME, false);
        Assert.Equal([0x90, 60, 0], udp[1].Bytes); // out again after 90 ms
        Assert.Equal(2, udp.Count);
    }

    [Fact]
    public void Osc_feedback_sends_the_value_as_a_fraction()
    {
        var (show, udp) = Rig();
        using var _ = show;
        show.Triggers.Triggers.Add(new Trigger { Source = TriggerSource.Osc, OscAddress = "/pad/1", Action = TriggerAction.FlashWhite, FeedbackEnabled = true });
        show.Triggers.SetFeedback(true, 9010, "192.168.1.20", 9100);
        show.RunTrigger(TriggerAction.FlashWhite, "");
        show.Tick(FRAME, false);
        var (host, port, bytes) = Assert.Single(udp);
        Assert.Equal(("192.168.1.20", 9100), (host, port));
        var m = Assert.Single(OscMessage.Parse(bytes));
        Assert.Equal(("/pad/1", 1f), (m.Address, (float)m.Args[0]!));
    }

    [Fact]
    public void The_show_answers_what_pads_follow()
    {
        using var show = new LightingShow();
        Assert.True(show.FeedbackState("run_mode", "0"));
        show.RunMode = RunMode.SoundReactive;
        Assert.True(show.FeedbackState("run_mode", "1"));
        Assert.True(show.FeedbackState("sending", ""));
        Assert.True(show.FeedbackState("fx_any", "")); // a new show's starter reactors run, as in sLight
        show.Effects.StopAll();
        Assert.False(show.FeedbackState("fx_any", ""));
        Assert.False(show.FeedbackState("flash", "9"));
        Assert.False(show.FeedbackState("chase", "nothing"));
        Assert.Equal(("", ""), new Trigger { Action = TriggerAction.CueGo }.FeedbackQuery);
        Assert.False(new Trigger { Action = TriggerAction.CueGo }.CanFeedback);
    }

    [Fact]
    public void Loading_a_show_opens_its_feedback_output()
    {
        var (show, _) = Rig();
        using var __ = show;
        var ports = new FakeOutputs();
        show.Triggers.MidiPorts = ports;
        show.Triggers.FromJson(Cuedeck.Common.Json.Parse("""{"feedback_enabled":true,"midi_out_port_name":"APC","osc_out_host":" ","osc_out_port":70000}"""));
        Assert.True(show.Triggers.FeedbackEnabled);
        Assert.Equal(["APC40 mkII"], ports.Opened);
        Assert.Equal(("127.0.0.1", 65535), (show.Triggers.OscOutHost, show.Triggers.OscOutPort));
    }
}

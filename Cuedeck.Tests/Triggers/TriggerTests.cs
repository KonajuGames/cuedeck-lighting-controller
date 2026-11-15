using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Midi;
using Cuedeck.Show;
using Cuedeck.Triggers;

namespace Cuedeck.Tests.Triggers;

/// <summary>
/// MIDI and OSC triggers: live_flash_test.gd's flash bindings and Learn, show_file_test.gd's saved feedback, OSC
/// packets, and the console actions they fire.
/// </summary>
public class TriggerTests
{
    static OscMessage Osc(string address, params object?[] args) => new(address, args);

    [Fact]
    public void Flash_bindings_press_and_release()
    {
        var t = new Trigger { Action = TriggerAction.FlashStrobe };
        Assert.True(t.IsFlash);
        Assert.Equal(FlashKind.Strobe, t.Flash);
        Assert.True(t.CanFeedback);
        Assert.Equal(Enum.GetValues<TriggerAction>().Length, Trigger.Actions.Length); // every action named
        Assert.Equal(Enum.GetValues<TriggerAction>().Order(), Trigger.MenuOrder.Order()); // and listed once
        Assert.Equal(13, (int)TriggerAction.CueClear); // after sLight's, so its shows' numbers still mean the same
        foreach (var unknown in new[] { 99.0, -1.0 })
            Assert.Equal(TriggerAction.None, Trigger.FromJson(new JsonObject { ["action"] = unknown }).Action); // does nothing, not something drastic

        t.MidiNumber = 40;
        var on = MidiMessage.NoteOn(0, 40, 100);
        Assert.True(t.Matches(on) && !t.MatchesRelease(on)); // note-on: pressed
        Assert.True(t.MatchesRelease(MidiMessage.NoteOn(0, 40, 0))); // velocity 0: released
        Assert.True(t.MatchesRelease(MidiMessage.NoteOff(0, 40)));
        Assert.False(t.MatchesRelease(MidiMessage.NoteOff(0, 41))); // not another note's

        t.MidiKind = TriggerMidiKind.ControlChange;
        var low = MidiMessage.Control(0, 40, 10);
        Assert.True(t.MatchesRelease(low) && !t.Matches(low)); // CC below 64: released

        t.Source = TriggerSource.Osc;
        t.OscAddress = "/flash/strobe";
        Assert.True(t.MatchesRelease(Osc("/flash/strobe", 0f)) && !t.Matches(Osc("/flash/strobe", 0f)));
        Assert.True(t.Matches(Osc("/flash/strobe", 1f)) && !t.MatchesRelease(Osc("/flash/strobe", 1f)));
        Assert.False(t.MatchesRelease(Osc("/flash/strobe"))); // no arguments: a press only
        Assert.True(t.Matches(Osc("/flash/strobe")));
        Assert.False(t.Matches(Osc("/other")));
    }

    [Fact]
    public void Learn_hears_a_cc_and_a_pitch_bend()
    {
        var engine = new TriggersEngine();
        var got = new List<Learned>();
        engine.Learned += got.Add;
        engine.StartLearn();
        engine.HandleMidi(MidiMessage.NoteOff(3, 10)); // a release isn't learned
        engine.HandleMidi(MidiMessage.Control(3, 21, 90));
        Assert.Equal(new Learned(TriggerSource.Midi, TriggerMidiKind.ControlChange, 3, 21), Assert.Single(got));
        Assert.False(engine.Learning);

        got.Clear();
        engine.StartLearn();
        engine.HandleMidi(MidiMessage.Bend(5, 9000));
        Assert.Equal((TriggerMidiKind.PitchBend, 5), (got[0].Kind, got[0].Channel));
        var t = new Trigger { Action = TriggerAction.FlashWhite };
        t.LearnFrom(got[0]);
        Assert.Equal("MIDI  Pitch Bend  ch 6", t.SourceSummary);
        Assert.False(t.UsesNumber);

        // Swept up and back down: one press, one release, however many values.
        engine.Triggers.Add(t);
        var events = new List<string>();
        engine.Fired += (a, _) => events.Add($"fired {a}");
        engine.Released += (a, _) => events.Add($"released {a}");
        foreach (var v in new[] { 8192, 10000, 12288, 14000, 16383, 13000, 12000, 4000, 0 })
            engine.HandleMidi(MidiMessage.Bend(5, v));
        Assert.Equal(["fired FlashWhite", "released FlashWhite"], events);
        events.Clear();
        engine.HandleMidi(MidiMessage.Bend(2, 16383));
        Assert.Empty(events); // not another channel's bend
        Assert.Equal(TriggerMidiKind.PitchBend, Trigger.FromJson(t.ToJson()).MidiKind);
    }

    [Fact]
    public void Learn_hears_osc()
    {
        var engine = new TriggersEngine();
        Learned? got = null;
        engine.Learned += l => got = l;
        engine.StartLearn();
        engine.HandleOscPacket(OscMessage.Encode("/cue/go", 1));
        Assert.Equal(new Learned(TriggerSource.Osc, Address: "/cue/go"), got);
    }

    [Fact]
    public void A_program_change_flash_toggles()
    {
        var engine = new TriggersEngine();
        engine.Triggers.Add(new Trigger { MidiKind = TriggerMidiKind.ProgramChange, MidiNumber = 7, Action = TriggerAction.FlashColour });
        var fired = new List<string>();
        engine.Fired += (a, target) => fired.Add($"{a} {target}");
        engine.HandleMidi(MidiMessage.Program(0, 7));
        engine.HandleMidi(MidiMessage.Program(0, 8));
        Assert.Equal(["FlashColour toggle"], fired);
    }

    [Fact]
    public void Disabled_and_feedback_only_bindings_fire_nothing()
    {
        var engine = new TriggersEngine();
        engine.Triggers.Add(new Trigger { MidiNumber = 1, Enabled = false });
        engine.Triggers.Add(new Trigger { MidiNumber = 2, Action = TriggerAction.None });
        engine.Triggers.Add(new Trigger { MidiNumber = 3, MidiChannel = 4 });
        var fired = 0;
        engine.Fired += (_, _) => fired++;
        engine.HandleMidi(MidiMessage.NoteOn(0, 1, 100));
        engine.HandleMidi(MidiMessage.NoteOn(0, 2, 100));
        engine.HandleMidi(MidiMessage.NoteOn(0, 3, 100)); // the wrong channel
        Assert.Equal(0, fired);
        engine.HandleMidi(MidiMessage.NoteOn(4, 3, 100));
        Assert.Equal(1, fired);
        engine.MidiEnabled = false;
        engine.HandleMidi(MidiMessage.NoteOn(4, 3, 100));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Activity_says_what_was_heard()
    {
        var engine = new TriggersEngine();
        var heard = new List<string>();
        engine.Activity += heard.Add;
        engine.HandleMidi(MidiMessage.Control(1, 21, 90));
        engine.HandleMidi(MidiMessage.ClockTick); // not reported
        engine.HandleOscPacket(OscMessage.Encode("/go", 1f));
        Assert.Equal(["MIDI  CC 21 = 90  ch 2", "OSC  /go  [1]"], heard);
    }

    [Fact]
    public void Feedback_watches_save_by_name_and_old_numbers_still_read()
    {
        var t = new Trigger { FeedbackWatch = FeedbackWatch.SoundMode };
        var d = t.ToJson();
        Assert.Equal("mode_sound", d.Str("fb_watch_key"));
        Assert.Equal(FeedbackWatch.SoundMode, Trigger.FromJson(d).FeedbackWatch);
        var legacy = Trigger.FromJson(new JsonObject { ["fb_enabled"] = true, ["fb_watch"] = 6.0 });
        Assert.True(legacy.FeedbackWatch == FeedbackWatch.SoundMode && legacy.FeedbackEnabled);
        legacy = Trigger.FromJson(new JsonObject { ["fb_enabled"] = true, ["fb_watch"] = 4.0 });
        Assert.True(legacy.FeedbackWatch == FeedbackWatch.Action && !legacy.FeedbackEnabled); // an old Auto Show one: off
        legacy = Trigger.FromJson(new JsonObject { ["fb_enabled"] = true, ["fb_watch"] = 3.0 });
        Assert.True(legacy.FeedbackWatch == FeedbackWatch.AnyRunning && legacy.FeedbackEnabled);
    }

    [Fact]
    public void Osc_packets_and_bundles_are_read()
    {
        var packet = OscMessage.Encode("/fader/1", 0.5f, 3, "hi");
        var m = Assert.Single(OscMessage.Parse(packet));
        Assert.Equal("/fader/1", m.Address);
        Assert.Equal([0.5f, 3, "hi"], m.Args);

        // A bundle of two, by hand: "#bundle", a time tag, then each element's size and bytes.
        var a = OscMessage.Encode("/a");
        var b = OscMessage.Encode("/b", 1);
        var bundle = new List<byte>();
        bundle.AddRange("#bundle\0"u8.ToArray());
        bundle.AddRange(new byte[8]);
        foreach (var e in new[] { a, b })
        {
            bundle.AddRange(BitConverter.GetBytes(e.Length).Reverse());
            bundle.AddRange(e);
        }
        Assert.Equal(["/a", "/b"], OscMessage.Parse(bundle.ToArray()).Select(x => x.Address));

        // True, false and nil, and a blob skipped.
        var tags = new List<byte>();
        tags.AddRange("/t\0\0,TFbN\0\0\0"u8.ToArray());
        tags.AddRange(new byte[] { 0, 0, 0, 2, 9, 9, 0, 0 });
        Assert.Equal([true, false, null], Assert.Single(OscMessage.Parse(tags.ToArray())).Args);
        Assert.Empty(OscMessage.Parse([1, 2]));
    }

    // ------------------------------------------------------------ the show --

    [Fact]
    public void Triggers_run_the_console()
    {
        using var show = new LightingShow();
        show.Patch.Add(0, Profiles.Simple("dim", "Dim", ("Dimmer", "DIMMER")), 0);
        for (var level = 1; level <= 3; level++)
        {
            show.Dmx.Universes[0].SetChannel(0, level * 50);
            show.Cues.Record(-1, 0);
        }
        show.Cues.GoTo(1);
        show.RunTrigger(TriggerAction.CueGo, "");
        Assert.Equal(1, show.Cues.Current); // the second cue
        show.RunTrigger(TriggerAction.CueGoTo, "3");
        Assert.Equal(2, show.Cues.Current);
        show.RunTrigger(TriggerAction.CueBack, "");
        Assert.Equal(1, show.Cues.Current);
        show.RunTrigger(TriggerAction.CueClear, "");
        Assert.Equal(-1, show.Cues.Current); // no cue live
        Assert.Equal(TriggerAction.CueClear, Trigger.FromJson(new Trigger { Action = TriggerAction.CueClear }.ToJson()).Action);
        show.Cues.GoTo(2); // back where it was

        var chase = new Chase { Name = "Swirl" };
        show.Effects.Chases.Add(chase);
        show.RunTrigger(TriggerAction.ChaseToggle, "swirl");
        Assert.True(chase.Running);
        show.RunTrigger(TriggerAction.ChaseToggle, "1"); // by number
        Assert.False(chase.Running);

        var effect = new WaveEffect { Name = "Breathe", Role = "DIMMER" };
        show.Effects.Effects.Add(effect);
        show.RunTrigger(TriggerAction.EffectToggle, "Breathe");
        Assert.True(effect.Running);
        Assert.Single(effect.Targets);

        show.RunTrigger(TriggerAction.Blackout, "");
        Assert.False(effect.Running);
        Assert.Equal(0, show.Dmx.Universes[0].GetChannel(0));
    }

    [Fact]
    public void Flash_pads_hold_and_toggle()
    {
        // live_flash_test.gd's shell part.
        using var show = new LightingShow();
        show.RunTrigger(TriggerAction.FlashWhite, "");
        Assert.True(show.Flash.IsOn(FlashKind.White));
        show.ReleaseTrigger(TriggerAction.FlashWhite, "");
        Assert.False(show.Flash.AnyOn);
        show.RunTrigger(TriggerAction.FlashColour, Trigger.FLASH_TOGGLE);
        show.RunTrigger(TriggerAction.FlashStrobe, "");
        show.ReleaseTrigger(TriggerAction.FlashStrobe, "");
        Assert.True(show.Flash.IsOn(FlashKind.Colour) && !show.Flash.IsOn(FlashKind.Strobe)); // a Program Change flash toggles on
        show.RunTrigger(TriggerAction.FlashColour, Trigger.FLASH_TOGGLE);
        Assert.False(show.Flash.AnyOn);
    }

    [Fact]
    public void Midi_goes_to_the_triggers_faders_and_tempo_clock()
    {
        using var show = new LightingShow();
        show.Triggers.Triggers.Add(new Trigger { MidiNumber = 36, Action = TriggerAction.FlashBlackout });
        show.HandleMidi(MidiMessage.NoteOn(0, 36, 127));
        Assert.True(show.Flash.IsOn(FlashKind.Blackout));
        show.HandleMidi(MidiMessage.NoteOff(0, 36));
        Assert.False(show.Flash.AnyOn);

        var fader = show.Faders.Add()!;
        show.Faders.Learn(0);
        show.HandleMidi(MidiMessage.Control(0, 7, 127));
        Assert.Equal(7, fader.MidiCc);
        show.HandleMidi(MidiMessage.Control(0, 7, 0));
        Assert.Equal(0, fader.Level);

        Assert.False(show.Tempo.Running);
        for (var i = 0; i < 24 * 4; i++)
            show.HandleMidi(MidiMessage.ClockTick);
        Assert.True(show.Tempo.Running);
    }

    [Fact]
    public void Triggers_are_saved_with_the_show()
    {
        using var show = new LightingShow();
        show.Triggers.Triggers.Add(new Trigger { Name = "Go pad", MidiKind = TriggerMidiKind.ControlChange, MidiNumber = 20, Action = TriggerAction.CueGo });
        show.Triggers.SendUdp = (_, _, _) => { };
        show.Triggers.SetFeedback(true, 9010, "10.0.0.5", 9001);
        var saved = ShowFile.ToJson(show);
        Assert.Equal("Go pad", saved.Get("triggers").Arr("triggers")[0].Str("name"));

        using var again = new LightingShow();
        ShowFile.Apply(again, saved);
        var t = Assert.Single(again.Triggers.Triggers);
        Assert.Equal(("Go pad", TriggerMidiKind.ControlChange, 20), (t.Name, t.MidiKind, t.MidiNumber));
        Assert.True(again.Triggers.FeedbackEnabled);
        Assert.Equal("10.0.0.5", again.Triggers.OscOutHost);
        Assert.True(Json.SameTree(saved.Get("triggers"), ShowFile.ToJson(again).Get("triggers")));

        again.NewShow();
        Assert.Empty(again.Triggers.Triggers);
    }
}

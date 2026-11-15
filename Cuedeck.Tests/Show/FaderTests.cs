using Cuedeck.Common;
using System.Text.Json.Nodes;
using Cuedeck.Midi;
using Cuedeck.Show;

namespace Cuedeck.Tests.Show;

// The show's Cue, not the framework's Cue namespace (a name in the global namespace beats a using's).
using Cue = Cuedeck.Show.Cue;

/// <summary>
/// sLight's faders_test.gd, through the show rather than its Faders tab: cue submasters, chase, group and effect
/// faders, MIDI Learn on CCs, pitch bends and notes, Blackout All, and the show file.
/// </summary>
public sealed class FaderTests : IDisposable
{
    readonly LightingShow _show = new();

    public void Dispose() => _show.Dispose();

    /// <summary>
    /// Dimmer, pan, red, green, blue.
    /// </summary>
    static Cuedeck.Fixtures.FixtureProfile Spot() =>
        Profiles.Simple("fspot", "Fader Spot", ("Dimmer", "DIMMER"), ("Pan", "PAN"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));

    [Fact]
    public void Intensity_channels_are_a_dimmer_or_without_one_the_colours()
    {
        _show.Dmx.SetUniverseCount(2);
        _show.Patch.SyncToUniverses();
        _show.Patch.Add(0, Spot(), 0);
        var b = _show.Patch.Add(0, Profiles.Rgb(), 10);
        _show.Patch.Add(1, Profiles.Rgb(), 0);
        var m = _show.Patch.IntensityChannels(2);
        Assert.Equal([0, 10, 11, 12], m[0].Order());
        Assert.Equal([0, 1, 2], m[1].Order());
        var only = _show.Patch.IntensityChannels(2, new HashSet<string> { b.Uid });
        Assert.Equal([10, 11, 12], only[0].Order());
        Assert.Empty(only[1]);
    }

    [Fact]
    public void Cue_submasters()
    {
        IReadOnlySet<int>[] inten = [new HashSet<int> { 0, 10 }];
        var look = new Look([new() { [0] = 200, [1] = 90, [10] = 100 }]);
        var stage = new byte[512];
        var o = FaderBank.CueOutput([(look, 0.5)], inten, [stage], 1)[0];
        Assert.Equal((100, 50), (o[0], o[10])); // intensity scaled by the fader
        Assert.Equal(90, o[1]); // anything else at the look's value
        stage[0] = 150;
        Assert.Equal(150, FaderBank.CueOutput([(look, 0.5)], inten, [stage], 1)[0][0]); // the stage higher wins
        var other = new Look([new() { [0] = 255, [1] = 10 }]);
        o = FaderBank.CueOutput([(look, 0.5), (other, 0.2)], inten, [new byte[0]], 1)[0];
        Assert.Equal(100, o[0]); // the higher intensity
        Assert.Equal(90, o[1]); // the higher fader's other channels
    }

    [Fact]
    public void In_the_show()
    {
        var u = _show.Dmx.Universes[0];
        _show.Patch.Add(0, Spot(), 0, name: "Spot");
        var par = _show.Patch.Add(0, Profiles.Rgb(), 10, name: "Par");
        u.SetAll(0);
        var bank = _show.Faders;

        // A cue submaster.
        var cue = new Cue("Look", 0, 0);
        cue.SetLevels([new Dictionary<int, int> { [0] = 200, [1] = 77, [10] = 120 }]);
        _show.Cues.Cues.Add(cue);
        bank.Add();
        bank.SetTarget(0, FaderKind.Cue, "1");
        _show.Dmx.Tick(false);
        Assert.Equal((0, 0), (u.Output[0], u.Output[1]));
        bank.SetLevel(0, 0.5);
        _show.Dmx.Tick(false);
        Assert.Equal((100, 60), (u.Output[0], u.Output[10]));
        Assert.Equal(77, u.Output[1]); // its pan at the cue's
        bank.SetLevel(0, 0);

        // A chase fader.
        var chase = new Chase { Name = "Pulse" };
        chase.Steps.Add(new Look([new() { [0] = 255, [1] = 30 }]));
        chase.Steps.Add(new Look([new() { [0] = 255, [1] = 30 }]));
        _show.Effects.Chases.Add(chase);
        bank.Add();
        bank.SetTarget(1, FaderKind.Chase, "pulse");
        Assert.False(chase.Running);
        bank.SetLevel(1, 0.4);
        _show.Dmx.Tick(false);
        Assert.True(chase.Running);
        Assert.Equal(102, u.Output[0]); // its intensity at 40%
        Assert.Equal(30, u.Output[1]);
        bank.SetLevel(1, 0);
        Assert.False(chase.Running);
        Assert.Equal(1.0, chase.Level);
        chase.Running = true; // run from the Chases tab
        bank.SetLevel(1, 0);
        Assert.True(chase.Running); // a fader that's down doesn't stop it
        chase.Running = false;

        // A group master.
        _show.Groups.Add(new FixtureGroup { Name = "Pars", Members = { par.Uid } });
        bank.Add();
        bank.SetTarget(2, FaderKind.Group, "Pars");
        u.SetChannel(10, 200);
        u.SetChannel(0, 200);
        bank.SetLevel(2, 1);
        _show.Dmx.Tick(false);
        Assert.Equal(200, u.Output[10]);
        bank.SetLevel(2, 0.25);
        _show.Dmx.Tick(false);
        Assert.Equal((50, 200), (u.Output[10], u.Output[0]));
        _show.Flash.Press(FlashKind.White);
        _show.Dmx.Tick(false);
        Assert.Equal(255, u.Output[10]); // a live flash still goes over it
        _show.Flash.Clear();

        // MIDI.
        bank.Learn(0);
        Assert.True(bank.HandleMidi(MidiMessage.Control(2, 7, 127)));
        Assert.Equal((7, 2), (bank.Faders[0].MidiCc, bank.Faders[0].MidiChannel));
        Assert.Equal(1.0, bank.Faders[0].Level);
        Assert.False(bank.IsLearning(0));
        bank.HandleMidi(MidiMessage.Control(2, 7, 64));
        Assert.True(Math.Abs(bank.Faders[0].Level - FaderBank.MidiLevel(64 / 127.0)) < 0.001);
        Assert.False(bank.HandleMidi(MidiMessage.Control(3, 7, 0))); // the same CC on another channel
        Assert.True(bank.Faders[0].Level > 0.4);
        Assert.Equal("CC 7 · ch 3", bank.Faders[0].MidiText);

        // Blackout All.
        bank.SetLevel(1, 0.5);
        _show.BlackoutAll();
        Assert.Equal((0.0, 0.0), (bank.Faders[0].Level, bank.Faders[1].Level));
        Assert.False(chase.Running);
        Assert.Equal(0.25, bank.Faders[2].Level); // not the group master

        // An effect fader on a pitch bend: 0 out, 16383 full.
        var fx = new WaveEffect { Name = "Glow", Role = "DIMMER", Size = 0, Center = 200 };
        _show.Effects.Effects.Add(fx);
        bank.Add();
        bank.SetTarget(3, FaderKind.Effect, "glow");
        Assert.False(fx.Running);
        bank.Learn(3);
        Assert.True(bank.HandleMidi(MidiMessage.Bend(1, 16383)));
        Assert.True(bank.Faders[3].MidiBend);
        Assert.Equal(1, bank.Faders[3].MidiChannel);
        Assert.Equal(1.0, bank.Faders[3].Level);
        Assert.True(fx.Running);
        Assert.NotEmpty(fx.Targets); // from the patch
        u.SetAll(0);
        _show.Dmx.Tick(false);
        Assert.Equal(200, u.Output[0]);
        bank.HandleMidi(MidiMessage.Bend(1, 8192));
        _show.Dmx.Tick(false);
        Assert.True(Math.Abs(bank.Faders[3].Level - 0.5) < 0.001);
        Assert.Equal(100, u.Output[0]);
        Assert.Equal("Bend · ch 2", bank.Faders[3].MidiText);
        bank.HandleMidi(MidiMessage.Control(1, 7, 0));
        Assert.True(bank.Faders[3].Level > 0.4); // a CC doesn't move a pitch-bend fader
        bank.HandleMidi(MidiMessage.Bend(1, 0));
        _show.Dmx.Tick(false);
        Assert.False(fx.Running);
        Assert.Equal(1.0, fx.Level);
        Assert.Equal(0, u.Output[0]);

        // The ends of a fader's travel: a bend just short of an end gets there.
        foreach (var (bend, level) in new[] { (200, 0.0), (16200, 1.0), (0, 0.0), (16383, 1.0) })
        {
            bank.HandleMidi(MidiMessage.Bend(1, bend));
            Assert.Equal(level, bank.Faders[3].Level);
        }
        bank.SetLevel(3, 0);

        // A note: on is full, off is 0.
        bank.Learn(3);
        Assert.False(bank.HandleMidi(MidiMessage.NoteOff(0, 36))); // a note-off doesn't map it
        Assert.True(bank.IsLearning(3));
        Assert.True(bank.HandleMidi(MidiMessage.NoteOn(0, 36, 100)));
        Assert.Equal((36, false, -1, 1.0), (bank.Faders[3].MidiNote, bank.Faders[3].MidiBend, bank.Faders[3].MidiCc, bank.Faders[3].Level));
        Assert.True(fx.Running);
        Assert.Equal("Note 36 · ch 1", bank.Faders[3].MidiText);
        bank.HandleMidi(MidiMessage.NoteOff(0, 36));
        Assert.Equal(0.0, bank.Faders[3].Level);
        Assert.False(fx.Running);
        bank.HandleMidi(MidiMessage.NoteOn(0, 36, 100));
        bank.HandleMidi(MidiMessage.NoteOn(0, 36, 0));
        Assert.Equal(0.0, bank.Faders[3].Level); // a note-on at velocity 0 is a note-off
        Assert.False(bank.HandleMidi(MidiMessage.NoteOn(0, 37, 100)));
        bank.HandleMidi(MidiMessage.Bend(0, 16383));
        Assert.Equal(0.0, bank.Faders[3].Level); // nor a pitch bend

        var saved = Fader.FromJson(bank.Faders[3].ToJson());
        Assert.Equal(36, saved.MidiNote);
        Assert.True(saved.IsMapped);
        saved.ForgetMidi();
        Assert.False(saved.IsMapped);
        bank.Remove(3);
        _show.Effects.Effects.Remove(fx);

        // The show file.
        bank.SetLevel(1, 0.6);
        var d = ShowFile.ToJson(_show);
        Assert.Equal(3, d.Get("faders").Arr("faders").Count);
        Assert.Equal("chase", d.Get("faders").Arr("faders")[1].Str("kind"));
        bank.SetLevel(1, 0);
        ShowFile.Apply(_show, d);
        var c2 = _show.Effects.Chases[0];
        Assert.Equal(3, _show.Faders.Faders.Count);
        Assert.Equal(7, _show.Faders.Faders[0].MidiCc);
        Assert.True(c2.Running); // a chase fader that was up runs its chase again
        Assert.True(Math.Abs(c2.Level - 0.6) < 0.001);
        ShowFile.Apply(_show, Json.Parse("""{"universes":[{}],"live":[{}]}""")!);
        Assert.Empty(_show.Faders.Faders);
        Assert.False(c2.Running);
    }

    [Fact]
    public void A_movement_effect_faded_shrinks_its_swing_towards_its_centre()
    {
        var swing = new WaveEffect { Role = "PAN", Waveform = Waveform.Square, Size = 100, Center = 128, Level = 0.5 };
        swing.SetTargets([new ChannelTarget(0, 1)]);
        var layers = new List<Dictionary<int, int>> { new() };
        swing.WriteInto(layers);
        Assert.Equal(153, layers[0][1]);
    }

    [Fact]
    public void A_midi_faders_travel_climbs_evenly_with_no_jump_off_either_end()
    {
        var prev = 0.0;
        for (var v = 0; v < 16384; v += 64)
        {
            var level = FaderBank.MidiLevel(v / 16383.0);
            Assert.True(level >= prev && level - prev <= 0.01, $"{v}: {level} after {prev}");
            prev = level;
        }
        Assert.Equal(0.0, FaderBank.MidiLevel(1 / 127.0));
        Assert.Equal(1.0, FaderBank.MidiLevel(126 / 127.0));
        Assert.True(FaderBank.MidiLevel(3 / 127.0) > 0);
    }
}

using Cuedeck.Midi;
using Cuedeck.Show;
using Cuedeck.Triggers;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The Triggers dialog, clicked through: bindings made, learned from MIDI, pointed at a chase and fired.
/// </summary>
public sealed class TriggersDialogTests : ConsoleHarness
{
    TriggersDialog Dialog => Ui.Elements.OfType<TriggersDialog>().Single();

    T Field<T>(string name)
        where T : Element =>
        Find<Row>(r => r.Children.Count > 1 && r.Children[0] is FixedWidth { Inner: Label l } && l.Text == name).Children.OfType<T>().First();

    [Fact]
    public void A_pad_is_learned_and_toggles_a_chase()
    {
        var chase = new Chase { Name = "Swirl" };
        Show.Effects.Chases.Add(chase);
        Click(ButtonCalled("Triggers…"));
        Assert.True(Dialog.Visible);
        Assert.Contains(All(Dialog).OfType<Label>(), l => l.Text == "No MIDI devices found");

        Click(ButtonCalled("Add"));
        var t = Assert.Single(Show.Triggers.Triggers);
        Assert.Same(t, Dialog.List.SelectedItem);

        Click(Find<Button>(b => b.Text.StartsWith("Learn", StringComparison.Ordinal)));
        Assert.True(Show.Triggers.Learning);
        Show.HandleMidi(MidiMessage.Control(3, 21, 90));
        Frame();
        Assert.Equal("MIDI  CC 21  ch 4", t.SourceSummary);
        Assert.Contains(All(Dialog).OfType<Label>(), l => l.Text == "Learned: MIDI  CC 21  ch 4");
        Assert.Equal(21, Field<NumberBox>("Note / CC #").Value);

        Choose(Field<Dropdown<TriggerAction>>("Action"), "Chase — toggle");
        Choose(Find<Dropdown<string>>(d => d.Text("") == "pick…"), "Swirl");
        Assert.Equal("Swirl", t.Target);

        // Pressed from the controller: the chase runs; again, it stops.
        Show.HandleMidi(MidiMessage.Control(3, 21, 127));
        Assert.True(chase.Running);
        Assert.Contains(All(Dialog).OfType<Label>(), l => l.Text == "MIDI  CC 21 = 127  ch 4");
        Show.HandleMidi(MidiMessage.Control(3, 21, 0));
        Show.HandleMidi(MidiMessage.Control(3, 21, 127));
        Assert.False(chase.Running);

        // A frame on, the dialog has focus again (the menu that had it has gone), so Escape reaches it.
        Frame();
        Ui.KeyDown(Key.Escape, Modifiers.None);
        Frame();
        Assert.False(Dialog.Visible);
    }

    [Fact]
    public void An_osc_binding_fires_a_cue()
    {
        Show.Patch.Add(0, Profiles.Simple("dim", "Dim", ("Dimmer", "DIMMER")), 0);
        Show.Cues.Record(-1, 0);
        Show.Cues.Record(-1, 0);
        Click(ButtonCalled("Triggers…"));
        Click(ButtonCalled("Add"));
        Choose(Field<Dropdown<TriggerSource>>("Source"), "OSC");
        var address = Field<TextBox>("Address");
        Click(address);
        address.SelectAll();
        Type("/show/goto");
        Choose(Field<Dropdown<TriggerAction>>("Action"), "Cue — Go to #");
        var target = Find<TextBox>(b => b.Placeholder == "e.g. 5");
        Click(target);
        Type("2");
        var t = Show.Triggers.Triggers.Single();
        Assert.Equal(("OSC  /show/goto", "2"), (t.SourceSummary, t.Target));

        Show.Triggers.HandleOscPacket(OscMessage.Encode("/show/goto", 1f));
        Assert.Equal(1, Show.Cues.Current);
    }

    [Fact]
    public void Bindings_are_copied_and_deleted()
    {
        Click(ButtonCalled("Triggers…"));
        Click(ButtonCalled("Add"));
        Click(ButtonCalled("Duplicate"));
        Assert.Equal(["Trigger 1", "Trigger 1 copy"], Show.Triggers.Triggers.Select(t => t.Name));
        Click(ButtonCalled("Delete"));
        Assert.Equal(["Trigger 1"], Show.Triggers.Triggers.Select(t => t.Name));
        Assert.Equal("Trigger 1", Dialog.List.SelectedItem?.Name);
    }

    [Fact]
    public void Escape_stops_a_learn_before_closing()
    {
        Click(ButtonCalled("Triggers…"));
        Click(ButtonCalled("Add"));
        Click(Find<Button>(b => b.Text.StartsWith("Learn", StringComparison.Ordinal)));
        Ui.KeyDown(Key.Escape, Modifiers.None);
        Frame();
        Assert.False(Show.Triggers.Learning);
        Assert.True(Dialog.Visible);
    }
}

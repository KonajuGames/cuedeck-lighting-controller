using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Input;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The Sound tab, clicked through: the input's settings and the sound reactors.
/// </summary>
public sealed class SoundTabTests : ConsoleHarness
{
    SoundTab Tab => Screen.Console.Sound;

    void OpenSound() => Click(Find<Button>(b => b.Text == "Sound"));

    T Field<T>(string name)
        where T : Element =>
        Find<Row>(r => r.Children.Count > 1 && r.Children[0] is FixedWidth { Inner: Label l } && l.Text == name).Children.OfType<T>().First();

    [Fact]
    public void Reactors_are_made_edited_and_armed()
    {
        Show.Effects.Reactors.Clear();
        OpenSound();
        Click(ButtonCalled("New Reactor"));
        var r = Assert.Single(Show.Effects.Reactors);
        Assert.Same(r, Tab.Selected);
        Assert.Equal("Reactor 1", r.Name);

        Choose(Field<Dropdown<SoundBand>>("Band"), "Treble");
        Choose(Field<Dropdown<ReactorMode>>("Mode"), "Pulse");
        Choose(Field<Dropdown<string>>("Role"), "RED");
        Assert.Equal((SoundBand.Treble, ReactorMode.Pulse, Roles.Red), (r.Band, r.Mode, r.Role));
        Assert.Contains(All(Tab).OfType<Label>(), l => l.Text == "0 target channels.");

        // Nothing patched to drive: it won't run.
        Click(Find<Checkbox>(c => c.Text == "Running"));
        Assert.False(r.Running);
        Assert.Equal("No RED channels patched in that universe.", Screen.StatusText);

        Show.Patch.Add(0, Profiles.Rgb(), 0);
        Click(Find<Checkbox>(c => c.Text == "Running"));
        Assert.True(r.Running);
        Assert.Single(r.Targets);
        Assert.Equal("Armed 'Reactor 1' — switch the run mode to Sound Reactive to hear it.", Screen.StatusText);
        Assert.Contains(All(Tab).OfType<Label>(), l => l.Text == "1 target channel.");

        Click(ButtonCalled("Delete Reactor"));
        Assert.Empty(Show.Effects.Reactors);
    }

    [Fact]
    public void A_new_show_has_the_starter_reactors()
    {
        OpenSound();
        Assert.Equal(Show.Effects.Reactors.Count, Tab.List.Items.Count);
        Assert.True(Tab.List.Items.Count > 0);
    }

    [Fact]
    public void The_input_settings_are_the_shows()
    {
        OpenSound();
        Show.Sound.Gain = 2;
        Frame();
        var gain = Field<Row>("Gain").Children.OfType<Slider>().Single();
        Assert.Equal(2f, gain.Value);
        Assert.Contains(All(Tab).OfType<Label>(), l => l.Text == "2.00");

        // Dragging the slider to its left end sets the show's gain to the least.
        Ui.MouseMove(gain.ScreenPosition + new float2(gain.Size.X / 2 * gain.Value / 4, gain.Size.Y / 2));
        Ui.MouseButton(MouseButton.Left, down: true);
        Ui.MouseMove(gain.ScreenPosition + new float2(-50, gain.Size.Y / 2));
        Ui.MouseButton(MouseButton.Left, down: false);
        Frame();
        Assert.Equal(0.25, Show.Sound.Gain, 6);

        // No inputs in the tests: just the default, and no signal.
        Click(Find<Dropdown<string>>(d => d.Text("") == "Default"));
        Assert.Contains(All(Ui.Elements.OfType<ContextMenu>().Single()).OfType<Button>(), b => b.Text == "Default");
        Ui.KeyDown(Key.Escape, Modifiers.None);
        Assert.False(Find<Spectrogram>(_ => true).Lit);
    }
}

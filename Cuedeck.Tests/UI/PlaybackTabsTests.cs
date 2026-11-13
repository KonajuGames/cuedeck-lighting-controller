using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The Chases, Effects and Groups tabs, clicked through.
/// </summary>
public sealed class PlaybackTabsTests : ConsoleHarness
{
    int Output(int channel) => Show.Dmx.Universes[0].Output[channel];

    void Run(double seconds)
    {
        for (var t = 0.0; t < seconds - 1e-9; t += 1.0 / 30)
            Show.Tick(1.0 / 30, transmit: false);
        Frame();
    }

    void OpenTab(string name) => Click(ButtonCalled(name));

    /// <summary>
    /// The control in the editor row called <paramref name="name"/>.
    /// </summary>
    T Field<T>(string name)
        where T : Element =>
        Find<Row>(r => r.Children.Count > 1 && r.Children[0] is FixedWidth { Inner: Label l } && l.Text == name).Children.OfType<T>().First();

    /// <summary>
    /// Chooses <paramref name="choice"/> from a dropdown, by opening it and clicking it in the menu.
    /// </summary>
    void Choose<T>(Dropdown<T> dropdown, string choice)
    {
        Click(dropdown);
        Click(Find<Button>(b => b.Text == choice));
    }

    [Fact]
    public void Chases_record_steps_and_run()
    {
        OpenTab("Chases");
        Click(ButtonCalled("New Chase"));
        var chase = Assert.Single(Show.Effects.Chases);
        Assert.Equal("Chase 1", chase.Name);
        Assert.Same(chase, Screen.Console.Chases.Selected);

        // Two steps from the live levels.
        Show.Dmx.Universes[0].SetChannel(0, 255);
        Click(ButtonCalled("Record Step"));
        Show.Dmx.Universes[0].SetAll(0);
        Show.Dmx.Universes[0].SetChannel(1, 255);
        Click(ButtonCalled("Record Step"));
        Show.Dmx.Universes[0].SetAll(0);
        Assert.Equal(2, chase.Steps.Count);
        Assert.Contains("Recorded step 2", Screen.StatusText);

        // Its tempo: 60 BPM, a step a second.
        var bpm = Field<NumberBox>("Tempo (BPM)");
        for (var i = 0; i < 60; i++)
            Click(All(bpm).OfType<Button>().First());
        Assert.Equal(60, chase.Bpm);

        Click(Find<Checkbox>(c => c.Text == "Running"));
        Assert.True(chase.Running);
        Run(0.1);
        Assert.Equal((255, 0), (Output(0), Output(1)));
        Run(1.0);
        Assert.Equal((0, 255), (Output(0), Output(1)));

        Choose(Field<Dropdown<ChaseDirection>>("Direction"), "Bounce");
        Assert.Equal(ChaseDirection.Bounce, chase.Direction);
        Click(Find<Checkbox>(c => c.Text == "step on each beat"));
        Assert.True(chase.BeatSync);

        Click(Find<Checkbox>(c => c.Text == "Running"));
        Assert.False(chase.Running);
        Click(ButtonCalled("Delete Chase"));
        Assert.Empty(Show.Effects.Chases);
    }

    [Fact]
    public void A_chase_step_loaded_updated_and_deleted()
    {
        OpenTab("Chases");
        Click(ButtonCalled("New Chase"));
        var chase = Show.Effects.Chases[0];
        Show.Dmx.Universes[0].SetChannel(5, 50);
        Click(ButtonCalled("Record Step"));
        Show.Dmx.Universes[0].SetAll(0);

        // Choosing the step in its list.
        var steps = Find<ListView<StepRow>>(_ => true);
        ClickAt(steps.ScreenPosition + new float2(40, steps.RowHeight / 2));
        Click(ButtonCalled("Load Step to Patch"));
        Assert.Equal(50, Show.Dmx.Universes[0].GetChannel(5));
        Show.Dmx.Universes[0].SetChannel(5, 60);
        Click(ButtonCalled("Update Step"));
        Assert.Equal(60, chase.Steps[0].Level(0, 5));
        Click(ButtonCalled("Delete Step"));
        Assert.Empty(chase.Steps);
    }

    [Fact]
    public void Effects_run_on_their_role_and_group()
    {
        var a = Show.Patch.Add(0, Profiles.DimmedRgb(), 0, name: "A");
        Show.Patch.Add(0, Profiles.DimmedRgb(), 4, name: "B");
        Show.Groups.Add(new FixtureGroup { Name = "Left", Members = { a.Uid } });
        OpenTab("Effects");
        Click(ButtonCalled("New Effect"));
        var effect = Assert.Single(Show.Effects.Effects);

        // A steady dimmer at 200: no swing.
        var size = Field<NumberBox>("Size");
        effect.Size = 0;
        effect.Center = 200;
        Frame();
        Assert.Equal(0, size.Value);

        Click(Find<Checkbox>(c => c.Text == "Running"));
        Assert.True(effect.Running);
        Assert.Contains(All(Screen.Console.Effects).OfType<Label>(), l => l.Text == "2 target channels.");
        Run(0.1);
        Assert.Equal((200, 200), (Output(0), Output(4)));

        // On the group: just its fixture.
        Choose(Field<Dropdown<string>>("Group"), "Left");
        Assert.Equal("Left", effect.Group);
        Assert.Equal([new ChannelTarget(0, 0)], effect.Targets);
        Assert.False(Field<Dropdown<int>>("Universe").Enabled);

        // Pan is capped as a motor.
        Choose(Field<Dropdown<string>>("Role"), "PAN");
        Assert.Equal(WaveEffect.MotorMaxBpm, Field<NumberBox>("Rate (BPM)").Max);
        Assert.Empty(effect.Targets); // no pan on these
    }

    [Fact]
    public void An_effect_with_nothing_to_drive_doesnt_start()
    {
        OpenTab("Effects");
        Click(ButtonCalled("New Effect"));
        Click(Find<Checkbox>(c => c.Text == "Running"));
        Assert.False(Show.Effects.Effects[0].Running);
        Assert.Contains("No DIMMER channels patched", Screen.StatusText);
    }

    [Fact]
    public void Groups_tick_their_fixtures_and_rename_what_uses_them()
    {
        var a = Show.Patch.Add(0, Profiles.Rgb(), 0, name: "Par A");
        var b = Show.Patch.Add(0, Profiles.Rgb(), 3, name: "Par B");
        Show.Effects.Effects.Add(new WaveEffect { Name = "Wash", Group = "Group 1" });
        OpenTab("Groups");
        Click(ButtonCalled("New Group"));
        var group = Assert.Single(Show.Groups);
        Assert.Contains(All(Screen.Console.Groups).OfType<Label>(), l => l.Text == "0 of 2 fixtures in 'Group 1':");

        Click(Find<Checkbox>(c => c.Text?.Contains("Par B") == true));
        Assert.Equal([b.Uid], group.Members);
        Click(Find<Checkbox>(c => c.Text?.Contains("Par A") == true));
        Assert.Equal([b.Uid, a.Uid], group.Members);
        Click(Find<Checkbox>(c => c.Text?.Contains("Par B") == true));
        Assert.Equal([a.Uid], group.Members);

        // Renamed: the effect on it follows.
        var name = Field<TextBox>("Name");
        Click(name);
        name.SelectAll();
        Type("Front");
        Assert.Equal("Front", group.Name);
        Assert.Equal("Front", Show.Effects.Effects[0].Group);

        // A fixture unpatched leaves it.
        Show.Patch.Remove(a);
        Frame();
        Assert.Empty(group.Members);

        Click(ButtonCalled("Delete Group"));
        Assert.Empty(Show.Groups);
    }
}

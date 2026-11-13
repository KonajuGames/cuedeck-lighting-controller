using Brutal.Numerics;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The Faders, Palettes and DMX Output tabs, clicked through.
/// </summary>
public sealed class RigTabsTests : ConsoleHarness
{
    void OpenTab(string name) => Click(ButtonCalled(name));

    void Choose<T>(Dropdown<T> dropdown, string choice)
    {
        Click(dropdown);
        Click(Find<Button>(b => b.Text == choice));
    }

    [Fact]
    public void A_cue_fader_brings_its_look_up()
    {
        Show.Patch.Add(0, Profiles.DimmedRgb(), 0, name: "Par");
        Show.Dmx.Universes[0].SetAll(0);
        var cue = new Cue("Red", 0, 0);
        cue.SetLevels([new Dictionary<int, int> { [0] = 200, [1] = 255 }]);
        Show.Cues.Cues.Add(cue);

        OpenTab("Faders");
        Click(ButtonCalled("Add Fader"));
        var fader = Assert.Single(Show.Faders.Faders);
        Assert.Equal((FaderKind.Cue, "1"), (fader.Kind, fader.Target)); // on cue 1 to start with
        var strip = Find<FaderStrip>(_ => true);
        Assert.Contains(All(strip).OfType<Label>(), l => l.Text == "0%");

        // Dragging the fader half way up.
        var slider = All(strip).OfType<Slider>().Single();
        Ui.MouseMove(slider.ScreenPosition + slider.Size / 2);
        Ui.MouseButton(Cuedeck.Input.MouseButton.Left, down: true);
        Ui.MouseButton(Cuedeck.Input.MouseButton.Left, down: false);
        Frame();
        Assert.InRange(fader.Level, 0.4, 0.6);
        Show.Dmx.Tick(false);
        Assert.InRange(Show.Dmx.Universes[0].Output[0], 80, 120); // the dimmer, scaled
        Assert.Equal(255, Show.Dmx.Universes[0].Output[1]); // the colour, at the look's

        // Moved by MIDI (or code): the strip follows.
        Show.Faders.SetLevel(0, 1);
        Frame();
        Assert.Equal(1, slider.Value);
        Assert.Contains(All(strip).OfType<Label>(), l => l.Text == "100%");
    }

    [Fact]
    public void A_fader_put_on_a_chase_runs_it_and_shows_a_missing_target()
    {
        var chase = new Chase { Name = "Pulse" };
        chase.Steps.Add(new Look([new() { [0] = 255 }]));
        Show.Effects.Chases.Add(chase);
        OpenTab("Faders");
        Click(ButtonCalled("Add Fader"));
        var strip = Find<FaderStrip>(_ => true);
        Choose(All(strip).OfType<Dropdown<FaderKind>>().Single(), "Chase");
        var fader = Show.Faders.Faders[0];
        Assert.Equal((FaderKind.Chase, "Pulse"), (fader.Kind, fader.Target)); // the first chase
        Show.Faders.SetLevel(0, 0.5);
        Assert.True(chase.Running);

        // The chase renamed: the fader's target shows as missing.
        chase.Name = "Other";
        Frame();
        var target = All(strip).OfType<Dropdown<(string Label, string Target)>>().Single();
        Assert.Equal("Pulse (missing)", target.Selected.Label);

        Click(Find<Button>(b => b.Tooltip == "Remove this fader"));
        Assert.Empty(Show.Faders.Faders);
        Assert.False(chase.Running); // let go
    }

    [Fact]
    public void A_mapped_fader_shows_its_midi_and_forgets_it()
    {
        Show.Faders.FromJson(Cuedeck.Common.Json.Parse("""{"faders":[{"name":"Sub","kind":"cue","target":"1","midi_cc":7,"midi_channel":0}]}"""));
        OpenTab("Faders");
        Assert.NotNull(Find<Label>(l => l.Text == "MIDI: CC 7 · ch 1"));
        Click(ButtonCalled("Forget MIDI"));
        Assert.False(Show.Faders.Faders[0].IsMapped);
        Assert.NotNull(Find<Label>(l => l.Text == "MIDI: not mapped"));
    }

    [Fact]
    public void Palettes_recorded_applied_updated_and_deleted()
    {
        var par = Show.Patch.Add(0, Profiles.Rgb(), 0, name: "Par");
        var u = Show.Dmx.Universes[0];
        u.SetChannel(0, 255);
        OpenTab("Palettes");
        Click(ButtonCalled("Record Colour"));
        var red = Assert.Single(Show.Palettes.Palettes);
        Assert.Equal("Colour 1", red.Name);
        Assert.Contains("Recorded Colour 1 for 1 fixture", Screen.StatusText);
        var button = ButtonCalled("Colour 1");
        Assert.Equal(Colors.FromSrgb(0.85f, 0, 0), button.Background); // in its colour

        Click(ButtonCalled("Record Position"));
        Assert.Contains("None of those fixtures has position channels", Screen.StatusText);

        // Applying it.
        u.SetAll(0);
        Click(ButtonCalled("Colour 1"));
        Assert.Equal(255, u.GetChannel(0));

        // A cue uses it; updating it changes the cue; deleting it leaves its values in the cue.
        Show.Cues.Record(-1);
        Assert.Equal(red.Id, Show.Cues.Cues[0].Refs[par.Uid]["colour"]);
        u.SetChannel(0, 0);
        u.SetChannel(2, 255);
        var b = ButtonCalled("Colour 1");
        Ui.MouseMove(b.ScreenPosition + b.Size / 2);
        Ui.MouseButton(Cuedeck.Input.MouseButton.Right, down: true);
        Ui.MouseButton(Cuedeck.Input.MouseButton.Right, down: false);
        Frame();
        Click(ButtonCalled("Update from Live"));
        Assert.Equal(255, Show.Cues.StandingLook(1)!.Level(0, 2));
        Assert.Contains("1 cue uses it", Screen.StatusText);

        b = ButtonCalled("Colour 1");
        Ui.MouseMove(b.ScreenPosition + b.Size / 2);
        Ui.MouseButton(Cuedeck.Input.MouseButton.Right, down: true);
        Ui.MouseButton(Cuedeck.Input.MouseButton.Right, down: false);
        Frame();
        Click(ButtonCalled("Delete…"));
        Click(ButtonCalled("Delete"));
        Assert.Empty(Show.Palettes.Palettes);
        Assert.Equal(255, Show.Cues.StandingLook(1)!.Level(0, 2)); // the cue kept it
    }

    [Fact]
    public void A_palette_recorded_for_a_group()
    {
        var a = Show.Patch.Add(0, Profiles.Rgb(), 0, name: "A");
        Show.Patch.Add(0, Profiles.Rgb(), 3, name: "B");
        Show.Groups.Add(new FixtureGroup { Name = "Left", Members = { a.Uid } });
        OpenTab("Palettes");
        Choose(Find<Dropdown<string>>(d => d.Selected == ""), "Left");
        Click(ButtonCalled("Record Beam")); // no beam channels
        Click(ButtonCalled("Record Colour"));
        Assert.Equal([a.Uid], Show.Palettes.Palettes.Single().Values.Keys);
    }

    [Fact]
    public void The_dmx_output_grid_shows_every_universe()
    {
        Show.Patch.AddUniverse();
        Show.Patch.Add(0, Profiles.Rgb(), 0);
        OpenTab("DMX Output");
        Assert.NotNull(ButtonCalled("Universe 2"));
        var grid = Find<ChannelGrid>(_ => true);
        Assert.Equal(3, grid.Tints.Count); // the par's three channels tinted
        Assert.Equal(512 / ChannelGrid.COLUMNS, (int)Math.Round((grid.Size.Y + 4) / 46));
    }
}

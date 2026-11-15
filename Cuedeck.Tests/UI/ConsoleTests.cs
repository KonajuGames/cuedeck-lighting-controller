using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The console driven as someone would: clicking the top bar's buttons, patching fixtures from the Patch tab and
/// moving their controls.
/// </summary>
public sealed class ConsoleTests : ConsoleHarness
{
    [Fact]
    public void The_console_shows_the_playback_and_rig_tabs()
    {
        foreach (var tab in new[] { "Cues", "Chases", "Effects", "Sound", "Groups", "Patch", "3D Visualizer", "DMX Output", "Faders", "Palettes", "Universe 1" })
            Assert.NotNull(ButtonCalled(tab));
        Assert.True(ButtonCalled("Patch").Lit);
    }

    [Fact]
    public void Adding_a_fixture_from_the_patch_tab()
    {
        Click(ButtonCalled("Add Fixture"));
        var f = Assert.Single(Show.Patch.All);
        Assert.Equal("Dimmer (1ch)", f.Profile.Model); // the first profile
        Assert.Equal(0, f.Start);
        Assert.Equal("Dimmer (1ch) #1", f.Name);
        Assert.Contains("Patched Dimmer (1ch) #1 at channel 1", Screen.StatusText);

        // The start channel moves on past it, so the next goes after it.
        Click(ButtonCalled("Add Fixture"));
        Assert.Equal(1, Show.Patch.All.Last().Start);
        Assert.Equal(2, Screen.Console.Patch.Children.Count > 0 ? All(Screen).OfType<FixtureRow>().Count() : 0);
    }

    [Fact]
    public void A_fixtures_controls_follow_and_set_its_levels()
    {
        var spot = FixtureProfile.BuiltIn().First(p => p.Id == "mh_rgbw_pt7");
        var f = Show.Patch.Add(0, spot, 0, name: "Spot");
        Frame();
        var row = Find<FixtureRow>(r => r.Fixture == f);
        Assert.False(row.Expanded); // starts collapsed
        Click(Find<Button>(b => b.Tooltip == "Expand"));
        Assert.True(row.Expanded);
        Frame();

        // A pad for pan and tilt (both at their middle defaults), and a slider for the dimmer.
        var pad = All(row).OfType<PanTiltPad>().Single();
        Assert.Contains(All(pad).OfType<Label>(), l => l.Text == "P128  T128");
        var dimmer = All(row).OfType<ChannelSlider>().First();
        var slider = All(dimmer).OfType<Slider>().Single();
        Assert.Equal(0, slider.Value);

        // A fade moves the levels: the slider follows.
        Show.Dmx.Universes[0].SetChannel(2, 200);
        Frame();
        Assert.Equal(200, slider.Value);

        // Dragging the pad aims the head.
        var padArea = All(pad).Last(e => e.IsInteractive);
        Ui.MouseMove(padArea.ScreenPosition + new float2(0, 0));
        Ui.MouseButton(MouseButton.Left, down: true);
        Ui.MouseButton(MouseButton.Left, down: false);
        Frame();
        Assert.Equal((0, 255), (Show.Dmx.Universes[0].GetChannel(0), Show.Dmx.Universes[0].GetChannel(1))); // pan left, tilt up

        // Home puts it back.
        Click(Find<Button>(b => b.Tooltip == "Home: back to its default levels"));
        Assert.Equal((128, 128, 0), (Show.Dmx.Universes[0].GetChannel(0), Show.Dmx.Universes[0].GetChannel(1), Show.Dmx.Universes[0].GetChannel(2)));

        // The wheel nudges it a step: tilt, then pan with Shift (which turns the wheel sideways).
        Ui.MouseMove(padArea.ScreenPosition + padArea.Size / 2);
        Ui.MouseWheel(1);
        Frame();
        Assert.Equal((128, 129), (Show.Dmx.Universes[0].GetChannel(0), Show.Dmx.Universes[0].GetChannel(1)));
        Ui.MouseWheel(new float2(-1, 0));
        Ui.MouseWheel(new float2(-1, 0));
        Frame();
        // As in sLight, the first lands on the grid line the channel's 128 rounds back to.
        Assert.Equal((127, 129), (Show.Dmx.Universes[0].GetChannel(0), Show.Dmx.Universes[0].GetChannel(1)));

        // The category group buttons.
        Click(Find<Button>(b => b.Tooltip == "Category group 3"));
        Assert.Equal(3, f.CategoryGroup);
        Assert.True(Find<Button>(b => b.Tooltip == "Category group 3").Lit);

        Click(Find<Button>(b => b.Tooltip == "Remove from the patch"));
        Assert.Empty(Show.Patch.All);
    }

    [Fact]
    public void The_top_bar()
    {
        Click(ButtonCalled("Add Universe"));
        Assert.Equal(2, Show.Patch.Universes.Count);
        Assert.NotNull(ButtonCalled("Universe 2"));

        Click(ButtonCalled("Sending"));
        Assert.False(Screen.TopBar.Sending);
        Assert.NotNull(ButtonCalled("Not sending"));

        Show.Dmx.Universes[0].SetChannel(0, 99);
        Click(ButtonCalled("Blackout All"));
        Assert.Equal(0, Show.Dmx.Universes[0].GetChannel(0));
        Assert.StartsWith("Blackout", Screen.StatusText);

        // Removing a universe asks first.
        Click(ButtonCalled("Remove Universe"));
        Assert.Equal(2, Show.Patch.Universes.Count);
        Click(ButtonCalled("Remove"));
        Assert.Single(Show.Patch.Universes);
        Assert.False(ButtonCalled("Remove Universe").Enabled);

        Click(ButtonCalled("Tap"));
        Assert.True(Show.Tempo.Running);
    }

    [Fact]
    public void The_menu_bar()
    {
        var bar = Screen.MenuBar;
        Assert.Equal(["File", "Edit", "Mode"], bar.MenuTitles);
        Assert.Null(bar.OpenTitle);

        // File opens below its title, with the file commands and their keys.
        Click(MenuTitle("File"));
        Assert.Equal("File", bar.OpenTitle);
        var menu = Ui.Elements.OfType<ContextMenu>().Single();
        Assert.Equal(["New Show", "Load Show…", "Save Show", "Save Show As…", "Save Preset", "Load Preset", "Options…", "Exit"], menu.ItemTexts);
        Assert.Equal(3, All(menu).OfType<Divider>().Count());
        Assert.True(MenuItemCalled("New Show").ScreenPosition.Y >= bar.ScreenPosition.Y + bar.Size.Y - 1);
        Assert.True(MenuTitle("File").Lit);

        // Moving onto another title opens its menu instead; clicking the open title puts it away.
        Ui.MouseMove(MenuTitle("Edit").ScreenPosition + MenuTitle("Edit").Size / 2);
        Frame();
        Assert.Equal("Edit", bar.OpenTitle);
        Assert.Contains("Triggers…", menu.ItemTexts);
        ClickAt(MenuTitle("Edit").ScreenPosition + MenuTitle("Edit").Size / 2); // the open menu takes the click
        Assert.Null(bar.OpenTitle);
        Assert.False(menu.Visible);

        // Edit: nothing to undo in the console; the universes and Triggers.
        Click(MenuTitle("Edit"));
        Assert.False(MenuItemCalled("Undo Move").Enabled);
        Assert.False(MenuItemCalled("Remove Universe…").Enabled);
        Click(MenuItemCalled("Add Universe"));
        Assert.Equal(2, Show.Patch.Universes.Count);
        Assert.False(menu.Visible);
        FromMenu("Edit", "Remove Universe…");
        Click(ButtonCalled("Remove"));
        Assert.Single(Show.Patch.Universes);

        // Mode: the views (only the console here) and the run mode, the one in effect ticked.
        FromMenu("Mode", "Sound Reactive");
        Assert.Equal(RunMode.SoundReactive, Show.RunMode);
        Assert.StartsWith("Sound Reactive", Screen.StatusText);
        Click(MenuTitle("Mode"));
        Assert.Equal(["Console", "Cue Mode", "Sound Reactive"], menu.ItemTexts);
        Click(MenuItemCalled("Cue Mode"));
        Assert.Equal(RunMode.Cues, Show.RunMode);

        // File's New Show goes to the shell, which asks about the universes changed above.
        FromMenu("File", "New Show");
        Click(ButtonCalled("Don't Save"));
        Assert.Equal("New show (not saved yet).", Screen.StatusText);
    }

    void Press(Key key, Modifiers modifiers = Modifiers.None)
    {
        Ui.KeyDown(key, modifiers);
        Frame();
    }

    [Fact]
    public void The_menus_from_the_keyboard()
    {
        var bar = Screen.MenuBar;
        var menu = Ui.Elements.OfType<ContextMenu>().Single();

        // Alt and a title's letter opens it with its first item highlighted, even with a text box focused.
        Click(Find<TextBox>(t => t.Placeholder == "(optional)"));
        Press(Key.F, Modifiers.Alt);
        Assert.Equal("File", bar.OpenTitle);
        Assert.Equal("New Show", menu.HighlightedText);

        // Up and Down go round the items, skipping the separators.
        Press(Key.Down);
        Assert.Equal("Load Show…", menu.HighlightedText);
        Press(Key.Up);
        Press(Key.Up);
        Assert.Equal("Exit", menu.HighlightedText);
        Press(Key.Down);
        Assert.Equal("New Show", menu.HighlightedText);

        // Right and Left go to the menus beside it, round the ends; disabled items are skipped.
        Press(Key.Right);
        Assert.Equal("Edit", bar.OpenTitle);
        Assert.Equal("Add Universe", menu.HighlightedText); // nothing to undo
        Press(Key.Left);
        Press(Key.Left);
        Assert.Equal("Mode", bar.OpenTitle);

        // A letter moves to the next item starting with it; Enter chooses it.
        Press(Key.S);
        Assert.Equal("Sound Reactive", menu.HighlightedText);
        Press(Key.Enter);
        Assert.Equal(RunMode.SoundReactive, Show.RunMode);
        Assert.Null(bar.OpenTitle);
        Assert.False(menu.Visible);

        // F10 opens the first menu and closes it again; Escape closes one too.
        Press(Key.F10);
        Assert.Equal("File", bar.OpenTitle);
        Press(Key.F10);
        Assert.Null(bar.OpenTitle);
        Press(Key.E, Modifiers.Alt);
        Press(Key.Escape);
        Assert.False(menu.Visible);

        // Alt+E, Enter: the first item that can be chosen.
        Press(Key.E, Modifiers.Alt);
        Press(Key.Enter);
        Assert.Equal(2, Show.Patch.Universes.Count);

        // The mouse moves the highlight, and the keys carry on from there.
        Click(MenuTitle("File"));
        Assert.Null(menu.HighlightedText);
        Ui.MouseMove(MenuItemCalled("Save Show").ScreenPosition + new float2(4, 4));
        Frame();
        Assert.Equal("Save Show", menu.HighlightedText);
        Press(Key.Down);
        Assert.Equal("Save Show As…", menu.HighlightedText);
        Press(Key.Escape);
    }

    [Fact]
    public void A_dropdown_opens_on_its_choice()
    {
        var runMode = Find<Dropdown<RunMode>>(_ => true);
        var menu = Ui.Elements.OfType<ContextMenu>().Single();
        Click(runMode);
        Assert.Equal("Cue Mode", menu.HighlightedText);
        Press(Key.Down);
        Press(Key.Enter);
        Assert.Equal(RunMode.SoundReactive, Show.RunMode);
        Assert.False(menu.Visible);
    }
}

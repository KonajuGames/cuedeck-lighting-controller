using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Input;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The patch's Flash, the colour picker, presets and the fixture library dialog, clicked through.
/// </summary>
public sealed class PatchExtrasTests : ConsoleHarness
{
    int Level(int channel) => Show.Dmx.Universes[0].GetChannel(channel);

    [Fact]
    public void Flash_lights_a_fixture_while_held()
    {
        Show.Patch.Add(0, Profiles.DimmedRgb(), 0, name: "Par");
        Show.Dmx.Universes[0].SetChannel(1, 30);
        Frame();
        var flash = ButtonCalled("Flash");
        Ui.MouseMove(flash.ScreenPosition + flash.Size / 2);
        Ui.MouseButton(MouseButton.Left, down: true);
        Assert.Equal([255, 255, 255, 255], Enumerable.Range(0, 4).Select(Level));
        Ui.MouseButton(MouseButton.Left, down: false);
        Assert.Equal([0, 30, 0, 0], Enumerable.Range(0, 4).Select(Level)); // put back exactly
    }

    [Fact]
    public void Picking_a_colour_sets_a_heads_red_green_and_blue()
    {
        Show.Patch.Add(0, Profiles.Rgb(), 0, name: "Par");
        Frame();
        Click(Find<Button>(b => b.Tooltip == "Expand"));
        var swatch = All(Find<ColourSwatch>(_ => true)).Last(e => e.IsInteractive);
        Click(swatch);
        var picker = Ui.Elements.OfType<ColourPicker>().Single();
        Assert.True(picker.Visible);

        // Typing a hex code.
        var hex = All(picker).OfType<TextBox>().Single();
        Click(hex);
        hex.SelectAll();
        Type("ff8000");
        Assert.Equal((255, 128, 0), (Level(0), Level(1), Level(2)));

        // Clicking the hue strip at its top (red), then the square's top-right corner (full and bright).
        var strip = All(picker).Where(e => e != picker && e is not Panel && e.IsInteractive && e.Size.X < 30).First();
        ClickAt(strip.ScreenPosition + new float2(strip.Size.X / 2, 1));
        var square = All(picker).Where(e => e != picker && e is not Panel && e.IsInteractive && e.Size.X > 150).First();
        ClickAt(square.ScreenPosition + new float2(square.Size.X - 1, 1));
        Assert.True(Level(0) > 250 && Level(1) < 15 && Level(2) < 5, $"{Level(0)} {Level(1)} {Level(2)}");

        // A click outside closes it.
        ClickAt(new float2(5, ScreenSize.Y - 5));
        Assert.False(picker.Visible);
    }

    [Fact]
    public void Presets_saved_and_loaded()
    {
        Shell.PresetPath = Dir.File("preset.json");
        Click(ButtonCalled("Load Preset"));
        Assert.Equal("No preset found.", Screen.StatusText);
        Show.Dmx.Universes[0].SetChannel(7, 70);
        Click(ButtonCalled("Save Preset"));
        Assert.Equal("Preset saved (1 universe).", Screen.StatusText);
        Show.Dmx.Universes[0].SetAll(0);
        Click(ButtonCalled("Load Preset"));
        Assert.Equal(70, Level(7));
        Assert.Equal("Preset loaded.", Screen.StatusText);
    }

    [Fact]
    public void A_fixture_from_the_library_is_patched()
    {
        Click(ButtonCalled("Library…"));
        var dialog = Ui.Elements.OfType<LibraryDialog>().Single();
        Assert.True(dialog.Visible);
        Frame();
        Assert.Equal(638, dialog.Results.Items.Count);

        // Searching, with the search box focused as it opens.
        Type("auro beam 150");
        Assert.Equal("Auro Beam 150", Assert.Single(dialog.Results.Items).Model);
        var list = dialog.Results;
        ClickAt(list.ScreenPosition + new float2(40, list.RowHeight / 2));
        Frame();
        Assert.Contains(All(dialog).OfType<Label>(), l => l.Text == "Auro Beam 150"); // its preview
        Click(ButtonCalled("Add to Patch"));
        Assert.False(dialog.Visible);
        Assert.Contains("Added 'Auro Beam 150' from the library", Screen.StatusText);

        // It's the profile to patch now.
        Click(ButtonCalled("Add Fixture"));
        var f = Assert.Single(Show.Patch.All);
        Assert.Equal(("Cameo", "Auro Beam 150"), (f.Profile.Manufacturer, f.Profile.Model));
    }

    [Fact]
    public void The_library_filters()
    {
        Click(ButtonCalled("Library…"));
        var dialog = Ui.Elements.OfType<LibraryDialog>().Single();
        Click(Find<Checkbox>(c => c.Text == "Pan/Tilt only"));
        Assert.All(dialog.Results.Items, e => Assert.True(e.PanTilt));
        Assert.True(dialog.Results.Items.Count is > 0 and < 638);
        Ui.KeyDown(Key.Escape, Modifiers.None);
        Frame();
        Assert.False(dialog.Visible);
    }
}

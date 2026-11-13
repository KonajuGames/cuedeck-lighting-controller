using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Input;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The console driven as someone would: clicking the top bar's buttons, patching fixtures from the Patch tab and
/// moving their controls.
/// </summary>
public sealed class ConsoleTests : IDisposable
{
    static readonly float2 ScreenSize = new(1600, 1000);

    readonly LightingShow _show = new();
    readonly TempFolder _dir = new();
    readonly Root _ui = new();
    readonly MainScreen _screen;
    readonly AppShell _shell;

    public ConsoleTests()
    {
        var menu = new ContextMenu(MainScreenTests.TestFonts.Body);
        _screen = _ui.Add(new MainScreen(MainScreenTests.TestFonts, MainScreenTests.Pictures, menu, _show, new ProfileStore(_dir.File("profiles"))));
        var confirm = _ui.Add(new ConfirmDialog(_ui, MainScreenTests.TestFonts.Body, MainScreenTests.TestFonts.Bold, MainScreenTests.TestFonts.Heading));
        _ui.Add(menu);
        var session = new ShowSession(_show) { LastShowStore = _dir.File("last.json") };
        _shell = new AppShell(session, _screen, confirm, () => 0);
        Frame();
        Frame(); // the Patch tab builds its pages on the first
    }

    public void Dispose()
    {
        _show.Dispose();
        _dir.Dispose();
    }

    void Frame()
    {
        _screen.ScreenSize = ScreenSize;
        foreach (var dialog in _ui.Elements.OfType<ConfirmDialog>())
            dialog.ScreenSize = ScreenSize;
        _screen.Update();
        _ui.Layout(ScreenSize);
    }

    /// <summary>
    /// The visible element of type <typeparamref name="T"/> that <paramref name="match"/> picks, anywhere on screen.
    /// </summary>
    T Find<T>(Func<T, bool> match)
        where T : Element => _ui.Elements.Where(e => e.Visible).SelectMany(All).OfType<T>().First(match);

    static IEnumerable<Element> All(Element e)
    {
        yield return e;
        if (e is Panel p)
            foreach (var child in p.Children.Where(c => c.Visible))
                foreach (var d in All(child))
                    yield return d;
    }

    void Click(Element e)
    {
        var at = e.ScreenPosition + e.Size / 2;
        _ui.MouseMove(at);
        Assert.Same(e, _ui.Hovered);
        _ui.MouseButton(MouseButton.Left, down: true);
        _ui.MouseButton(MouseButton.Left, down: false);
        Frame();
    }

    Button ButtonCalled(string text) => Find<Button>(b => b.Text == text);

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
        var f = Assert.Single(_show.Patch.All);
        Assert.Equal("Dimmer (1ch)", f.Profile.Model); // the first profile
        Assert.Equal(0, f.Start);
        Assert.Equal("Dimmer (1ch) #1", f.Name);
        Assert.Contains("Patched Dimmer (1ch) #1 at channel 1", _screen.StatusText);

        // The start channel moves on past it, so the next goes after it.
        Click(ButtonCalled("Add Fixture"));
        Assert.Equal(1, _show.Patch.All.Last().Start);
        Assert.Equal(2, _screen.Console.Patch.Children.Count > 0 ? All(_screen).OfType<FixtureRow>().Count() : 0);
    }

    [Fact]
    public void A_fixtures_controls_follow_and_set_its_levels()
    {
        var spot = FixtureProfile.BuiltIn().First(p => p.Id == "mh_rgbw_pt7");
        var f = _show.Patch.Add(0, spot, 0, name: "Spot");
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
        _show.Dmx.Universes[0].SetChannel(2, 200);
        Frame();
        Assert.Equal(200, slider.Value);

        // Dragging the pad aims the head.
        var padArea = All(pad).Last(e => e.IsInteractive);
        _ui.MouseMove(padArea.ScreenPosition + new float2(0, 0));
        _ui.MouseButton(MouseButton.Left, down: true);
        _ui.MouseButton(MouseButton.Left, down: false);
        Frame();
        Assert.Equal((0, 255), (_show.Dmx.Universes[0].GetChannel(0), _show.Dmx.Universes[0].GetChannel(1))); // pan left, tilt up

        // Home puts it back.
        Click(Find<Button>(b => b.Text == "Home"));
        Assert.Equal((128, 128, 0), (_show.Dmx.Universes[0].GetChannel(0), _show.Dmx.Universes[0].GetChannel(1), _show.Dmx.Universes[0].GetChannel(2)));

        // The category group buttons.
        Click(Find<Button>(b => b.Tooltip == "Category group 3"));
        Assert.Equal(3, f.CategoryGroup);
        Assert.True(Find<Button>(b => b.Tooltip == "Category group 3").Lit);

        Click(Find<Button>(b => b.Text == "Remove"));
        Assert.Empty(_show.Patch.All);
    }

    [Fact]
    public void The_top_bar()
    {
        Click(ButtonCalled("Add Universe"));
        Assert.Equal(2, _show.Patch.Universes.Count);
        Assert.NotNull(ButtonCalled("Universe 2"));

        Click(ButtonCalled("Sending"));
        Assert.False(_screen.TopBar.Sending);
        Assert.NotNull(ButtonCalled("Not sending"));

        _show.Dmx.Universes[0].SetChannel(0, 99);
        Click(ButtonCalled("Blackout All"));
        Assert.Equal(0, _show.Dmx.Universes[0].GetChannel(0));
        Assert.StartsWith("Blackout", _screen.StatusText);

        // Removing a universe asks first.
        Click(ButtonCalled("Remove Universe"));
        Assert.Equal(2, _show.Patch.Universes.Count);
        Click(ButtonCalled("Remove"));
        Assert.Single(_show.Patch.Universes);
        Assert.False(ButtonCalled("Remove Universe").Enabled);

        Click(ButtonCalled("Tap"));
        Assert.True(_show.Tempo.Running);
    }
}

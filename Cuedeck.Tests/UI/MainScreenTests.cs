using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

public class MainScreenTests
{
    internal static readonly FontAtlas Atlas = new();
    internal static readonly Images Pictures = Images.Load(Atlas);

    internal static readonly Fonts TestFonts = MakeFonts();

    internal static readonly FixtureLibrary Bundled = new(FixtureLibrary.DefaultFolder);

    static Fonts MakeFonts()
    {
        var regular = FontAtlas.FindSystemFont(bold: false) ?? FontAtlas.FindSystemFont()!;
        var bold = FontAtlas.FindSystemFont(bold: true) ?? regular;
        return new Fonts(Atlas.Add(regular, 12), Atlas.Add(regular, 14), Atlas.Add(bold, 14), Atlas.Add(bold, 18), Atlas.Add(bold, 26));
    }

    internal static (Root Ui, MainScreen Screen) LaidOut(float2 size)
    {
        var ui = new Root();
        var menu = new ContextMenu(TestFonts.Body);
        var screen = ui.Add(new MainScreen(TestFonts, Pictures, menu, new Dialogs(new ConfirmDialog(ui, TestFonts.Body, TestFonts.Bold, TestFonts.Heading), new NamePrompt(ui, TestFonts.Body, TestFonts.Bold, TestFonts.Heading), new ColourPicker(ui, TestFonts.Body), new LibraryDialog(ui, TestFonts, menu, Bundled, new OnlineLibrary(Bundled, Path.Combine(Path.GetTempPath(), "cuedeck-no-online"), new FakeWeb())), new ProfileEditor(ui, TestFonts, menu), new TriggersDialog(ui, TestFonts, menu, new LightingShow()), new OptionsDialog(ui, TestFonts, menu, new AppSettings(Path.Combine(Path.GetTempPath(), "cuedeck-no-settings.json")))), new LightingShow(), new ProfileStore(Path.Combine(Path.GetTempPath(), "cuedeck-no-profiles"))));
        ui.Add(menu);
        screen.ScreenSize = size;
        screen.Update();
        ui.Layout(size);
        return (ui, screen);
    }

    [Fact]
    public void The_screen_fills_the_window_with_the_title_bar_across_the_top()
    {
        var (_, screen) = LaidOut(new float2(1366, 820));

        Assert.Equal(new float2(1366, 820), screen.Size);
        Assert.Equal(new float2(0, 0), screen.TitleBar.ScreenPosition);
        Assert.Equal(new float2(1366, TitleBar.HEIGHT), screen.TitleBar.Size);
    }

    [Fact]
    public void The_window_drags_from_the_title_bars_empty_space_but_not_its_buttons_or_below_it()
    {
        var (_, screen) = LaidOut(new float2(1366, 820));

        Assert.True(screen.TitleBar.IsCaption(new float2(400, 10)));
        // The close button is the last 46 pixels.
        Assert.False(screen.TitleBar.IsCaption(new float2(1366 - 20, 10)));
        Assert.False(screen.TitleBar.IsCaption(new float2(400, TitleBar.HEIGHT + 10)));
    }

    [Fact]
    public void The_close_button_asks_to_close()
    {
        var (ui, screen) = LaidOut(new float2(1366, 820));
        var closed = false;
        screen.TitleBar.CloseClicked += () => closed = true;

        ui.MouseMove(new float2(1366 - 20, 10));
        Assert.Equal("Close Cuedeck", ui.Hovered?.Tooltip);
        ui.MouseButton(MouseButton.Left, down: true);
        ui.MouseButton(MouseButton.Left, down: false);

        Assert.True(closed);
    }

    [Fact]
    public void The_show_name_follows_the_apps_name_only_when_there_is_one()
    {
        var (ui, screen) = LaidOut(new float2(1366, 820));
        var bare = screen.TitleBar.Children[0].Size.X;

        screen.TitleBar.Subtitle = "Friday club night *";
        screen.Update();
        ui.Layout(screen.ScreenSize);

        Assert.True(screen.TitleBar.Children[0].Size.X > bare);
    }
}

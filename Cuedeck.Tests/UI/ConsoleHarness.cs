using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Show;
using Cuedeck.UI;
using Cuedeck.UI.Console;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The whole screen with a show and its shell, laid out at a fixed size, for tests that drive it as someone would.
/// </summary>
public abstract class ConsoleHarness : IDisposable
{
    private protected static readonly float2 ScreenSize = new(1600, 1000);

    private protected readonly LightingShow Show = new();
    private protected readonly TempFolder Dir = new();
    private protected readonly Root Ui = new();
    private protected readonly MainScreen Screen;
    private protected readonly AppShell Shell;
    private protected readonly ProfileStore Store;

    /// <summary>
    /// Stands in for the web: the library's online sources ask it, never the network.
    /// </summary>
    private protected readonly FakeWeb Web = new();

    private protected ConsoleHarness()
    {
        var menu = new ContextMenu(MainScreenTests.TestFonts.Body);
        var fonts = MainScreenTests.TestFonts;
        var confirm = new ConfirmDialog(Ui, fonts.Body, fonts.Bold, fonts.Heading);
        var prompt = new NamePrompt(Ui, fonts.Body, fonts.Bold, fonts.Heading);
        var picker = new ColourPicker(Ui, fonts.Body);
        var bundled = new FixtureLibrary(FixtureLibrary.DefaultFolder);
        var library = new LibraryDialog(Ui, fonts, menu, bundled, new OnlineLibrary(bundled, Dir.File("online"), Web));
        var editor = new ProfileEditor(Ui, fonts, menu);
        var triggers = new TriggersDialog(Ui, fonts, menu, Show);
        var options = new OptionsDialog(Ui, fonts, menu, new AppSettings(Dir.File("settings.json")));
        Store = new ProfileStore(Dir.File("profiles"));
        Screen = Ui.Add(new MainScreen(fonts, MainScreenTests.Pictures, menu, new Dialogs(confirm, prompt, picker, library, editor, triggers, options), Show, Store));
        Ui.Add(editor);
        Ui.Add(triggers);
        Ui.Add(options);
        Screen.Shortcuts = Ui.Add(new ShortcutsDialog(Ui, fonts));
        Ui.Add(library);
        Ui.Add(confirm);
        Ui.Add(prompt);
        Ui.Add(picker);
        Ui.Add(menu);
        var session = new ShowSession(Show) { LastShowStore = Dir.File("last.json") };
        Shell = new AppShell(session, Screen, confirm, () => 0);
        Frame();
        Frame(); // the Patch tab builds its pages on the first
    }

    public virtual void Dispose()
    {
        Show.Dispose();
        Dir.Dispose();
    }

    private protected void Frame()
    {
        Screen.ScreenSize = ScreenSize;
        foreach (var dialog in Ui.Elements.OfType<ConfirmDialog>())
            dialog.ScreenSize = ScreenSize;
        foreach (var prompt in Ui.Elements.OfType<NamePrompt>())
            prompt.ScreenSize = ScreenSize;
        foreach (var picker in Ui.Elements.OfType<ColourPicker>())
            picker.ScreenSize = ScreenSize;
        foreach (var library in Ui.Elements.OfType<LibraryDialog>())
            library.ScreenSize = ScreenSize;
        foreach (var editor in Ui.Elements.OfType<ProfileEditor>())
            editor.ScreenSize = ScreenSize;
        foreach (var triggers in Ui.Elements.OfType<TriggersDialog>())
            triggers.ScreenSize = ScreenSize;
        foreach (var options in Ui.Elements.OfType<OptionsDialog>())
            options.ScreenSize = ScreenSize;
        foreach (var shortcuts in Ui.Elements.OfType<ShortcutsDialog>())
            shortcuts.ScreenSize = ScreenSize;
        foreach (var menu in Ui.Elements.OfType<ContextMenu>())
            menu.ScreenSize = ScreenSize;
        Screen.Update();
        Ui.Layout(ScreenSize);
    }

    /// <summary>
    /// The visible element of type <typeparamref name="T"/> that <paramref name="match"/> picks, anywhere on screen.
    /// </summary>
    private protected T Find<T>(Func<T, bool> match)
        where T : Element => Ui.Elements.Where(e => e.Visible).SelectMany(All).OfType<T>().First(match);

    private protected static IEnumerable<Element> All(Element e)
    {
        yield return e;
        if (e is Panel p)
            foreach (var child in p.Children.Where(c => c.Visible))
                foreach (var d in All(child))
                    yield return d;
    }

    private protected void Click(Element e)
    {
        var at = e.ScreenPosition + e.Size / 2;
        Ui.MouseMove(at);
        Assert.Same(e, Ui.Hovered);
        Ui.MouseButton(MouseButton.Left, down: true);
        Ui.MouseButton(MouseButton.Left, down: false);
        Frame();
    }

    private protected Button ButtonCalled(string text) => Find<Button>(b => b.Text == text);

    /// <summary>
    /// Clicks at <paramref name="at"/>, twice for a double-click.
    /// </summary>
    private protected void ClickAt(float2 at, int times = 1)
    {
        Ui.MouseMove(at);
        for (var i = 0; i < times; i++)
        {
            Ui.MouseButton(MouseButton.Left, down: true);
            Ui.MouseButton(MouseButton.Left, down: false);
        }
        Frame();
    }

    /// <summary>
    /// Chooses <paramref name="choice"/> from a dropdown, by opening it and clicking it in the menu.
    /// </summary>
    private protected void Choose<T>(Dropdown<T> dropdown, string choice)
    {
        Click(dropdown);
        Click(Find<Button>(b => b.Text == choice));
    }

    /// <summary>
    /// The menu bar's title called <paramref name="title"/>.
    /// </summary>
    private protected Button MenuTitle(string title) => Screen.MenuBar.Children.OfType<Button>().Single(b => b.Text == title);

    /// <summary>
    /// The open menu's item called <paramref name="text"/>.
    /// </summary>
    private protected Button MenuItemCalled(string text) => All(Ui.Elements.OfType<ContextMenu>().Single()).OfType<Button>().First(b => b.Text == text);

    /// <summary>
    /// Chooses <paramref name="item"/> from the menu bar's <paramref name="menu"/>, by clicking its title, then it.
    /// </summary>
    private protected void FromMenu(string menu, string item)
    {
        Click(MenuTitle(menu));
        Click(MenuItemCalled(item));
    }

    /// <summary>
    /// Runs frames until <paramref name="done"/>, for work in the background (the library's online sources); fails
    /// after five seconds.
    /// </summary>
    private protected void WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting");
            Thread.Sleep(5);
            Frame();
        }
    }

    /// <summary>
    /// Types <paramref name="text"/> into whatever has focus.
    /// </summary>
    private protected void Type(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            Ui.TextInput(rune);
        Frame();
    }
}

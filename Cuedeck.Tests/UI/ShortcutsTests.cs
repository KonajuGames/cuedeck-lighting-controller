using Cuedeck.UI;

namespace Cuedeck.Tests.UI;

/// <summary>
/// The keyboard shortcuts overlay: opened from the Help menu (or ?), listing every key the app answers.
/// </summary>
public sealed class ShortcutsTests : ConsoleHarness
{
    ShortcutsDialog Shortcuts => Ui.Elements.OfType<ShortcutsDialog>().Single();

    IEnumerable<string> Texts => All(Shortcuts).OfType<Label>().Select(l => l.Text);

    [Fact]
    public void The_help_menu_opens_the_shortcuts_and_escape_closes_them()
    {
        Assert.Equal(["File", "Edit", "Mode", "Help"], Screen.MenuBar.MenuTitles);
        FromMenu("Help", "Keyboard Shortcuts");
        Assert.True(Shortcuts.Visible);
        Assert.Contains("Keyboard shortcuts", Texts);
        Assert.Contains("Go the next cue (Cue Mode)", Texts);
        Assert.Contains("Space", Texts);

        Frame();
        Ui.KeyDown(Key.Escape, Modifiers.None);
        Frame();
        Assert.False(Shortcuts.Visible);
    }

    [Fact]
    public void Close_or_a_click_outside_puts_the_shortcuts_away()
    {
        Screen.ShowShortcuts();
        Frame();
        Click(Find<Button>(b => b.Text == "Close" && All(Shortcuts).Contains(b)));
        Assert.False(Shortcuts.Visible);

        Screen.ShowShortcuts();
        Frame();
        ClickAt(new(4, ScreenSize.Y - 4));
        Assert.False(Shortcuts.Visible);
    }

    [Fact]
    public void Every_menu_shortcut_is_listed()
    {
        var listed = KeyboardShortcuts.Sections.SelectMany(s => s.Rows).Select(r => r.Keys).ToList();
        static IEnumerable<MenuItem> Flat(IEnumerable<MenuItem> items) => items.SelectMany(i => (IEnumerable<MenuItem>)[i, .. Flat(i.Submenu ?? [])]);
        foreach (var title in Screen.MenuBar.MenuTitles)
            foreach (var item in Flat(Screen.Menus.Items(title)).Where(i => i.Shortcut is not null))
                Assert.Contains(listed, keys => keys.Contains(item.Shortcut!, StringComparison.Ordinal));
    }
}

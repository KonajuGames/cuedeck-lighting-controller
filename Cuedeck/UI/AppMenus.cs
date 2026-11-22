using Cuedeck.Show;

namespace Cuedeck.UI;

/// <summary>
/// The menu bar's menus. File: the show's file commands, the preset, Options and Exit (handled by whoever listens:
/// the app's shell). Edit: Undo, Redo, Cut, Copy, Paste and the rest for what's being edited (DJ Edit Mode's
/// timeline or position, or the 3D Visualizer's moves), then the universes and Triggers. Mode: the console, DJ Edit
/// Mode or DJ Perform Mode, and the run mode. Help: the keyboard shortcuts. Each menu's items are worked out as it
/// opens.
/// </summary>
sealed class AppMenus
{
    readonly MainScreen _screen;
    readonly LightingShow _show;

    public event Action? NewShow;
    public event Action? LoadShow;
    public event Action? SaveShow;
    public event Action? SaveShowAs;
    public event Action? SavePreset;
    public event Action? LoadPreset;
    public event Action? AddUniverse;
    public event Action? RemoveUniverse;
    public event Action? Exit;

    public AppMenus(MainScreen screen, MenuBar bar, LightingShow show)
    {
        _screen = screen;
        _show = show;
        bar.AddMenu("File", FileItems);
        bar.AddMenu("Edit", EditItems);
        bar.AddMenu("Mode", ModeItems);
        bar.AddMenu("Help", HelpItems);
    }

    /// <summary>
    /// The items the menu called <paramref name="title"/> has now.
    /// </summary>
    internal IReadOnlyList<MenuItem> Items(string title) =>
        title switch
        {
            "File" => FileItems(),
            "Edit" => EditItems(),
            "Mode" => ModeItems(),
            "Help" => HelpItems(),
            _ => [],
        };

    IReadOnlyList<MenuItem> HelpItems() => [new("Keyboard Shortcuts", _screen.ShowShortcuts, _screen.Shortcuts is not null, Shortcut: "?")];

    IReadOnlyList<MenuItem> FileItems() =>
        [
            new("New Show", () => NewShow?.Invoke(), Shortcut: "Ctrl+N"),
            new("Load Show…", () => LoadShow?.Invoke(), Shortcut: "Ctrl+O"),
            new("Save Show", () => SaveShow?.Invoke(), Shortcut: "Ctrl+S"),
            new("Save Show As…", () => SaveShowAs?.Invoke(), Shortcut: "Ctrl+Shift+S"),
            MenuItem.Separator,
            new("Save Preset", () => SavePreset?.Invoke()),
            new("Load Preset", () => LoadPreset?.Invoke()),
            MenuItem.Separator,
            new("Options…", _screen.Dialogs.Options.Show),
            MenuItem.Separator,
            new("Exit", () => Exit?.Invoke(), Shortcut: "Alt+F4"),
        ];

    IReadOnlyList<MenuItem> EditItems()
    {
        var items = new List<MenuItem>();
        if (_screen.View == AppView.DjEdit && _screen.DjEdit is { IsTimelineShown: true } timelineView)
        {
            // DJ Edit Mode's timeline: the open song's edits and the selection on it.
            var tl = timelineView.Timeline.Timelines;
            var selected = !tl.Selection.IsEmpty;
            items.AddRange(
                [
                    new("Undo", () => tl.Undo(), tl.CanUndo, Shortcut: "Ctrl+Z"),
                    new("Redo", () => tl.Redo(), tl.CanRedo, Shortcut: "Ctrl+Y"),
                    MenuItem.Separator,
                    new("Cut", () => tl.CutSelection(), selected, Shortcut: "Ctrl+X"),
                    new("Copy", () => tl.CopySelection(), selected, Shortcut: "Ctrl+C"),
                    new("Paste", () => tl.Paste(), tl.Clipboard is { Tracks.Count: > 0 } && tl.HasWave, Shortcut: "Ctrl+V"),
                    new("Delete", () => tl.DeleteSelection(), selected, Shortcut: "Del"),
                    MenuItem.Separator,
                    new("Select All", () => tl.SelectAll(), tl.HasWave, Shortcut: "Ctrl+A"),
                    new("Select None", tl.Selection.Clear, selected, Shortcut: "Ctrl+D"),
                ]
            );
        }
        else if (_screen.View == AppView.DjEdit && _screen.DjEdit is { IsPositionShown: true } positionView)
        {
            // A position: the selected fixtures' points.
            var editor = positionView.Editor;
            items.AddRange(
                [
                    new("Undo", () => { }, false, Shortcut: "Ctrl+Z"),
                    new("Redo", () => { }, false, Shortcut: "Ctrl+Y"),
                    MenuItem.Separator,
                    new("Copy Points", () => editor.CopyPoints(), editor.CanCopyPoints, Shortcut: "Ctrl+C"),
                    new("Paste Points", () => editor.PastePoints(), editor.CanPastePoints, Shortcut: "Ctrl+V"),
                ]
            );
        }
        else
        {
            // The console and DJ Perform Mode: the 3D Visualizer's moves, while it shows.
            var viz = _screen.Visualizer.IsShown();
            var scene = _show.Visualizer;
            items.AddRange(
                [
                    new("Undo Move", () => scene.Undo(), viz && scene.CanUndo, Shortcut: "Ctrl+Z"),
                    new("Redo Move", () => scene.Redo(), viz && scene.CanRedo, Shortcut: "Ctrl+Y"),
                ]
            );
        }
        items.AddRange(
            [
                MenuItem.Separator,
                new("Add Universe", () => AddUniverse?.Invoke(), Shortcut: "Ctrl+Shift+N"),
                new("Remove Universe…", () => RemoveUniverse?.Invoke(), _show.Patch.Universes.Count > 1, Shortcut: "Ctrl+Shift+R"),
                MenuItem.Separator,
                new("Triggers…", _screen.Dialogs.Triggers.Show),
            ]
        );
        return items;
    }

    IReadOnlyList<MenuItem> ModeItems()
    {
        var view = _screen.View;
        var items = new List<MenuItem> { new("Console", () => _screen.SetView(AppView.Console), Checked: view == AppView.Console) };
        if (_screen.DjEdit is not null)
            items.Add(new("DJ Edit Mode", () => _screen.SetView(AppView.DjEdit), Checked: view == AppView.DjEdit));
        if (_screen.DjPerform is not null)
            items.Add(new("DJ Perform Mode", () => _screen.SetView(AppView.DjPerform), Checked: view == AppView.DjPerform));
        items.AddRange(
            [
                MenuItem.Separator,
                new("Cue Mode", () => _screen.TopBar.SetRunMode(RunMode.Cues), Checked: _show.RunMode == RunMode.Cues),
                new("Sound Reactive", () => _screen.TopBar.SetRunMode(RunMode.SoundReactive), Checked: _show.RunMode == RunMode.SoundReactive),
            ]
        );
        return items;
    }
}

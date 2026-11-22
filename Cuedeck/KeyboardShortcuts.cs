namespace Cuedeck;

/// <summary>
/// One line of the shortcuts: what it does, and the keys (or mouse) for it, written as the menus write them.
/// </summary>
sealed record Shortcut(string Action, string Keys);

/// <summary>
/// A heading of the shortcuts, with its lines: where they work.
/// </summary>
sealed record ShortcutSection(string Title, IReadOnlyList<Shortcut> Rows);

/// <summary>
/// Every keyboard shortcut, in words, for the shortcuts overlay (<see cref="UI.ShortcutsDialog"/>): the menus' keys,
/// those the app's key handler and each view answer, and the mouse's in the 3D Visualizer and the timeline.
/// </summary>
static class KeyboardShortcuts
{
    public static IReadOnlyList<ShortcutSection> Sections { get; } =
    [
        new(
            "Shows",
            [
                new("New show", "Ctrl+N"),
                new("Open a show", "Ctrl+O"),
                new("Save the show / save it as", "Ctrl+S / Ctrl+Shift+S"),
                new("Add a universe / remove one", "Ctrl+Shift+N / Ctrl+Shift+R"),
                new("Exit", "Alt+F4"),
            ]
        ),
        new(
            "Console",
            [
                new("Go the next cue (Cue Mode)", "Space"),
                new("Tap the tempo", "T"),
            ]
        ),
        new(
            "3D Visualizer",
            [
                new("Undo / redo a move, the pointer over it", "Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z)"),
                new("Orbit; drag a fixture or set piece", "Drag"),
                new("Slide the camera", "Shift + drag, or middle drag"),
                new("Zoom", "Mouse wheel"),
            ]
        ),
        new(
            "DJ Edit Mode's timeline",
            [
                new("Play / pause the song", "Space"),
                new("Undo / redo", "Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z)"),
                new("Cut, copy, paste", "Ctrl+X, Ctrl+C, Ctrl+V"),
                new("Delete what's selected", "Del"),
                new("Select all / none", "Ctrl+A / Ctrl+D (or Esc)"),
                new("Zoom / scroll the waveform", "Mouse wheel / Shift + wheel"),
                new("Pan the view", "Middle drag"),
            ]
        ),
        new("DJ Edit Mode's positions", [new("Copy / paste the selected fixtures' points", "Ctrl+C / Ctrl+V")]),
        new(
            "DJ Perform Mode",
            [new($"Hold {string.Join(", ", Show.LiveFlash.Names).ToLowerInvariant()}", string.Join(" / ", UI.DjPerform.DjPerformView.FlashKeys))]
        ),
        new(
            "Menus and the rest",
            [
                new("Open a menu", "Alt + its underlined letter, or F10"),
                new("Next control / the one before", "Tab / Shift+Tab"),
                new("Close a dialog", "Esc"),
                new("These shortcuts", "?"),
            ]
        ),
    ];
}

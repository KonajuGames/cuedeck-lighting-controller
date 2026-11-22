using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// Every keyboard shortcut, over the screen dimmed behind it, a heading for where each group works, from
/// <see cref="KeyboardShortcuts"/>. ? or the Help menu opens it, and ?, Close, Escape or a click outside it closes it.
/// Sized to the screen by whoever lays it out, with <see cref="ScreenSize"/>; on a screen too short for it all, it
/// scrolls. Cuepoint's shortcuts overlay.
/// </summary>
sealed class ShortcutsDialog : Panel
{
    const float MARGIN = 16;
    const float ACTION_WIDTH = 330;
    const float KEYS_WIDTH = 270;

    readonly Root _root;
    readonly ScrollPanel _box;
    bool _pressedInBox;

    public float2 ScreenSize { get; set; }

    public ShortcutsDialog(Root root, Fonts fonts)
    {
        _root = root;
        Visible = false;
        Background = Theme.Scrim(0.75f);

        _box = Add(new ScrollPanel { Padding = 20, Background = Theme.PanelRaised, BorderColor = Theme.PanelBorder, BorderWidth = 1 });
        var content = _box.Add(new Panel { Spacing = 14 });
        content.Add(new Label(fonts.Heading, "Keyboard shortcuts") { Color = Theme.Text });
        foreach (var section in KeyboardShortcuts.Sections)
        {
            var table = content.Add(new Panel { Spacing = 3 });
            table.Add(new Label(fonts.Bold, section.Title) { Color = Theme.Text });
            foreach (var shortcut in section.Rows)
            {
                var row = table.Add(new Panel { Direction = Direction.Horizontal, Spacing = 12 });
                row.Add(new Panel { MinSize = new float2(ACTION_WIDTH, 0) }).Add(new Label(fonts.Body, shortcut.Action) { Color = Theme.Muted, MaxWidth = ACTION_WIDTH, Wrap = true });
                row.Add(new Panel { MinSize = new float2(KEYS_WIDTH, 0) }).Add(new Label(fonts.Body, shortcut.Keys) { Color = Theme.Text, MaxWidth = KEYS_WIDTH, Wrap = true });
            }
        }
        var close = content.Add(new Button(fonts.Body, "Close") { MinSize = new float2(90, 0) });
        close.Clicked += _ => Close();

        // A click on the dimmed screen around it closes it too; one on the box's own empty space doesn't.
        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    public override bool IsInteractive => true;
    public override bool IsModal => true;
    public override Cursor Cursor => Cursor.Arrow;
    public override bool IsFocusable => Visible;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key != Key.Escape)
            return false;
        Close();
        return true;
    }

    public void Show()
    {
        Visible = true;
        _root.Focus(this);
    }

    public void Toggle()
    {
        if (Visible)
            Close();
        else
            Show();
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        _root.Focus(null);
    }

    public override float2 Measure()
    {
        if (Visible && _root.Focused is null)
            _root.Focus(this); // back on it once a menu's gone, so Escape still reaches it
        _box.MaxWidth = MathF.Max(0, ScreenSize.X - 2 * MARGIN);
        _box.MaxHeight = MathF.Max(0, ScreenSize.Y - 2 * MARGIN);
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(float2.Round(position + float2.Max(float2.Zero, (ScreenSize - _box.Size) / 2)));
    }
}

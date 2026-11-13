using Brutal.Numerics;
using Cuedeck.Input;

namespace Cuedeck.UI;

/// <summary>
/// Asks whether to go ahead with something that can't easily be taken back, in a box over the dimmed screen. The
/// confirming button says what will happen ("Delete"); Enter presses it, and Escape, Cancel or a click on the
/// dimmed screen gives up. It takes focus itself while shown, so those keys reach it.
/// </summary>
sealed class ConfirmDialog : Panel
{
    readonly Root _root;
    readonly Panel _box;
    readonly Label _title;
    readonly Label _message;
    readonly Button _confirm;
    readonly Button _alternative;
    Action _confirmed = () => { };
    Action? _alternativeChosen;

    public float2 ScreenSize { get; set; }

    public ConfirmDialog(Root root, Font body, Font bold, Font heading)
    {
        _root = root;
        Visible = false;
        Background = Colors.Hex(0x000000, 0.6f);

        _box = Add(
            new Panel
            {
                Padding = 20,
                Spacing = 12,
                Background = Theme.PanelRaised,
                BorderColor = Theme.PanelBorder,
                BorderWidth = 1,
                MinSize = new float2(420, 0),
            }
        );
        _title = _box.Add(new Label(heading) { Color = Theme.Text, MaxWidth = 520 });
        _message = _box.Add(new Label(body) { Color = Theme.Muted, MaxWidth = 520, Wrap = true });
        var buttons = _box.Add(new Panel { Direction = Direction.Horizontal, Spacing = 8 });
        _confirm = buttons.Add(
            new Button(bold, "")
            {
                MinSize = new float2(90, 0),
                Background = Colors.Hex(0xB83A3A),
                HoverBackground = Colors.Hex(0xD04848),
                PressedBackground = Colors.Hex(0x962E2E),
                FocusColor = Theme.Focus,
            }
        );
        _alternative = buttons.Add(new Button(bold, "") { MinSize = new float2(90, 0), FocusColor = Theme.Focus, Visible = false });
        var cancel = buttons.Add(new Button(bold, "Cancel") { MinSize = new float2(90, 0), FocusColor = Theme.Focus });

        _confirm.Clicked += _ => Confirm();
        _alternative.Clicked += _ =>
        {
            var chosen = _alternativeChosen;
            Close();
            chosen?.Invoke();
        };
        cancel.Clicked += _ => Close();
        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    // A press on the box itself, around its text, mustn't count as a click on the dimmed screen.
    bool _pressedInBox;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override bool IsInteractive => true;
    public override bool IsFocusable => Visible;
    public override Cursor Cursor => Cursor.Arrow;

    /// <summary>
    /// Asks <paramref name="title"/>, explained by <paramref name="message"/>, with <paramref name="confirm"/> on
    /// the button that goes ahead and runs <paramref name="confirmed"/>.
    /// </summary>
    public void Show(string title, string message, string confirm, Action confirmed) => Show(title, message, confirm, confirmed, null, null);

    /// <summary>
    /// Asks as <see cref="Show(string, string, string, Action)"/> does, with a second way to go ahead between the
    /// confirming button and Cancel: <paramref name="alternative"/> on its button, running
    /// <paramref name="alternativeChosen"/>. As Save, Don't Save and Cancel.
    /// </summary>
    public void Show(string title, string message, string confirm, Action confirmed, string? alternative, Action? alternativeChosen)
    {
        // A choice between ways to go ahead (Save, Don't Save) isn't a warning, so its button isn't red.
        var warning = alternative is null;
        _confirm.Background = warning ? Colors.Hex(0xB83A3A) : Colors.Hex(0x3D6FB8);
        _confirm.HoverBackground = warning ? Colors.Hex(0xD04848) : Colors.Hex(0x4A80CC);
        _confirm.PressedBackground = warning ? Colors.Hex(0x962E2E) : Colors.Hex(0x335E9C);
        _title.Text = title;
        _message.Text = message;
        _confirm.Text = confirm;
        _confirmed = confirmed;
        _alternative.Visible = alternative is not null;
        _alternative.Text = alternative ?? "";
        _alternativeChosen = alternativeChosen;
        Visible = true;
        _root.Focus(this);
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        if (_root.Focused == this || _root.Focused is { } focused && IsInside(focused))
            _root.Focus(null);
    }

    bool IsInside(Element element) => _box.Children.OfType<Panel>().Any(panel => panel.Children.Contains(element));

    void Confirm()
    {
        Close();
        _confirmed();
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        switch (key)
        {
            case Key.Enter:
                Confirm();
                return true;
            case Key.Escape:
                Close();
                return true;
        }
        return false;
    }

    public override float2 Measure()
    {
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(new float2(MathF.Round(position.X + (ScreenSize.X - _box.Size.X) / 2), MathF.Round(position.Y + (ScreenSize.Y - _box.Size.Y) / 3)));
    }
}

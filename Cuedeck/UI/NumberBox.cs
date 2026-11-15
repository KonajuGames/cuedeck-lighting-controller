using System.Globalization;
using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// A number to type, step with the up and down arrows stacked at its right, or nudge with the mouse wheel, kept
/// between <see cref="Min"/> and <see cref="Max"/>: sLight's spin boxes. What's typed takes effect on Enter or when the box loses focus.
/// </summary>
sealed class NumberBox : Row
{
    readonly TextBox _box;
    readonly StepArrow _up;
    readonly StepArrow _down;
    double _value;

    public NumberBox(Font font, double min, double max, double step = 1, int decimals = 0, float width = 70)
        : base(1)
    {
        Min = min;
        Max = max;
        Step = step;
        Decimals = decimals;
        _box = Add(new NumberText(font, this) { Width = width });
        var arrows = Add(new Panel { Direction = Direction.Vertical });
        _up = arrows.Add(new StepArrow(true) { Tooltip = "Step up" });
        _down = arrows.Add(new StepArrow(false) { Tooltip = "Step down" });
        _up.Clicked += _ => Nudge(1);
        _down.Clicked += _ => Nudge(-1);
        _box.Submitted += _ =>
        {
            Commit();
            Submitted?.Invoke(this);
        };
        _box.Cancelled += _ => Show();
        Show();
    }

    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; }
    public int Decimals { get; set; }

    /// <summary>
    /// Written after the number in the box, as "s" for seconds.
    /// </summary>
    public string Suffix { get; set; } = "";

    /// <summary>
    /// The number. Setting it doesn't raise <see cref="Changed"/>.
    /// </summary>
    public double Value
    {
        get => _value;
        set
        {
            _value = Clamp(value);
            if (!_box.IsFocused)
                Show();
        }
    }

    /// <summary>
    /// The person changed the number.
    /// </summary>
    public event Action<NumberBox>? Changed;

    /// <summary>
    /// Enter was pressed in it (after what was typed is taken).
    /// </summary>
    public event Action<NumberBox>? Submitted;

    double Clamp(double v) => Math.Round(Math.Clamp(v, Min, Max), Decimals);

    void Show() => _box.Text = _value.ToString("F" + Decimals, CultureInfo.InvariantCulture) + Suffix;

    void Set(double v)
    {
        v = Clamp(v);
        var changed = v != _value;
        _value = v;
        Show();
        if (changed)
            Changed?.Invoke(this);
    }

    void Nudge(int steps) => Set(_value + steps * Step);

    void Commit()
    {
        var text = _box.Text.Trim();
        if (Suffix != "" && text.EndsWith(Suffix, StringComparison.Ordinal))
            text = text[..^Suffix.Length].Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            Set(v);
        else
            Show();
    }

    public override bool Wheel(float2 notches)
    {
        Nudge(notches.Y > 0 ? 1 : -1);
        return true;
    }

    // The arrows share the box's height between them, the top one taking any odd pixel's shortfall.
    public override float2 Measure()
    {
        var height = _box.Measure().Y;
        _up.Height = MathF.Floor(height / 2);
        _down.Height = height - _up.Height;
        return base.Measure();
    }

    /// <summary>
    /// One of the two small buttons stacked beside the box: a triangle pointing up or down, that steps the number.
    /// </summary>
    sealed class StepArrow(bool up) : Element
    {
        const float WIDTH = 16;
        const int ARROW = 4;

        public float Height { get; set; }

        public override bool IsInteractive => true;

        // Leaves focus in the box, so a click doesn't commit and reshow half-typed text.
        public override bool KeepsFocusOnClick => true;

        public override float2 Measure() => Size = new float2(WIDTH, Height);

        public override void Draw(Batch batch)
        {
            var background =
                IsPressed && IsHovered ? Colors.Hex(0x1E1E26)
                : IsHovered || IsPressed ? Colors.Hex(0x3A3A48)
                : Colors.Hex(0x2A2A35);
            batch.AddRect(ScreenPosition, Size, background);

            // A line between the two, so they read as two buttons.
            if (!up)
                batch.AddRect(ScreenPosition, new float2(Size.X, 1), Colors.Hex(0x1E1E26));

            var color = IsHovered ? Theme.Text : Theme.Muted;
            var left = MathF.Round(ScreenPosition.X + Size.X / 2 - ARROW + 0.5f);
            var top = MathF.Round(ScreenPosition.Y + Size.Y / 2 - ARROW / 2f + (up ? 0 : 0.5f));
            for (var i = 0; i < ARROW; i++)
            {
                var row = up ? ARROW - 1 - i : i;
                batch.AddRect(new float2(left + row, top + i), new float2(2 * (ARROW - row) - 1, 1), color);
            }
        }
    }

    /// <summary>
    /// The box itself: commits what's typed when it loses focus, and steps with the wheel and Up and Down.
    /// </summary>
    sealed class NumberText(Font font, NumberBox owner) : TextBox(font)
    {
        public override void FocusLost() => owner.Commit();

        public override bool Wheel(float2 notches) => owner.Wheel(notches);

        public override bool KeyDown(Key key, Modifiers modifiers)
        {
            switch (key)
            {
                case Key.Up:
                    owner.Nudge(1);
                    return true;
                case Key.Down:
                    owner.Nudge(-1);
                    return true;
                default:
                    return base.KeyDown(key, modifiers);
            }
        }
    }
}

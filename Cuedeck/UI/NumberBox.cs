using System.Globalization;
using Brutal.Numerics;

namespace Cuedeck.UI;

/// <summary>
/// A number to type, step with − and +, or nudge with the mouse wheel, kept between <see cref="Min"/> and
/// <see cref="Max"/>: sLight's spin boxes. What's typed takes effect on Enter or when the box loses focus.
/// </summary>
sealed class NumberBox : Row
{
    readonly TextBox _box;
    double _value;

    public NumberBox(Font font, double min, double max, double step = 1, int decimals = 0, float width = 70)
        : base(2)
    {
        Min = min;
        Max = max;
        Step = step;
        Decimals = decimals;
        var down = Add(new Button(font, "−") { Padding = 4, MinSize = new float2(22, 0) });
        _box = Add(new NumberText(font, this) { Width = width });
        var up = Add(new Button(font, "+") { Padding = 4, MinSize = new float2(22, 0) });
        down.Clicked += _ => Nudge(-1);
        up.Clicked += _ => Nudge(1);
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

    /// <summary>
    /// The box itself: commits what's typed when it loses focus, and steps with the wheel and Up and Down.
    /// </summary>
    sealed class NumberText(Font font, NumberBox owner) : TextBox(font)
    {
        public override void FocusLost() => owner.Commit();

        public override bool Wheel(float2 notches) => owner.Wheel(notches);

        public override bool KeyDown(Input.Key key, Input.Modifiers modifiers)
        {
            switch (key)
            {
                case Input.Key.Up:
                    owner.Nudge(1);
                    return true;
                case Input.Key.Down:
                    owner.Nudge(-1);
                    return true;
                default:
                    return base.KeyDown(key, modifiers);
            }
        }
    }
}

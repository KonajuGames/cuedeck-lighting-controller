using System.Globalization;
using Brutal.Numerics;
using Cuedeck.Common;

namespace Cuedeck.UI;

/// <summary>
/// A colour to pick, in a box that opens beside what asked for it: a square for saturation (across) and brightness
/// (up), a strip for hue, and the hex code to type. Every change is passed on as it's made; a click outside the box or
/// Escape closes it.
/// </summary>
sealed class ColourPicker : Panel
{
    const float SQUARE = 180;
    const float STRIP = 22;
    const int STEPS = 36;

    readonly Root _root;
    readonly Panel _box;
    readonly SaturationValue _square;
    readonly HueStrip _strip;
    readonly TextBox _hex;
    readonly Swatch _swatch;
    Action<Rgb> _changed = _ => { };
    float2 _at;
    bool _pressedInBox;

    public float2 ScreenSize { get; set; }

    public ColourPicker(Root root, Font body)
    {
        _root = root;
        Visible = false;
        _box = Add(new Panel { Padding = 10, Spacing = 8, Background = Theme.PanelRaised, BorderColor = Theme.PanelBorder, BorderWidth = 1 });
        var pick = _box.Add(new Row(8));
        _square = pick.Add(new SaturationValue(this));
        _strip = pick.Add(new HueStrip(this));
        var bottom = _box.Add(new Row(8));
        _swatch = bottom.Add(new Swatch());
        bottom.Add(new Label(body, "#") { Color = Theme.Muted });
        _hex = bottom.Add(new TextBox(body) { Width = 100, MaxLength = 7 });
        _hex.Submitted += _ => TypedHex();
        _hex.TextChanged += t =>
        {
            if (t.Text.TrimStart('#').Length == 6)
                TypedHex();
        };
        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    public float Hue { get; private set; }
    public float Saturation { get; private set; }
    public float Value { get; private set; }

    public Rgb Colour => Rgb.FromHsv(Hue, Saturation, Value);

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;
    public override bool IsFocusable => Visible;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    /// <summary>
    /// Opens with <paramref name="colour"/> beside <paramref name="at"/> (kept on screen), passing each change to
    /// <paramref name="changed"/>.
    /// </summary>
    public void Show(float2 at, Rgb colour, Action<Rgb> changed)
    {
        _at = at;
        _changed = changed;
        (Hue, Saturation, Value) = ToHsv(colour);
        ShowHex();
        Visible = true;
        _root.Focus(this);
    }

    public void Close()
    {
        Visible = false;
        if (_root.Focused == this || _root.Focused == _hex)
            _root.Focus(null);
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key is Key.Escape or Key.Enter)
        {
            Close();
            return true;
        }
        return false;
    }

    void Set(float? hue = null, float? saturation = null, float? value = null)
    {
        Hue = Math.Clamp(hue ?? Hue, 0, 1);
        Saturation = Math.Clamp(saturation ?? Saturation, 0, 1);
        Value = Math.Clamp(value ?? Value, 0, 1);
        ShowHex();
        _changed(Colour);
    }

    void ShowHex()
    {
        if (!_hex.IsFocused)
            _hex.Text = Colour.ToHtml();
    }

    void TypedHex()
    {
        if (!Rgb.TryParse(_hex.Text.Trim(), out var c) || _hex.Text.TrimStart('#').Length != 6)
            return;
        (Hue, Saturation, Value) = ToHsv(c);
        _changed(Colour);
    }

    /// <summary>
    /// A colour's hue, saturation and value, each 0 to 1.
    /// </summary>
    public static (float H, float S, float V) ToHsv(Rgb c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        return (c.Hue, max > 0 ? (max - min) / max : 0, max);
    }

    static float4 Linear(Rgb c) => Colors.FromSrgb(c.R, c.G, c.B);

    public override float2 Measure()
    {
        _swatch.Colour = Linear(Colour);
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        var at = float2.Clamp(_at, float2.Zero, float2.Max(float2.Zero, ScreenSize - _box.Size));
        _box.Arrange(float2.Round(at));
    }

    /// <summary>
    /// The square: saturation across, brightness up, at the picker's hue.
    /// </summary>
    sealed class SaturationValue(ColourPicker picker) : Element
    {
        public override bool IsInteractive => true;
        public override Cursor Cursor => Cursor.Arrow;

        public override float2 Measure() => Size = new float2(SQUARE, SQUARE);

        void Pick(float2 point)
        {
            var local = (point - ScreenPosition) / Size;
            picker.Set(saturation: local.X, value: 1 - local.Y);
        }

        public override void PointerDown(float2 point, Modifiers modifiers) => Pick(point);

        public override void PointerDrag(float2 point) => Pick(point);

        public override void Draw(Batch batch)
        {
            var cell = SQUARE / STEPS;
            for (var x = 0; x < STEPS; x++)
                for (var y = 0; y < STEPS; y++)
                {
                    var c = Rgb.FromHsv(picker.Hue, (x + 0.5) / STEPS, 1 - (y + 0.5) / STEPS);
                    batch.AddRect(ScreenPosition + new float2(x * cell, y * cell), new float2(MathF.Ceiling(cell), MathF.Ceiling(cell)), Linear(c));
                }
            var at = ScreenPosition + new float2(picker.Saturation * SQUARE, (1 - picker.Value) * SQUARE);
            batch.AddBorder(float2.Round(at - new float2(5, 5)), new float2(10, 10), 2, picker.Value > 0.5f ? Colors.Hex(0x000000) : Colors.White);
            batch.AddBorder(ScreenPosition, Size, 1, Theme.PanelBorder);
        }
    }

    /// <summary>
    /// The hue strip, red at the top round to red again at the bottom.
    /// </summary>
    sealed class HueStrip(ColourPicker picker) : Element
    {
        public override bool IsInteractive => true;
        public override Cursor Cursor => Cursor.Arrow;

        public override float2 Measure() => Size = new float2(STRIP, SQUARE);

        void Pick(float2 point) => picker.Set(hue: Math.Clamp((point.Y - ScreenPosition.Y) / Size.Y, 0, 0.9999f));

        public override void PointerDown(float2 point, Modifiers modifiers) => Pick(point);

        public override void PointerDrag(float2 point) => Pick(point);

        public override void Draw(Batch batch)
        {
            var cell = SQUARE / STEPS;
            for (var y = 0; y < STEPS; y++)
                batch.AddRect(ScreenPosition + new float2(0, y * cell), new float2(STRIP, MathF.Ceiling(cell)), Linear(Rgb.FromHsv((y + 0.5) / STEPS, 1, 1)));
            var at = MathF.Round(ScreenPosition.Y + picker.Hue * SQUARE);
            batch.AddRect(new float2(ScreenPosition.X - 2, at - 1), new float2(STRIP + 4, 3), Colors.White);
            batch.AddBorder(ScreenPosition, Size, 1, Theme.PanelBorder);
        }
    }

    sealed class Swatch : Element
    {
        public float4 Colour { get; set; }

        public override float2 Measure() => Size = new float2(40, 26);

        public override void Draw(Batch batch)
        {
            batch.AddRect(ScreenPosition, Size, Colour);
            batch.AddBorder(ScreenPosition, Size, 1, Theme.PanelBorder);
        }
    }

    /// <summary>
    /// The picker's colour as "rrggbb", for tests and the status line.
    /// </summary>
    public override string ToString() => Colour.ToHtml().ToString(CultureInfo.InvariantCulture);
}

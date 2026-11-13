using System.Globalization;
using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Dmx;
using Cuedeck.Fixtures;
using Cuedeck.Input;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// One of a patched fixture's controls, which follows the universe's levels (a cue's fade, a look loaded) unless
/// it's being moved.
/// </summary>
interface IFixtureControl
{
    /// <summary>
    /// Shows the levels in <paramref name="data"/>, the universe's.
    /// </summary>
    void Sync(byte[] data);
}

/// <summary>
/// A labelled column: a name over a control over its value.
/// </summary>
class ControlColumn : Panel
{
    protected readonly Label Value;

    public ControlColumn(Fonts fonts, string name)
    {
        Spacing = 4;
        Add(new Label(fonts.Small, name) { Color = Theme.Muted, MaxWidth = 110 });
        Value = new Label(fonts.Small, "0") { Color = Theme.Text };
    }

    protected void AddValue() => Add(Value);
}

/// <summary>
/// A plain channel: a vertical slider kept between the channel's minimum and maximum.
/// </summary>
sealed class ChannelSlider : ControlColumn, IFixtureControl
{
    readonly Slider _slider;
    readonly int _channel;

    public ChannelSlider(Fonts fonts, DmxUniverse universe, int channel, FixtureChannel ch)
        : base(fonts, ch.Name)
    {
        _channel = channel;
        _slider = Add(new Slider { Orientation = Orientation.Vertical, Min = ch.Min, Max = ch.Max, Length = 90, DefaultValue = ch.Default, ShowFill = true });
        _slider.Changed += s => universe.SetChannel(channel, (int)MathF.Round(s.Value));
        AddValue();
    }

    public void Sync(byte[] data)
    {
        if (!_slider.IsDragging)
            _slider.Value = data[_channel];
        Value.Text = data[_channel].ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A 16-bit pair, a channel and the fine channel after it, as one slider from 0 to 65535.
/// </summary>
sealed class WideSlider : ControlColumn, IFixtureControl
{
    readonly Slider _slider;
    readonly int _coarse;

    public WideSlider(Fonts fonts, DmxUniverse universe, int coarse, FixtureChannel coarseChannel, FixtureChannel fineChannel)
        : base(fonts, $"{coarseChannel.Name} (16-bit)")
    {
        _coarse = coarse;
        _slider = Add(
            new Slider
            {
                Orientation = Orientation.Vertical,
                Min = 0,
                Max = 65535,
                Length = 120,
                DefaultValue = (coarseChannel.Default << 8) | fineChannel.Default,
                ShowFill = true,
            }
        );
        _slider.Changed += s =>
        {
            var v = Math.Clamp((int)MathF.Round(s.Value), 0, 65535);
            universe.SetChannel(coarse, (v >> 8) & 0xFF);
            universe.SetChannel(coarse + 1, v & 0xFF);
        };
        AddValue();
    }

    public void Sync(byte[] data)
    {
        var v = (data[_coarse] << 8) | data[_coarse + 1];
        if (!_slider.IsDragging)
            _slider.Value = v;
        Value.Text = v.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A channel with named slots (a gobo wheel, a colour wheel, a shutter): a list of the slots, choosing one sets the
/// channel to its middle, over a slider for the value itself.
/// </summary>
sealed class SlotControl : ControlColumn, IFixtureControl
{
    readonly Dropdown<RangeSlot> _slots;
    readonly Slider _slider;
    readonly int _channel;
    readonly FixtureChannel _ch;

    public SlotControl(Fonts fonts, ContextMenu menu, DmxUniverse universe, int channel, FixtureChannel ch)
        : base(fonts, ch.Name)
    {
        _channel = channel;
        _ch = ch;
        _slots = Add(new Dropdown<RangeSlot>(fonts.Small) { Menu = menu, Width = 170, Items = () => ch.Ranges, Text = r => $"{r.Label} ({r.Lo}-{r.Hi})" });
        _slots.Chosen += r => universe.SetChannel(channel, Math.Clamp((r.Lo + r.Hi) / 2, ch.Min, ch.Max));
        _slider = Add(new Slider { Min = ch.Min, Max = ch.Max, Length = 170, DefaultValue = ch.Default });
        _slider.Changed += s => universe.SetChannel(channel, (int)MathF.Round(s.Value));
        AddValue();
    }

    public void Sync(byte[] data)
    {
        var v = data[_channel];
        if (!_slider.IsDragging)
            _slider.Value = v;
        if (_ch.SlotAt(v) is { } slot)
            _slots.Selected = slot;
        Value.Text = v.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A head's colour, from its red, green and blue channels (and white, amber and UV mixed in as the 3D view mixes
/// them), shown as a swatch. The channels themselves have sliders of their own.
/// </summary>
sealed class ColourSwatch : ControlColumn, IFixtureControl
{
    readonly Swatch _swatch;
    readonly HeadGroup _head;
    readonly FixtureProfile _profile;
    readonly int _mode;
    readonly int _start;
    byte[] _data = [];

    public ColourSwatch(Fonts fonts, string name, PatchedFixture fixture, HeadGroup head, DmxUniverse universe, ColourPicker picker)
        : base(fonts, name)
    {
        _head = head;
        _profile = fixture.Profile;
        _mode = fixture.Mode;
        _start = fixture.Start;
        _swatch = Add(new Swatch { Tooltip = "Pick its red, green and blue" });
        // Picking a colour sets the head's red, green and blue (its white, amber and UV are left as they are).
        _swatch.Clicked += _ =>
            picker.Show(
                _swatch.ScreenPosition + new float2(0, _swatch.Size.Y + 4),
                new Rgb(Level(head.R) / 255f, Level(head.G) / 255f, Level(head.B) / 255f),
                c =>
                {
                    universe.SetChannel(_start + head.R, Gd.RoundToInt(c.R * 255.0));
                    universe.SetChannel(_start + head.G, Gd.RoundToInt(c.G * 255.0));
                    universe.SetChannel(_start + head.B, Gd.RoundToInt(c.B * 255.0));
                }
            );
    }

    int Level(int offset) => _start + offset < _data.Length ? _data[_start + offset] : 0;

    public void Sync(byte[] data)
    {
        _data = data;
        var state = DmxRender.Evaluate(_profile, _mode, data, _start);
        var index = _profile.HeadGroups(_mode).ToList().IndexOf(_head);
        var h = state.Heads[Math.Max(index, 0)];
        // Shown at the level its colour channels give, ignoring the dimmer: the colour as mixed.
        var c = h.Color;
        var level = Math.Clamp(Math.Max(data[_start + _head.R], Math.Max(data[_start + _head.G], data[_start + _head.B])) / 255f, 0, 1);
        if (_head.Extra.Count > 0)
            level = Math.Max(level, _head.Extra.Max(e => data[_start + e] / 255f));
        _swatch.Colour = Colors.FromSrgb(c.R * level, c.G * level, c.B * level);
    }

    sealed class Swatch : Element
    {
        public float4 Colour { get; set; } = Colors.Hex(0x000000);

        public override bool IsInteractive => true;

        public override float2 Measure() => Size = new float2(60, 24);

        public override void Draw(Batch batch)
        {
            batch.AddRect(ScreenPosition, Size, Colour);
            batch.AddBorder(ScreenPosition, Size, IsHovered ? 2 : 1, IsHovered ? Theme.Focus : Theme.PanelBorder);
        }
    }
}

/// <summary>
/// One axis of a pan / tilt pad: its channel, and its fine channel if it's 16-bit.
/// </summary>
readonly record struct Axis(int Coarse, int Fine, int Lo, int Hi, int Default)
{
    public static Axis For(IReadOnlyList<FixtureChannel> chans, int index)
    {
        var ch = chans[index];
        if (index + 1 < chans.Count && chans[index + 1].Fine)
            return new Axis(index, index + 1, 0, 65535, (ch.Default << 8) | chans[index + 1].Default);
        return new Axis(index, -1, ch.Min, ch.Max, ch.Default);
    }

    public int Read(byte[] data, int start) => Fine >= 0 ? (data[start + Coarse] << 8) | data[start + Fine] : data[start + Coarse];

    public float Normalised(int v) => Math.Clamp((float)(v - Lo) / Math.Max(1, Hi - Lo), 0, 1);

    /// <summary>
    /// Writes <paramref name="norm"/> (0 to 1 over the axis) and returns the value written.
    /// </summary>
    public int Write(DmxUniverse universe, int start, float norm)
    {
        var v = Math.Clamp(Lo + Gd.RoundToInt(norm * (Hi - Lo)), Lo, Hi);
        if (Fine >= 0)
        {
            universe.SetChannel(start + Coarse, (v >> 8) & 0xFF);
            universe.SetChannel(start + Fine, v & 0xFF);
        }
        else
        {
            universe.SetChannel(start + Coarse, v);
        }
        return v;
    }
}

/// <summary>
/// Pan and tilt as one pad: drag the puck to aim the head, across for pan and up for tilt.
/// </summary>
sealed class PanTiltPad : ControlColumn, IFixtureControl
{
    readonly Pad _pad;
    readonly Axis _pan;
    readonly Axis _tilt;
    readonly int _start;

    public PanTiltPad(Fonts fonts, DmxUniverse universe, int start, Axis pan, Axis tilt)
        : base(fonts, "Pan / Tilt")
    {
        _pan = pan;
        _tilt = tilt;
        _start = start;
        _pad = Add(new Pad());
        _pad.Moved += p =>
        {
            pan.Write(universe, start, p.X);
            tilt.Write(universe, start, p.Y);
        };
        AddValue();
    }

    public void Sync(byte[] data)
    {
        var pv = _pan.Read(data, _start);
        var tv = _tilt.Read(data, _start);
        if (!_pad.IsPressed)
            _pad.Point = new float2(_pan.Normalised(pv), _tilt.Normalised(tv));
        Value.Text = $"P{pv}  T{tv}";
    }

    /// <summary>
    /// The square itself: the point is 0 to 1 across, and 0 to 1 bottom to top.
    /// </summary>
    sealed class Pad : Element
    {
        const float SIZE = 120;

        public float2 Point { get; set; } = new(0.5f, 0.5f);

        public event Action<float2>? Moved;

        public override bool IsInteractive => true;
        public override Cursor Cursor => Cursor.Arrow;

        public override float2 Measure() => Size = new float2(SIZE, SIZE);

        void MoveTo(float2 point)
        {
            var local = (point - ScreenPosition) / Size;
            Point = new float2(Math.Clamp(local.X, 0, 1), Math.Clamp(1 - local.Y, 0, 1));
            Moved?.Invoke(Point);
        }

        public override void PointerDown(float2 point, Modifiers modifiers) => MoveTo(point);

        public override void PointerDrag(float2 point) => MoveTo(point);

        public override void Draw(Batch batch)
        {
            batch.AddRect(ScreenPosition, Size, Theme.Well);
            batch.AddRect(ScreenPosition + new float2(MathF.Round(SIZE / 2), 0), new float2(1, SIZE), Theme.PanelBorder);
            batch.AddRect(ScreenPosition + new float2(0, MathF.Round(SIZE / 2)), new float2(SIZE, 1), Theme.PanelBorder);
            var at = ScreenPosition + new float2(Point.X * SIZE, (1 - Point.Y) * SIZE);
            batch.AddRect(new float2(at.X, ScreenPosition.Y), new float2(1, SIZE), Theme.Fade(Theme.Focus, 0.35f));
            batch.AddRect(new float2(ScreenPosition.X, at.Y), new float2(SIZE, 1), Theme.Fade(Theme.Focus, 0.35f));
            batch.AddRect(float2.Round(at - new float2(5, 5)), new float2(10, 10), IsHovered || IsPressed ? Colors.White : Theme.Focus);
            batch.AddBorder(ScreenPosition, Size, 1, Theme.PanelBorder);
        }
    }
}

/// <summary>
/// A virtual dimmer, for a fixture without a dimmer channel: scales its colour in the output, not its stored levels.
/// </summary>
sealed class VirtualDimmerSlider : ControlColumn, IFixtureControl
{
    readonly Slider _slider;
    readonly Func<double?> _level;

    public VirtualDimmerSlider(Fonts fonts, Func<double?> level, Action<double> set)
        : base(fonts, "Dimmer (virtual)")
    {
        _level = level;
        _slider = Add(new Slider { Orientation = Orientation.Vertical, Min = 0, Max = 255, Length = 90, DefaultValue = 255, ShowFill = true });
        _slider.Changed += s => set(s.Value / 255.0);
        AddValue();
    }

    public void Sync(byte[] data)
    {
        var v = (int)Math.Round((_level() ?? 1) * 255);
        if (!_slider.IsDragging)
            _slider.Value = v;
        Value.Text = v.ToString(CultureInfo.InvariantCulture);
    }
}

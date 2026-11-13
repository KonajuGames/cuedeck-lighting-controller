using System.Globalization;
using Brutal.Numerics;
using Cuedeck.Audio;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// The Sound tab: the audio input, its live band meters and spectrogram, the gain, beat sensitivity and response,
/// then the sound reactors, each mapping a band (or the beat) onto a channel role across the patched fixtures. They
/// only drive the output in the Sound Reactive run mode (on the top bar), which is also when the input's heard.
/// </summary>
sealed class SoundTab : ListPage<SoundReactor>
{
    /// <summary>
    /// The roles a reactor can drive.
    /// </summary>
    static readonly string[] RoleChoices = [Roles.Dimmer, Roles.Red, Roles.Green, Roles.Blue, Roles.White, Roles.Amber, Roles.Uv, Roles.Strobe, Roles.Zoom];

    readonly LightingShow _show;
    readonly Dropdown<string> _device;
    readonly SoundMeter _meter;
    readonly Spectrogram _spectrogram;
    readonly Slider _gain;
    readonly Slider _sensitivity;
    readonly Slider _response;
    readonly Label _gainValue;
    readonly Label _sensitivityValue;
    readonly Label _responseValue;
    readonly TextBox _name;
    readonly Checkbox _running;
    readonly Dropdown<SoundBand> _band;
    readonly Dropdown<ReactorMode> _mode;
    readonly Dropdown<string> _role;
    readonly Dropdown<int> _universe;
    readonly Dropdown<string> _group;
    readonly NumberBox _low;
    readonly NumberBox _high;
    readonly NumberBox _attack;
    readonly NumberBox _release;
    readonly NumberBox _fan;
    readonly Label _targets;
    IReadOnlyList<string> _devices;

    protected override IReadOnlyList<SoundReactor> Source => _show.Effects.Reactors;

    public SoundTab(Fonts fonts, ContextMenu menu, LightingShow show)
        : base(fonts, "Sound Reactive", "No reactors yet: New Reactor, pick a band and a role, then Run.")
    {
        _show = show;
        _devices = show.Audio.Devices();

        // The input and its meters, between the title and the reactors.
        var input = new Panel { Spacing = 8 };
        Children.Insert(1, input);
        input.Add(new Label(fonts.Body, "Set the run mode to \"Sound Reactive\" (top bar) to drive output from these.") { Color = Theme.Muted, MaxWidth = 360, Wrap = true });
        var deviceRow = input.Add(new Row(6));
        deviceRow.Add(new Label(fonts.Body, "Input") { Color = Theme.Muted });
        _device = deviceRow.Add(
            new Dropdown<string>(fonts.Body)
            {
                Menu = menu,
                Width = 230,
                Items = () => ["", .. _devices],
                Text = d => d == "" ? "Default" : d,
            }
        );
        _device.Chosen += d =>
        {
            show.Sound.Device = d;
            Say($"Input: {(d == "" ? "Default" : d)}");
        };
        var rescan = deviceRow.Add(new Button(fonts.Body, "Rescan") { Tooltip = "Look for audio inputs again" });
        rescan.Clicked += _ => _devices = show.Audio.Devices();

        _meter = input.Add(new SoundMeter(fonts.Small, show.Audio));
        _spectrogram = input.Add(new Spectrogram(fonts.Small, show.Audio));

        var settings = input.Add(new FieldGrid(fonts.Body, 120).Init());
        (Slider, Label) SliderField(string name, float min, float max, string tip)
        {
            var row = new Row(6);
            var slider = row.Add(new Slider { Min = min, Max = max, Length = 150, Tooltip = tip });
            var value = row.Add(new Label(fonts.Body) { Color = Theme.Text });
            settings.Field(name, row);
            return (slider, value);
        }
        (_gain, _gainValue) = SliderField("Gain", 0.25f, 4, "A trim on the input: more for a quiet one");
        _gain.Changed += s => show.Sound.Gain = Math.Round(s.Value / 0.05) * 0.05;
        (_sensitivity, _sensitivityValue) = SliderField("Beat sensitivity", 1.05f, 2.5f, "How far over its recent average the bass must jump to be a beat: higher hears fewer");
        _sensitivity.Changed += s => show.Sound.BeatSensitivity = Math.Round(s.Value / 0.05) * 0.05;
        (_response, _responseValue) = SliderField("Response", 0, 1, "0 smooth and slower, 1 snappy");
        _response.Changed += s => show.Sound.Response = Math.Round(s.Value / 0.05) * 0.05;
        input.Add(new Divider(360));

        float4? Running(SoundReactor r) => r.Running ? Theme.Accent : null;
        List.Columns.Add(new ListColumn<SoundReactor>("", 22, r => r.Running ? "►" : "") { Color = Running, Resizable = false });
        List.Columns.Add(new ListColumn<SoundReactor>("Name", 120, r => r.Name) { Color = Running, Resizable = false });
        List.Columns.Add(new ListColumn<SoundReactor>("What", 200, Describe) { Color = r => Running(r) ?? Theme.Muted, Resizable = false });

        var add = Buttons.Add(new Button(fonts.Body, "New Reactor"));
        add.Clicked += _ =>
        {
            var r = new SoundReactor { Name = $"Reactor {show.Effects.Reactors.Count + 1}" };
            show.Effects.Reactors.Add(r);
            Select(r);
        };
        var delete = Buttons.Add(new Button(fonts.Body, "Delete Reactor"));
        delete.Clicked += _ =>
        {
            if (Selected is not { } r)
                return;
            var i = show.Effects.Reactors.IndexOf(r);
            show.Effects.Reactors.Remove(r);
            Select(show.Effects.Reactors.Count > 0 ? show.Effects.Reactors[Math.Min(i, show.Effects.Reactors.Count - 1)] : null);
        };

        var fields = Editor.Add(new FieldGrid(fonts.Body).Init());
        _name = new TextBox(fonts.Body) { Width = 200 };
        fields.Field("Name", _name);
        _name.TextChanged += t => Edit(r => r.Name = t.Text);

        _running = new Checkbox(fonts.Body, "Running");
        fields.Field("Run", _running);
        _running.Changed += b => Edit(r =>
        {
            if (!b.Checked)
            {
                r.Running = false;
                Say($"Stopped '{r.Name}'.");
                return;
            }
            Retarget(r);
            if (r.Targets.Count == 0)
            {
                Say($"No {r.Role} channels patched in {(r.Group != "" ? $"group '{r.Group}'" : "that universe")}.");
                b.Checked = false;
                return;
            }
            r.Running = true;
            Say(show.RunMode == RunMode.SoundReactive ? $"Running '{r.Name}'." : $"Armed '{r.Name}' — switch the run mode to Sound Reactive to hear it.");
        });

        _band = new Dropdown<SoundBand>(fonts.Body) { Menu = menu, Width = 140, Items = () => Enum.GetValues<SoundBand>(), Text = b => SoundReactor.Bands[(int)b] };
        fields.Field("Band", _band);
        _band.Chosen += b => Edit(r => r.Band = b);
        _mode = new Dropdown<ReactorMode>(fonts.Body)
        {
            Menu = menu,
            Width = 140,
            Items = () => Enum.GetValues<ReactorMode>(),
            Text = m => SoundReactor.Modes[(int)m],
            Tooltip = "Follow tracks the band; Pulse snaps to High on each beat, then falls away",
        };
        fields.Field("Mode", _mode);
        _mode.Chosen += m => Edit(r => r.Mode = m);
        _role = new Dropdown<string>(fonts.Body) { Menu = menu, Width = 140, Items = () => RoleChoices };
        fields.Field("Role", _role);
        _role.Chosen += role => Edit(r =>
        {
            r.Role = role;
            Retarget(r);
        });
        _universe = new Dropdown<int>(fonts.Body)
        {
            Menu = menu,
            Width = 140,
            Items = () => [.. Enumerable.Range(-1, show.Patch.Universes.Count + 1)],
            Text = u => u < 0 ? "All universes" : $"Universe {u + 1}",
        };
        fields.Field("Universe", _universe);
        _universe.Chosen += u => Edit(r =>
        {
            r.Universe = u;
            Retarget(r);
        });
        _group = new Dropdown<string>(fonts.Body)
        {
            Menu = menu,
            Width = 140,
            Items = () => ["", .. show.Groups.Select(g => g.Name)],
            Text = g => g == "" ? "(use universe)" : g,
        };
        fields.Field("Group", _group);
        _group.Chosen += g => Edit(r =>
        {
            r.Group = g;
            Retarget(r);
        });
        var lowHigh = new Row(6);
        _low = lowHigh.Add(new NumberBox(fonts.Body, 0, 255) { Tooltip = "The DMX value when the band's silent" });
        _high = lowHigh.Add(new NumberBox(fonts.Body, 0, 255) { Tooltip = "The DMX value when the band's full" });
        fields.Field("Low / High", lowHigh);
        _low.Changed += n => Edit(r => r.Low = n.Value);
        _high.Changed += n => Edit(r => r.High = n.Value);
        var attackRelease = new Row(6);
        _attack = attackRelease.Add(new NumberBox(fonts.Body, 0, 1, 0.02, 2) { Tooltip = "How quickly it rises to a louder band, 0 to 1" });
        _release = attackRelease.Add(new NumberBox(fonts.Body, 0, 1, 0.02, 2) { Tooltip = "How quickly it falls to a quieter band, 0 to 1" });
        fields.Field("Attack / Release", attackRelease);
        _attack.Changed += n => Edit(r => r.Attack = n.Value);
        _release.Changed += n => Edit(r => r.Release = n.Value);
        _fan = new NumberBox(fonts.Body, 0, 1, 0.05, 2) { Tooltip = "Spreads the response along its fixtures, so they don't move as one" };
        fields.Field("Fan", _fan);
        _fan.Changed += n => Edit(r => r.Fan = n.Value);

        var targets = Editor.Add(new Row(8));
        var rebuild = targets.Add(new Button(fonts.Body, "Rebuild targets from patch"));
        rebuild.Clicked += _ => Edit(Retarget);
        _targets = targets.Add(new Label(fonts.Body) { Color = Theme.Muted });
    }

    /// <summary>
    /// "Bass DIMMER  Follow  (all)": what a reactor does, and where.
    /// </summary>
    static string Describe(SoundReactor r)
    {
        var scope = r.Group != "" ? r.Group : r.Universe < 0 ? "all" : $"U{r.Universe + 1}";
        return $"{SoundReactor.Bands[(int)r.Band]} {r.Role}  {SoundReactor.Modes[(int)r.Mode]}  ({scope})";
    }

    void Edit(Action<SoundReactor> change)
    {
        if (Selected is { } r)
            change(r);
    }

    void Retarget(SoundReactor r) => r.SetTargets(_show.Targets(r.Role, r.Universe, r.Group));

    protected override void Load(SoundReactor r) => _name.Text = r.Name;

    protected override void Sync(SoundReactor r)
    {
        _running.Checked = r.Running;
        _band.Selected = r.Band;
        _mode.Selected = r.Mode;
        _role.Selected = r.Role;
        _universe.Selected = r.Universe;
        _universe.Enabled = r.Group == "";
        _group.Selected = r.Group;
        _low.Value = r.Low;
        _high.Value = r.High;
        _attack.Value = r.Attack;
        _release.Value = r.Release;
        _fan.Value = r.Fan;
        _targets.Text = $"{r.Targets.Count} target channel{(r.Targets.Count == 1 ? "" : "s")}.";
    }

    public override void Update()
    {
        var sound = _show.Sound;
        _device.Selected = sound.Device;
        if (!_gain.IsDragging)
            _gain.Value = (float)sound.Gain;
        if (!_sensitivity.IsDragging)
            _sensitivity.Value = (float)sound.BeatSensitivity;
        if (!_response.IsDragging)
            _response.Value = (float)sound.Response;
        _gainValue.Text = sound.Gain.ToString("0.00", CultureInfo.InvariantCulture);
        _sensitivityValue.Text = sound.BeatSensitivity.ToString("0.00", CultureInfo.InvariantCulture);
        _responseValue.Text = sound.Response.ToString("0.00", CultureInfo.InvariantCulture);
        _spectrogram.Pull();
        base.Update();
    }

    public override float2 Measure()
    {
        _meter.Width = _spectrogram.Width = Width;
        return base.Measure();
    }
}

/// <summary>
/// The live band meter: bass, mid, treble and level as bars, with a flash round it on each beat.
/// </summary>
sealed class SoundMeter(Font font, SoundEngine sound) : Element
{
    public float Width { get; set; } = 360;

    public override float2 Measure() => Size = new float2(Width, 54);

    public override void Draw(Batch batch)
    {
        var at = ScreenPosition;
        batch.AddRect(at, Size, Theme.SoundMeter);
        (string Name, double Value, float4 Colour)[] bars =
        [
            ("BASS", sound.Bass, Theme.SoundBass),
            ("MID", sound.Mid, Theme.SoundMid),
            ("TREB", sound.Treble, Theme.SoundTreble),
            ("LEVEL", sound.Level, Theme.SoundLevel),
        ];
        const float GAP = 6;
        var width = (Size.X - GAP * (bars.Length + 1)) / bars.Length;
        var x = GAP;
        var track = Size.Y - 16 - font.LineHeight / 2;
        foreach (var (name, value, colour) in bars)
        {
            var height = track * (float)Math.Clamp(value, 0, 1);
            batch.AddRect(at + new float2(x, 4), new float2(width, track), Colors.Hex(0xFFFFFF, 0.05f));
            batch.AddRect(at + new float2(x, 4 + track - height), new float2(width, height), colour);
            batch.AddText(font, name, at + new float2(x, Size.Y - font.LineHeight - 1), Theme.Muted);
            x += width + GAP;
        }
        if (sound.BeatPulse > 0.01)
            batch.AddBorder(at, Size, 3, Colors.Hex(0xFFFFFF, 0.28f * (float)sound.BeatPulse));
    }
}

/// <summary>
/// A scrolling heat map of the input's spectrum: a column of log-spaced bands (low at the bottom) each frame, the
/// newest at the right, with a flash round it on each beat. Says "no signal" until it's heard something.
/// </summary>
sealed class Spectrogram(Font font, SoundEngine sound) : Element
{
    public const int BANDS = 32;
    public const int HISTORY = 160;

    readonly float[][] _columns = [.. Enumerable.Range(0, HISTORY).Select(_ => new float[BANDS])];
    int _write;

    /// <summary>
    /// Whether anything's been heard yet.
    /// </summary>
    public bool Lit { get; private set; }

    public float Width { get; set; } = 360;

    /// <summary>
    /// Takes the next column from the input. Call once a frame while it shows.
    /// </summary>
    public void Pull()
    {
        var column = sound.Spectrum(BANDS);
        Lit |= column.Any(v => v > 0.01f);
        _columns[_write] = column;
        _write = (_write + 1) % HISTORY;
    }

    public override float2 Measure() => Size = new float2(Width, 104);

    /// <summary>
    /// A level's colour on the heat scale, with a square root for contrast.
    /// </summary>
    public static float4 Heat(float v)
    {
        v = MathF.Sqrt(Math.Clamp(v, 0, 1));
        var f = v * (Theme.Heat.Length - 1);
        var i = (int)f;
        return i >= Theme.Heat.Length - 1 ? Theme.Heat[^1] : Theme.Mix(Theme.Heat[i], Theme.Heat[i + 1], f - i);
    }

    public override void Draw(Batch batch)
    {
        var at = ScreenPosition;
        batch.AddRect(at, Size, Theme.Heat[0]);
        if (!Lit)
        {
            batch.AddText(font, "no signal", at + new float2(8, (Size.Y - font.LineHeight) / 2), Colors.Hex(0x80808C));
            return;
        }
        var cellWidth = Size.X / HISTORY;
        var cellHeight = Size.Y / BANDS;
        for (var c = 0; c < HISTORY; c++)
        {
            var column = _columns[(_write + c) % HISTORY]; // oldest first, the newest at the right
            var x = MathF.Floor(c * cellWidth);
            var w = MathF.Floor((c + 1) * cellWidth) - x;
            for (var b = 0; b < BANDS; b++)
            {
                if (column[b] <= 0.003f)
                    continue;
                var y = MathF.Floor((BANDS - 1 - b) * cellHeight);
                var h = MathF.Floor((BANDS - b) * cellHeight) - y;
                batch.AddRect(at + new float2(x, y), new float2(w, h), Heat(column[b]));
            }
        }
        if (sound.BeatPulse > 0.02)
            batch.AddBorder(at, Size, 2, Colors.Hex(0xFFFFFF, 0.3f * (float)sound.BeatPulse));
    }
}

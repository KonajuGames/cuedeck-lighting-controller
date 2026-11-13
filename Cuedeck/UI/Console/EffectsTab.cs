using Brutal.Numerics;
using System.Globalization;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// The Effects tab: waveform effects, each on a channel role across a universe's fixtures or a group's, with its
/// waveform, base, rate (or the tempo clock's), size, centre, fan and phase.
/// </summary>
sealed class EffectsTab : ListPage<WaveEffect>
{
    /// <summary>
    /// The roles an effect can drive.
    /// </summary>
    static readonly string[] RoleChoices = [Roles.Dimmer, Roles.Red, Roles.Green, Roles.Blue, Roles.White, Roles.Amber, Roles.Uv, Roles.Pan, Roles.Tilt];

    readonly LightingShow _show;
    readonly TextBox _name;
    readonly Checkbox _running;
    readonly Dropdown<string> _role;
    readonly Dropdown<int> _universe;
    readonly Dropdown<string> _group;
    readonly Dropdown<Waveform> _waveform;
    readonly Dropdown<WaveBase> _base;
    readonly NumberBox _bpm;
    readonly Dropdown<double> _tempo;
    readonly NumberBox _size;
    readonly NumberBox _center;
    readonly NumberBox _fan;
    readonly NumberBox _phase;
    readonly Label _targets;

    protected override IReadOnlyList<WaveEffect> Source => _show.Effects.Effects;

    public EffectsTab(Fonts fonts, ContextMenu menu, LightingShow show)
        : base(fonts, "Effects", "No effects yet: New Effect, pick a role, then Run.")
    {
        _show = show;
        float4? Running(WaveEffect e) => e.Running ? Theme.Accent : null;
        List.Columns.Add(new ListColumn<WaveEffect>("", 22, e => e.Running ? "►" : "") { Color = Running, Resizable = false });
        List.Columns.Add(new ListColumn<WaveEffect>("Name", 120, e => e.Name) { Color = Running, Resizable = false });
        List.Columns.Add(new ListColumn<WaveEffect>("What", 200, Describe) { Color = e => Running(e) ?? Theme.Muted, Resizable = false });

        var add = Buttons.Add(new Button(fonts.Body, "New Effect"));
        add.Clicked += _ =>
        {
            var e = new WaveEffect { Name = $"Effect {show.Effects.Effects.Count + 1}" };
            show.Effects.Effects.Add(e);
            Select(e);
        };
        var delete = Buttons.Add(new Button(fonts.Body, "Delete Effect"));
        delete.Clicked += _ =>
        {
            if (Selected is not { } e)
                return;
            var i = show.Effects.Effects.IndexOf(e);
            show.Effects.Effects.Remove(e);
            Select(show.Effects.Effects.Count > 0 ? show.Effects.Effects[Math.Min(i, show.Effects.Effects.Count - 1)] : null);
        };

        var fields = Editor.Add(new FieldGrid(fonts.Body).Init());
        _name = new TextBox(fonts.Body) { Width = 200 };
        fields.Field("Name", _name);
        _name.TextChanged += t => Edit(e => e.Name = t.Text);

        _running = new Checkbox(fonts.Body, "Running");
        fields.Field("Run", _running);
        _running.Changed += b => Edit(e =>
        {
            if (!b.Checked)
            {
                e.Running = false;
                Say($"Stopped '{e.Name}'.");
                return;
            }
            e.SetTargets(show.Targets(e.Role, e.Universe, e.Group));
            if (e.Targets.Count == 0)
            {
                Say($"No {e.Role} channels patched in {(e.Group != "" ? $"group '{e.Group}'" : "that universe")}.");
                b.Checked = false;
                return;
            }
            e.Running = true;
            Say($"Running '{e.Name}'.");
        });

        _role = new Dropdown<string>(fonts.Body) { Menu = menu, Width = 140, Items = () => RoleChoices };
        fields.Field("Role", _role);
        _role.Chosen += r => Edit(e =>
        {
            e.Role = r;
            Retarget(e);
        });
        _universe = new Dropdown<int>(fonts.Body)
        {
            Menu = menu,
            Width = 140,
            Items = () => [.. Enumerable.Range(-1, show.Patch.Universes.Count + 1)],
            Text = u => u < 0 ? "All universes" : $"Universe {u + 1}",
        };
        fields.Field("Universe", _universe);
        _universe.Chosen += u => Edit(e =>
        {
            e.Universe = u;
            Retarget(e);
        });
        _group = new Dropdown<string>(fonts.Body)
        {
            Menu = menu,
            Width = 140,
            Items = () => ["", .. show.Groups.Select(g => g.Name)],
            Text = g => g == "" ? "(use universe)" : g,
        };
        fields.Field("Group", _group);
        _group.Chosen += g => Edit(e =>
        {
            e.Group = g;
            Retarget(e);
        });
        _waveform = new Dropdown<Waveform>(fonts.Body) { Menu = menu, Width = 140, Items = () => Enum.GetValues<Waveform>(), Text = w => WaveEffect.Waveforms[(int)w] };
        fields.Field("Waveform", _waveform);
        _waveform.Chosen += w => Edit(e => e.Waveform = w);
        _base = new Dropdown<WaveBase>(fonts.Body) { Menu = menu, Width = 140, Items = () => Enum.GetValues<WaveBase>(), Text = b => WaveEffect.BaseModes[(int)b] };
        _base.Tooltip = "Absolute swings around the centre; Pickup swings around the channel's live level";
        fields.Field("Base", _base);
        _base.Chosen += b => Edit(e => e.BaseMode = b);
        _bpm = new NumberBox(fonts.Body, 1, 1200);
        fields.Field("Rate (BPM)", _bpm);
        _bpm.Changed += n => Edit(e => e.Bpm = n.Value);
        _tempo = new Dropdown<double>(fonts.Body)
        {
            Menu = menu,
            Width = 140,
            Items = () => WaveEffect.TempoBeatChoices,
            Text = b => WaveEffect.TempoBeatNames[Math.Max(Array.IndexOf(WaveEffect.TempoBeatChoices, b), 0)],
            Tooltip = "While the tempo clock is running (Tap, MIDI clock or VirtualDJ), one cycle every this many beats, in phase with them, instead of the rate",
        };
        fields.Field("Follow tempo", _tempo);
        _tempo.Chosen += b => Edit(e => e.TempoBeats = b);
        _size = new NumberBox(fonts.Body, 0, 255);
        fields.Field("Size", _size);
        _size.Changed += n => Edit(e => e.Size = n.Value);
        _center = new NumberBox(fonts.Body, 0, 255);
        fields.Field("Center", _center);
        _center.Changed += n => Edit(e => e.Center = n.Value);
        _fan = new NumberBox(fonts.Body, -360, 360, 5);
        fields.Field("Fan (deg)", _fan);
        _fan.Changed += n => Edit(e => e.FanDeg = n.Value);
        _phase = new NumberBox(fonts.Body, -360, 360, 15);
        fields.Field("Phase (deg)", _phase);
        _phase.Changed += n => Edit(e => e.PhaseDeg = n.Value);

        var targets = Editor.Add(new Row(8));
        var rebuild = targets.Add(new Button(fonts.Body, "Rebuild targets from patch"));
        rebuild.Clicked += _ => Edit(Retarget);
        _targets = targets.Add(new Label(fonts.Body) { Color = Theme.Muted });
    }

    /// <summary>
    /// "Sine DIMMER  60 BPM  (all)": what an effect does, and where.
    /// </summary>
    static string Describe(WaveEffect e)
    {
        var scope = e.Group != "" ? e.Group : e.Universe < 0 ? "all" : $"U{e.Universe + 1}";
        var rate = $"{e.Bpm.ToString("F0", CultureInfo.InvariantCulture)} BPM";
        var beats = Array.IndexOf(WaveEffect.TempoBeatChoices, e.TempoBeats);
        if (beats > 0)
            rate += " / tempo " + WaveEffect.TempoBeatNames[beats];
        return $"{WaveEffect.Waveforms[(int)e.Waveform]} {e.Role}  {rate}  ({scope}){(e.BaseMode == WaveBase.Pickup ? "  pickup" : "")}";
    }

    void Edit(Action<WaveEffect> change)
    {
        if (Selected is { } e)
            change(e);
    }

    void Retarget(WaveEffect e) => e.SetTargets(_show.Targets(e.Role, e.Universe, e.Group));

    protected override void Load(WaveEffect e) => _name.Text = e.Name;

    protected override void Sync(WaveEffect e)
    {
        _running.Checked = e.Running;
        _role.Selected = e.Role;
        _universe.Selected = e.Universe;
        _universe.Enabled = e.Group == "";
        _group.Selected = e.Group;
        _waveform.Selected = e.Waveform;
        _base.Selected = e.BaseMode;
        _bpm.Max = e.MaxBpm; // pan, tilt and zoom are capped
        _bpm.Value = e.Bpm;
        _tempo.Selected = e.TempoBeats;
        _size.Value = e.Size;
        _center.Value = e.Center;
        _fan.Value = e.FanDeg;
        _phase.Value = e.PhaseDeg;
        _targets.Text = $"{e.Targets.Count} target channel{(e.Targets.Count == 1 ? "" : "s")}.";
    }
}

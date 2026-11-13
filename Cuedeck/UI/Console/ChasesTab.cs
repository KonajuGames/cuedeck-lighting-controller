using Brutal.Numerics;
using System.Globalization;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// A chase's step, as its list shows it.
/// </summary>
sealed record StepRow(int Index, Look Look);

/// <summary>
/// The Chases tab: the chases, each run or stopped, with its tempo, crossfade, direction and beat sync; and its steps,
/// recorded from the live levels.
/// </summary>
sealed class ChasesTab : ListPage<Chase>
{
    static readonly string[] Directions = ["Forward", "Backward", "Bounce"];

    readonly LightingShow _show;
    readonly TextBox _name;
    readonly Checkbox _running;
    readonly NumberBox _bpm;
    readonly NumberBox _crossfade;
    readonly Dropdown<ChaseDirection> _direction;
    readonly Checkbox _beatSync;
    readonly ListView<StepRow> _steps;
    List<StepRow> _stepRows = [];
    Chase? _stepsOf;

    protected override IReadOnlyList<Chase> Source => _show.Effects.Chases;

    public ChasesTab(Fonts fonts, ContextMenu menu, LightingShow show)
        : base(fonts, "Chases", "No chases yet: New Chase, then record its steps.")
    {
        _show = show;
        float4? Running(Chase c) => c.Running ? Theme.Accent : null;
        List.Columns.Add(new ListColumn<Chase>("", 22, c => c.Running ? "►" : "") { Color = Running, Resizable = false });
        List.Columns.Add(new ListColumn<Chase>("Name", 130, c => c.Name) { Color = Running, Resizable = false });
        List.Columns.Add(new ListColumn<Chase>("Steps", 70, c => $"{c.Steps.Count} steps") { Color = c => Running(c) ?? Theme.Muted, Resizable = false });
        List.Columns.Add(
            new ListColumn<Chase>("Tempo", 100, c => c.BeatSync ? "beat" : $"{c.Bpm.ToString("F0", CultureInfo.InvariantCulture)} BPM") { Color = c => Running(c) ?? Theme.Muted, Resizable = false }
        );

        var add = Buttons.Add(new Button(fonts.Body, "New Chase"));
        add.Clicked += _ =>
        {
            var c = new Chase { Name = $"Chase {show.Effects.Chases.Count + 1}" };
            show.Effects.Chases.Add(c);
            Select(c);
        };
        var delete = Buttons.Add(new Button(fonts.Body, "Delete Chase"));
        delete.Clicked += _ =>
        {
            if (Selected is not { } c)
                return;
            var i = show.Effects.Chases.IndexOf(c);
            show.Effects.Chases.Remove(c);
            Select(show.Effects.Chases.Count > 0 ? show.Effects.Chases[Math.Min(i, show.Effects.Chases.Count - 1)] : null);
        };

        var fields = Editor.Add(new FieldGrid(fonts.Body).Init());
        _name = new TextBox(fonts.Body) { Width = 200 };
        fields.Field("Name", _name);
        _name.TextChanged += t => Edit(c => c.Name = t.Text);
        _running = new Checkbox(fonts.Body, "Running");
        fields.Field("Run", _running);
        _running.Changed += b => Edit(c =>
        {
            if (b.Checked)
                c.Reset();
            c.Running = b.Checked;
            Say(b.Checked ? $"Running '{c.Name}'." : $"Stopped '{c.Name}'.");
        });
        _bpm = new NumberBox(fonts.Body, 1, 600);
        fields.Field("Tempo (BPM)", _bpm);
        _bpm.Changed += n => Edit(c => c.Bpm = n.Value);
        _crossfade = new NumberBox(fonts.Body, 0, 100);
        fields.Field("Crossfade (%)", _crossfade);
        _crossfade.Changed += n => Edit(c => c.Crossfade = n.Value / 100);
        _direction = new Dropdown<ChaseDirection>(fonts.Body)
        {
            Menu = menu,
            Width = 140,
            Items = () => [ChaseDirection.Forward, ChaseDirection.Backward, ChaseDirection.Bounce],
            Text = d => Directions[(int)d],
        };
        fields.Field("Direction", _direction);
        _direction.Chosen += d => Edit(c => c.Direction = d);
        _beatSync = new Checkbox(fonts.Body, "step on each beat")
        {
            Tooltip = "Step on the tempo clock's beats while it's running (Tap, MIDI clock or VirtualDJ); with it off, on the beats heard in Sound Reactive; otherwise at the tempo.",
        };
        fields.Field("Beat sync", _beatSync);
        _beatSync.Changed += b => Edit(c => c.BeatSync = b.Checked);

        var stepButtons = Editor.Add(new Flow(6));
        var record = stepButtons.Add(new Button(fonts.Body, "Record Step") { Tooltip = "Add the live levels as a new step" });
        record.Clicked += _ => Edit(c =>
        {
            c.CaptureStep(show.Dmx);
            Say($"Recorded step {c.Steps.Count} of '{c.Name}'.");
        });
        var update = stepButtons.Add(new Button(fonts.Body, "Update Step") { Tooltip = "Replace the selected step with the live levels" });
        update.Clicked += _ => EditStep((c, i) =>
        {
            c.UpdateStep(i, show.Dmx);
            Say($"Updated step {i + 1} of '{c.Name}' from live output.");
        });
        var load = stepButtons.Add(new Button(fonts.Body, "Load Step to Patch") { Tooltip = "Set the fixture controls to the selected step, to tweak it and Update Step" });
        load.Clicked += _ => EditStep((c, i) =>
        {
            show.LoadLook(c.StepBuffers(i, show.Dmx.UniverseCount));
            Say($"Step {i + 1} loaded into the patch — tweak the fixtures, then Update Step.");
        });
        var deleteStep = stepButtons.Add(new Button(fonts.Body, "Delete Step"));
        deleteStep.Clicked += _ => EditStep((c, i) =>
        {
            c.Steps.RemoveAt(i);
            c.Reset();
        });

        _steps = new ListView<StepRow>(fonts.Body) { EmptyText = "No steps yet: set a look, then Record Step." };
        _steps.Columns.Add(new ListColumn<StepRow>("Step", 300, s => $"Step {s.Index + 1}  ({s.Look.Count} ch)") { Resizable = false });
        var stepScroll = Editor.Add(new ScrollPanel { Background = Theme.Well, MaxHeight = 130, MinSize = new float2(300, 60) });
        stepScroll.Add(_steps);
        _steps.ScrollOwner = stepScroll;
    }

    void Edit(Action<Chase> change)
    {
        if (Selected is { } c)
            change(c);
    }

    void EditStep(Action<Chase, int> change)
    {
        if (Selected is { } c && _steps.SelectedItem is { } step && step.Index < c.Steps.Count)
            change(c, step.Index);
    }

    protected override void Load(Chase c) => _name.Text = c.Name;

    protected override void Sync(Chase c)
    {
        _running.Checked = c.Running;
        _bpm.Value = c.Bpm;
        _crossfade.Value = Math.Round(c.Crossfade * 100);
        _direction.Selected = c.Direction;
        _beatSync.Checked = c.BeatSync;

        // The steps, listed again when they change (keeping the one selected).
        if (_stepsOf != c || _stepRows.Count != c.Steps.Count || _stepRows.Where((r, i) => r.Look != c.Steps[i]).Any())
        {
            var selected = _stepsOf == c ? _steps.SelectedIndex : -1;
            _stepsOf = c;
            _stepRows = [.. c.Steps.Select((look, i) => new StepRow(i, look))];
            _steps.Items = _stepRows;
            _steps.SelectedIndex = Math.Min(selected, _stepRows.Count - 1);
        }
    }

    public override float2 Measure()
    {
        _steps.Columns[0].Width = Width - 12;
        return base.Measure();
    }
}

using System.Globalization;
using Brutal.Numerics;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// A bar that fills from left to right, 0 to 1.
/// </summary>
sealed class ProgressBar(float width, float height = 10) : Element
{
    public float Value { get; set; }
    public float Width { get; set; } = width;
    public float4 Fill { get; set; } = Theme.Accent;

    public override float2 Measure() => Size = new float2(Width, height);

    public override void Draw(Batch batch)
    {
        batch.AddRect(ScreenPosition, Size, Theme.Well);
        var filled = MathF.Round(Size.X * Math.Clamp(Value, 0, 1));
        if (filled > 0)
            batch.AddRect(ScreenPosition, new float2(filled, Size.Y), Fill);
        batch.AddBorder(ScreenPosition, Size, 1, Theme.PanelBorder);
    }
}

/// <summary>
/// The Cues tab: GO, Back and Halt, a bar for the delay, fade or follow under way, the cue list, the selected cue's
/// settings and buttons, and recording new cues. Space is GO too (in Cue Mode, when nothing else takes it).
/// </summary>
sealed class CuesTab : Region
{
    const float GAP = 8;

    readonly LightingShow _show;
    readonly Panel _top;
    readonly Panel _bottom;
    readonly ScrollPanel _scroll;
    readonly ProgressBar _progress;
    readonly Label _progressText;
    readonly Label _next;

    readonly TextBox _label;
    readonly NumberBox _fadeUp;
    readonly NumberBox _fadeDown;
    readonly NumberBox _delay;
    readonly Checkbox _follow;
    readonly NumberBox _followWait;
    readonly Checkbox _tracks;
    readonly NumberBox _newFade;
    readonly Checkbox _recordTracking;
    readonly Panel _editor;
    readonly List<Element> _needsSelection = [];

    List<Cue> _listed = [];
    Cue? _editing;

    public ListView<Cue> List { get; }

    CueList Cues => _show.Cues;

    public CuesTab(Fonts fonts, LightingShow show)
    {
        _show = show;

        _top = Add(new Panel { Spacing = 8 });
        _top.Add(new Label(fonts.Heading, "Cue List") { Color = Theme.Text });
        var transport = _top.Add(new Row(6));
        var go = transport.Add(new Button(fonts.Big, "GO") { MinSize = new float2(84, 0), Padding = 8, Tooltip = "GO the next cue (Space)" });
        go.Background = Colors.Hex(0x2F6B3F);
        go.HoverBackground = Colors.Hex(0x3A8450);
        go.Clicked += _ => Cues.Go();
        var back = transport.Add(new Button(fonts.Body, "Back") { Tooltip = "The cue before" });
        back.Clicked += _ => Cues.GoBack();
        var halt = transport.Add(new Button(fonts.Body, "Halt") { Tooltip = "Stop the fade, and anything waiting" });
        halt.Clicked += _ => Cues.Halt();
        var progressRow = _top.Add(new Row(8));
        _progress = progressRow.Add(new ProgressBar(200));
        _progressText = progressRow.Add(new Label(fonts.Small) { Color = Theme.Muted });

        List = new ListView<Cue>(fonts.Body) { EmptyText = "No cues — dial a look, then Record Cue." };
        float4? Live(Cue c) => Cues.Current >= 0 && Cues.Current < Cues.Cues.Count && Cues.Cues[Cues.Current] == c ? Theme.Accent : null;
        List.Columns.Add(new ListColumn<Cue>("", 26, c => Live(c) is null ? "" : "►") { Color = Live, Resizable = false });
        List.Columns.Add(new ListColumn<Cue>("#", 30, c => (Cues.Cues.IndexOf(c) + 1).ToString(CultureInfo.InvariantCulture)) { AlignRight = true, Color = Live, Resizable = false });
        List.Columns.Add(new ListColumn<Cue>("", 26, c => c.Tracking ? "T" : "B") { Color = c => Live(c) ?? Theme.Muted, Resizable = false });
        List.Columns.Add(new ListColumn<Cue>("Label", 110, c => c.Label) { Color = Live, Resizable = false });
        List.Columns.Add(new ListColumn<Cue>("Times", 600, TimesAndPalettes) { Color = c => Live(c) ?? Theme.Muted, Resizable = false });
        List.RowProgress = c => Cues.Current >= 0 && Cues.Current < Cues.Cues.Count && Cues.Cues[Cues.Current] == c && Cues.Progress.Value < 1 ? (float)Cues.Progress.Value : null;
        List.ItemActivated += c => Cues.GoTo(Cues.Cues.IndexOf(c) + 1);
        List.DeleteRequested += items =>
        {
            foreach (var c in items)
                Cues.Delete(Cues.Cues.IndexOf(c));
        };
        _scroll = Add(new ScrollPanel { Background = Theme.Well });
        _scroll.Add(List);
        List.ScrollOwner = _scroll;

        _bottom = Add(new Panel { Spacing = 8 });
        _next = _bottom.Add(new Label(fonts.Body) { Color = Theme.Text });
        _bottom.Add(new Divider(340));

        _editor = _bottom.Add(new Panel { Spacing = 6 });
        Row Field(string name)
        {
            var row = _editor.Add(new Row(8));
            row.Add(new FixedWidth(new Label(fonts.Body, name) { Color = Theme.Muted, MaxWidth = 104 }, 104));
            return row;
        }
        _label = Field("Label").Add(new TextBox(fonts.Body) { Width = 210 });
        _label.TextChanged += t =>
        {
            if (_editing is { } c)
                c.Label = t.Text;
        };
        _fadeUp = Field("Fade up (s)").Add(new NumberBox(fonts.Body, 0, 600, 0.1, 1));
        _fadeUp.Changed += n => Edit(c => c.FadeUp = n.Value);
        _fadeDown = Field("Fade down (s)").Add(new NumberBox(fonts.Body, 0, 600, 0.1, 1));
        _fadeDown.Changed += n => Edit(c => c.FadeDown = n.Value);
        _delay = Field("Delay (s)").Add(new NumberBox(fonts.Body, 0, 600, 0.1, 1) { Tooltip = "How long after GO this cue's fade starts" });
        _delay.Changed += n => Edit(c => c.Delay = n.Value);
        var follow = Field("Follow");
        _follow = follow.Add(new Checkbox(fonts.Body, "then GO next after") { Tooltip = "When its fade finishes, wait this long, then GO the next cue" });
        _follow.Changed += b => Edit(c => c.Follow = b.Checked);
        _followWait = follow.Add(new NumberBox(fonts.Body, 0, 600, 0.1, 1, width: 50) { Suffix = " s" });
        _followWait.Changed += n => Edit(c => c.FollowWait = n.Value);
        _tracks = Field("Tracking").Add(
            new Checkbox(fonts.Body, "tracks from previous cue")
            {
                Tooltip = "On: it stores only what it changes. Off (a block): it stores a whole look, and stops edits upstream tracking through.",
            }
        );
        _tracks.Changed += b =>
        {
            if (_editing is { } c)
                Cues.SetTracking(Cues.Cues.IndexOf(c), b.Checked);
        };
        var buttons = _editor.Add(new Flow(6) { Width = 360 });
        var toPatch = buttons.Add(new Button(fonts.Body, "Load to Patch") { Tooltip = "Set the fixture controls to this cue's look, to tweak it and Update" });
        toPatch.Clicked += _ =>
        {
            if (Selected is not { } i)
                return;
            _show.LoadLook(Cues.LookBuffers(i));
            Status?.Invoke($"Cue {i + 1} loaded into the patch — tweak the fixtures, then Update.");
        };
        var update = buttons.Add(new Button(fonts.Body, "Update") { Tooltip = "Record this cue again from the live levels" });
        update.Clicked += _ =>
        {
            if (Selected is { } i)
                Cues.Update(i);
        };
        var duplicate = buttons.Add(new Button(fonts.Body, "Duplicate"));
        duplicate.Clicked += _ =>
        {
            if (Selected is not { } i)
                return;
            Cues.Duplicate(i);
            Select(i + 1);
        };
        var delete = buttons.Add(new Button(fonts.Body, "Delete"));
        delete.Clicked += _ =>
        {
            if (Selected is not { } i)
                return;
            Cues.Delete(i);
            Select(Math.Min(i, Cues.Cues.Count - 1));
        };
        _needsSelection.AddRange([_editor]);

        _bottom.Add(new Divider(340));
        var record = _bottom.Add(new Row(8));
        record.Add(new Label(fonts.Body, "New cue fade (s)") { Color = Theme.Muted });
        _newFade = record.Add(new NumberBox(fonts.Body, 0, 600, 0.1, 1, width: 50) { Value = 3 });
        var rec = record.Add(new Button(fonts.Body, "Record Cue") { Tooltip = "Record the live levels as a new cue, after the selected one" });
        rec.Clicked += _ =>
        {
            var at = Cues.Record(Selected ?? -1, _newFade.Value);
            Select(at);
        };
        _recordTracking = _bottom.Add(new Checkbox(fonts.Body, "Tracking") { Tooltip = "New cues store only what they change from the cues before" });
        _recordTracking.Changed += b => Cues.TrackingEnabled = b.Checked;
    }

    /// <summary>
    /// Something to say on the status line.
    /// </summary>
    public event Action<string>? Status;

    /// <summary>
    /// The selected cue's index, or null.
    /// </summary>
    public int? Selected => List.SelectedItem is { } c && Cues.Cues.IndexOf(c) is var i && i >= 0 ? i : null;

    void Select(int index)
    {
        Update();
        List.SelectedIndex = index;
    }

    void Edit(Action<Cue> change)
    {
        if (_editing is { } c)
            change(c);
    }

    /// <summary>
    /// A cue's times, then the palettes it uses.
    /// </summary>
    string TimesAndPalettes(Cue c) => Cues.PaletteNames(c) is { Count: > 0 } names ? $"{Times(c)}  • {string.Join(", ", names)}" : Times(c);

    static string Times(Cue c)
    {
        var text = $"{c.FadeUp:F1}/{c.FadeDown:F1}s";
        if (c.Delay > 0)
            text += $"  d{c.Delay:F1}";
        if (c.Follow)
            text += $"  → {c.FollowWait:F1}s";
        return text;
    }

    public void Update()
    {
        if (!_listed.SequenceEqual(Cues.Cues))
        {
            _listed = [.. Cues.Cues];
            List.Items = _listed;
        }
        var (value, text) = Cues.Progress;
        _progress.Value = (float)value;
        _progressText.Text = text;
        _next.Text = Cues.Cues.Count == 0
            ? "No cues — dial a look, then Record Cue."
            : $"Next: cue {Math.Clamp(Cues.Next, 0, Cues.Cues.Count - 1) + 1}   (recording: {(Cues.TrackingEnabled ? "tracking" : "block")})";
        _recordTracking.Checked = Cues.TrackingEnabled;

        // The editor shows the selected cue, and takes in what's typed into it.
        var selected = Selected is { } i ? Cues.Cues[i] : null;
        foreach (var e in _needsSelection)
            e.Visible = selected is not null;
        if (selected != _editing)
        {
            _editing = selected;
            if (selected is not null)
                _label.Text = selected.Label;
        }
        if (selected is not null)
        {
            _fadeUp.Value = selected.FadeUp;
            _fadeDown.Value = selected.FadeDown;
            _delay.Value = selected.Delay;
            _follow.Checked = selected.Follow;
            _followWait.Value = selected.FollowWait;
            _followWait.Visible = selected.Follow;
            _tracks.Checked = selected.Tracking;
        }
    }

    protected override void MeasureContent()
    {
        var width = Available.X - 2 * GAP;
        _progress.Width = MathF.Max(80, width - 150);
        _top.Measure();
        _bottom.Measure();
        _scroll.MaxWidth = width;
        // The last column takes what the others leave, so the list never scrolls sideways.
        List.Columns[^1].Width = MathF.Max(60, width - List.Columns.Take(List.Columns.Count - 1).Sum(c => c.Width) - 12);
        _scroll.MaxHeight = MathF.Max(80, Available.Y - _top.Size.Y - _bottom.Size.Y - 4 * GAP);
        _scroll.MinSize = new float2(width, _scroll.MaxHeight);
        _scroll.Measure();
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        var at = position + new float2(GAP, GAP);
        _top.Arrange(at);
        at.Y += _top.Size.Y + GAP;
        _scroll.Arrange(at);
        at.Y += _scroll.Size.Y + GAP;
        _bottom.Arrange(at);
    }
}

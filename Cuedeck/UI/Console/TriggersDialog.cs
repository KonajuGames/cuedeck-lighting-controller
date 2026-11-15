using Brutal.Numerics;
using Cuedeck.Show;
using Cuedeck.Triggers;

namespace Cuedeck.UI.Console;

/// <summary>
/// The MIDI and OSC triggers, over the dimmed screen: whether MIDI and OSC are listened to (and on which UDP port), the
/// bindings on the left, and the chosen one on the right: what it listens for (a MIDI note, CC, program change or
/// pitch bend on a channel, or an OSC address), which Learn takes from the next message heard, and the action it fires.
/// The last message heard shows along the bottom. Feedback lights a binding's pad from what it follows, through the
/// chosen MIDI output or as OSC. sLight's Triggers dialog.
/// </summary>
sealed class TriggersDialog : Panel
{
    const float WIDTH = 820;

    readonly Root _root;
    readonly LightingShow _show;
    readonly Panel _box;
    readonly Checkbox _midiOn;
    readonly Label _devices;
    readonly Checkbox _oscOn;
    readonly NumberBox _oscPort;
    readonly Label _oscState;
    readonly ListView<Trigger> _list;
    readonly ScrollPanel _listScroll;
    readonly Panel _editor;
    readonly TextBox _name;
    readonly Checkbox _enabled;
    readonly Dropdown<TriggerSource> _source;
    readonly Row _midiKindRow;
    readonly Row _channelRow;
    readonly Row _numberRow;
    readonly Row _addressRow;
    readonly Dropdown<TriggerMidiKind> _midiKind;
    readonly Dropdown<int> _channel;
    readonly NumberBox _number;
    readonly TextBox _address;
    readonly Button _learn;
    readonly Dropdown<TriggerAction> _action;
    readonly Row _targetRow;
    readonly Label _targetName;
    readonly TextBox _target;
    readonly Dropdown<string> _pick;
    readonly Label _activity;
    readonly Checkbox _feedbackOn;
    readonly Dropdown<string> _midiOut;
    readonly TextBox _oscOutHost;
    readonly NumberBox _oscOutPort;
    readonly Checkbox _lightPad;
    readonly Row _followsRow;
    readonly Dropdown<FeedbackWatch> _follows;
    readonly NumberBox _onValue;
    readonly NumberBox _offValue;
    IReadOnlyList<string> _outputs = [];
    List<Trigger> _listed = [];
    Trigger? _shown;
    bool _pressedInBox;

    public float2 ScreenSize { get; set; }

    public TriggersDialog(Root root, Fonts fonts, ContextMenu menu, LightingShow show)
    {
        _root = root;
        _show = show;
        Visible = false;
        Background = Colors.Hex(0x000000, 0.6f);
        var engine = show.Triggers;

        _box = Add(new Panel { Padding = 14, Spacing = 8, Background = Theme.PanelRaised, BorderColor = Theme.PanelBorder, BorderWidth = 1 });
        _box.Add(new Label(fonts.Heading, "MIDI / OSC Triggers") { Color = Theme.Text });
        _activity = new Label(fonts.Body, "Waiting for MIDI / OSC…") { Color = Colors.Hex(0xA6CCA6), MaxWidth = WIDTH - 28 };

        var midiRow = _box.Add(new Row(8));
        _midiOn = midiRow.Add(new Checkbox(fonts.Body, "MIDI input"));
        _midiOn.Changed += c => engine.MidiEnabled = c.Checked;
        _devices = midiRow.Add(new Label(fonts.Body) { Color = Theme.Muted, MaxWidth = 560 });
        var rescan = midiRow.Add(new Button(fonts.Body, "Rescan") { Tooltip = "Look for MIDI devices plugged in since, and list them" });
        rescan.Clicked += _ => engine.RescanMidi();

        var oscRow = _box.Add(new Row(8));
        _oscOn = oscRow.Add(new Checkbox(fonts.Body, "OSC input"));
        _oscOn.Changed += _ => ApplyOsc();
        oscRow.Add(new Label(fonts.Body, "UDP port") { Color = Theme.Muted });
        _oscPort = oscRow.Add(new NumberBox(fonts.Body, 1, 65535, width: 80));
        var apply = oscRow.Add(new Button(fonts.Body, "Apply"));
        apply.Clicked += _ => ApplyOsc();
        _oscState = oscRow.Add(new Label(fonts.Body) { Color = Theme.Muted });

        // Feedback out: a MIDI output (none: sLight's UDP bridge), and where OSC feedback goes.
        var feedbackRow = _box.Add(new Row(8));
        _feedbackOn = feedbackRow.Add(new Checkbox(fonts.Body, "Feedback (LEDs)") { Tooltip = "Light the pads of bindings set to, from what they follow" });
        _feedbackOn.Changed += _ => ApplyFeedback();
        feedbackRow.Add(new Label(fonts.Body, "MIDI output:") { Color = Theme.Muted });
        _midiOut = feedbackRow.Add(
            new Dropdown<string>(fonts.Body)
            {
                Menu = menu,
                Width = 220,
                Items = () => ["", .. _outputs],
                Text = n => n == "" ? $"(none — UDP bridge :{engine.MidiOutPort})" : n,
            }
        );
        _midiOut.Chosen += n => engine.SetMidiOutput(n);
        var rescanOut = feedbackRow.Add(new Button(fonts.Body, "Rescan") { Tooltip = "List the MIDI outputs again" });
        rescanOut.Clicked += _ => _outputs = engine.MidiOutputs();
        var oscOutRow = _box.Add(new Row(8));
        oscOutRow.Add(new FixedWidth(new Label(fonts.Body, ""), 130)); // under the MIDI output
        oscOutRow.Add(new Label(fonts.Body, "OSC →") { Color = Theme.Muted });
        _oscOutHost = oscOutRow.Add(new TextBox(fonts.Body) { Width = 130, Placeholder = "127.0.0.1" });
        oscOutRow.Add(new Label(fonts.Body, ":") { Color = Theme.Muted });
        _oscOutPort = oscOutRow.Add(new NumberBox(fonts.Body, 1, 65535, width: 80));
        var applyOut = oscOutRow.Add(new Button(fonts.Body, "Apply"));
        applyOut.Clicked += _ => ApplyFeedback();
        _box.Add(new Divider(WIDTH - 28));

        var body = _box.Add(new Row(12));
        var left = body.Add(new Panel { Spacing = 6 });
        _list = new ListView<Trigger>(fonts.Body) { EmptyText = "No triggers yet: Add one" };
        _list.Columns.Add(new ListColumn<Trigger>("", 18, t => t.Enabled ? "" : "×") { Color = _ => Theme.Muted, Resizable = false });
        _list.Columns.Add(new ListColumn<Trigger>("Name", 110, t => t.Name) { Resizable = false });
        _list.Columns.Add(new ListColumn<Trigger>("Listens for", 170, t => t.SourceSummary) { Color = _ => Theme.Muted, Resizable = false });
        _listScroll = left.Add(new ScrollPanel { Background = Theme.Well, MaxWidth = 310, MinSize = new float2(310, 300), MaxHeight = 300 });
        _listScroll.Add(_list);
        _list.ScrollOwner = _listScroll;
        var buttons = left.Add(new Row(6));
        var add = buttons.Add(new Button(fonts.Body, "Add"));
        add.Clicked += _ =>
        {
            var t = new Trigger { Name = $"Trigger {engine.Triggers.Count + 1}" };
            engine.Triggers.Add(t);
            Select(t);
        };
        var duplicate = buttons.Add(new Button(fonts.Body, "Duplicate"));
        duplicate.Clicked += _ =>
        {
            if (_list.SelectedItem is not { } t)
                return;
            var copy = t.Clone();
            copy.Name += " copy";
            engine.Triggers.Add(copy);
            Select(copy);
        };
        var delete = buttons.Add(new Button(fonts.Body, "Delete"));
        delete.Clicked += _ =>
        {
            if (_list.SelectedItem is not { } t)
                return;
            var i = engine.Triggers.IndexOf(t);
            engine.Triggers.Remove(t);
            Select(engine.Triggers.Count > 0 ? engine.Triggers[Math.Min(i, engine.Triggers.Count - 1)] : null);
        };

        _editor = body.Add(new Panel { Spacing = 6 });
        var fields = _editor.Add(new FieldGrid(fonts.Body, 110).Init());
        _name = new TextBox(fonts.Body) { Width = 260 };
        fields.Field("Name", _name);
        _name.TextChanged += b => Edit(t => t.Name = b.Text);
        _enabled = new Checkbox(fonts.Body, "Enabled");
        fields.Field("Enabled", _enabled);
        _enabled.Changed += c => Edit(t => t.Enabled = c.Checked);
        _source = new Dropdown<TriggerSource>(fonts.Body) { Menu = menu, Width = 140, Items = () => Enum.GetValues<TriggerSource>(), Text = s => Trigger.Sources[(int)s] };
        fields.Field("Source", _source);
        _source.Chosen += s => Edit(t => t.Source = s);

        _midiKind = new Dropdown<TriggerMidiKind>(fonts.Body) { Menu = menu, Width = 170, Items = () => Enum.GetValues<TriggerMidiKind>(), Text = k => Trigger.MidiKinds[(int)k] };
        _midiKindRow = fields.Field("Message", _midiKind);
        _midiKind.Chosen += k => Edit(t => t.MidiKind = k);
        _channel = new Dropdown<int>(fonts.Body)
        {
            Menu = menu,
            Width = 100,
            Items = () => [.. Enumerable.Range(-1, 17)],
            Text = c => c < 0 ? "Any" : $"Ch {c + 1}",
        };
        _channelRow = fields.Field("Channel", _channel);
        _channel.Chosen += c => Edit(t => t.MidiChannel = c);
        _number = new NumberBox(fonts.Body, 0, 127);
        _numberRow = fields.Field("Note / CC #", _number);
        _number.Changed += n => Edit(t => t.MidiNumber = (int)n.Value);
        _address = new TextBox(fonts.Body) { Width = 260, Placeholder = "/cue/go" };
        _addressRow = fields.Field("Address", _address);
        _address.TextChanged += b => Edit(t => t.OscAddress = b.Text);

        _learn = _editor.Add(new Button(fonts.Body, "Learn — hit a key / send a message") { Tooltip = "Take what to listen for from the next MIDI or OSC message heard" });
        _learn.Clicked += _ =>
        {
            if (engine.Learning)
            {
                engine.CancelLearn();
                return;
            }
            if (_list.SelectedItem is null)
                return;
            engine.StartLearn();
            _activity.Text = "Learning — send the MIDI / OSC message now…";
        };

        var actions = _editor.Add(new FieldGrid(fonts.Body, 110).Init());
        _action = new Dropdown<TriggerAction>(fonts.Body) { Menu = menu, Width = 220, Items = () => Trigger.MenuOrder, Text = a => Trigger.Actions[(int)a] };
        actions.Field("Action", _action);
        _action.Chosen += a => Edit(t => t.Action = a);
        _target = new TextBox(fonts.Body) { Width = 140 };
        _target.TextChanged += b => Edit(t => t.Target = b.Text);
        _pick = new Dropdown<string>(fonts.Body)
        {
            Menu = menu,
            Width = 110,
            Items = () => [.. TargetNames()],
            Text = n => n == "" ? "pick…" : n,
            Selected = "",
        };
        _pick.Chosen += n =>
        {
            _target.Text = n;
            Edit(t => t.Target = n);
        };
        var targetBox = new Row(6);
        targetBox.Add(_target);
        targetBox.Add(_pick);
        _targetRow = actions.Field("Target", targetBox);
        _targetName = _targetRow.Children.OfType<FixedWidth>().Select(f => f.Inner).OfType<Label>().First();

        _lightPad = new Checkbox(fonts.Body, "light this pad");
        actions.Field("Feedback", _lightPad);
        _lightPad.Changed += c => Edit(t => t.FeedbackEnabled = c.Checked);
        var follows = new Row(6);
        _follows = follows.Add(
            new Dropdown<FeedbackWatch>(fonts.Body)
            {
                Menu = menu,
                Width = 210,
                Items = () => Enum.GetValues<FeedbackWatch>(),
                Text = w => Trigger.FeedbackWatches[(int)w],
                Tooltip = "\"This binding's action\" follows a Go to cue, a chase or effect toggle, or a flash",
            }
        );
        _follows.Chosen += w => Edit(t => t.FeedbackWatch = w);
        follows.Add(new Label(fonts.Body, "on") { Color = Theme.Muted });
        _onValue = follows.Add(new NumberBox(fonts.Body, 0, 127, width: 50) { Tooltip = "The note velocity or CC value it sends while on" });
        _onValue.Changed += n => Edit(t => t.FeedbackOn = (int)n.Value);
        follows.Add(new Label(fonts.Body, "off") { Color = Theme.Muted });
        _offValue = follows.Add(new NumberBox(fonts.Body, 0, 127, width: 50) { Tooltip = "…and while off" });
        _offValue.Changed += n => Edit(t => t.FeedbackOff = (int)n.Value);
        _followsRow = actions.Field("  follows", follows);

        _box.Add(_activity);
        var footer = _box.Add(new Row(8));
        var close = footer.Add(new Button(fonts.Body, "Close"));
        close.Clicked += _ => Close();

        engine.Activity += text => _activity.Text = text;
        engine.Learned += heard =>
        {
            if (_list.SelectedItem is not { } t)
                return;
            t.LearnFrom(heard);
            _activity.Text = "Learned: " + t.SourceSummary;
        };

        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    /// <summary>
    /// The chases or effects to pick from for the chosen trigger's target.
    /// </summary>
    IEnumerable<string> TargetNames() =>
        _list.SelectedItem?.Action switch
        {
            TriggerAction.ChaseToggle => _show.Effects.Chases.Select(c => c.Name),
            TriggerAction.EffectToggle => _show.Effects.Effects.Select(e => e.Name),
            _ => [],
        };

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;
    public override bool IsFocusable => Visible;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key != Key.Escape)
            return false;
        if (_show.Triggers.Learning)
            _show.Triggers.CancelLearn();
        else
            Close();
        return true;
    }

    public ListView<Trigger> List => _list;

    public void Show()
    {
        var engine = _show.Triggers;
        engine.RescanMidi();
        _outputs = engine.MidiOutputs();
        _oscPort.Value = engine.OscPort;
        _oscOutHost.Text = engine.OscOutHost;
        _oscOutPort.Value = engine.OscOutPort;
        Visible = true;
        _root.Focus(this);
    }

    void ApplyFeedback()
    {
        var engine = _show.Triggers;
        engine.SetFeedback(_feedbackOn.Checked, engine.MidiOutPort, _oscOutHost.Text, (int)_oscOutPort.Value);
        _oscOutHost.Text = engine.OscOutHost;
    }

    public void Close()
    {
        if (!Visible)
            return;
        _show.Triggers.CancelLearn();
        Visible = false;
        _root.Focus(null);
    }

    void ApplyOsc() => _show.Triggers.SetOsc(_oscOn.Checked, (int)_oscPort.Value);

    void Edit(Action<Trigger> change)
    {
        if (_list.SelectedItem is { } t)
            change(t);
    }

    void Select(Trigger? t)
    {
        Refresh();
        _list.SelectedIndex = t is null ? -1 : _listed.IndexOf(t);
    }

    void Refresh()
    {
        if (_listed.SequenceEqual(_show.Triggers.Triggers))
            return;
        _listed = [.. _show.Triggers.Triggers];
        _list.Items = _listed;
    }

    /// <summary>
    /// Brings it up to date with the triggers and the engine. Call once a frame while it shows.
    /// </summary>
    public void Update()
    {
        if (!Visible)
            return;
        // Focus back on the dialog once nothing has it (a menu closed), so Escape still reaches it.
        if (_root.Focused is null)
            _root.Focus(this);
        var engine = _show.Triggers;
        Refresh();
        _midiOn.Checked = engine.MidiEnabled;
        var devices = engine.MidiInputs();
        _devices.Text = devices.Count > 0 ? "Devices: " + string.Join(", ", devices) : "No MIDI devices found";
        _oscOn.Checked = engine.OscEnabled;
        _oscState.Text = "OSC: " + engine.OscState;
        _feedbackOn.Checked = engine.FeedbackEnabled;
        _midiOut.Selected = engine.MidiOutPortName;

        var t = _list.SelectedItem;
        _editor.Visible = t is not null;
        _learn.Lit = engine.Learning;
        if (t is null)
            return;
        if (t != _shown)
        {
            _shown = t;
            _name.Text = t.Name;
            _address.Text = t.OscAddress;
            _target.Text = t.Target;
        }
        _enabled.Checked = t.Enabled;
        _source.Selected = t.Source;
        var midi = t.Source == TriggerSource.Midi;
        _midiKindRow.Visible = _channelRow.Visible = midi;
        _numberRow.Visible = midi && t.UsesNumber; // a pitch bend has no number: it's the channel's own
        _addressRow.Visible = !midi;
        _midiKind.Selected = t.MidiKind;
        _channel.Selected = t.MidiChannel;
        _number.Value = t.MidiNumber;
        if (!_address.IsFocused)
            _address.Text = t.OscAddress;
        _action.Selected = t.Action;
        _targetRow.Visible = t.NeedsTarget;
        (_targetName.Text, _target.Placeholder) = t.Action switch
        {
            TriggerAction.ChaseToggle => ("Chase name", "chase name or number"),
            TriggerAction.EffectToggle => ("Effect name", "effect name or number"),
            _ => ("Cue number", "e.g. 5"),
        };
        _pick.Visible = TargetNames().Any();
        _pick.Selected = "";
        _lightPad.Checked = t.FeedbackEnabled;
        _followsRow.Visible = t.FeedbackEnabled;
        _follows.Selected = t.FeedbackWatch;
        _onValue.Value = t.FeedbackOn;
        _offValue.Value = t.FeedbackOff;
    }

    public override float2 Measure()
    {
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(float2.Round(position + float2.Max(float2.Zero, (ScreenSize - _box.Size) / 2)));
    }
}

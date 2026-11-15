using System.Globalization;
using System.Text.Json.Nodes;
using Brutal.Numerics;
using Cuedeck.Fixtures;

namespace Cuedeck.UI.Console;

/// <summary>
/// The fixture profile editor, over the dimmed screen: manufacturer and model, the physical side (category, beam,
/// pan and tilt range, and where a multi-head fixture's heads are), then its modes, each a list of channel rows (name,
/// role, default, min, max, 16-bit and named ranges). Save writes it to the profiles folder. sLight's "New Fixture
/// Profile" and "Edit Fixture Profile" window.
/// </summary>
sealed class ProfileEditor : Panel
{
    const float WIDTH = 1040;
    const float HEIGHT = 640;

    readonly Root _root;
    readonly Fonts _fonts;
    readonly ContextMenu _menu;
    readonly Panel _box;
    readonly Label _title;
    readonly TextBox _maker;
    readonly TextBox _model;
    readonly Label _builtInNote;
    readonly Dropdown<string> _category;
    readonly NumberBox _beam;
    readonly NumberBox _pan;
    readonly NumberBox _tilt;
    readonly Label _geometryNote;
    readonly TextBox _heads;
    readonly Dropdown<int> _modeChoice;
    readonly TextBox _modeName;
    readonly Button _deleteMode;
    readonly ScrollPanel _scroll;
    readonly Panel _channels;
    readonly Label _error;
    readonly Button _save;
    readonly List<(string Name, List<JsonObject> Channels)> _modes = [];
    ProfileStore? _store;
    FixtureProfile? _existing;
    Action<FixtureProfile> _saved = _ => { };
    int _mode;
    bool _pressedInBox;

    public float2 ScreenSize { get; set; }

    public ProfileEditor(Root root, Fonts fonts, ContextMenu menu)
    {
        _root = root;
        _fonts = fonts;
        _menu = menu;
        Visible = false;
        Background = Colors.Hex(0x000000, 0.6f);

        _box = Add(new Panel { Padding = 14, Spacing = 8, Background = Theme.PanelRaised, BorderColor = Theme.PanelBorder, BorderWidth = 1 });
        _title = _box.Add(new Label(fonts.Heading, "") { Color = Theme.Text });

        var names = _box.Add(new Row(8));
        names.Add(new Label(fonts.Body, "Manufacturer:") { Color = Theme.Muted });
        _maker = names.Add(new TextBox(fonts.Body) { Width = 180, Placeholder = "Generic" });
        names.Add(new Label(fonts.Body, "Model:") { Color = Theme.Muted });
        _model = names.Add(new TextBox(fonts.Body) { Width = 300 });
        _builtInNote = _box.Add(new Label(fonts.Small, "Editing a built-in — Save keeps this as your own copy.") { Color = Theme.Muted });

        var phys = _box.Add(new Row(8));
        phys.Add(new Label(fonts.Body, "Category:") { Color = Theme.Muted });
        _category = phys.Add(
            new Dropdown<string>(fonts.Body)
            {
                Menu = menu,
                Width = 180,
                Items = () => [.. ProfileText.Categories.Select(c => c.Key)],
                Text = k => ProfileText.Categories.First(c => c.Key == k).Name,
                Selected = "",
            }
        );
        phys.Add(new Label(fonts.Body, "Beam:") { Color = Theme.Muted });
        _beam = phys.Add(new NumberBox(fonts.Body, 1, 120, 0.5, 1, 70) { Suffix = "°", Tooltip = "Beam angle" });
        phys.Add(new Label(fonts.Body, "Pan range:") { Color = Theme.Muted });
        _pan = phys.Add(new NumberBox(fonts.Body, 0, 1080, 1, 0, 70) { Suffix = "°", Tooltip = "Pan travel across the whole DMX range" });
        phys.Add(new Label(fonts.Body, "Tilt range:") { Color = Theme.Muted });
        _tilt = phys.Add(new NumberBox(fonts.Body, 0, 540, 1, 0, 70) { Suffix = "°", Tooltip = "Tilt travel across the whole DMX range" });
        _geometryNote = _box.Add(new Label(fonts.Small, "The imported 3D model sets the beam angle in the visualizer.") { Color = Theme.Muted });

        var headsRow = _box.Add(new Row(8));
        headsRow.Add(new Label(fonts.Body, "Heads:") { Color = Theme.Muted });
        _heads = headsRow.Add(
            new TextBox(fonts.Body)
            {
                Width = WIDTH - 110,
                MaxLength = 8192,
                Placeholder = "x, y, z metres [, rot x, y, z degrees] per RGB head, split by /  —  blank = single head / evenly spread",
            }
        );

        var modeRow = _box.Add(new Row(8));
        modeRow.Add(new Label(fonts.Body, "Mode:") { Color = Theme.Muted });
        _modeChoice = modeRow.Add(
            new Dropdown<int>(fonts.Body)
            {
                Menu = menu,
                Width = 160,
                Items = () => [.. Enumerable.Range(0, _modes.Count)],
                Text = m => m < _modes.Count ? _modes[m].Name : "",
            }
        );
        _modeChoice.Chosen += SwitchMode;
        _modeName = modeRow.Add(new TextBox(fonts.Body) { Width = 160, Tooltip = "The mode's name" });
        _modeName.TextChanged += t =>
        {
            if (_mode < _modes.Count)
                _modes[_mode] = (t.Text.Trim() != "" ? t.Text : "Mode", _modes[_mode].Channels);
        };
        var addMode = modeRow.Add(new Button(fonts.Body, "Add Mode"));
        addMode.Clicked += _ =>
        {
            ReadRows();
            _modes.Add(($"Mode {_modes.Count + 1}", []));
            SwitchMode(_modes.Count - 1);
        };
        var duplicateMode = modeRow.Add(new Button(fonts.Body, "Duplicate Mode") { Tooltip = "Copy this mode in after it" });
        duplicateMode.Clicked += _ =>
        {
            ReadRows();
            var (name, chans) = _modes[_mode];
            _modes.Insert(_mode + 1, ($"{name} (copy)", [.. chans.Select(c => (JsonObject)c.DeepClone())]));
            SwitchMode(_mode + 1);
        };
        _deleteMode = modeRow.Add(new Button(fonts.Body, "Delete Mode"));
        _deleteMode.Clicked += _ =>
        {
            if (_modes.Count <= 1)
                return;
            _modes.RemoveAt(_mode);
            _mode = Math.Clamp(_mode, 0, _modes.Count - 1);
            ShowMode();
        };

        _box.Add(new Label(fonts.Small, "Ranges: \"0-9:Open, 10-19:Red:#f00\"  (label + optional colour; blank = plain slider)") { Color = Theme.Muted });
        _scroll = _box.Add(new ScrollPanel { Background = Theme.Well, Padding = 6 });
        _channels = _scroll.Add(new Panel { Spacing = 4 });
        var addChannel = _box.Add(new Button(fonts.Body, "Add Channel"));
        addChannel.Clicked += _ => AddRow(null);

        var footer = _box.Add(new Row(8));
        _save = footer.Add(new Button(fonts.Bold, "Save Profile"));
        _save.Clicked += _ => Save();
        var cancel = footer.Add(new Button(fonts.Body, "Cancel"));
        cancel.Clicked += _ => Close();
        _error = footer.Add(new Label(fonts.Body, "") { Color = Colors.Hex(0xFF8C8C), MaxWidth = WIDTH - 260, Wrap = true });

        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
    }

    public override bool IsInteractive => true;
    public override bool IsModal => true;
    public override Cursor Cursor => Cursor.Arrow;
    public override bool IsFocusable => Visible;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point);

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key != Key.Escape)
            return false;
        Close();
        return true;
    }

    /// <summary>
    /// The channel rows showing: the current mode's.
    /// </summary>
    public IEnumerable<ChannelRow> Rows => _channels.Children.OfType<ChannelRow>();

    /// <summary>
    /// Opens it on <paramref name="existing"/> to edit, or a new profile for null. Save writes the profile to
    /// <paramref name="store"/>'s folder (an edit keeps its id; a new one gets one from its name) and hands it to
    /// <paramref name="saved"/>.
    /// </summary>
    public void Show(ProfileStore store, FixtureProfile? existing, Action<FixtureProfile> saved)
    {
        _store = store;
        _existing = existing;
        _saved = saved;
        var editing = existing is not null;
        _title.Text = editing ? "Edit Fixture Profile" : "New Fixture Profile";
        _save.Text = editing ? "Save Changes" : "Save Profile";
        _maker.Text = existing?.Manufacturer ?? "";
        _model.Text = existing?.Model ?? "Custom Fixture";
        _builtInNote.Visible = existing is not null && store.IsBuiltIn(existing.Id) && !store.HasFile(existing);
        var phys = existing?.Physical ?? new FixturePhysical();
        _category.Selected = ProfileText.Categories.Any(c => c.Key == phys.Category) ? phys.Category : "";
        _beam.Value = phys.BeamDeg;
        _pan.Value = phys.PanRange;
        _tilt.Value = phys.TiltRange;
        _geometryNote.Visible = existing is { Geometry.Count: > 0 };
        _heads.Text = ProfileText.FormatHeads(phys.Heads).Replace("\n", " / ", StringComparison.Ordinal);
        _error.Text = "";

        _modes.Clear();
        foreach (var m in existing?.Modes ?? [])
            _modes.Add((m.Name, [.. m.Channels.Select(c => c.ToJson())]));
        if (_modes.Count == 0)
            _modes.Add(("Default", []));
        _mode = 0;
        ShowMode();
        Visible = true;
        _root.Focus(_model);
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        _root.Focus(null);
    }

    void SwitchMode(int mode)
    {
        ReadRows();
        _mode = Math.Clamp(mode, 0, _modes.Count - 1);
        ShowMode();
    }

    /// <summary>
    /// Builds the rows for the current mode: one blank one if it has none.
    /// </summary>
    void ShowMode()
    {
        _modeChoice.Selected = _mode;
        _modeName.Text = _modes[_mode].Name;
        _deleteMode.Enabled = _modes.Count > 1;
        _channels.Children.Clear();
        if (_modes[_mode].Channels.Count == 0)
            AddRow(null);
        else
            foreach (var c in _modes[_mode].Channels)
                AddRow(c);
        _scroll.ScrollTo(float2.Zero);
    }

    /// <summary>
    /// Puts what the rows say back into the current mode.
    /// </summary>
    void ReadRows() => _modes[_mode] = (_modes[_mode].Name, [.. Rows.Select(r => r.ToJson())]);

    void AddRow(JsonObject? data)
    {
        var row = _channels.Add(new ChannelRow(_fonts, _menu, data, Rows.Count() + 1));
        row.Removed += r => _channels.Children.Remove(r);
        row.Duplicated += r => AddRow(r.ToJson());
        row.Moved += (r, by) =>
        {
            var at = _channels.Children.IndexOf(r);
            var to = at + by;
            if (to < 0 || to >= _channels.Children.Count)
                return;
            _channels.Children.RemoveAt(at);
            _channels.Children.Insert(to, r);
        };
    }

    void Save()
    {
        if (_store is not { } store)
            return;
        ReadRows();
        var modes = new JsonArray();
        foreach (var (name, chans) in _modes)
        {
            if (chans.Count == 0)
            {
                _error.Text = $"Mode '{name}' has no channels.";
                return;
            }
            modes.Add(new JsonObject { ["name"] = name, ["channels"] = new JsonArray([.. chans.Select(c => (JsonNode)c.DeepClone())]) });
        }
        var model = _model.Text.Trim();
        if (model == "")
            model = "Custom Fixture";
        var maker = _maker.Text.Trim();
        var (heads, why) = ProfileText.ParseHeads(_heads.Text.Replace('/', '\n'));
        if (heads is null)
        {
            _error.Text = why;
            return;
        }

        // An edit keeps its id, so its file (or the built-in it stands in for) stays put; a new one's from its name.
        var id = _existing?.Id ?? store.UniqueId(ProfileStore.SafeId($"{maker} {model}".Trim()));
        var physical = new JsonObject
        {
            ["category"] = _category.Selected,
            ["beam_deg"] = _beam.Value,
            ["pan_range"] = _pan.Value,
            ["tilt_range"] = _tilt.Value,
            ["heads"] = new JsonArray([.. heads.Select(h => (JsonNode)new JsonArray([.. h.Select(v => (JsonNode)v)]))]),
        };
        var profile = new FixtureProfile(id, model, null, modes, physical, maker);
        if (_existing is not null)
            profile.Geometry = (JsonObject)_existing.Geometry.DeepClone(); // an imported GDTF's 3D model
        try
        {
            store.Save(profile);
        }
        catch (IOException e)
        {
            _error.Text = $"Couldn't save the profile: {e.Message}";
            return;
        }
        Close();
        _saved(profile);
    }

    public override float2 Measure()
    {
        var height = Math.Min(HEIGHT, ScreenSize.Y - 40);
        var listHeight = Math.Max(120, height - 360);
        _scroll.MaxHeight = listHeight;
        _scroll.MinSize = new float2(WIDTH - 28, listHeight);
        _scroll.MaxWidth = WIDTH - 28;
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(float2.Round(position + float2.Max(float2.Zero, (ScreenSize - _box.Size) / 2)));
    }
}

/// <summary>
/// One channel in the profile editor: its name, role, default, min and max, whether it's the fine half of a 16-bit
/// pair, and its named ranges as text. Buttons move it up or down, remove it, or copy it to the end.
/// </summary>
sealed class ChannelRow : Row
{
    readonly TextBox _name;
    readonly Dropdown<string> _role;
    readonly NumberBox _default;
    readonly NumberBox _min;
    readonly NumberBox _max;
    readonly Checkbox _fine;
    readonly TextBox _ranges;
    readonly List<RangeSlot> _originalRanges;

    public event Action<ChannelRow>? Removed;
    public event Action<ChannelRow>? Duplicated;
    public event Action<ChannelRow, int>? Moved;

    public ChannelRow(Fonts fonts, ContextMenu menu, JsonObject? data, int number)
        : base(6)
    {
        var ch = data is null ? new FixtureChannel { Name = $"Ch {number}" } : FixtureChannel.FromJson(data);
        _originalRanges = [.. ch.Ranges];

        var up = Add(new Button(fonts.Body, "▲") { Padding = 4, Tooltip = "Move up" });
        up.Clicked += _ => Moved?.Invoke(this, -1);
        var down = Add(new Button(fonts.Body, "▼") { Padding = 4, Tooltip = "Move down" });
        down.Clicked += _ => Moved?.Invoke(this, 1);
        _name = Add(new TextBox(fonts.Body) { Width = 110, Placeholder = "Channel name", Text = ch.Name });
        _role = Add(
            new Dropdown<string>(fonts.Body)
            {
                Menu = menu,
                Width = 116,
                Items = () => Roles.All,
                Text = r => r,
                Selected = Roles.All.Contains(ch.Role) ? ch.Role : Roles.Generic,
            }
        );
        Add(new Label(fonts.Body, "d") { Color = Theme.Muted });
        _default = Add(new NumberBox(fonts.Body, 0, 255, width: 44) { Value = ch.Default, Tooltip = "Default (home) value" });
        Add(new Label(fonts.Body, "min") { Color = Theme.Muted });
        _min = Add(new NumberBox(fonts.Body, 0, 255, width: 44) { Value = ch.Min });
        Add(new Label(fonts.Body, "max") { Color = Theme.Muted });
        _max = Add(new NumberBox(fonts.Body, 0, 255, width: 44) { Value = ch.Max });
        _fine = Add(new Checkbox(fonts.Body, "16-bit") { Checked = ch.Fine, Tooltip = "The fine half of a 16-bit pair, with the channel before it" });
        _ranges = Add(new TextBox(fonts.Body) { Width = 170, MaxLength = 8192, Placeholder = "ranges", Text = ProfileText.FormatRanges(ch.Ranges) });
        var remove = Add(new Button(fonts.Body, "×") { Padding = 4, Tooltip = "Remove channel" });
        remove.Clicked += _ => Removed?.Invoke(this);
        var duplicate = Add(new Button(fonts.Body, "Copy") { Tooltip = "Duplicate channel (added at the end)" });
        duplicate.Clicked += _ => Duplicated?.Invoke(this);
    }

    public string ChannelName
    {
        get => _name.Text;
        set => _name.Text = value;
    }

    /// <summary>
    /// The row as the profile's channel JSON. Gobo pictures, which the ranges text has no room for, are kept by
    /// label.
    /// </summary>
    public JsonObject ToJson()
    {
        var ranges = ProfileText.CarryImages(ProfileText.ParseRanges(_ranges.Text), _originalRanges);
        return new JsonObject
        {
            ["name"] = _name.Text,
            ["role"] = _role.Selected,
            ["default"] = (int)_default.Value,
            ["min"] = (int)_min.Value,
            ["max"] = (int)_max.Value,
            ["fine"] = _fine.Checked,
            ["ranges"] = new JsonArray([.. ranges.Select(r => (JsonNode)r.ToJson())]),
        };
    }
}

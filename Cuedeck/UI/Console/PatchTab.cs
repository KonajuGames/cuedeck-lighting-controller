using System.Globalization;
using Brutal.Numerics;
using Cuedeck.Dmx;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// One universe's page of the Patch tab: where it's sent, then its fixtures, with a row to patch more.
/// </summary>
sealed class UniversePage : Panel
{
    readonly LightingShow _show;
    readonly Fonts _fonts;
    readonly ContextMenu _menu;
    readonly Dialogs _dialogs;
    readonly int _index;
    readonly Panel _rows;
    readonly TextBox _ip;
    readonly NumberBox _port;
    readonly NumberBox _number;
    readonly Dropdown<OutputMode> _output;
    readonly NumberBox _priority;
    readonly TextBox _sacnIp;
    readonly Row _sacnRow;
    readonly Label _where;
    readonly Dropdown<FixtureProfile> _profile;
    readonly Dropdown<int> _mode;
    readonly TextBox _name;
    readonly NumberBox _start;
    List<PatchedFixture> _shown = [];

    /// <summary>
    /// Something to say on the status line.
    /// </summary>
    public event Action<string>? Status;

    public UniversePage(Fonts fonts, ContextMenu menu, Dialogs dialogs, LightingShow show, ProfileStore profiles, int index)
    {
        _show = show;
        _fonts = fonts;
        _menu = menu;
        _dialogs = dialogs;
        _index = index;
        Spacing = 8;
        var patch = show.Patch.Universes[index];

        var connection = Add(new Row(6));
        connection.Add(new Label(fonts.Body, "IP:") { Color = Theme.Muted });
        _ip = connection.Add(new TextBox(fonts.Body) { Width = 130, Text = patch.Ip });
        connection.Add(new Label(fonts.Body, "Port:") { Color = Theme.Muted });
        _port = connection.Add(new NumberBox(fonts.Body, 1, 65535, width: 60) { Value = patch.Port });
        connection.Add(new Label(fonts.Body, "Universe:") { Color = Theme.Muted });
        _number = connection.Add(new NumberBox(fonts.Body, 0, 32767, width: 50) { Value = patch.ArtNetUniverse });
        connection.Add(new Label(fonts.Body, "Output:") { Color = Theme.Muted });
        _output = connection.Add(
            new Dropdown<OutputMode>(fonts.Body)
            {
                Menu = menu,
                Width = 110,
                Items = () => [OutputMode.ArtNet, OutputMode.Sacn],
                Text = m => m == OutputMode.Sacn ? "sACN" : m == OutputMode.Usb ? "USB" : "Art-Net",
                Selected = patch.Output,
            }
        );
        _output.Chosen += _ => Apply();
        var apply = connection.Add(new Button(fonts.Body, "Apply Connection"));
        apply.Clicked += _ => Apply();
        _ip.Submitted += _ => Apply();

        _sacnRow = Add(new Row(6));
        _sacnRow.Add(new Label(fonts.Body, "sACN priority:") { Color = Theme.Muted });
        _priority = _sacnRow.Add(new NumberBox(fonts.Body, 0, 200, width: 50) { Value = patch.SacnPriority });
        _sacnRow.Add(new Label(fonts.Body, "Unicast IP:") { Color = Theme.Muted });
        _sacnIp = _sacnRow.Add(new TextBox(fonts.Body) { Width = 130, Placeholder = "(multicast)", Text = patch.SacnIp });

        var levels = Add(new Row(6));
        _where = levels.Add(new Label(fonts.Body) { Color = Theme.Muted });
        levels.Add(new Spacer(10));
        var blackout = levels.Add(new Button(fonts.Body, "Blackout Universe"));
        blackout.Clicked += _ => show.Dmx.Universes[index].SetAll(0);
        var full = levels.Add(new Button(fonts.Body, "Universe Full"));
        full.Clicked += _ => show.Dmx.Universes[index].SetAll(255);

        Add(new Divider(600));
        Add(new Label(fonts.Heading, "Fixture Patch") { Color = Theme.Text });

        var add = Add(new Row(6));
        add.Add(new Label(fonts.Body, "Profile:") { Color = Theme.Muted });
        _profile = add.Add(
            new Dropdown<FixtureProfile>(fonts.Body) { Menu = menu, Width = 260, Items = () => profiles.Profiles, Text = p => p.FullName, Selected = profiles.Profiles[0] }
        );
        add.Add(new Label(fonts.Body, "Mode:") { Color = Theme.Muted });
        _mode = add.Add(
            new Dropdown<int>(fonts.Body)
            {
                Menu = menu,
                Width = 150,
                Items = () => [.. Enumerable.Range(0, Math.Max(_profile.Selected.ModeCount, 1))],
                Text = m => m < _profile.Selected.ModeCount ? _profile.Selected.ModeNames[m] : "Default",
            }
        );
        _profile.Chosen += _ => _mode.Selected = 0;
        add.Add(new Label(fonts.Body, "Name:") { Color = Theme.Muted });
        _name = add.Add(new TextBox(fonts.Body) { Width = 140, Placeholder = "(optional)" });
        add.Add(new Label(fonts.Body, "Start ch:") { Color = Theme.Muted });
        _start = add.Add(new NumberBox(fonts.Body, 1, 512, width: 50) { Value = 1 });
        var addButton = add.Add(new Button(fonts.Body, "Add Fixture"));
        addButton.Clicked += _ => AddFixture();
        var library = add.Add(new Button(fonts.Body, "Library…") { Tooltip = "Choose a fixture from the bundled fixture library" });
        library.Clicked += _ =>
            dialogs.Library.Show(p =>
            {
                SelectProfile(profiles.AddForSession(p));
                Status?.Invoke($"Added '{p.Model}' from the library — set a start channel and Add Fixture.");
            });

        _rows = Add(new Panel { Spacing = 8 });
    }

    /// <summary>
    /// Makes <paramref name="profile"/> the one to patch next, in its first mode.
    /// </summary>
    public void SelectProfile(FixtureProfile profile)
    {
        _profile.Selected = profile;
        _mode.Selected = 0;
    }

    void Apply()
    {
        var p = _show.Patch.Universes[_index];
        p.Ip = _ip.Text.Trim();
        p.Port = (int)_port.Value;
        p.ArtNetUniverse = (int)_number.Value;
        p.Output = _output.Selected;
        p.SacnPriority = (int)_priority.Value;
        p.SacnIp = _sacnIp.Text.Trim();
        _show.Patch.ApplyConnection(_index);
        var u = _show.Dmx.Universes[_index];
        Status?.Invoke(u.Connected ? $"Universe {_index + 1} now sends to {Where()}." : $"Universe {_index + 1} couldn't reach {p.Ip}.");
    }

    string Where()
    {
        var u = _show.Dmx.Universes[_index];
        return u.Mode switch
        {
            OutputMode.Sacn => $"→ sACN universe {Math.Max(u.Number, 1)}, {(u.SacnUnicastIp != "" ? u.SacnUnicastIp : Sacn.MulticastIp(Math.Max(u.Number, 1)))}",
            OutputMode.Usb => $"→ USB {u.UsbSerial}",
            _ => $"→ {u.TargetIp}:{u.TargetPort}  Art-Net U{u.Number}",
        };
    }

    void AddFixture()
    {
        var profile = _profile.Selected.Clone();
        var mode = Math.Clamp(_mode.Selected, 0, Math.Max(profile.ModeCount - 1, 0));
        var start = (int)_start.Value - 1;
        var count = profile.ChannelCount(mode);
        if (count == 0)
        {
            Status?.Invoke($"{profile.FullName} has no channels to patch.");
            return;
        }
        if (start + count > DmxUniverse.Size)
        {
            Status?.Invoke($"{profile.FullName} needs {count} channels: it won't fit from channel {start + 1}.");
            return;
        }
        var overlap = _show.Patch.Universes[_index].Fixtures.FirstOrDefault(f => f.Start < start + count && start < f.Start + f.Channels.Count);
        var fixture = _show.Patch.Add(_index, profile, start, mode, _name.Text);
        _name.Text = "";
        _start.Value = Math.Min(start + count + 1, DmxUniverse.Size);
        _show.Retarget();
        Status?.Invoke(
            overlap is null
                ? $"Patched {fixture.Name} at channel {start + 1}."
                : $"Patched {fixture.Name} at channel {start + 1}; it overlaps {overlap.Name}."
        );
    }

    /// <summary>
    /// Follows the patch and the levels. Call once a frame while it shows.
    /// </summary>
    public void Update()
    {
        var p = _show.Patch.Universes[_index];
        _sacnRow.Visible = _output.Selected == OutputMode.Sacn;
        _where.Text = Where() + (_show.Dmx.Universes[_index].Connected || p.Output == OutputMode.Usb ? "" : "  (not connected)");

        if (!_shown.SequenceEqual(p.Fixtures))
        {
            var expanded = _rows.Children.OfType<FixtureRow>().Where(r => r.Expanded).Select(r => r.Fixture).ToHashSet();
            _rows.Children.Clear();
            foreach (var f in p.Fixtures)
            {
                var row = _rows.Add(new FixtureRow(_fonts, _menu, _dialogs, _show, f) { Expanded = expanded.Contains(f) });
                row.RemoveClicked += fixture =>
                {
                    _show.Patch.Remove(fixture);
                    foreach (var g in _show.Groups)
                        g.Members.Remove(fixture.Uid);
                    _show.Retarget();
                    Status?.Invoke($"Removed {fixture.Name}.");
                };
            }
            _shown = [.. p.Fixtures];
        }
        if (_rows.Children.Count == 0 && p.Fixtures.Count == 0)
            _rows.Children.Add(new Label(_fonts.Body, "Nothing patched in this universe yet.") { Color = Theme.Faint });
        var data = _show.Dmx.Universes[_index].Data;
        var colours = ModelColours.Of(_show.Patch);
        foreach (var row in _rows.Children.OfType<FixtureRow>())
        {
            // Each model's fixtures in its colour, as in the DMX Output grid.
            row.Background = colours.GetValueOrDefault(row.Fixture.Profile.FullName, Theme.PanelRaised);
            row.Update(data);
        }
    }

    /// <summary>
    /// Shows the patch's settings again, after another show's loaded.
    /// </summary>
    public void Reload()
    {
        var p = _show.Patch.Universes[_index];
        _ip.Text = p.Ip;
        _port.Value = p.Port;
        _number.Value = p.ArtNetUniverse;
        _output.Selected = p.Output;
        _priority.Value = p.SacnPriority;
        _sacnIp.Text = p.SacnIp;
        _shown = [];
        _rows.Children.Clear();
    }
}

/// <summary>
/// The Patch tab: a page per universe.
/// </summary>
sealed class PatchTab : Region
{
    readonly TabView _universes;
    readonly Fonts _fonts;
    readonly ContextMenu _menu;
    readonly Dialogs _dialogs;
    readonly LightingShow _show;
    readonly ProfileStore _profiles;
    readonly List<UniversePage> _pages = [];

    public event Action<string>? Status;

    public PatchTab(Fonts fonts, ContextMenu menu, Dialogs dialogs, LightingShow show, ProfileStore profiles)
    {
        _fonts = fonts;
        _menu = menu;
        _dialogs = dialogs;
        _show = show;
        _profiles = profiles;
        _universes = Add(new TabView(fonts.Body));
    }

    /// <summary>
    /// The universe whose page is showing.
    /// </summary>
    public int CurrentUniverse => Math.Max(_universes.Selected, 0);

    /// <summary>
    /// Builds the pages again from scratch, after another show's loaded.
    /// </summary>
    public void Reload()
    {
        _pages.Clear();
        _universes.Clear();
    }

    public void Update()
    {
        var count = _show.Patch.Universes.Count;
        if (_pages.Count != count)
        {
            var selected = _universes.Selected;
            _pages.Clear();
            _universes.Clear();
            for (var i = 0; i < count; i++)
            {
                var page = new UniversePage(_fonts, _menu, _dialogs, _show, _profiles, i);
                page.Status += s => Status?.Invoke(s);
                _pages.Add(page);
                _universes.AddTab($"Universe {(i + 1).ToString(CultureInfo.InvariantCulture)}", new ScrollingPage(page));
            }
            _universes.Selected = Math.Clamp(selected, 0, count - 1);
        }
        if (_universes.Selected >= 0)
            _pages[_universes.Selected].Update();
    }

    protected override void MeasureContent()
    {
        _universes.Available = Available - new float2(8, 8);
        _universes.Measure();
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _universes.Arrange(position + new float2(4, 4));
    }
}

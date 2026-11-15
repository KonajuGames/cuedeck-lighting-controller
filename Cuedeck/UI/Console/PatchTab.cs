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
    readonly Row _usbRow;
    readonly Dropdown<string> _usbDevice;
    readonly Dropdown<int> _usbMode;
    List<UsbDevice> _usbDevices = [];
    readonly Label _where;
    readonly Dropdown<FixtureProfile> _profile;
    readonly Dropdown<int> _mode;
    readonly TextBox _name;
    readonly NumberBox _start;
    readonly ProfileStore _profiles;
    List<(PatchedFixture Fixture, FixtureProfile Profile)> _shown = [];

    /// <summary>
    /// Something to say on the status line.
    /// </summary>
    public event Action<string>? Status;

    /// <summary>
    /// A profile's been made, changed or deleted: every page's choice wants checking.
    /// </summary>
    public event Action? ProfilesChanged;

    /// <summary>
    /// Asks for a fixture definition file to import (Windows' Open dialog in the app); null if none's chosen.
    /// </summary>
    public Func<string?> AskForFixtureFile { get; set; } = () => null;

    /// <summary>
    /// The USB DMX interfaces (null, or without the driver: no USB output to offer).
    /// </summary>
    public Func<UsbDmx?> Usb { get; set; } = () => null;

    public UniversePage(Fonts fonts, ContextMenu menu, Dialogs dialogs, LightingShow show, ProfileStore profiles, int index)
    {
        _show = show;
        _profiles = profiles;
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
                Items = () => Usb() is { Available: true } ? [OutputMode.ArtNet, OutputMode.Sacn, OutputMode.Usb] : [OutputMode.ArtNet, OutputMode.Sacn],
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

        // USB DMX: which interface, and what kind.
        _usbRow = Add(new Row(6));
        _usbRow.Add(new Label(fonts.Body, "Device:") { Color = Theme.Muted });
        _usbDevice = _usbRow.Add(
            new Dropdown<string>(fonts.Body)
            {
                Menu = menu,
                Width = 300,
                Items = () => [.. _usbDevices.Select(d => d.Serial)],
                Text = serial =>
                    _usbDevices.FirstOrDefault(d => d.Serial == serial) is { } d ? $"{d.Description}  ({d.Serial})"
                    : serial != "" ? serial
                    : "(no USB DMX interfaces found)",
                Selected = patch.UsbSerial,
            }
        );
        _usbDevice.Chosen += _ => Apply();
        _usbRow.Add(new Label(fonts.Body, "Interface:") { Color = Theme.Muted });
        _usbMode = _usbRow.Add(
            new Dropdown<int>(fonts.Body)
            {
                Menu = menu,
                Width = 120,
                Items = () => [.. Enumerable.Range(0, UsbDmx.ModeNames.Length)],
                Text = m => UsbDmx.ModeNames[Math.Clamp(m, 0, UsbDmx.ModeNames.Length - 1)],
                Selected = Math.Clamp(patch.UsbMode, 0, UsbDmx.ModeNames.Length - 1),
            }
        );
        _usbMode.Chosen += _ => Apply();
        var rescan = _usbRow.Add(new Button(fonts.Body, "Rescan") { Tooltip = "Look for USB DMX interfaces again" });
        rescan.Clicked += _ => RescanUsb();

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
        var library = add.Add(new Button(fonts.Body, "Library…") { Tooltip = "Choose a fixture from the fixture library" });
        library.Clicked += _ =>
            dialogs.Library.Show(p =>
            {
                SelectProfile(profiles.AddForSession(p));
                Status?.Invoke($"Added '{p.Model}' from the library — set a start channel and Add Fixture.");
            });
        var newProfile = add.Add(new Button(fonts.Body, "New…") { Tooltip = "Make a fixture profile" });
        newProfile.Clicked += _ =>
            dialogs.Editor.Show(
                profiles,
                null,
                p =>
                {
                    ProfilesChanged?.Invoke();
                    SelectProfile(p);
                    Status?.Invoke($"Saved profile '{p.Model}' ({p.ModeCount} mode(s)).");
                }
            );
        var import = add.Add(new Button(fonts.Body, "Import…") { Tooltip = "Import a GDTF or Open Fixture Library file as a profile" });
        import.Clicked += _ => Import();
        var edit = add.Add(new Button(fonts.Body, "Edit…") { Tooltip = "Edit the chosen profile" });
        edit.Clicked += _ => dialogs.Editor.Show(profiles, _profile.Selected, Edited);
        var delete = add.Add(new Button(fonts.Body, "Delete…") { Tooltip = "Delete the chosen profile (or reset an edited built-in)" });
        delete.Clicked += _ => Delete();

        _rows = Add(new Panel { Spacing = 8 });
    }

    void Import()
    {
        if (AskForFixtureFile() is not { } path)
            return;
        var (profile, message) = _profiles.Import(path);
        if (profile is not null)
        {
            ProfilesChanged?.Invoke();
            SelectProfile(profile);
        }
        Status?.Invoke(message);
    }

    /// <summary>
    /// An edited profile goes to the fixtures patched with it.
    /// </summary>
    void Edited(FixtureProfile profile)
    {
        ProfilesChanged?.Invoke();
        var (updated, repatch) = _show.Patch.UpdateProfile(profile);
        if (updated > 0)
            _show.Retarget();
        Status?.Invoke(ProfileText.UpdateMessage(profile.Model, updated, repatch));
    }

    void Delete()
    {
        var p = _profile.Selected;
        if (_profiles.IsBuiltIn(p.Id) && !_profiles.HasFile(p))
        {
            Status?.Invoke(_profiles.Delete(p));
            return;
        }
        var reverts = _profiles.IsBuiltIn(p.Id);
        _dialogs.Confirm.Show(
            reverts ? "Reset Profile" : "Delete Profile",
            reverts
                ? $"Reset \"{p.Model}\" to its built-in default?\n\nYour edited copy ({p.Id}.json) will be removed."
                : $"Delete the profile \"{p.Model}\"?\n\n{p.Id}.json will be removed. This can't be undone.",
            reverts ? "Reset" : "Delete",
            () =>
            {
                var message = _profiles.Delete(p);
                ProfilesChanged?.Invoke();
                Status?.Invoke(message);
            }
        );
    }

    /// <summary>
    /// Keeps the chosen profile one that's offered: the one with its id now, else the first.
    /// </summary>
    public void RefreshProfile()
    {
        var chosen = _profile.Selected;
        if (_profiles.Profiles.Contains(chosen))
            return;
        _profile.Selected = _profiles.Profiles.FirstOrDefault(p => p.Id == chosen.Id) ?? _profiles.Profiles[0];
        _mode.Selected = Math.Clamp(_mode.Selected, 0, Math.Max(_profile.Selected.ModeCount - 1, 0));
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
        if (p.Output == OutputMode.Usb && _usbDevices.Count == 0)
            RescanUsb();
        p.UsbSerial = _usbDevice.Selected ?? "";
        if (p.UsbSerial == "" && _usbDevices.Count > 0)
            p.UsbSerial = _usbDevice.Selected = _usbDevices[0].Serial;
        p.UsbMode = _usbMode.Selected;
        var serial = p.UsbSerial;
        if (!_show.Patch.ApplyConnection(_index))
        {
            // As sLight: the interface wouldn't open, so the universe stays on Art-Net.
            _output.Selected = OutputMode.ArtNet;
            Status?.Invoke(serial == "" ? "USB DMX: no interface found (plug one in and press Rescan) — sending Art-Net." : $"USB DMX: {Usb()?.Status(serial) ?? "no driver"} — sending Art-Net.");
            return;
        }
        if (p.Output == OutputMode.Usb)
        {
            Status?.Invoke($"Universe {_index + 1} now sends to USB DMX {p.UsbSerial}.");
            return;
        }
        var u = _show.Dmx.Universes[_index];
        Status?.Invoke(u.Connected ? $"Universe {_index + 1} now sends to {Where()}." : $"Universe {_index + 1} couldn't reach {p.Ip}.");
    }

    string Where()
    {
        var u = _show.Dmx.Universes[_index];
        return u.Mode switch
        {
            OutputMode.Sacn => $"→ sACN universe {Math.Max(u.Number, 1)}, {(u.SacnUnicastIp != "" ? u.SacnUnicastIp : Sacn.MulticastIp(Math.Max(u.Number, 1)))}",
            OutputMode.Usb => $"→ USB DMX {u.UsbSerial}  [{Usb()?.Status(u.UsbSerial) ?? "no driver"}]",
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
        _usbRow.Visible = _output.Selected == OutputMode.Usb;
        _where.Text = Where() + (_show.Dmx.Universes[_index].Connected || p.Output == OutputMode.Usb ? "" : "  (not connected)");

        // Built again when the patch changes, or a fixture's profile has been edited.
        if (!_shown.SequenceEqual(p.Fixtures.Select(f => (f, f.Profile))))
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
            _shown = [.. p.Fixtures.Select(f => (f, f.Profile))];
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
    /// Looks for USB DMX interfaces again.
    /// </summary>
    public void RescanUsb()
    {
        _usbDevices = Usb()?.ListDevices() ?? [];
        Status?.Invoke(
            Usb() is not { Available: true } ? "USB DMX needs FTDI's D2XX driver."
            : _usbDevices.Count == 0 ? "No USB DMX interfaces found."
            : $"Found {_usbDevices.Count} USB DMX interface{(_usbDevices.Count == 1 ? "" : "s")}."
        );
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
        _usbDevice.Selected = p.UsbSerial;
        _usbMode.Selected = Math.Clamp(p.UsbMode, 0, UsbDmx.ModeNames.Length - 1);
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

    /// <summary>
    /// Asks for a fixture definition file to import (Windows' Open dialog in the app); null if none's chosen.
    /// </summary>
    public Func<string?> AskForFixtureFile { get; set; } = () => null;

    /// <summary>
    /// The USB DMX interfaces, for universes sent to one (null: none offered).
    /// </summary>
    public UsbDmx? Usb { get; set; }

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
                var page = new UniversePage(_fonts, _menu, _dialogs, _show, _profiles, i) { Usb = () => Usb };
                page.Status += s => Status?.Invoke(s);
                page.ProfilesChanged += () => _pages.ForEach(pg => pg.RefreshProfile());
                page.AskForFixtureFile = () => AskForFixtureFile();
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

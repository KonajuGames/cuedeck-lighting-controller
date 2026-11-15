using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Show;

namespace Cuedeck.UI.Console;

/// <summary>
/// One patched fixture in the Patch tab: its name and channels, its show category and category group, Home and
/// Remove; and, expanded, its controls. A fixture with red, green and blue gets a swatch per head; pan and tilt
/// together get a pad; a 16-bit pair one slider; a channel with named slots a list of them; anything else a slider.
/// A fixture without a dimmer gets a virtual one.
/// </summary>
sealed class FixtureRow : Panel
{
    readonly Panel _controls;
    readonly Ends _header;
    readonly Button _expand;
    readonly List<IFixtureControl> _bound = [];
    readonly List<Button> _groupButtons = [];

    public PatchedFixture Fixture { get; }

    /// <summary>
    /// How wide the row is: the list's width, so its category and buttons line up down the right of it.
    /// </summary>
    public float Width
    {
        get => MinSize.X;
        set
        {
            MinSize = new float2(value, 0);
            _header.Width = value - 2 * Inset;
        }
    }

    /// <summary>
    /// Remove was clicked.
    /// </summary>
    public event Action<PatchedFixture>? RemoveClicked;

    /// <summary>
    /// The fixture's category or category group changed.
    /// </summary>
    public event Action? Edited;

    public FixtureRow(Fonts fonts, FixtureIcons icons, ContextMenu menu, Dialogs dialogs, LightingShow show, PatchedFixture fixture)
    {
        // An icon button, its name in its tooltip, as sLight's.
        Button IconButton(Panel row, Image icon, string tooltip) =>
            row.Add(new Button(fonts.Small, "") { Icon = icon, Padding = 4, MinSize = new float2(26, 24), Tooltip = tooltip });
        Fixture = fixture;
        Padding = 8;
        Spacing = 8;
        Background = Theme.PanelRaised;
        BorderColor = Theme.PanelBorder;
        BorderWidth = 1;

        // The fixture on the left; its category and buttons on the right.
        _header = Add(new Ends());
        var header = _header.Add(new Row(6));
        _expand = header.Add(new Button(fonts.Small, "►") { Padding = 4, MinSize = new float2(26, 0), Tooltip = "Expand" });
        var chans = fixture.Channels;
        var mode = fixture.Profile.ModeCount > 1 ? $"  [{fixture.Profile.ModeNames[Math.Clamp(fixture.Mode, 0, fixture.Profile.ModeCount - 1)]}]" : "";
        header.Add(
            new Label(fonts.Bold, $"{fixture.Name}  (ch {fixture.Start + 1}-{fixture.Start + chans.Count})")
            {
                Color = Theme.Text,
                MaxWidth = 320,
            }
        ).Tooltip = $"{fixture.Profile.FullName}{mode}";
        header.Add(new Label(fonts.Small, fixture.Profile.FullName + mode) { Color = Theme.Muted, MaxWidth = 340 });
        header = _header.Add(new Row(6));

        var category = header.Add(
            new Dropdown<string>(fonts.Small) { Menu = menu, Width = 160, Items = () => FixtureProfile.ShowCategories, Selected = fixture.Category }
        );
        category.Tooltip = "Its category, for generated shows";
        category.Chosen += c =>
        {
            fixture.Category = c;
            Edited?.Invoke();
        };
        for (var g = 1; g <= 4; g++)
        {
            var group = g;
            var b = header.Add(new Button(fonts.Small, g.ToString()) { Padding = 4, MinSize = new float2(22, 0), Tooltip = $"Category group {g}" });
            b.Clicked += _ =>
            {
                fixture.CategoryGroup = group;
                Edited?.Invoke();
            };
            _groupButtons.Add(b);
        }
        var exclude = IconButton(header, icons.LeaveOut, "Leave out of show generation");
        exclude.Clicked += _ =>
        {
            fixture.ExcludeGenerate = !fixture.ExcludeGenerate;
            Edited?.Invoke();
        };
        header.Add(new Spacer(6));
        // Flash: full white while it's held, then the channels it changed put back exactly as they were.
        var flash = IconButton(header, icons.Flash, "Flash: full white while held");
        flash.Enabled = Patch.FlashTargets(fixture.Channels).Count > 0;
        Dictionary<int, int>? flashed = null;
        flash.PressStarted += _ => flashed ??= show.Patch.FlashOn(fixture);
        flash.PressEnded += _ =>
        {
            if (flashed is not null)
                show.Patch.FlashOff(fixture, flashed);
            flashed = null;
        };
        var home = IconButton(header, icons.Home, "Home: back to its default levels");
        home.Clicked += _ => show.Patch.Home(fixture);
        var remove = IconButton(header, icons.Remove, "Remove from the patch");
        remove.Clicked += _ => RemoveClicked?.Invoke(fixture);

        _controls = Add(new Row(14) { Visible = false });
        _expand.Clicked += _ => Expanded = !Expanded;
        BuildControls(fonts, menu, dialogs, show);

        // Kept current each frame.
        Updating = () =>
        {
            for (var i = 0; i < _groupButtons.Count; i++)
                _groupButtons[i].Lit = fixture.CategoryGroup == i + 1;
            exclude.Lit = fixture.ExcludeGenerate;
            exclude.Tooltip = fixture.ExcludeGenerate ? "Left out of show generation: click to put it back in" : "Leave out of show generation";
            category.Selected = fixture.Category;
        };
    }

    readonly Action Updating;

    public bool Expanded
    {
        get => _controls.Visible;
        set
        {
            _controls.Visible = value;
            _expand.Text = value ? "▼" : "►";
            _expand.Tooltip = value ? "Collapse" : "Expand";
        }
    }

    void BuildControls(Fonts fonts, ContextMenu menu, Dialogs dialogs, LightingShow show)
    {
        var fixture = Fixture;
        var universe = show.Dmx.Universes[fixture.Universe];
        var chans = fixture.Channels;
        var start = fixture.Start;
        var handled = new HashSet<int>();

        T Bind<T>(T control)
            where T : Element, IFixtureControl
        {
            _controls.Add(control);
            _bound.Add(control);
            return control;
        }

        if (fixture.Profile.VirtualDimmerChannels(fixture.Mode).Count > 0)
            Bind(new VirtualDimmerSlider(fonts, () => show.Patch.VirtualDimmer(fixture), level => show.Patch.SetVirtualDimmer(fixture, level)));

        var heads = fixture.Profile.HeadGroups(fixture.Mode);
        for (var h = 0; h < heads.Count; h++)
            Bind(new ColourSwatch(fonts, heads.Count > 1 ? $"Colour {h + 1}" : "Colour", fixture, heads[h], universe, dialogs.Picker));

        // Pan and tilt together: one pad instead of two sliders.
        var pan = -1;
        var tilt = -1;
        for (var k = 0; k < chans.Count; k++)
        {
            if (chans[k].Role == Roles.Pan && pan < 0)
                pan = k;
            else if (chans[k].Role == Roles.Tilt && tilt < 0)
                tilt = k;
        }
        if (pan >= 0 && tilt >= 0)
        {
            var panAxis = Axis.For(chans, pan);
            var tiltAxis = Axis.For(chans, tilt);
            Bind(new PanTiltPad(fonts, universe, start, panAxis, tiltAxis));
            handled.UnionWith([pan, tilt]);
            if (panAxis.Fine >= 0)
                handled.Add(panAxis.Fine);
            if (tiltAxis.Fine >= 0)
                handled.Add(tiltAxis.Fine);
        }

        for (var ci = 0; ci < chans.Count; ci++)
        {
            if (handled.Contains(ci))
                continue;
            var ch = chans[ci];
            if (ci + 1 < chans.Count && chans[ci + 1].Fine && !ch.Fine)
            {
                Bind(new WideSlider(fonts, universe, start + ci, ch, chans[ci + 1]));
                ci++;
            }
            else if (ch.Ranges.Count > 0)
            {
                Bind(new SlotControl(fonts, menu, universe, start + ci, ch));
            }
            else
            {
                Bind(new ChannelSlider(fonts, universe, start + ci, ch));
            }
        }
    }

    /// <summary>
    /// Follows the universe's levels and the fixture's settings. Call once a frame while it shows.
    /// </summary>
    public void Update(byte[] data)
    {
        Updating();
        if (!Expanded)
            return;
        foreach (var control in _bound)
            control.Sync(data);
    }
}

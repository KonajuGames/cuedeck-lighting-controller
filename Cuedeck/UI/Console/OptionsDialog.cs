using Brutal.Numerics;

namespace Cuedeck.UI.Console;

/// <summary>
/// The app's options (<see cref="AppSettings"/>), over the dimmed screen: this machine's setup, kept between runs and
/// not in the show. A change applies, and is saved, at once. Opened from the title bar's cog. sLight's Options dialog.
/// </summary>
sealed class OptionsDialog : Panel
{
    const float WIDTH = 520;

    readonly Root _root;
    readonly AppSettings _settings;

    public AppSettings Settings => _settings;
    readonly Panel _box;
    readonly Dropdown<DjSoftware> _dj;
    readonly Label _hint;
    readonly Panel _slight;
    bool _pressedInBox;

    /// <summary>
    /// sLight's folders on this computer, if any: its import is offered while there are.
    /// </summary>
    public IReadOnlyList<string> SlightFolders { get; set; } = [];

    /// <summary>
    /// Import from sLight was pressed (the dialog's closed first).
    /// </summary>
    public event Action? ImportFromSlight;

    public float2 ScreenSize { get; set; }

    /// <summary>
    /// The DJ software setting changed: the music library's to be read again.
    /// </summary>
    public event Action<DjSoftware>? DjSoftwareChanged;

    readonly Label _lookHint;
    readonly Dropdown<float?> _size;
    readonly Label _restartNote;

    /// <summary>
    /// The screen size Windows has for the screen, the one used when none's chosen.
    /// </summary>
    public float WindowsScale { get; set; } = 1;

    /// <summary>
    /// The biggest screen size the screen has room for (see <see cref="ScreenScale.Largest"/>): a bigger one chosen is
    /// drawn at this.
    /// </summary>
    public float LargestScale { get; set; } = float.MaxValue;

    /// <summary>
    /// The screen size it's drawn at now, taken on as the app started: a size chosen that comes out different says
    /// Cuedeck needs starting again to show it.
    /// </summary>
    public float RunningScale { get; set; } = 1;

    /// <summary>
    /// A screen size was chosen (null for Windows' own), and saved: it's taken on the next time the app starts.
    /// </summary>
    public event Action<float?>? ScreenSizeChosen;

    /// <summary>
    /// The scale a screen size chosen is drawn at: as chosen, unless the screen hasn't room for it.
    /// </summary>
    public float Drawn(float? chosen) => ScreenScale.Use(chosen, WindowsScale, LargestScale);

    public OptionsDialog(Root root, Fonts fonts, ContextMenu menu, AppSettings settings)
    {
        _root = root;
        _settings = settings;
        Visible = false;
        Background = Theme.Scrim(0.6f);

        _box = Add(new Panel { Padding = 14, Spacing = 10, Background = Theme.PanelRaised, BorderColor = Theme.PanelBorder, BorderWidth = 1 });
        _box.Add(new Label(fonts.Heading, "Options") { Color = Theme.Text });
        var row = _box.Add(new Row(8));
        row.Add(new Label(fonts.Body, "DJ software:") { Color = Theme.Muted });
        _dj = row.Add(
            new Dropdown<DjSoftware>(fonts.Body)
            {
                Menu = menu,
                Width = 160,
                Items = () => Enum.GetValues<DjSoftware>(),
                Text = AppSettings.NameOf,
                Tooltip = "Whose music library DJ Edit and DJ Perform Mode read",
            }
        );
        _dj.Chosen += d =>
        {
            if (d == _settings.DjSoftware)
                return;
            _settings.DjSoftware = d;
            DjSoftwareChanged?.Invoke(d);
        };
        _hint = _box.Add(new Label(fonts.Body) { Color = Theme.Muted, MaxWidth = WIDTH - 28, Wrap = true });

        // The look, which the screen takes on as it's built, so a new one shows the next time the app starts.
        var lookRow = _box.Add(new Row(8));
        lookRow.Add(new Label(fonts.Body, "Look:") { Color = Theme.Muted });
        var look = lookRow.Add(
            new Dropdown<ThemeKind>(fonts.Body)
            {
                Menu = menu,
                Width = 160,
                Items = () => [ThemeKind.Dark, ThemeKind.Light, ThemeKind.HighContrast],
                Text = Theme.NameOf,
                Selected = Theme.Kind,
                Tooltip = "Dark for dark rooms, Light for daylight, High contrast for bright rooms",
            }
        );
        _lookHint = _box.Add(new Label(fonts.Body, "") { Color = Theme.Muted, MaxWidth = WIDTH - 28, Wrap = true });
        look.Chosen += kind =>
        {
            _settings.Theme = kind;
            _lookHint.Text = kind == Theme.Kind ? "" : $"Cuedeck will look {Theme.NameOf(kind).ToLowerInvariant()} the next time it starts.";
        };

        // How big everything's drawn, also taken on as the app starts.
        var sizeRow = _box.Add(new Row(8));
        sizeRow.Add(new Label(fonts.Body, "Screen size:") { Color = Theme.Muted });
        _size = sizeRow.Add(
            new Dropdown<float?>(fonts.Body)
            {
                Menu = menu,
                Width = 260,
                Items = () => [null, .. ScreenScale.Choices.Select(choice => (float?)choice)],
                Text = scale => ScreenScale.Name(scale, WindowsScale, LargestScale),
                Selected = settings.ScreenScale,
                Tooltip = "Everything drawn bigger, for a 4K screen or one seen from further away",
            }
        );
        _restartNote = _box.Add(new Label(fonts.Body, "Restart Cuedeck to see it.") { Color = Theme.Warning, Visible = false });
        _size.Chosen += scale =>
        {
            _settings.ScreenScale = scale;
            ScreenSizeChosen?.Invoke(scale);
        };
        _slight = _box.Add(new Panel { Spacing = 6 });
        var slightRow = _slight.Add(new Row(8));
        slightRow.Add(new Label(fonts.Body, "sLight:") { Color = Theme.Muted });
        var import = slightRow.Add(new Button(fonts.Body, "Import from sLight") { Tooltip = "Copy sLight's settings and custom fixtures into Cuedeck" });
        import.Clicked += _ =>
        {
            Close();
            ImportFromSlight?.Invoke();
        };
        _slight.Add(
            new Label(
                fonts.Body,
                "Copies sLight's custom fixture profiles and their models, its settings, remembered folders and preset, and "
                    + "the show it kept untitled, into Cuedeck's folder. Anything Cuedeck already has is kept, and sLight's "
                    + "files aren't changed."
            )
            {
                Color = Theme.Muted,
                MaxWidth = WIDTH - 28,
                Wrap = true,
            }
        );
        var footer = _box.Add(new Row(8));
        var close = footer.Add(new Button(fonts.Body, "Close"));
        close.Clicked += _ => Close();

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

    public void Show()
    {
        Visible = true;
        _root.Focus(this);
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        _root.Focus(null);
    }

    public override float2 Measure()
    {
        if (Visible && _root.Focused is null)
            _root.Focus(this); // back on the dialog once a menu's gone, so Escape still reaches it
        _dj.Selected = _settings.DjSoftware;
        _slight.Visible = SlightFolders.Count > 0;
        _restartNote.Visible = Drawn(_size.Selected) != RunningScale;
        _hint.Text = _settings.DjSoftware == DjSoftware.Cuepoint
            ? "Songs' beat grids, keys and tempos come from Cuepoint's library files (%LOCALAPPDATA%\\Cuepoint\\library.json, and "
                + "\\Cuepoint\\library.json on each drive), and DJ Edit Mode lists its virtual folders (the .m3u8 files in "
                + "Cuepoint\\VirtualFolders). DJ Perform Mode follows Cuepoint's decks over OS2L."
            : "Songs' beat grids, keys and tempos come from VirtualDJ's databases (database.xml in Documents\\VirtualDJ, and "
                + "\\VirtualDJ on each drive), and DJ Edit Mode lists its Virtual Folders (MyLists). DJ Perform Mode follows "
                + "VirtualDJ's decks over OS2L.";
        _box.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(float2.Round(position + float2.Max(float2.Zero, (ScreenSize - _box.Size) / 2)));
    }
}

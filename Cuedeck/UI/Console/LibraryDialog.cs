using System.Globalization;
using Brutal.Numerics;
using Cuedeck.Fixtures;

namespace Cuedeck.UI.Console;

/// <summary>
/// Where the library dialog's fixtures come from.
/// </summary>
enum LibrarySource
{
    Bundled,
    OflOnline,
    GdtfShare,
}

/// <summary>
/// The fixture library, browsed over the dimmed screen: the source, search and filters across the top, the fixtures
/// found on the left, the chosen one's modes and channels on the right, and Add to Patch, which hands its profile back
/// to patch. Double-clicking a fixture adds it too. Escape, Close or a click outside closes it.
/// <para>
/// Besides the bundled library, it lists the current Open Fixture Library from GitHub, or GDTF-Share once logged in
/// (the login's asked for in a box of its own, and kept in memory only). An online fixture's downloaded when it's
/// chosen. The fetching runs in the background; <see cref="Update"/> picks up the results.
/// </para>
/// </summary>
sealed class LibraryDialog : Panel
{
    const float WIDTH = 940;
    const float HEIGHT = 620;
    const float LIST_WIDTH = 520;

    static readonly Dictionary<string, string> CategoryNames = new()
    {
        ["moving_head"] = "Moving head",
        ["wash"] = "Wash",
        ["par"] = "Par / colour",
        ["beam"] = "Beam",
        ["strip"] = "Strip / bar",
        ["blinder"] = "Blinder / effect",
        ["generic"] = "Other",
    };

    readonly Root _root;
    readonly Fonts _fonts;
    readonly ContextMenu _menu;
    readonly FixtureLibrary _library;
    readonly OnlineLibrary _online;
    readonly Panel _box;
    readonly Dropdown<LibrarySource> _source;
    readonly TextBox _search;
    readonly Dropdown<string> _maker;
    readonly Dropdown<string> _category;
    readonly Checkbox _panTilt;
    readonly Label _maxLabel;
    readonly NumberBox _maxChannels;
    readonly ScrollPanel _resultsScroll;
    readonly ScrollPanel _previewScroll;
    readonly Panel _preview;
    readonly Label _count;
    readonly Button _clearCache;
    readonly Button _add;
    readonly Panel _login;
    readonly TextBox _user;
    readonly TextBox _password;
    readonly Label _loginError;
    readonly Button _logIn;
    Action<FixtureProfile> _chosen = _ => { };
    bool _pressedInBox;
    string _filters = "";
    FixtureProfile? _profile;
    LibraryEntry? _previewed;
    int _previewMode;

    // Background work: fetching a source's list, logging in, downloading the chosen fixture.
    Task<bool>? _fetching;
    Task<bool>? _loggingIn;
    Task<FixtureProfile?>? _downloading;
    string _downloadingId = "";

    public float2 ScreenSize { get; set; }

    public ListView<LibraryEntry> Results { get; }

    public LibraryDialog(Root root, Fonts fonts, ContextMenu menu, FixtureLibrary library, OnlineLibrary online)
    {
        _root = root;
        _fonts = fonts;
        _menu = menu;
        _library = library;
        _online = online;
        Visible = false;
        Background = Colors.Hex(0x000000, 0.6f);

        _box = Add(new Panel { Padding = 14, Spacing = 10, Background = Theme.PanelRaised, BorderColor = Theme.PanelBorder, BorderWidth = 1 });
        _box.Add(new Label(fonts.Heading, "Fixture Library") { Color = Theme.Text });
        var filters = _box.Add(new Row(8));
        _source = filters.Add(
            new Dropdown<LibrarySource>(fonts.Body)
            {
                Menu = menu,
                Width = 140,
                Items = () => [LibrarySource.Bundled, LibrarySource.OflOnline, LibrarySource.GdtfShare],
                Text = s =>
                    s switch
                    {
                        LibrarySource.OflOnline => "OFL online",
                        LibrarySource.GdtfShare => "GDTF-Share",
                        _ => "Bundled library",
                    },
                Selected = LibrarySource.Bundled,
            }
        );
        _source.Chosen += SetSource;
        _search = filters.Add(new TextBox(fonts.Body) { Width = 220, Placeholder = "Search manufacturer / model…" });
        _maker = filters.Add(new Dropdown<string>(fonts.Body) { Menu = menu, Width = 170, Items = Makers, Text = m => m == "" ? "All manufacturers" : m, Selected = "" });
        _category = filters.Add(
            new Dropdown<string>(fonts.Body)
            {
                Menu = menu,
                Width = 130,
                Items = () => ["", .. library.Categories],
                Text = c => c == "" ? "All types" : CategoryNames.GetValueOrDefault(c, c),
                Selected = "",
            }
        );
        _panTilt = filters.Add(new Checkbox(fonts.Body, "Pan/Tilt only"));
        _maxLabel = filters.Add(new Label(fonts.Body, "Max ch:") { Color = Theme.Muted });
        _maxChannels = filters.Add(new NumberBox(fonts.Body, 0, 512, width: 44) { Tooltip = "0 = any width" });

        var split = _box.Add(new Row(12));
        Results = new ListView<LibraryEntry>(fonts.Body) { EmptyText = "No fixtures match" };
        Results.Columns.Add(new ListColumn<LibraryEntry>("Model", 210, e => e.Model) { Resizable = false });
        Results.Columns.Add(new ListColumn<LibraryEntry>("Manufacturer", 150, e => e.Maker) { Color = _ => Theme.Muted, Resizable = false });
        Results.Columns.Add(new ListColumn<LibraryEntry>("Details", 140, Details) { Color = _ => Theme.Muted, Resizable = false });
        Results.ItemActivated += _ => Add();
        _resultsScroll = split.Add(new ScrollPanel { Background = Theme.Well, MaxWidth = LIST_WIDTH, MinSize = new float2(LIST_WIDTH, 0) });
        _resultsScroll.Add(Results);
        Results.ScrollOwner = _resultsScroll;
        _previewScroll = split.Add(new ScrollPanel { Padding = 4, MaxWidth = WIDTH - LIST_WIDTH - 40, MinSize = new float2(WIDTH - LIST_WIDTH - 40, 0) });
        _preview = _previewScroll.Add(new Panel { Spacing = 6 });

        var footer = _box.Add(new Row(8));
        _count = footer.Add(new Label(fonts.Small) { Color = Theme.Muted, MaxWidth = WIDTH - 360 });
        footer.Add(new Spacer(10));
        _clearCache = footer.Add(new Button(fonts.Body, "Clear cache") { Tooltip = "Delete this source's downloaded fixtures", Visible = false });
        _clearCache.Clicked += _ => ClearCache();
        _add = footer.Add(new Button(fonts.Bold, "Add to Patch") { Tooltip = "Make it the profile to patch (or double-click it)" });
        _add.Clicked += _ => Add();
        var close = footer.Add(new Button(fonts.Body, "Close"));
        close.Clicked += _ => Close();

        // GDTF-Share's login, over the dialog.
        _login = Add(new Panel { Padding = 14, Spacing = 8, Background = Theme.PanelRaised, BorderColor = Theme.Accent, BorderWidth = 1, Visible = false });
        _login.Add(new Label(fonts.Heading, "Log in to GDTF-Share") { Color = Theme.Text });
        _login.Add(
            new Label(fonts.Body, "Your gdtf-share.com account. Used only for this session — never saved to disk; log in again next launch.")
            {
                Color = Theme.Muted,
                MaxWidth = 340,
                Wrap = true,
            }
        );
        _user = _login.Add(new TextBox(fonts.Body) { Width = 340, Placeholder = "Username" });
        _password = _login.Add(new TextBox(fonts.Body) { Width = 340, Placeholder = "Password", Secret = true });
        _password.Submitted += _ => LogIn();
        _loginError = _login.Add(new Label(fonts.Body, "") { Color = Colors.Hex(0xFF8C8C), MaxWidth = 340, Wrap = true });
        var loginButtons = _login.Add(new Row(8));
        _logIn = loginButtons.Add(new Button(fonts.Bold, "Log In"));
        _logIn.Clicked += _ => LogIn();
        var cancelLogin = loginButtons.Add(new Button(fonts.Body, "Cancel"));
        cancelLogin.Clicked += _ => BackToBundled();

        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
        BuildPreview();
    }

    public LibrarySource Source => _source.Selected;

    IReadOnlyList<string> Makers() =>
        [
            "",
            .. _source.Selected switch
            {
                LibrarySource.OflOnline => _online.OflManufacturers,
                LibrarySource.GdtfShare => _online.GdtfManufacturers,
                _ => _library.Manufacturers,
            },
        ];

    /// <summary>
    /// "8 / 16 ch · P/T" for a bundled fixture; an online one's version, and whether it's bundled too.
    /// </summary>
    static string Details(LibraryEntry e)
    {
        if (e.Online)
            return string.Join("  · ", new[] { e.Gdtf ? $"v{(e.Version != "" ? e.Version : "?")}" : "", e.Bundled ? "bundled" : "" }.Where(s => s != ""));
        return string.Join(" / ", e.Modes.Select(m => m.Channels.ToString(CultureInfo.InvariantCulture)).Distinct()) + " ch" + (e.PanTilt ? "  · P/T" : "");
    }

    public override bool IsInteractive => true;
    public override Cursor Cursor => Cursor.Arrow;
    public override bool IsFocusable => Visible;

    public override void PointerDown(float2 point, Modifiers modifiers) => _pressedInBox = _box.Contains(point) || _login.Visible && _login.Contains(point);

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (key != Key.Escape)
            return false;
        if (_login.Visible)
            BackToBundled();
        else
            Close();
        return true;
    }

    /// <summary>
    /// Opens it; Add to Patch hands the chosen fixture's profile (its own copy) to <paramref name="chosen"/>.
    /// </summary>
    public void Show(Action<FixtureProfile> chosen)
    {
        _chosen = chosen;
        Visible = true;
        _root.Focus(_login.Visible ? _user : _search);
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        _root.Focus(null);
    }

    void Add()
    {
        if (_profile is null)
            return;
        _chosen(_profile.Clone());
        Close();
    }

    /// <summary>
    /// Changes source: an online one's list is fetched first if it hasn't been, and GDTF-Share asks to log in.
    /// </summary>
    void SetSource(LibrarySource source)
    {
        _source.Selected = source;
        _search.Text = "";
        _maker.Selected = "";
        var online = source != LibrarySource.Bundled;
        _category.Visible = _panTilt.Visible = _maxLabel.Visible = _maxChannels.Visible = !online;
        _clearCache.Visible = online;
        _filters = ""; // list again
        _login.Visible = false;
        if (source == LibrarySource.GdtfShare && !_online.GdtfLoggedIn)
        {
            ShowMessage("Log in to GDTF-Share to browse…");
            _loginError.Text = "";
            _password.Text = "";
            _login.Visible = true;
            _root.Focus(_user);
            return;
        }
        if (source == LibrarySource.GdtfShare && !_online.GdtfReady)
        {
            ShowMessage("Fetching the GDTF-Share catalogue…", "contacting GDTF-Share…");
            _fetching = _online.RefreshGdtf();
        }
        else if (source == LibrarySource.OflOnline && !_online.OflReady)
        {
            ShowMessage("Fetching the OFL catalogue from GitHub…", "contacting GitHub…");
            _fetching = _online.RefreshOfl();
        }
    }

    void ShowMessage(string message, string count = "")
    {
        Results.Items = [];
        Results.EmptyText = message;
        _count.Text = count;
        _source.Enabled = !message.StartsWith("Fetching", StringComparison.Ordinal);
    }

    void BackToBundled()
    {
        _login.Visible = false;
        _password.Text = "";
        SetSource(LibrarySource.Bundled);
        _root.Focus(_search);
    }

    void LogIn()
    {
        if (_loggingIn is not null)
            return;
        _loginError.Text = "Logging in…";
        _logIn.Enabled = false;
        // The password goes straight into the request and is cleared from the box.
        _loggingIn = _online.GdtfLogin(_user.Text, _password.Text);
        _password.Text = "";
    }

    void ClearCache()
    {
        if (_source.Selected == LibrarySource.GdtfShare)
            _online.ClearGdtfCache();
        else
            _online.ClearOflCache();
        _filters = "";
        _previewed = null;
    }

    /// <summary>
    /// Brings the results up to date with the filters and the background work, and the preview with the choice. Call
    /// once a frame while it shows.
    /// </summary>
    public void Update()
    {
        if (!Visible)
            return;
        var source = _source.Selected;

        if (_loggingIn is { IsCompleted: true } login)
        {
            _loggingIn = null;
            _logIn.Enabled = true;
            if (login.Result)
            {
                _login.Visible = false;
                SetSource(LibrarySource.GdtfShare);
            }
            else
                _loginError.Text = _online.GdtfError;
        }
        if (_fetching is { IsCompleted: true } fetch)
        {
            _fetching = null;
            _source.Enabled = true;
            if (!fetch.Result)
            {
                ShowMessage(source == LibrarySource.GdtfShare ? $"Couldn't reach GDTF-Share: {_online.GdtfError}" : $"Couldn't reach OFL: {_online.OflError}");
                _source.Enabled = true;
            }
            else
                _filters = "";
        }
        var waiting = _fetching is not null || _login.Visible || source != LibrarySource.Bundled && !(source == LibrarySource.GdtfShare ? _online.GdtfReady : _online.OflReady);

        var filters = $"{source}|{_search.Text}|{_maker.Selected}|{_category.Selected}|{_panTilt.Checked}|{_maxChannels.Value}";
        if (!waiting && filters != _filters)
        {
            _filters = filters;
            Results.EmptyText = "No fixtures match";
            var rows = source switch
            {
                LibrarySource.OflOnline => _online.SearchOfl(_search.Text, _maker.Selected),
                LibrarySource.GdtfShare => _online.SearchGdtf(_search.Text, _maker.Selected),
                _ => _library.Search(_search.Text, _maker.Selected, _category.Selected, (int)_maxChannels.Value, _panTilt.Checked),
            };
            Results.Items = rows;
            _resultsScroll.ScrollTo(float2.Zero);
            _count.Text = source switch
            {
                LibrarySource.OflOnline => $"{rows.Count} of {_online.OflCount} OFL fixtures · {_online.CachedOflCount} cached",
                LibrarySource.GdtfShare => $"{rows.Count} of {_online.GdtfCount} GDTF-Share fixtures · {_online.CachedGdtfCount} cached · logged in as {_online.GdtfUser}",
                // The source note starts with the count, said already.
                _ => _library.Available ? $"{rows.Count} of {_library.Count} fixtures · {string.Join(" · ", _library.SourceNote.Split(" · ").Skip(1))}" : "No bundled library found.",
            };
        }

        var chosen = Results.SelectedItem;
        if (chosen != _previewed)
        {
            _previewed = chosen;
            _previewMode = 0;
            _profile = null;
            _downloading = null;
            if (chosen is not null)
            {
                _profile = chosen.Online ? _online.Resolve(chosen.Id) : _library.Resolve(chosen.Id);
                if (_profile is null && chosen.Online)
                {
                    _downloadingId = chosen.Id;
                    _downloading = _online.Download(chosen.Id);
                }
            }
            BuildPreview();
        }
        if (_downloading is { IsCompleted: true } download)
        {
            _downloading = null;
            if (_previewed?.Id == _downloadingId)
            {
                _profile = download.Result;
                BuildPreview();
            }
        }
        _add.Enabled = _profile is not null;
    }

    void BuildPreview()
    {
        _preview.Children.Clear();
        if (_previewed is { } pending && _profile is null)
        {
            var why = pending.Gdtf ? _online.GdtfError : _online.OflError;
            _preview.Add(
                new Label(_fonts.Body, _downloading is not null ? "Downloading…" : $"Couldn't load this fixture.\n{why}") { Color = Theme.Muted, MaxWidth = 340, Wrap = true }
            );
            return;
        }
        if (_previewed is not { } e || _profile is not { } p)
        {
            _preview.Add(new Label(_fonts.Body, "Choose a fixture to see its modes and channels.") { Color = Theme.Faint, MaxWidth = 340, Wrap = true });
            return;
        }
        _preview.Add(new Label(_fonts.Heading, p.Model) { Color = Theme.Text, MaxWidth = 360, Wrap = true });
        var sub = string.Join(" · ", new[] { p.Manufacturer }.Where(s => s != "").Concat(e.OflCategories));
        var authors = string.Join(", ", e.Authors.Where(a => a != ""));
        if (authors != "")
            sub += (sub != "" ? "   ·   " : "") + authors;
        if (sub != "")
            _preview.Add(new Label(_fonts.Small, sub) { Color = Theme.Muted, MaxWidth = 360, Wrap = true });
        if (e.Online)
        {
            var from = e.Gdtf ? "GDTF-Share" : "Open Fixture Library";
            var text = e.Bundled ? $"bundled + fetched from {(e.Gdtf ? "GDTF-Share" : "OFL")}" : $"fetched from {from}";
            _preview.Add(new Label(_fonts.Body, text) { Color = Colors.Hex(0x8CCCFF) });
        }
        if (p.Physical.Heads.Count >= 2)
            _preview.Add(new Label(_fonts.Body, $"{p.Physical.Heads.Count} heads") { Color = Theme.Text });
        if (e.Approximated > 0)
            _preview.Add(new Label(_fonts.Body, $"{e.Approximated} channel{(e.Approximated == 1 ? "" : "s")} approximated on import") { Color = Colors.Hex(0xFFCC66) });

        var modeRow = _preview.Add(new Row(8));
        modeRow.Add(new Label(_fonts.Body, "Mode:") { Color = Theme.Muted });
        var mode = modeRow.Add(
            new Dropdown<int>(_fonts.Body)
            {
                Menu = _menu,
                Width = 260,
                Items = () => [.. Enumerable.Range(0, p.ModeCount)],
                Text = m => $"{p.ModeNames[m]} ({p.ChannelCount(m)} ch)",
                Selected = _previewMode,
            }
        );
        mode.Chosen += m =>
        {
            _previewMode = m;
            BuildPreview();
        };
        var chans = p.Channels(_previewMode);
        for (var i = 0; i < chans.Count; i++)
        {
            var ch = chans[i];
            var text = $"{i + 1,2}   {ch.Name}" + (ch.Role != Roles.Generic ? $"   · {ch.Role.ToLowerInvariant()}" : "");
            _preview.Add(new Label(_fonts.Small, text) { Color = ch.Fine ? Theme.Faint : Theme.Text });
        }
    }

    public override float2 Measure()
    {
        var listHeight = HEIGHT - 150;
        _resultsScroll.MaxHeight = listHeight;
        _resultsScroll.MinSize = new float2(LIST_WIDTH, listHeight);
        _previewScroll.MaxHeight = listHeight;
        _previewScroll.MinSize = new float2(WIDTH - LIST_WIDTH - 40, listHeight);
        _box.Measure();
        _login.Measure();
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(float2.Round(position + float2.Max(float2.Zero, (ScreenSize - _box.Size) / 2)));
        _login.Arrange(float2.Round(position + float2.Max(float2.Zero, (ScreenSize - _login.Size) / 2)));
    }
}

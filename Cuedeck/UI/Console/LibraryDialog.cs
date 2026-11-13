using System.Globalization;
using Brutal.Numerics;
using Cuedeck.Fixtures;
using Cuedeck.Input;

namespace Cuedeck.UI.Console;

/// <summary>
/// The fixture library, browsed over the dimmed screen: search and filters across the top, the fixtures found on the
/// left, the chosen one's modes and channels on the right, and Add to Patch, which hands its profile back to patch.
/// Double-clicking a fixture adds it too. Escape, Close or a click outside closes it.
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
    readonly Panel _box;
    readonly TextBox _search;
    readonly Dropdown<string> _maker;
    readonly Dropdown<string> _category;
    readonly Checkbox _panTilt;
    readonly NumberBox _maxChannels;
    readonly ScrollPanel _resultsScroll;
    readonly ScrollPanel _previewScroll;
    readonly Panel _preview;
    readonly Label _count;
    readonly Button _add;
    Action<FixtureProfile> _chosen = _ => { };
    bool _pressedInBox;
    string _filters = "";
    FixtureProfile? _profile;
    LibraryEntry? _previewed;
    int _previewMode;

    public float2 ScreenSize { get; set; }

    public ListView<LibraryEntry> Results { get; }

    public LibraryDialog(Root root, Fonts fonts, ContextMenu menu, FixtureLibrary library)
    {
        _root = root;
        _fonts = fonts;
        _menu = menu;
        _library = library;
        Visible = false;
        Background = Colors.Hex(0x000000, 0.6f);

        _box = Add(new Panel { Padding = 14, Spacing = 10, Background = Theme.PanelRaised, BorderColor = Theme.PanelBorder, BorderWidth = 1 });
        _box.Add(new Label(fonts.Heading, "Fixture Library") { Color = Theme.Text });
        var filters = _box.Add(new Row(8));
        _search = filters.Add(new TextBox(fonts.Body) { Width = 240, Placeholder = "Search manufacturer / model…" });
        _maker = filters.Add(
            new Dropdown<string>(fonts.Body) { Menu = menu, Width = 190, Items = () => ["", .. library.Manufacturers], Text = m => m == "" ? "All manufacturers" : m, Selected = "" }
        );
        _category = filters.Add(
            new Dropdown<string>(fonts.Body)
            {
                Menu = menu,
                Width = 150,
                Items = () => ["", .. library.Categories],
                Text = c => c == "" ? "All types" : CategoryNames.GetValueOrDefault(c, c),
                Selected = "",
            }
        );
        _panTilt = filters.Add(new Checkbox(fonts.Body, "Pan/Tilt only"));
        filters.Add(new Label(fonts.Body, "Max ch:") { Color = Theme.Muted });
        _maxChannels = filters.Add(new NumberBox(fonts.Body, 0, 512, width: 44) { Tooltip = "0 = any width" });

        var split = _box.Add(new Row(12));
        Results = new ListView<LibraryEntry>(fonts.Body) { EmptyText = "No fixtures match" };
        Results.Columns.Add(new ListColumn<LibraryEntry>("Model", 210, e => e.Model) { Resizable = false });
        Results.Columns.Add(new ListColumn<LibraryEntry>("Manufacturer", 150, e => e.Maker) { Color = _ => Theme.Muted, Resizable = false });
        Results.Columns.Add(new ListColumn<LibraryEntry>("Channels", 140, Channels) { Color = _ => Theme.Muted, Resizable = false });
        Results.ItemActivated += _ => Add();
        _resultsScroll = split.Add(new ScrollPanel { Background = Theme.Well, MaxWidth = LIST_WIDTH, MinSize = new float2(LIST_WIDTH, 0) });
        _resultsScroll.Add(Results);
        Results.ScrollOwner = _resultsScroll;
        _previewScroll = split.Add(new ScrollPanel { Padding = 4, MaxWidth = WIDTH - LIST_WIDTH - 40, MinSize = new float2(WIDTH - LIST_WIDTH - 40, 0) });
        _preview = _previewScroll.Add(new Panel { Spacing = 6 });

        var footer = _box.Add(new Row(8));
        _count = footer.Add(new Label(fonts.Small) { Color = Theme.Muted, MaxWidth = WIDTH - 260 });
        footer.Add(new Spacer(10));
        _add = footer.Add(new Button(fonts.Bold, "Add to Patch") { Tooltip = "Make it the profile to patch (or double-click it)" });
        _add.Clicked += _ => Add();
        var close = footer.Add(new Button(fonts.Body, "Close"));
        close.Clicked += _ => Close();

        Clicked += _ =>
        {
            if (!_pressedInBox)
                Close();
        };
        BuildPreview();
    }

    /// <summary>
    /// "8 / 16 ch" and "P/T" for one that pans and tilts.
    /// </summary>
    static string Channels(LibraryEntry e) =>
        string.Join(" / ", e.Modes.Select(m => m.Channels.ToString(CultureInfo.InvariantCulture)).Distinct()) + " ch" + (e.PanTilt ? "  · P/T" : "");

    public override bool IsInteractive => true;
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
    /// Opens it; Add to Patch hands the chosen fixture's profile (its own copy) to <paramref name="chosen"/>.
    /// </summary>
    public void Show(Action<FixtureProfile> chosen)
    {
        _chosen = chosen;
        Visible = true;
        _root.Focus(_search);
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
    /// Brings the results up to date with the filters, and the preview with the choice. Call once a frame while it shows.
    /// </summary>
    public void Update()
    {
        if (!Visible)
            return;
        var filters = $"{_search.Text}|{_maker.Selected}|{_category.Selected}|{_panTilt.Checked}|{_maxChannels.Value}";
        if (filters != _filters)
        {
            _filters = filters;
            var rows = _library.Search(_search.Text, _maker.Selected, _category.Selected, (int)_maxChannels.Value, _panTilt.Checked);
            Results.Items = rows;
            _resultsScroll.ScrollTo(float2.Zero);
            // The source note starts with the count, said already.
            var source = string.Join(" · ", _library.SourceNote.Split(" · ").Skip(1));
            _count.Text = _library.Available ? $"{rows.Count} of {_library.Count} fixtures · {source}" : "No bundled library found.";
        }
        var chosen = Results.SelectedItem;
        if (chosen != _previewed)
        {
            _previewed = chosen;
            _previewMode = 0;
            _profile = chosen is null ? null : _library.Resolve(chosen.Id);
            BuildPreview();
        }
        _add.Enabled = _profile is not null;
    }

    void BuildPreview()
    {
        _preview.Children.Clear();
        if (_previewed is not { } e || _profile is not { } p)
        {
            _preview.Add(new Label(_fonts.Body, "Choose a fixture to see its modes and channels.") { Color = Theme.Faint, MaxWidth = 340, Wrap = true });
            return;
        }
        _preview.Add(new Label(_fonts.Heading, p.Model) { Color = Theme.Text, MaxWidth = 360, Wrap = true });
        var sub = string.Join(" · ", new[] { p.Manufacturer }.Where(s => s != "").Concat(e.OflCategories));
        if (e.Authors.Count > 0)
            sub += (sub != "" ? "   ·   " : "") + string.Join(", ", e.Authors);
        if (sub != "")
            _preview.Add(new Label(_fonts.Small, sub) { Color = Theme.Muted, MaxWidth = 360, Wrap = true });
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
        return Size = ScreenSize;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        _box.Arrange(float2.Round(position + float2.Max(float2.Zero, (ScreenSize - _box.Size) / 2)));
    }
}

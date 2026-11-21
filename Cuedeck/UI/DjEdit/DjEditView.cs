using Brutal.Numerics;
using Cuedeck.Dj;
using Cuedeck.Music;
using Cuedeck.Timelines;
using Cuedeck.UI.Timeline;

namespace Cuedeck.UI.DjEdit;

/// <summary>
/// A row of DJ Edit Mode's folder tree: a drive or folder (its subfolders read as it's opened), the DJ software's lists
/// heading and its groups, one of its lists, or a note.
/// </summary>
sealed class FolderNode(string name)
{
    public enum Kinds
    {
        Drive,
        Folder,
        ListsHeading,
        Group,
        List,
        Note,
    }

    public string Name { get; } = name;
    public Kinds Kind { get; init; }
    public string Path { get; init; } = "";
    public DjList? List { get; init; }
    public List<FolderNode> Items { get; } = [];
    List<FolderNode>? _subfolders;

    /// <summary>
    /// Its children: a folder's subfolders, read the first time they're asked for; the lists' groups and lists.
    /// </summary>
    public IReadOnlyList<FolderNode> Children =>
        Kind is Kinds.Drive or Kinds.Folder
            ? _subfolders ??= [.. MusicFolders.Subfolders(Path).Select(n => new FolderNode(n) { Kind = Kinds.Folder, Path = Common.Gd.PathJoin(Path, n) })]
            : Items;

    public bool CanExpand => Kind is Kinds.Drive or Kinds.Folder ? _subfolders is null || _subfolders.Count > 0 : Items.Count > 0;

    /// <summary>
    /// Whether selecting it lists music.
    /// </summary>
    public bool Lists => Kind is Kinds.Drive or Kinds.Folder or Kinds.List;
}

/// <summary>
/// DJ Edit Mode: a narrow column on the left (the drives and folders, then the DJ software's lists; the song open in
/// the timeline, a click to go back to it), and filling the rest, the music in what's selected there, or the open
/// song's timeline. Double-click a song to open its timeline; right-click it to open its timeline, show it in the file
/// explorer, copy its path, regenerate the lighting timelines of it (and every other selected song), or remove its
/// timelines. A song with lighting timelines in the show has a node-line mark before its title (orange for temporary
/// ones). The DJ software's library and lists are read the first time the view's shown. sLight's
/// <c>MusicBrowserPanel</c>.
/// </summary>
sealed class DjEditView : Region
{
    const float LEFT_WIDTH = 260;
    const float GAP = 8;

    readonly Fonts _fonts;
    readonly Images _images;
    readonly ContextMenu _menu;
    readonly ConfirmDialog _confirm;
    readonly Label _foldersHead;
    readonly ScrollPanel _treeScroll;
    readonly Label _timelineHead;
    readonly Button _songRow;
    readonly Label _heading;
    readonly ListHeader<MusicRow> _header;
    readonly ScrollPanel _listScroll;
    readonly Dictionary<MusicColumn, ListColumn<MusicRow>> _columns = [];
    readonly Label _positionsHead;
    readonly Button _addPosition;
    readonly ScrollPanel _positionsScroll;
    MusicRow? _openRow;
    bool _treeBuilt;

    public DjEditView(
        Fonts fonts,
        Images images,
        ContextMenu menu,
        ConfirmDialog confirm,
        SongTimelines timelines,
        TimelineView timeline,
        MusicBrowser browser,
        PositionEditor editor
    )
    {
        _fonts = fonts;
        _images = images;
        _menu = menu;
        _confirm = confirm;
        Timelines = timelines;
        Browser = browser;
        Timeline = timeline;

        _foldersHead = Add(new Label(fonts.Bold, "Folders") { Color = Theme.Text });
        _treeScroll = Add(new ScrollPanel { Background = Theme.Well });
        Tree = new TreeView<FolderNode>(fonts.Body)
        {
            Text = n => n.Name,
            Children = n => n.Children,
            CanExpand = n => n.CanExpand,
            Icon = n =>
                n.Kind switch
                {
                    FolderNode.Kinds.Drive => (images.Folders.Drive, Theme.Text),
                    FolderNode.Kinds.Folder => (images.Folders.Folder, Theme.Text),
                    FolderNode.Kinds.List => (images.Folders.Playlist, Theme.Text),
                    FolderNode.Kinds.Group or FolderNode.Kinds.ListsHeading => (images.Folders.List, Theme.Muted),
                    _ => null,
                },
            Arrows = (images.TriangleRight, images.TriangleDown),
            TextColor = Theme.Text,
            ScrollOwner = _treeScroll,
        };
        _treeScroll.Add(Tree);
        Tree.SelectionChanged += FolderSelected;
        // Clicking the folder that's selected already still leaves a song's timeline for the list.
        Tree.Clicked += _ =>
        {
            if (Tree.SelectedItem is { Lists: true })
                ShowTrackList();
        };

        _timelineHead = Add(new Label(fonts.Bold, "Timeline") { Color = Theme.Text });
        _songRow = Add(new Button(fonts.Body, "No song open") { TextIndent = images.Timeline.Size.X + 2 });
        _songRow.Clicked += _ => ShowTimeline();

        // The named positions, under their header, its "+" adding one.
        Editor = editor;
        Positions = editor.Library;
        _positionsHead = Add(new Label(fonts.Bold, "Positions") { Color = Theme.Text });
        _addPosition = Add(new Button(fonts.Bold, "+") { Padding = 2, MinSize = new float2(26, 0), Tooltip = "Add a new position" });
        _addPosition.Clicked += _ => AddPosition();
        _positionsScroll = Add(new ScrollPanel { Background = Theme.Well });
        PositionList = new PositionList(fonts.Body, Positions);
        _positionsScroll.Add(PositionList);
        PositionList.Picked += OpenPosition;
        PositionList.DeleteRequested += ConfirmDeletePosition;
        PositionList.ContextRequested += (id, at) => _menu.Show(at, PositionMenu(id));
        Positions.Changed += () =>
        {
            // Its position gone (deleted, another show loaded): back to the list.
            if (IsPositionShown && Positions.Find(Editor.PositionId) is null)
                ShowTrackList();
        };

        _heading = Add(new Label(fonts.Bold, "") { Color = Theme.Text });
        List = new ListView<MusicRow>(fonts.Body) { EmptyText = "No music here", TextColor = Theme.Text };
        AddColumns();
        _header = Add(new ListHeader<MusicRow>(List));
        _header.ColumnClicked += column =>
        {
            var which = _columns.First(kv => kv.Value == column).Key;
            Browser.SortBy(which, which == Browser.SortColumn ? !Browser.SortAscending : true);
        };
        _listScroll = Add(new ScrollPanel());
        _listScroll.Add(List);
        List.ScrollOwner = _listScroll;
        List.ItemActivated += OpenTrack;
        List.ItemsSecondaryClicked += (rows, at) => _menu.Show(at, TrackMenu(rows));
        List.RowProgress = r => Browser.Progress(r.Path) is { } f ? (float)f : null;
        List.ProgressColor = AppTheme.RegenerateProgress;
        Add(timeline);
        timeline.Visible = false;
        Add(editor);
        editor.Visible = false;

        browser.RowsChanged += () =>
        {
            List.Items = browser.Rows;
            _header.SortColumn = _columns[browser.SortColumn];
            _header.SortDescending = !browser.SortAscending;
        };
        ShowTrackList();
    }

    public SongTimelines Timelines { get; }
    public MusicBrowser Browser { get; }
    public TimelineView Timeline { get; }
    public TreeView<FolderNode> Tree { get; }
    public ListView<MusicRow> List { get; }

    /// <summary>
    /// The heading over the list (its folder or list), or over the open song's timeline (the song).
    /// </summary>
    public string Heading => _heading.Text;

    /// <summary>
    /// The Timeline section's row: the open song, a click to go back to it.
    /// </summary>
    public Button SongRow => _songRow;

    public bool IsTimelineShown => Timeline.Visible;

    public PositionEditor Editor { get; }
    public Show.PositionLibrary Positions { get; }
    public PositionList PositionList { get; }
    public Button AddPositionButton => _addPosition;

    /// <summary>
    /// Whether a position's open in the editor (rather than the list or a timeline).
    /// </summary>
    public bool IsPositionShown => Editor.Visible;

    void AddColumns()
    {
        float4? Missing(MusicRow r) => r.Missing ? AppTheme.OnPage(AppTheme.MusicMissing) : null;
        void Column(MusicColumn c, string title, float width, Func<MusicRow, string> text, bool right = false)
        {
            var col = new ListColumn<MusicRow>(title, width, text) { AlignRight = right, SortKey = _ => 0, Color = Missing };
            if (c == MusicColumn.Title)
                col = new ListColumn<MusicRow>(title, width, text)
                {
                    SortKey = _ => 0,
                    Color = Missing,
                    Icon = r => (_images.Timeline, Browser.HasTimelinesMark(r.Path) ? Browser.IsTemporaryMark(r.Path) ? AppTheme.TemporaryMark : AppTheme.TimelineMark : Colors.Transparent),
                };
            _columns[c] = col;
            List.Columns.Add(col);
        }
        Column(MusicColumn.Num, "#", 48, r => r.Num.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
        Column(MusicColumn.Title, "Title", 260, r => r.Title);
        Column(MusicColumn.Artist, "Artist", 170, r => r.Artist);
        Column(MusicColumn.Bpm, "BPM", 64, r => r.Bpm > 0 ? r.Bpm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "", true);
        Column(MusicColumn.Key, "Key", 56, r => r.Key, true);
        Column(MusicColumn.Length, "Length", 64, r => r.Length > 0 ? MusicBrowser.FormatTime(r.Length) : "", true);
        Column(MusicColumn.Album, "Album", 160, r => r.Album);
        Column(MusicColumn.Genre, "Genre", 100, r => r.Genre);
        Column(MusicColumn.File, "File", 220, r => r.File);
    }

    // ------------------------------------------------------------- loading --

    /// <summary>
    /// Builds the folder tree (and reads the DJ software's library and lists) if that hasn't happened yet: when the view
    /// is first shown.
    /// </summary>
    /// <summary>
    /// The folder tree's top level, by name and path: this machine's drives (the screenshot tool's demo folder
    /// instead).
    /// </summary>
    public Func<IEnumerable<(string Name, string Path)>> Drives { get; set; } = () => MusicFolders.Drives().Select(d => (d.TrimEnd('/'), d));

    public void EnsureLoaded()
    {
        if (_treeBuilt)
            return;
        _treeBuilt = true;
        var roots = Drives().Select(d => new FolderNode(d.Name) { Kind = FolderNode.Kinds.Drive, Path = d.Path }).ToList();
        var library = Browser.Library;
        var lists = new FolderNode($"{library.SoftwareName} Lists") { Kind = FolderNode.Kinds.ListsHeading };
        if (library.Lists.Count == 0)
            lists.Items.Add(new FolderNode("(none found)") { Kind = FolderNode.Kinds.Note });
        var groups = new Dictionary<string, FolderNode> { [""] = lists };
        FolderNode Group(string g)
        {
            if (groups.TryGetValue(g, out var found))
                return found;
            var slash = g.LastIndexOf('/');
            var parent = Group(slash >= 0 ? g[..slash] : "");
            var item = new FolderNode(g[(slash + 1)..]) { Kind = FolderNode.Kinds.Group };
            parent.Items.Add(item);
            return groups[g] = item;
        }
        foreach (var list in library.Lists)
            Group(list.Group).Items.Add(new FolderNode(list.Name) { Kind = FolderNode.Kinds.List, Path = list.Path, List = list });
        roots.Add(lists);
        Tree.Roots = roots;
        if (!IsTimelineShown && !IsPositionShown)
            _heading.Text = ListHeading(); // it names the DJ software now that's known
    }

    /// <summary>
    /// The DJ software setting changed: its library and lists are read again, now if the view's showing.
    /// </summary>
    public void ReloadLibrary()
    {
        Browser.ReloadLibrary();
        _treeBuilt = false;
        List.Items = [];
        if (!IsTimelineShown)
            ShowTrackList();
        if (Visible)
            EnsureLoaded();
    }

    void FolderSelected(FolderNode node)
    {
        if (node.Kind is FolderNode.Kinds.Drive or FolderNode.Kinds.Folder)
            ShowFolder(node.Path);
        else if (node.List is { } list)
            ShowList(list);
    }

    public void ShowFolder(string path)
    {
        EnsureLoaded();
        Browser.ShowFolder(path);
        ShowTrackList();
        _listScroll.ScrollTo(float2.Zero);
    }

    public void ShowList(DjList list)
    {
        EnsureLoaded();
        Browser.ShowList(list);
        ShowTrackList();
        _listScroll.ScrollTo(float2.Zero);
    }

    // ------------------------------------------------------------ switching --

    /// <summary>
    /// Opens <paramref name="row"/>'s timeline in place of the list.
    /// </summary>
    public void OpenTrack(MusicRow row)
    {
        _openRow = row;
        ShowTimelineView();
        Timeline.Open(row.Path, Browser.Library.FindSong(row.Path));
    }

    /// <summary>
    /// Back to the open song's timeline (the Timeline section's row). Returns whether one's open.
    /// </summary>
    public bool ShowTimeline()
    {
        if (_openRow is null || Timelines.Path == "")
            return false;
        ShowTimelineView();
        return true;
    }

    void ShowTimelineView()
    {
        CloseEditor();
        Timeline.Visible = true;
        _heading.Visible = true;
        _heading.Text = _openRow?.Heading ?? "";
    }

    void CloseEditor()
    {
        Editor.Visible = false;
        Editor.Showing = false;
        PositionList.SelectedId = "";
    }

    /// <summary>
    /// Back from a song's timeline to the track list.
    /// </summary>
    public void ShowTrackList()
    {
        if (Timeline.Visible)
            Timeline.Pause();
        Timeline.Visible = false;
        CloseEditor();
        _heading.Visible = true;
        _heading.Text = ListHeading();
    }

    /// <summary>
    /// The view's been hidden (another view chosen): its song pauses and is saved, and the editor's fixtures are put
    /// back.
    /// </summary>
    public void Hidden()
    {
        Timeline.Hidden();
        Editor.Showing = false;
    }

    // ------------------------------------------------------------ positions --

    /// <summary>
    /// Opens position <paramref name="id"/> in the editor, in place of the list or timeline, selected in the list.
    /// </summary>
    public void OpenPosition(string id)
    {
        if (Timeline.Visible)
            Timeline.Pause();
        Timeline.Visible = false;
        _heading.Visible = false; // the editor has its name
        Editor.Visible = true;
        Editor.Open(id);
        Editor.Showing = Visible;
        PositionList.SelectedId = id;
    }

    /// <summary>
    /// The Positions header's "+": adds one, and opens it.
    /// </summary>
    public void AddPosition() => OpenPosition(Positions.Add().Id);

    /// <summary>
    /// A position's right-click menu: Rename (opens it, its name selected to type over), Duplicate (a copy after it,
    /// opened) and Delete… (asks first).
    /// </summary>
    public List<MenuItem> PositionMenu(string id) =>
        [
            new("Rename", () =>
            {
                OpenPosition(id);
                if (Root is { } root)
                    Editor.FocusName(root);
            }),
            new("Duplicate", () =>
            {
                if (Positions.Duplicate(id) is { } copy)
                    OpenPosition(copy.Id);
            }),
            new("Delete…", () => ConfirmDeletePosition(id)),
        ];

    /// <summary>
    /// The root the view's in, for focusing the name (set by whoever adds it).
    /// </summary>
    public Root? Root { get; set; }

    /// <summary>
    /// Asks, then deletes a named position.
    /// </summary>
    public void ConfirmDeletePosition(string id)
    {
        if (Positions.Find(id) is not { } p)
            return;
        _confirm.Show("Delete Position", $"Delete the position \"{p.Name}\"?", "Delete", () => Positions.Remove(id));
    }

    /// <summary>
    /// <paramref name="text"/>, or as much of it as fits in <paramref name="width"/> with an ellipsis.
    /// </summary>
    string Fit(string text, float width)
    {
        var font = _fonts.Body;
        if (font.Measure(text).X <= width)
            return text;
        while (text.Length > 1 && font.Measure(text + "…").X > width)
            text = text[..^1];
        return text.TrimEnd() + "…";
    }

    string ListHeading() =>
        Browser.ListHeading != "" ? Browser.ListHeading : $"Select a folder or a {(Browser.IsLoaded ? Browser.Library.SoftwareName : "DJ software")} list.";

    // ------------------------------------------------------------ track menu --

    /// <summary>
    /// The right-click menu for <paramref name="rows"/> (the selected tracks, the one clicked first): Open Timeline,
    /// Show in File Explorer and Copy Path for that one, Regenerate Lighting Timelines for them all, and Remove
    /// Lighting Timelines… for it if it has some.
    /// </summary>
    public List<MenuItem> TrackMenu(IReadOnlyList<MusicRow> rows)
    {
        if (rows.Count == 0)
            return [];
        var row = rows[0];
        var regenerable = Browser.Regenerable(rows.Select(r => r.Path));
        var n = regenerable.Count;
        var saved = Timelines.Store is not null;
        return
        [
            new("Open Timeline", () => OpenTrack(row), !row.Missing),
            new("Show in File Explorer", () => ShowInExplorer(row.Path), !row.Missing),
            new("Copy Path", () => Clipboard.SetText(row.Path)),
            new($"Regenerate Lighting Timelines{(n > 1 ? $" ({n} songs)" : "")}", () => Browser.Regenerate(regenerable), n > 0 && saved),
            new("Remove Lighting Timelines…", () => ConfirmRemoveTimelines(row), Browser.HasTimelinesMark(row.Path)),
        ];
    }

    static void ShowInExplorer(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path.Replace('/', System.IO.Path.DirectorySeparatorChar)}\"");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>
    /// Asks, then deletes a song's lighting timelines from the show.
    /// </summary>
    public void ConfirmRemoveTimelines(MusicRow row) =>
        _confirm.Show(
            "Remove Lighting Timelines",
            $"Remove the lighting timelines for \"{row.Title}\" from this show? This can't be undone.",
            "Remove",
            () => Browser.RemoveTimelines(row.Path)
        );

    // --------------------------------------------------------------- per frame --

    public void Update()
    {
        if (Visible)
            EnsureLoaded();
        Browser.Update();
        if (Timeline.Visible)
            Timeline.Update();
        else
            Timelines.Update();
        var open = _openRow is not null && Timelines.Path != "";
        var temporary = open && Timelines.Store?.IsTemporary(Timelines.Path) == true;
        // Each line cut short to fit the column (the full title's in the tooltip).
        var room = LEFT_WIDTH - _songRow.TextIndent - 2 * _songRow.Padding - 4;
        _songRow.Text = open ? Fit(_openRow!.SongTitle, room) + (_openRow.Details != "" ? "\n" + Fit(_openRow.Details, room) : "") : "No song open";
        _songRow.Tooltip = open ? _openRow!.Heading : "Double-click a song in the list to open its timeline";
        _songRow.Enabled = open;
        _songRow.Lit = open && Timeline.Visible;
        Editor.Showing = Visible && Editor.Visible;
        _songMark = temporary ? AppTheme.TemporaryMark : AppTheme.TimelineMark;
    }

    float4 _songMark = AppTheme.TimelineMark;

    // ----------------------------------------------------------------- layout --

    protected override void MeasureContent()
    {
        var (w, h) = (Available.X, Available.Y);
        _foldersHead.Measure();
        _timelineHead.Measure();
        _songRow.MinSize = new float2(LEFT_WIDTH, 0);
        _songRow.Measure();
        _positionsHead.Measure();
        _addPosition.Measure();
        // The folders take three quarters of what's left, the positions the rest (at least 110).
        var room = Math.Max(h - _foldersHead.Size.Y - _timelineHead.Size.Y - _songRow.Size.Y - _positionsHead.Size.Y - 4 * 4 - 2 * GAP, 170);
        var posH = Math.Max(room / 4, 110);
        var treeH = Math.Max(room - posH, 60);
        PositionList.Width = LEFT_WIDTH - 2;
        _positionsScroll.MinSize = new float2(LEFT_WIDTH, posH);
        _positionsScroll.MaxWidth = LEFT_WIDTH;
        _positionsScroll.MaxHeight = posH;
        _positionsScroll.Measure();
        _treeScroll.MinSize = new float2(LEFT_WIDTH, treeH);
        _treeScroll.MaxWidth = LEFT_WIDTH;
        _treeScroll.MaxHeight = treeH;
        Tree.MinWidth = LEFT_WIDTH - 12;
        _treeScroll.Measure();
        var rightW = Math.Max(w - LEFT_WIDTH - GAP, 200);
        _heading.Measure();
        var below = h - _heading.Size.Y - 6;
        Timeline.Available = new float2(rightW, below);
        Timeline.Measure();
        Editor.Available = new float2(rightW, h);
        Editor.Measure();
        // The title column takes what the others leave.
        var others = List.Columns.Where(c => c != _columns[MusicColumn.Title]).Sum(c => c.Width);
        _columns[MusicColumn.Title].Width = Math.Max(160, rightW - others - _listScroll.ScrollbarHitWidth - 2);
        _header.Measure();
        var listH = Math.Max(below - _header.Size.Y, 40);
        _listScroll.MinSize = new float2(rightW, listH);
        _listScroll.MaxWidth = rightW;
        _listScroll.MaxHeight = listH;
        _listScroll.Measure();
        _header.Visible = _listScroll.Visible = !Timeline.Visible && !Editor.Visible;
    }

    public override void Arrange(float2 position)
    {
        ArrangeSelf(position);
        var y = position.Y;
        _foldersHead.Arrange(new float2(position.X, y));
        y += _foldersHead.Size.Y + 4;
        _treeScroll.Arrange(new float2(position.X, y));
        y += _treeScroll.Size.Y + GAP;
        _timelineHead.Arrange(new float2(position.X, y));
        y += _timelineHead.Size.Y + 4;
        _songRow.Arrange(new float2(position.X, y));
        y += _songRow.Size.Y + GAP;
        _positionsHead.Arrange(new float2(position.X, y));
        _addPosition.Arrange(new float2(position.X + LEFT_WIDTH - _addPosition.Size.X, y - 2));
        y += _positionsHead.Size.Y + 4;
        _positionsScroll.Arrange(new float2(position.X, y));
        var x = position.X + LEFT_WIDTH + GAP;
        _heading.Arrange(new float2(x, position.Y));
        var top = position.Y + _heading.Size.Y + 6;
        Timeline.Arrange(new float2(x, top));
        Editor.Arrange(new float2(x, position.Y));
        _header.Arrange(new float2(x - _listScroll.ScrollOffset.X, top));
        _listScroll.Arrange(new float2(x, top + _header.Size.Y));
    }

    public override void Draw(Batch batch)
    {
        batch.AddRect(ScreenPosition, Size, Theme.Background);
        foreach (var child in Children)
            if (child.Visible)
                child.Draw(batch);
        // The Timeline section's mark, before the song's name.
        var icon = _images.Timeline;
        var at = _songRow.ScreenPosition + new float2(_songRow.Padding + 2, (_songRow.Size.Y - icon.Size.Y) / 2);
        batch.AddImage(icon, at, _songRow.Enabled ? _songMark : Theme.Fade(_songMark, 0.4f));
    }
}

using System.Text.Json.Nodes;
using Brutal.Numerics;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Dj;
using Cuedeck.Fixtures;
using Cuedeck.Input;
using Cuedeck.Music;
using Cuedeck.Show;
using Cuedeck.Timelines;
using Cuedeck.UI;
using Cuedeck.UI.Console;
using Cuedeck.UI.DjEdit;
using Cuedeck.UI.Timeline;

namespace Cuedeck.Tests.UI;

/// <summary>
/// DJ Edit Mode: music_browser_test.gd (the folder listing, the panel and its switching between the list and a
/// song's timeline, the timelines marks, the track menu, regenerating songs' timelines and the view switch), and the
/// show's timelines folder following its file (sLight's shell). The DJ software's library is stood in for, so nothing
/// on this machine is read but the test's own folders.
/// </summary>
public sealed class DjEditTests : IDisposable
{
    static readonly float2 ScreenSize = new(1400, 800);

    readonly TempFolder _dir = new();
    readonly Root _ui = new();
    readonly ContextMenu _menu = new(MainScreenTests.TestFonts.Body);
    readonly ConfirmDialog _confirm;
    readonly SongTimelines _tl;
    readonly MusicBrowser _browser;
    readonly DjEditView _view;
    readonly LightingShow _show;
    readonly PositionEditor _editor;
    DjLibrary _library = DjLibrary.Empty();
    Func<string, SongAnalysis> _analyse = _ => throw new InvalidDataException("not analysed here");

    public DjEditTests()
    {
        var fonts = MainScreenTests.TestFonts;
        _confirm = new ConfirmDialog(_ui, fonts.Body, fonts.Bold, fonts.Heading);
        _tl = new SongTimelines { AnalyseStructure = (path, _) => _analyse(path) };
        var timeline = new TimelineView(fonts, _menu, null, _tl, new FakePlayer());
        _browser = new MusicBrowser(_tl, () => _library);
        _show = new LightingShow();
        _editor = new PositionEditor(fonts, _menu, _show.Positions, _show.Patch);
        _view = _ui.Add(new DjEditView(fonts, MainScreenTests.Pictures, _menu, _confirm, _tl, timeline, _browser, _editor) { Root = _ui });
        _ui.Add(_confirm);
        _ui.Add(_menu);
    }

    public void Dispose()
    {
        _show.Dispose();
        _dir.Dispose();
    }

    void Frame()
    {
        _view.Available = ScreenSize;
        _confirm.ScreenSize = _menu.ScreenSize = ScreenSize;
        _view.Update();
        _ui.Layout(ScreenSize);
    }

    void Touch(string path, int seed = 1, int size = 100)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. Enumerable.Range(0, size).Select(i => (byte)(i * seed))]);
    }

    /// <summary>
    /// A folder with audio files (in a muddle of cases and numbers), others that aren't, and subfolders.
    /// </summary>
    string Fixture()
    {
        var root = _dir.File("music").Replace('\\', '/');
        foreach (var f in new[] { "Track 10.mp3", "Track 2.M4A", "song.flac", "notes.txt", "cover.jpg", "Album B/x.mp3", "album a/y.wav", "Album B/deeper/z.ogg" })
            Touch(Path.Combine(root, f));
        return root;
    }

    static SongAnalysis Structure() =>
        new()
        {
            Duration = 60,
            Bpm = 120,
            BeatTimes = [.. Enumerable.Range(0, 120).Select(i => i * 0.5f)],
            Downbeats = [.. Enumerable.Range(0, 30).Select(i => i * 2f)],
            Sections = [new(0, 16, "Intro", 0.2, 0), new(16, 44, "Chorus", 0.9, 1), new(44, 60, "Outro", 0.3, 2)],
        };

    // ------------------------------------------------------------ the folders --

    [Fact]
    public void Folder_listing()
    {
        var root = Fixture();
        Assert.Equal(["song.flac", "Track 2.M4A", "Track 10.mp3"], MusicFolders.AudioFiles(root)); // audio only, any case, natural order
        Assert.Equal(["album a", "Album B"], MusicFolders.Subfolders(root));
        Assert.Empty(MusicFolders.AudioFiles(root + "/nope"));
    }

    [Fact]
    public void The_panel()
    {
        var root = Fixture();
        _view.EnsureLoaded();
        var top = _view.Tree.Roots;
        Assert.Equal(MusicFolders.Drives().Count + 1, top.Count); // a row a drive, then the DJ software's lists
        Assert.Equal("VirtualDJ Lists", top[^1].Name);
        Assert.Equal("(none found)", Assert.Single(top[^1].Children).Name);
        // Folders read as they're opened.
        var item = new FolderNode("music") { Kind = FolderNode.Kinds.Folder, Path = root };
        Assert.Equal(["album a", "Album B"], item.Children.Select(c => c.Name));
        var b = item.Children[1];
        Assert.Equal("deeper", Assert.Single(b.Children).Name);
        Assert.Empty(item.Children[0].Children);
        Assert.False(item.Children[0].CanExpand); // a folder without subfolders ends up empty

        // A folder lists its music.
        _view.ShowFolder(root);
        Frame();
        var rows = _browser.Rows;
        Assert.Equal(["song", "Track 2", "Track 10"], rows.Select(r => r.Title)); // titles from the file names
        Assert.All(rows, r => Assert.False(r.Missing));
        Assert.Equal(3, _view.List.Items.Count);
        Assert.True(_view.Heading.StartsWith(root, StringComparison.Ordinal) && _view.Heading.Contains("3 tracks"));
        var listHeading = _view.Heading;
        Assert.True(_view.SongRow.Text == "No song open" && !_view.SongRow.Enabled && !_view.ShowTimeline());

        // A track opened: its song's name over its timeline, and the Timeline section shows it.
        _view.OpenTrack(rows[1]);
        Frame();
        Assert.True(_view.IsTimelineShown && _view.Heading == "Track 2");
        Assert.True(_view.SongRow.Text.StartsWith("Track 2", StringComparison.Ordinal) && _view.SongRow.Enabled && _view.SongRow.Lit);
        var go = rows[1] with { Title = "Go", Artist = "Band", Bpm = 128, Key = "Am", Length = 225 };
        Assert.Equal("Go — Band · 128.0 BPM · Am · 3:45", go.Heading);
        _view.OpenTrack(go);
        Frame();
        Assert.Equal("Go — Band\n128.0 BPM · Am · 3:45", _view.SongRow.Text); // its details on a second line
        // Back to the list: its heading again; and back to the song.
        _view.ShowTrackList();
        Frame();
        Assert.True(!_view.IsTimelineShown && _view.Heading == listHeading && !_view.SongRow.Lit);
        Assert.True(_view.ShowTimeline() && _view.IsTimelineShown && _view.Timeline.CurrentPath == rows[1].Path);
        _view.ShowTrackList();

        // Sorting.
        _browser.SortBy(MusicColumn.Title, false);
        Assert.Equal(["Track 10", "Track 2", "song"], _browser.Rows.Select(r => r.Title));
        _browser.SortBy(MusicColumn.Num, true);
        Assert.Equal([1, 2, 3], _browser.Rows.Select(r => r.Num));
    }

    [Fact]
    public void The_DJ_softwares_lists_and_details()
    {
        var root = Fixture();
        var song = new DjSong { Path = root + "/Track 2.M4A", Title = "Two", Artist = "Band", ScanBpm = 124, Key = "Am", Length = 200 };
        var list = new DjList { Name = "Gig", Group = "Gigs/2026", Path = "gig.vdjfolder" };
        list.Entries.AddRange([new DjListEntry { Path = song.Path }, new DjListEntry { Path = root + "/gone.mp3", Title = "Gone", Bpm = 100 }]);
        _library = new DjLibrary(p => p == song.Path ? song : null, [list], "VirtualDJ");
        _view.EnsureLoaded();
        var lists = _view.Tree.Roots[^1];
        var gigs = Assert.Single(lists.Children);
        Assert.Equal("Gigs", gigs.Name);
        Assert.Equal("Gig", Assert.Single(Assert.Single(gigs.Children).Children).Name); // its group's folders
        _view.ShowList(list);
        var rows = _browser.Rows;
        Assert.Equal(("Two", "Band", 124.0, "Am"), (rows[0].Title, rows[0].Artist, rows[0].Bpm, rows[0].Key)); // the library's values
        Assert.True(rows[1].Title == "Gone" && rows[1].Bpm == 100 && rows[1].Missing); // the list's own, the file gone
        Assert.Equal("Gig  (VirtualDJ list) — 2 tracks · 3 min · 1 missing", _view.Heading);
        Assert.True(_view.TrackMenu([rows[1]]).All(i => i.Text is "Copy Path" or "Regenerate Lighting Timelines" or "Remove Lighting Timelines…" || !i.Enabled)); // a missing file: no opening it
    }

    // ------------------------------------------------------------ the marks --

    [Fact]
    public void The_timelines_marks()
    {
        var dir = _dir.File("icons").Replace('\\', '/');
        var songs = dir + "/songs";
        Touch(songs + "/a.mp3", 3, 120000); // saved here
        Touch(songs + "/b.mp3", 5, 130000); // saved elsewhere, since moved here
        Touch(songs + "/c.mp3", 7, 140000); // none
        Touch(songs + "/d.mp3", 9, 130000); // none, but b's size
        Touch(dir + "/old/b.mp3", 5, 130000);
        var store = new TimelineStore(dir + "/show.timelines");
        store.SaveSong(songs + "/a.mp3", new JsonObject { ["tracks"] = new JsonObject { ["master"] = new JsonObject { ["dimmer"] = new JsonArray(new JsonArray(1.0, 0.5)) } } });
        store.SaveSong(dir + "/old/b.mp3", new JsonObject { ["tracks"] = new JsonObject { ["master"] = new JsonObject { ["dimmer"] = new JsonArray(new JsonArray(2.0, 0.5)) } } });
        File.Delete(dir + "/old/b.mp3");

        _tl.SetTimelineStore(new TimelineStore(dir + "/show.timelines"));
        _view.ShowFolder(songs);
        Assert.True(_browser.HasTimelinesMark(songs + "/a.mp3")); // from the index, straight away
        Assert.False(_browser.HasTimelinesMark(songs + "/c.mp3")); // no indexed song's size: unmarked without reading it
        Assert.False(_browser.MarksDone); // same-size songs checked in the background
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!_browser.MarksDone && DateTime.UtcNow < until)
            Frame();
        Assert.True(_browser.HasTimelinesMark(songs + "/b.mp3")); // moved: found by fingerprint
        Assert.False(_browser.HasTimelinesMark(songs + "/d.mp3")); // same size, another song
        var title = _view.List.Columns[1];
        var (img, tint) = title.Icon!(_browser.RowFor(songs + "/a.mp3")!)!.Value;
        Assert.True(img.Size.X == 16 + Images.TIMELINE_GAP && tint == Theme.TimelineMark); // the node line before its title
        Assert.Equal(Colors.Transparent, title.Icon(_browser.RowFor(songs + "/c.mp3")!)!.Value.Tint); // a blank, so titles line up
        // Saving a song's timelines marks it.
        _tl.Open(songs + "/c.mp3");
        _tl.Master.DimmerNodes = [new System.Numerics.Vector2(1, 0.5f)];
        _tl.MarkEdited();
        _tl.Flush();
        Assert.True(_browser.HasTimelinesMark(songs + "/c.mp3"));
        // The track menu; Remove Lighting Timelines asks, then deletes and unmarks.
        var menu = _view.TrackMenu([_browser.RowFor(songs + "/a.mp3")!]);
        Assert.Equal(["Open Timeline", "Show in File Explorer", "Copy Path", "Regenerate Lighting Timelines", "Remove Lighting Timelines…"], menu.Select(i => i.Text));
        menu[^1].Chosen();
        Frame();
        Assert.True(_confirm.Visible && _browser.HasTimelinesMark(songs + "/a.mp3")); // nothing removed until it's confirmed
        Assert.Contains(All(_confirm).OfType<Label>(), l => l.Text.Contains("\"a\""));
        Frame();
        var remove = All(_confirm).OfType<Button>().Single(b => b.Text == "Remove");
        _ui.MouseMove(remove.ScreenPosition + remove.Size / 2);
        _ui.MouseButton(MouseButton.Left, true);
        _ui.MouseButton(MouseButton.Left, false);
        Assert.False(_browser.HasTimelinesMark(songs + "/a.mp3"));
        var fresh = new TimelineStore(dir + "/show.timelines");
        Assert.True(!fresh.HasSong(songs + "/a.mp3") && fresh.HasSong(songs + "/b.mp3"));
        Assert.False(_view.TrackMenu([_browser.RowFor(songs + "/a.mp3")!])[^1].Enabled); // nothing to remove now
        // Another show's folder: its own marks (none).
        _tl.SetTimelineStore(new TimelineStore(dir + "/other.timelines"));
        Assert.True(!_browser.HasTimelinesMark(songs + "/b.mp3") && _browser.MarksDone);
    }

    // ------------------------------------------------------------ regenerating --

    [Fact]
    public void Regenerating_songs_timelines_from_the_list()
    {
        var dir = _dir.File("regen").Replace('\\', '/');
        var songs = dir + "/songs";
        var p = Enumerable.Range(0, 4).Select(k => $"{songs}/s{k}.mp3").ToList();
        for (var k = 0; k < 4; k++)
            Touch(p[k], k + 1, 100000 + k * 1000);
        _view.ShowFolder(songs);
        var rows = _browser.Rows;
        // No show saved yet: nowhere to keep them.
        Assert.False(_view.TrackMenu([rows[0]])[3].Enabled);
        Assert.Equal(0, _browser.Regenerate([p[0]]));
        _tl.SetTimelineStore(new TimelineStore(dir + "/show.timelines"));
        for (var k = 0; k < 3; k++)
            _tl.AddStructure(p[k], Structure(), null); // analysed already, this session
        var item = _view.TrackMenu([.. rows.Take(3)])[3];
        Assert.True(item.Text == "Regenerate Lighting Timelines (3 songs)" && item.Enabled);
        _browser.Batch.Workers = 2;
        var most = 0;
        var saw = new Dictionary<string, double>();
        _browser.Batch.Progress += (path, f) =>
        {
            most = Math.Max(most, _browser.Batch.Running);
            saw[path] = Math.Max(saw.GetValueOrDefault(path, -1), f);
        };
        item.Chosen();
        Assert.True(_browser.Batch.IsBusy && _browser.Progress(p[2]) is not null); // under way or queued, each with a bar
        var until = DateTime.UtcNow.AddSeconds(20);
        while (_browser.Batch.IsBusy && DateTime.UtcNow < until)
            Frame();
        var store = new TimelineStore(dir + "/show.timelines");
        for (var k = 0; k < 3; k++)
            Assert.True(store.LoadSong(p[k]).Obj("tracks").Obj("master").Arr("dimmer").Count > 0 && _browser.HasTimelinesMark(p[k]));
        Assert.False(store.HasSong(p[3])); // an unselected one left alone
        Assert.True(most <= 2 && saw.Count == 3 && saw.Values.All(f => f == 1.0));
        Assert.Null(_browser.Progress(p[0])); // the bars go when they're done
        // Opened later: its new show, and an undo back to nothing.
        _view.OpenTrack(rows[0]);
        _tl.SetDuration(60);
        Assert.True(_tl.CanUndo && _tl.Master.DimmerNodes.Count > 0);
        _tl.Undo();
        Assert.Empty(_tl.Master.DimmerNodes);
        _tl.Redo();
        // Not analysed, and it can't be (not really audio): it says why.
        _view.ShowTrackList();
        _browser.Regenerate([p[3]]);
        until = DateTime.UtcNow.AddSeconds(20);
        while (_browser.Batch.IsBusy && DateTime.UtcNow < until)
            Frame();
        Assert.True(_browser.RegenerateError(p[3]) is { Length: > 0 } && !_browser.HasTimelinesMark(p[3]));
        // Another show: anything queued is dropped.
        _browser.Batch.Workers = 1;
        _browser.Regenerate([p[1], p[2]]);
        _tl.SetTimelineStore(new TimelineStore(dir + "/other.timelines"));
        Assert.True(!_browser.Batch.IsBusy && _browser.Progress(p[2]) is null);
    }

    // -------------------------------------------------------- the view switch --

    [Fact]
    public void The_view_switch()
    {
        var fonts = MainScreenTests.TestFonts;
        var ui = new Root();
        var menu = new ContextMenu(fonts.Body);
        var show = new LightingShow();
        var tl = new SongTimelines();
        var loaded = false;
        var browser = new MusicBrowser(
            tl,
            () =>
            {
                loaded = true;
                return DjLibrary.Empty();
            }
        );
        var dj = new DjEditView(fonts, MainScreenTests.Pictures, menu, new ConfirmDialog(ui, fonts.Body, fonts.Bold, fonts.Heading), tl, new TimelineView(fonts, menu, null, tl, new FakePlayer()), browser, new PositionEditor(fonts, menu, show.Positions, show.Patch));
        var dialogs = new Dialogs(
            new ConfirmDialog(ui, fonts.Body, fonts.Bold, fonts.Heading),
            new NamePrompt(ui, fonts.Body, fonts.Bold, fonts.Heading),
            new ColourPicker(ui, fonts.Body),
            new LibraryDialog(ui, fonts, menu, MainScreenTests.Bundled, new OnlineLibrary(MainScreenTests.Bundled, _dir.File("online"), new FakeWeb())),
            new ProfileEditor(ui, fonts, menu),
            new TriggersDialog(ui, fonts, menu, show),
            new OptionsDialog(ui, fonts, menu, new AppSettings(_dir.File("settings.json")))
        );
        var screen = ui.Add(new MainScreen(fonts, MainScreenTests.Pictures, menu, dialogs, show, new ProfileStore(_dir.File("profiles")), dj));
        ui.Add(menu);
        void Frame()
        {
            screen.ScreenSize = menu.ScreenSize = new float2(1400, 800);
            screen.Update();
            ui.Layout(screen.ScreenSize);
        }
        Frame();
        Assert.True(screen.View == AppView.Console && screen.Console.Visible && !dj.Visible);
        Assert.False(loaded); // DJ Edit Mode isn't loaded until it's shown
        // The top bar's View dropdown.
        var view = All(screen).OfType<Dropdown<AppView>>().Single();
        ui.MouseMove(view.ScreenPosition + view.Size / 2);
        ui.MouseButton(MouseButton.Left, true);
        ui.MouseButton(MouseButton.Left, false);
        Frame();
        Assert.Equal(["• Console", "DJ Edit Mode"], menu.ItemTexts.Select(t => t.Trim()).Select(t => t == "Console" ? "• Console" : t));
        var choice = All(menu).OfType<Button>().Single(b => b.Text.Contains("DJ Edit Mode"));
        ui.MouseMove(choice.ScreenPosition + choice.Size / 2);
        ui.MouseButton(MouseButton.Left, true);
        ui.MouseButton(MouseButton.Left, false);
        Frame();
        Assert.True(screen.View == AppView.DjEdit && !screen.Console.Visible && dj.Visible && loaded);
        screen.SetView(AppView.Console);
        Frame();
        Assert.True(screen.Console.Visible && !dj.Visible);
    }

    static IEnumerable<Element> All(Element e)
    {
        yield return e;
        if (e is Panel p)
            foreach (var child in p.Children.Where(c => c.Visible))
                foreach (var d in All(child))
                    yield return d;
    }

    // ---------------------------------------------- the show's timelines folder --

    [Fact]
    public void A_shows_timelines_follow_its_file()
    {
        using var show = new LightingShow();
        var tl = new SongTimelines { AnalyseStructure = (_, _) => throw new InvalidDataException("no") };
        var session = new ShowSession(show) { Timelines = tl, LastShowStore = _dir.File("last.json") };
        var song = _dir.File("song.mp3").Replace('\\', '/');
        Touch(song, 3, 5000);
        Assert.Null(tl.Store); // never saved: nowhere for them
        // Saved: a folder beside the show, the open song's timelines in it.
        tl.Open(song);
        tl.Master.DimmerNodes = [new System.Numerics.Vector2(1, 0.5f)];
        tl.MarkEdited();
        var first = _dir.File("one.slight").Replace('\\', '/');
        Assert.Null(session.Save(first));
        Assert.Equal(TimelineStore.FolderForShow(first), tl.Store!.Dir);
        Assert.True(new TimelineStore(TimelineStore.FolderForShow(first)).HasSong(song));
        // Save As: the folder copied beside the new file, which the timelines go on in.
        var second = _dir.File("two.slight").Replace('\\', '/');
        Assert.Null(session.Save(second));
        Assert.Equal(TimelineStore.FolderForShow(second), tl.Store.Dir);
        Assert.True(new TimelineStore(TimelineStore.FolderForShow(second)).HasSong(song));
        // Loading the first: its folder (the song's timelines as saved there).
        tl.Master.DimmerNodes = [new System.Numerics.Vector2(2, 0.25f)];
        tl.MarkEdited();
        Assert.Null(session.Load(first));
        Assert.Equal(TimelineStore.FolderForShow(first), tl.Store.Dir);
        Assert.Equal(1, tl.Master.DimmerNodes[0].X); // and the second's edit was saved as it was left
        Assert.Equal(2, new TimelineStore(TimelineStore.FolderForShow(second)).LoadSong(song).Obj("tracks").Obj("master").Arr("dimmer")[0]![0]!.GetValue<double>());
        // A new show: nowhere for them until it's saved.
        session.New();
        Assert.Null(tl.Store);
    }

    [Fact]
    public void Generate_moves_around_the_shows_positions()
    {
        var lib = new PositionLibrary();
        var ids = SongTimelines.PositionsFrom(lib);
        var made = Assert.Single(ids());
        Assert.Equal("Centre", lib.Find(made.Id)!.Name); // none named: a Centre one, every fixture centred
        Assert.Equal(0, made.Spread);
        var wide = lib.Add();
        wide.Points["a"] = new System.Numerics.Vector2(0.4f, 0.5f);
        wide.Points["b"] = new System.Numerics.Vector2(0.6f, 0.5f);
        Assert.Equal(2, ids().Count); // with positions already: none added
        Assert.Equal(0.1, ids()[1].Spread, 5);
    }
}

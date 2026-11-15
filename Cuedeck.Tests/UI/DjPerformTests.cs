using System.Text.Json.Nodes;
using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.Music;
using Cuedeck.Perform;
using Cuedeck.Show;
using Cuedeck.Tests.Perform;
using Cuedeck.Timelines;
using Cuedeck.UI;
using Cuedeck.UI.Console;
using Cuedeck.UI.DjEdit;
using Cuedeck.UI.DjPerform;
using Cuedeck.UI.Timeline;

namespace Cuedeck.Tests.UI;

/// <summary>
/// DJ Perform Mode's view (the panel half of sLight's tests/dj_perform_test.gd): the connection and the decks, the live
/// flashes, and the view switch starting and stopping the performer. The OS2L server listens on loopback only, and
/// VirtualDJ's messages are handed to it directly.
/// </summary>
public sealed class DjPerformTests : IDisposable
{
    static readonly float2 ScreenSize = new(1400, 800);

    readonly TempFolder _dir = new();
    readonly Root _ui = new();
    readonly LightingShow _show = new();
    readonly DjPerformer _perf;
    readonly DjPerformView _view;

    public DjPerformTests()
    {
        var fonts = MainScreenTests.TestFonts;
        _perf = new DjPerformer(_show.Dmx) { BindAddress = "127.0.0.1", Port = 0, Advertise = false };
        _view = _ui.Add(new DjPerformView(fonts, _perf, _show.Flash, new ColourPicker(_ui, fonts.Body)));
    }

    public void Dispose()
    {
        _perf.Stop();
        _show.Dispose();
        _dir.Dispose();
    }

    void Frame()
    {
        _view.Available = ScreenSize;
        _view.Update();
        _ui.Layout(ScreenSize);
    }

    void Vdj(string trigger, JsonNode? value) => _perf.Os2l!.HandleMessage(new JsonObject { ["evt"] = "subscribed", ["trigger"] = trigger, ["value"] = value });

    void WaitLoads()
    {
        var t = System.Diagnostics.Stopwatch.StartNew();
        while (!_perf.LoadsDone() && t.ElapsedMilliseconds < 10000)
        {
            _perf.Update();
            Thread.Sleep(2);
        }
    }

    void Click(Element e, bool down = true, bool up = true)
    {
        _ui.MouseMove(e.ScreenPosition + e.Size / 2);
        if (down)
            _ui.MouseButton(MouseButton.Left, true);
        if (up)
            _ui.MouseButton(MouseButton.Left, false);
        Frame();
    }

    [Fact]
    public void The_panel_shows_the_connection_and_the_decks()
    {
        var song1 = _dir.File("one.song");
        var song2 = _dir.File("two.song");
        File.WriteAllBytes(song1, [1, 2, 3]);
        File.WriteAllBytes(song2, [4, 5, 6]);
        var store = new TimelineStore(_dir.File("show.timelines"));
        store.SaveSong(song1, Json.Parse("""{"tracks": {"master": {"dimmer": [[0.0, 0.25], [20.0, 0.25]]}}}""")!.AsObject());
        var fx = SongOutputTests.Fixtures();
        _perf.FixturesProvider = () => fx;
        _perf.StoreProvider = () => store;
        Frame();
        Assert.Equal("OS2L: off", _view.ConnectionText);
        _perf.Start();
        Frame();
        Assert.Contains($"listening on :{_perf.Os2l!.Port}", _view.ConnectionText);
        Assert.Contains("waiting for VirtualDJ", _view.ConnectionText);

        Vdj("deck 1 get_filepath", song1);
        Vdj("deck 1 level", 1.0);
        Vdj("deck 1 play", "on");
        Vdj("deck 2 get_filepath", song2);
        WaitLoads();
        Frame();
        var rows = _view.Rows;
        Assert.Equal(("playing", "one", "playing (1 track)"), (rows[0].State.Text, rows[0].Song.Text, rows[0].Timelines.Text));
        Assert.StartsWith("0:0", rows[0].Position.Text);
        Assert.Equal(("paused", "none for this song"), (rows[1].State.Text, rows[1].Timelines.Text));
        Assert.Equal(("not in the skin", ""), (rows[2].State.Text, rows[2].Song.Text)); // a deck the skin doesn't show
        Vdj("deck 1 level", 0.0);
        Frame();
        Assert.Equal(("playing (muted)", "ready (1 track)"), (rows[0].State.Text, rows[0].Timelines.Text)); // its fader down
        _perf.SetSkinDecks(4);
        Frame();
        Assert.Equal(("empty", ""), (rows[3].State.Text, rows[3].Song.Text));
        _perf.Stop();
        Frame();
        Assert.Equal("OS2L: off", _view.ConnectionText);
        Assert.Equal("empty", rows[0].State.Text);
    }

    [Fact]
    public void What_the_timelines_column_says()
    {
        var d = new Deck { Path = "x", Temporary = true, LoadState = LoadState.Loading };
        Assert.Equal("generating…", DjPerformView.TimelinesText(d));
        d.Temporary = false;
        Assert.Equal("loading…", DjPerformView.TimelinesText(d));
        d.LoadState = LoadState.Loaded;
        d.Tracks = new() { ["master"] = new(), ["a"] = new() };
        d.Temporary = true;
        Assert.Equal("ready (2 tracks, temporary)", DjPerformView.TimelinesText(d));
        d.Analysing = true;
        d.Progress = 0.42;
        Assert.Contains("on the beat only while the song's analysed — 42%", DjPerformView.TimelinesText(d));
        d.Analysing = false;
        d.Note = "not saved: the show hasn't been saved";
        Assert.Equal("ready (2 tracks, temporary) — not saved: the show hasn't been saved", DjPerformView.TimelinesText(d));
        d.LoadState = LoadState.NoTimelines;
        d.Note = "can't decode it";
        Assert.Equal("none for this song (couldn't generate: can't decode it)", DjPerformView.TimelinesText(d));
        d.LoadState = LoadState.NoFolder;
        Assert.Equal("show not saved (no timelines folder)", DjPerformView.TimelinesText(d));
    }

    [Fact]
    public void The_live_flashes()
    {
        Frame();
        var blackout = _view.FlashButtons[(int)FlashKind.Blackout];
        Assert.StartsWith("Blackout (B)", blackout.Text);
        Click(blackout, up: false); // held
        Assert.True(_show.Flash.IsOn(FlashKind.Blackout) && blackout.Lit);
        Click(blackout, down: false); // let go
        Assert.False(_show.Flash.IsOn(FlashKind.Blackout) || blackout.Lit);

        // Latch: a click turns one on, another off; turning Latch off lets go of them all.
        Click(_view.LatchBox);
        Assert.True(_show.Flash.Latch);
        var white = _view.FlashButtons[(int)FlashKind.White];
        Click(white);
        Assert.True(_show.Flash.IsOn(FlashKind.White) && white.Lit);
        Click(white);
        Assert.False(_show.Flash.IsOn(FlashKind.White));
        Click(white);
        Click(_view.LatchBox);
        Assert.False(_show.Flash.Latch || _show.Flash.AnyOn);

        // The keys: held while the view shows; let go in any view.
        Assert.True(_view.FlashKey(Key.S, true, Modifiers.None));
        Assert.True(_show.Flash.IsOn(FlashKind.Strobe));
        _view.Visible = false;
        Assert.False(_view.FlashKey(Key.C, true, Modifiers.None)); // not from another view
        Assert.True(_view.FlashKey(Key.S, false, Modifiers.None));
        Assert.False(_show.Flash.AnyOn);
        _view.Visible = true;
        Assert.False(_view.FlashKey(Key.S, true, Modifiers.Control)); // Ctrl+S saves
        Assert.False(_view.FlashKey(Key.A, true, Modifiers.None));
    }

    [Fact]
    public void The_view_switch_starts_and_stops_the_performer()
    {
        var fonts = MainScreenTests.TestFonts;
        var ui = new Root();
        var menu = new ContextMenu(fonts.Body);
        var tl = new SongTimelines();
        var dj = new DjEditView(
            fonts,
            MainScreenTests.Pictures,
            menu,
            new ConfirmDialog(ui, fonts.Body, fonts.Bold, fonts.Heading),
            tl,
            new TimelineView(fonts, menu, null, tl, new FakePlayer()),
            new MusicBrowser(tl, () => DjLibrary.Empty()),
            new PositionEditor(fonts, menu, _show.Positions, _show.Patch)
        );
        var picker = new ColourPicker(ui, fonts.Body);
        var perform = new DjPerformView(fonts, _perf, _show.Flash, picker);
        var dialogs = new Dialogs(
            new ConfirmDialog(ui, fonts.Body, fonts.Bold, fonts.Heading),
            new NamePrompt(ui, fonts.Body, fonts.Bold, fonts.Heading),
            picker,
            new LibraryDialog(ui, fonts, menu, MainScreenTests.Bundled, new OnlineLibrary(MainScreenTests.Bundled, _dir.File("online"), new FakeWeb())),
            new ProfileEditor(ui, fonts, menu),
            new TriggersDialog(ui, fonts, menu, _show),
            new OptionsDialog(ui, fonts, menu, new AppSettings(_dir.File("settings.json")))
        );
        var screen = ui.Add(new MainScreen(fonts, MainScreenTests.Pictures, menu, dialogs, _show, new ProfileStore(_dir.File("profiles")), dj, perform));
        ui.Add(menu);
        void Frame()
        {
            screen.ScreenSize = menu.ScreenSize = ScreenSize;
            screen.Update();
            ui.Layout(screen.ScreenSize);
        }
        Frame();
        Assert.False(perform.Visible || _perf.IsActive);
        // The top bar's View dropdown.
        var view = All(screen).OfType<Dropdown<AppView>>().Single();
        ui.MouseMove(view.ScreenPosition + view.Size / 2);
        ui.MouseButton(MouseButton.Left, true);
        ui.MouseButton(MouseButton.Left, false);
        Frame();
        Assert.Contains("DJ Perform Mode", menu.ItemTexts.Select(t => t.Trim()));
        var choice = All(menu).OfType<Button>().Single(b => b.Text.Contains("DJ Perform Mode"));
        ui.MouseMove(choice.ScreenPosition + choice.Size / 2);
        ui.MouseButton(MouseButton.Left, true);
        ui.MouseButton(MouseButton.Left, false);
        Frame();
        Assert.True(screen.View == AppView.DjPerform && perform.Visible && !screen.Console.Visible && !dj.Visible);
        Assert.True(_perf.IsActive && _perf.IsListening); // listening while it shows
        Assert.Equal("DJ Perform Mode: listening for VirtualDJ (OS2L).", screen.StatusText);
        Assert.StartsWith("OS2L: listening on :", perform.ConnectionText);
        screen.SetView(AppView.DjEdit);
        Frame();
        Assert.False(_perf.IsActive || perform.Visible); // stopped once another view's chosen
        Assert.Equal("DJ Perform Mode stopped.", screen.StatusText);
    }

    static IEnumerable<Element> All(Element e)
    {
        yield return e;
        if (e is Panel p)
            foreach (var child in p.Children.Where(c => c.Visible))
                foreach (var d in All(child))
                    yield return d;
    }
}

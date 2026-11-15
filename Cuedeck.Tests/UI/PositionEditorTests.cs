using System.Numerics;
using Brutal.Numerics;
using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.Music;
using Cuedeck.Show;
using Cuedeck.Timelines;
using Cuedeck.UI;
using Cuedeck.UI.DjEdit;
using Cuedeck.UI.Timeline;

namespace Cuedeck.Tests.UI;

/// <summary>
/// Named positions in DJ Edit Mode: position_test.gd's editor (the pad, selecting and dragging points, the name),
/// copying and pasting points, the fixture list's menu, the Positions list in the left column, and the selected
/// fixtures lit full white and pointed at the position. Driven through <see cref="Root"/> as the mouse would.
/// </summary>
public sealed class PositionEditorTests : IDisposable
{
    static readonly float2 ScreenSize = new(1400, 800);

    readonly TempFolder _dir = new();
    readonly LightingShow _show = new();
    readonly Root _ui = new();
    readonly ContextMenu _menu = new(MainScreenTests.TestFonts.Body);
    readonly ConfirmDialog _confirm;
    readonly PositionEditor _editor;
    readonly DjEditView _view;

    public PositionEditorTests()
    {
        var fonts = MainScreenTests.TestFonts;
        _confirm = new ConfirmDialog(_ui, fonts.Body, fonts.Bold, fonts.Heading);
        var tl = new SongTimelines();
        _editor = new PositionEditor(fonts, _menu, _show.Positions, _show.Patch);
        _view = _ui.Add(
            new DjEditView(fonts, MainScreenTests.Pictures, _menu, _confirm, tl, new TimelineView(fonts, _menu, null, tl, new FakePlayer()), new MusicBrowser(tl, () => DjLibrary.Empty()), _editor)
            {
                Root = _ui,
            }
        );
        _ui.Add(_confirm);
        _ui.Add(_menu);
        _show.Patch.Changed += _editor.RefreshFixtures;
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

    static FixtureProfile Mover() => Profiles.Simple("mv", "Mover", ("Pan", "PAN"), ("Tilt", "TILT"), ("Dimmer", "DIMMER"));

    static FixtureProfile Par() => Profiles.Simple("par", "Par", ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));

    /// <summary>
    /// Two movers and a par, patched in that order; the movers' uids.
    /// </summary>
    (string L, string R) Patch2()
    {
        var l = _show.Patch.Add(0, Mover(), 0, name: "Mover L");
        _show.Patch.Add(0, Par(), 10, name: "Par");
        var r = _show.Patch.Add(0, Mover(), 20, name: "Mover R");
        return (l.Uid, r.Uid);
    }

    List<string> Movers(int n)
    {
        var uids = new List<string>();
        for (var k = 0; k < n; k++)
            uids.Add(_show.Patch.Add(0, Mover(), k * 3, name: $"Mover {k}").Uid);
        return uids;
    }

    void Press(float2 at, bool down)
    {
        _ui.MouseMove(at);
        _ui.MouseButton(MouseButton.Left, down);
        Frame();
    }

    void Drag(float2 a, float2 b)
    {
        _ui.MouseMove(a);
        _ui.MouseButton(MouseButton.Left, true);
        _ui.MouseMove((a + b) / 2);
        _ui.MouseMove(b);
        _ui.MouseButton(MouseButton.Left, false);
        Frame();
    }

    float2 PadPx(Vector2 v)
    {
        var (at, side) = _editor.PadRect;
        return at + new float2(v.X * side, (1 - v.Y) * side);
    }

    static bool Near(Vector2 a, Vector2 b) => Vector2.Distance(a, b) < 0.002f;

    // -------------------------------------------------------------- the editor --

    [Fact]
    public void The_position_editor()
    {
        var (l, r) = Patch2();
        var lib = _show.Positions;
        var pos = lib.Add();
        lib.SetPoint(pos.Id, r, new Vector2(0.8f, 0.2f));
        _view.OpenPosition(pos.Id);
        Frame();
        Assert.Equal(["U1 · Mover L", "U1 · Mover R"], _editor.List.Items.Select(f => f.Name)); // only those with pan and tilt
        Assert.Equal("Position 1", _editor.NameBox.Text);
        var (padAt, side) = _editor.PadRect;
        Assert.True(side > 400); // a large square pad
        Assert.True(padAt.X + side <= _editor.List.ScreenPosition.X); // the side panel on its right
        // Nothing selected: a dimmed point doesn't move, a click selects its fixture.
        var centre = PadPx(new Vector2(0.5f, 0.5f));
        Drag(centre, centre + new float2(50, 0));
        Assert.Equal(new Vector2(0.5f, 0.5f), lib.Point(pos.Id, l));
        Assert.Equal([l], _editor.SelectedUids());
        // Selected: it drags (pan right, tilt up), only it.
        Drag(centre, centre + new float2(side * 0.1f, -side * 0.2f));
        Assert.True(Near(lib.Point(pos.Id, l), new Vector2(0.6f, 0.7f)) && lib.Point(pos.Id, r) == new Vector2(0.8f, 0.2f));
        // Both selected: dragging one moves them all.
        _editor.SelectUids([l, r]);
        var from = PadPx(lib.Point(pos.Id, r));
        Drag(from, from + new float2(-side * 0.1f, 0));
        Assert.True(Near(lib.Point(pos.Id, l), new Vector2(0.5f, 0.7f)) && Near(lib.Point(pos.Id, r), new Vector2(0.7f, 0.2f)));
        // Renaming.
        _ui.Focus(_editor.NameBox);
        _editor.NameBox.Text = "Crowd";
        _editor.NameBox.KeyDown(Key.Enter, Modifiers.None);
        Assert.Equal("Crowd", lib.Find(pos.Id)!.Name);
    }

    [Fact]
    public void Copying_and_pasting_points()
    {
        var m = Movers(3);
        var lib = _show.Positions;
        var a = lib.Add().Id;
        var b = lib.Add().Id;
        lib.SetPoint(a, m[0], new Vector2(0.1f, 0.2f));
        lib.SetPoint(a, m[1], new Vector2(0.3f, 0.4f));
        _view.OpenPosition(a);
        Frame();
        _editor.SelectUids([m[0]]);
        Assert.True(_editor.KeyDown(Key.C, Modifiers.Control));
        _editor.SelectUids([m[1], m[2]]);
        var changes = 0;
        lib.Changed += () => changes++;
        _editor.KeyDown(Key.V, Modifiers.Control);
        Assert.True(lib.Point(a, m[1]) == new Vector2(0.1f, 0.2f) && lib.Point(a, m[2]) == new Vector2(0.1f, 0.2f)); // one point to every selected
        Assert.True(changes > 0);
        // Several pair off in list order, into another position.
        lib.SetPoint(a, m[1], new Vector2(0.3f, 0.4f));
        lib.SetPoint(a, m[2], new Vector2(0.5f, 0.6f));
        _editor.SelectUids([m[2], m[0]]); // list order: m0, m2
        _editor.KeyDown(Key.C, Modifiers.Control);
        _view.OpenPosition(b);
        _editor.SelectUids([m[1], m[2]]);
        _editor.KeyDown(Key.V, Modifiers.Control);
        Assert.True(lib.Point(b, m[1]) == new Vector2(0.1f, 0.2f) && lib.Point(b, m[2]) == new Vector2(0.5f, 0.6f) && lib.Point(b, m[0]) == PositionLibrary.DefaultPoint);
        Assert.Equal(new Vector2(0.3f, 0.4f), lib.Point(a, m[1])); // the source left alone
        // More selected than copied: from the first again.
        _editor.SelectUids(m);
        Assert.True(_editor.PastePoints());
        Assert.True(lib.Point(b, m[0]) == new Vector2(0.1f, 0.2f) && lib.Point(b, m[1]) == new Vector2(0.5f, 0.6f) && lib.Point(b, m[2]) == new Vector2(0.1f, 0.2f));
        _editor.SelectUids([]);
        Assert.True(!_editor.CopyPoints() && PositionEditor.HasCopiedPoints); // nothing selected: the copy's kept
        // Not while the name's being typed in, nor while the editor's hidden.
        _editor.SelectUids([m[1]]);
        _ui.Focus(_editor.NameBox);
        Assert.False(_editor.KeyDown(Key.V, Modifiers.Control));
        _ui.Focus(null);
        _view.ShowTrackList();
        Frame();
        Assert.False(_editor.KeyDown(Key.V, Modifiers.Control));
    }

    [Fact]
    public void The_fixture_lists_menu()
    {
        var m = Movers(5);
        var lib = _show.Positions;
        var a = lib.Add().Id;
        lib.SetPoint(a, m[0], new Vector2(0.1f, 0.2f));
        lib.SetPoint(a, m[1], new Vector2(0.9f, 0.9f));
        lib.SetPoint(a, m[3], new Vector2(0.3f, 0.1f));
        lib.SetPoint(a, m[4], new Vector2(0.5f, 0.8f));
        _view.OpenPosition(a);
        Frame();
        // Right-clicking an unselected fixture: just it, and the menu, with only Reset for one.
        _editor.SelectUids([m[0], m[1]]);
        Frame();
        var list = _editor.List;
        float2 Row(int i) => list.ScreenPosition + new float2(20, (i + 0.5f) * list.RowHeight);
        _ui.MouseMove(Row(3));
        _ui.MouseButton(MouseButton.Right, true);
        Frame();
        Assert.Equal([m[3]], _editor.SelectedUids());
        Assert.True(_menu.Visible);
        Assert.Equal([false, false, true], _editor.MenuItems().Select(i => i.Enabled));
        _menu.Close();
        // On the selection: kept, everything offered.
        _editor.SelectUids([m[0], m[1], m[4]]);
        Frame();
        _ui.MouseMove(Row(1));
        _ui.MouseButton(MouseButton.Right, true);
        Assert.Equal([m[0], m[1], m[4]], _editor.SelectedUids());
        Assert.All(_editor.MenuItems(), i => Assert.True(i.Enabled));
        _menu.Close();
        // Snap Together: the average.
        _editor.SelectUids([m[0], m[1], m[3]]);
        _editor.MenuItems()[0].Chosen();
        var mid = new Vector2(1.3f / 3, 0.4f);
        Assert.True(Near(lib.Point(a, m[0]), mid) && Near(lib.Point(a, m[1]), mid) && Near(lib.Point(a, m[3]), mid) && lib.Point(a, m[4]) == new Vector2(0.5f, 0.8f));
        // Spread: evenly between the first and last selected, in list order.
        lib.SetPoint(a, m[0], new Vector2(0, 0.2f));
        _editor.SelectUids([m[4], m[0], m[1], m[3]]);
        Assert.True(_editor.Spread());
        Assert.True(lib.Point(a, m[0]) == new Vector2(0, 0.2f) && lib.Point(a, m[4]) == new Vector2(0.5f, 0.8f));
        Assert.True(Near(lib.Point(a, m[1]), new Vector2(0.5f / 3, 0.4f)) && Near(lib.Point(a, m[3]), new Vector2(1f / 3, 0.6f)));
        _editor.SelectUids([m[0], m[4]]);
        Assert.False(_editor.Spread()); // not with two
        // Reset: back to the default.
        _editor.SelectUids([m[1], m[4]]);
        _editor.MenuItems()[2].Chosen();
        Assert.True(lib.Point(a, m[1]) == PositionLibrary.DefaultPoint && lib.Point(a, m[4]) == PositionLibrary.DefaultPoint && !lib.Find(a)!.Points.ContainsKey(m[1]));
        Assert.Equal(new Vector2(0, 0.2f), lib.Point(a, m[0]));
    }

    // ------------------------------------------------------- the Positions list --

    [Fact]
    public void The_positions_list_in_DJ_Edit_Mode()
    {
        Patch2();
        var lib = _show.Positions;
        Frame();
        var list = _view.PositionList;
        Assert.True(list.ScreenPosition.Y > _view.Tree.ScreenPosition.Y); // below the folders
        Assert.Empty(list.Ids);
        var add = _view.AddPositionButton;
        Assert.Equal("+", add.Text);
        Press(add.ScreenPosition + add.Size / 2, true);
        Press(add.ScreenPosition + add.Size / 2, false);
        var id1 = lib.Positions[0].Id;
        Assert.Equal([id1], list.Ids);
        Assert.True(_view.IsPositionShown && _editor.PositionId == id1 && list.SelectedId == id1); // it's open, selected
        _view.AddPosition();
        Frame();
        var id2 = lib.Positions[1].Id;
        Assert.True(list.SelectedId == id2 && _editor.PositionId == id2);
        Press(list.PointOf(id1), true);
        Press(list.PointOf(id1), false);
        Assert.Equal(id1, _editor.PositionId); // picking one opens it
        // Renamed in the editor: the list follows.
        lib.Rename(id1, "Crowd");
        Frame();
        Assert.Equal("Crowd", lib.Find(id1)!.Name);
        // Hovering shows the ✕; it asks before deleting.
        Assert.False(list.ShowsDelete(id2));
        _ui.MouseMove(list.PointOf(id2));
        Frame();
        Assert.True(list.ShowsDelete(id2) && !list.ShowsDelete(id1));
        Press(list.PointOf(id2, x: true), true);
        Press(list.PointOf(id2, x: true), false);
        Assert.True(_confirm.Visible && lib.Positions.Count == 2);
        _confirm.Close();
        Frame();
        Assert.Equal(2, lib.Positions.Count); // cancelled: kept
        _view.ConfirmDeletePosition(id2);
        Frame();
        ClickButton(_confirm, "Delete");
        Assert.Equal([id1], list.Ids);
        // Deleting the open one goes back to the track list.
        Assert.True(_view.IsPositionShown);
        _view.ConfirmDeletePosition(id1);
        Frame();
        ClickButton(_confirm, "Delete");
        Assert.True(!_view.IsPositionShown && list.Ids.Count == 0);
        _view.AddPosition();
        _view.ShowTrackList();
        Assert.True(!_view.IsPositionShown && list.SelectedId == "");
        _view.OpenPosition(lib.Positions[0].Id);
        lib.FromJson(null); // another show
        Assert.False(_view.IsPositionShown); // its position gone: back to the list
        // The right-click menu.
        var src = lib.Add();
        lib.Rename(src.Id, "Stage");
        lib.SetPoint(src.Id, "m1", new Vector2(0.2f, 0.8f));
        Frame();
        var menu = _view.PositionMenu(src.Id);
        Assert.Equal(["Rename", "Duplicate", "Delete…"], menu.Select(i => i.Text));
        _ui.MouseMove(list.PointOf(src.Id));
        _ui.MouseButton(MouseButton.Right, true);
        Assert.True(_menu.Visible);
        _menu.Close();
        _view.ShowTrackList();
        menu[0].Chosen();
        Assert.True(_view.IsPositionShown && _editor.PositionId == src.Id && _editor.NameBox.IsFocused); // Rename: open, ready to type over
        menu[1].Chosen();
        var copy = lib.Positions[1];
        Assert.True(copy.Name == "Stage copy" && copy.Colour != src.Colour && lib.Point(copy.Id, "m1") == new Vector2(0.2f, 0.8f));
        Assert.True(_editor.PositionId == copy.Id && list.SelectedId == copy.Id);
        menu[2].Chosen();
        Frame();
        ClickButton(_confirm, "Delete");
        Assert.True(lib.Positions.Count == 1 && lib.Positions[0].Id == copy.Id);
    }

    void ClickButton(Element within, string text)
    {
        var b = All(within).OfType<Button>().Single(x => x.Text == text);
        Press(b.ScreenPosition + b.Size / 2, true);
        Press(b.ScreenPosition + b.Size / 2, false);
    }

    static IEnumerable<Element> All(Element e)
    {
        yield return e;
        if (e is Panel p)
            foreach (var child in p.Children.Where(c => c.Visible))
                foreach (var d in All(child))
                    yield return d;
    }

    // -------------------------------------------------------------- highlight --

    [Fact]
    public void Selected_fixtures_lit_full_white()
    {
        var spot = Profiles.Make(
            "spot",
            "Spot",
            """[{"name":"Pan","role":"PAN"},{"name":"Tilt","role":"TILT"},{"name":"Shutter","role":"STROBE","ranges":[{"lo":0,"hi":9,"label":"Closed"},{"lo":10,"hi":19,"label":"Open"},{"lo":20,"hi":255,"label":"Strobe"}]},{"name":"Dimmer","role":"DIMMER"},{"name":"Red","role":"RED"},{"name":"Green","role":"GREEN"},{"name":"Blue","role":"BLUE"}]"""
        );
        var rgbMover = Profiles.Simple("rgbm", "RGB mover", ("Pan", "PAN"), ("Tilt", "TILT"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));
        var s = _show.Patch.Add(0, spot, 0, name: "Spot");
        var w = _show.Patch.Add(0, rgbMover, 10, name: "Wash");
        var u = _show.Dmx.Universes[0];
        byte[] look = [100, 120, 0, 40, 255, 0, 0]; // pan, tilt, shutter closed, 40 dim, red
        for (var i = 0; i < look.Length; i++)
            u.SetChannel(i, look[i]);
        for (var i = 0; i < 5; i++)
            u.SetChannel(10 + i, new[] { 30, 60, 0, 0, 200 }[i]); // blue
        _show.Patch.SetVirtualDimmer(w, 0.25);
        var p = _show.Positions.Add();
        _view.OpenPosition(p.Id);
        Frame();
        Assert.Equal(40, u.GetChannel(3)); // nothing selected: nothing changes
        _editor.SelectUids([s.Uid]);
        Assert.True(u.GetChannel(3) == 255 && u.GetChannel(4) == 255 && u.GetChannel(5) == 255 && u.GetChannel(6) == 255); // full white
        Assert.InRange(u.GetChannel(2), 10, 19); // the shutter open
        Assert.True(u.GetChannel(0) == 128 && u.GetChannel(1) == 128); // pointed at the position (no point yet: centred)
        _show.Positions.SetPoint(p.Id, s.Uid, new Vector2(0.2f, 0.9f));
        Assert.True(Math.Abs(u.GetChannel(0) - 51) <= 1 && Math.Abs(u.GetChannel(1) - 230) <= 1); // its point moved: it follows
        Assert.True(u.GetChannel(12) == 0 && u.GetChannel(14) == 200); // the unselected one untouched
        var live = _show.Patch.LiveToJson(0);
        var chans = live.Obj("channels")!;
        Assert.True(chans.Int("3") == 40 && chans.Int("4") == 255 && !chans.ContainsKey("5") && chans.Int("0") == 100 && chans.Int("1") == 120); // a show saved meanwhile: the real levels
        _editor.SelectUids([s.Uid, w.Uid]);
        Assert.True(u.GetChannel(12) == 255 && u.GetChannel(14) == 255 && Gd.ApproxEqual(_show.Patch.VirtualDimmer(w)!.Value, 1)); // no dimmer: white, its virtual dimmer full
        _editor.SelectUids([w.Uid]);
        Assert.True(u.GetChannel(3) == 40 && u.GetChannel(4) == 255 && u.GetChannel(5) == 0 && u.GetChannel(2) == 0 && u.GetChannel(0) == 100 && u.GetChannel(1) == 120); // put back exactly
        Assert.True(u.GetChannel(10) == 128 && u.GetChannel(11) == 128); // the other pointed at the position
        Assert.Equal(0.25, Json.AsDouble(_show.Patch.LiveToJson(0).Arr("virtual_dimmers")[1]), 2); // its own level's what's saved
        _view.ShowTrackList();
        Frame();
        Assert.True(u.GetChannel(14) == 200 && u.GetChannel(12) == 0 && Math.Abs(_show.Patch.VirtualDimmer(w)!.Value - 0.25) < 0.01); // leaving puts them back
        _view.OpenPosition(p.Id);
        _editor.SelectUids([s.Uid]);
        Assert.Equal(255, u.GetChannel(3));
        _view.Hidden(); // another view
        Assert.Equal(40, u.GetChannel(3));
    }
}

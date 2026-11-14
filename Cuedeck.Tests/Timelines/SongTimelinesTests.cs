using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Dj;
using Cuedeck.Fixtures;
using Cuedeck.Show;
using Cuedeck.Timelines;
using StrobeSpeed = Cuedeck.Timelines.StrobeSpeed;

namespace Cuedeck.Tests.Timelines;

/// <summary>
/// DJ Edit Mode's timelines without the screen: timeline_track_test.gd's checks of the timelines in the track
/// timeline (fixture tracks, selecting, Delete, copy and paste, undo, the rows' menus, dragging selections, mute, solo
/// and collapsing), timeline_store_test.gd's autosave and music_timeline_test.gd's VirtualDJ grid. Where sLight's
/// tests pressed the mouse at a point, these press at a row and a time.
/// </summary>
public sealed class SongTimelinesTests : IDisposable
{
    const TimelineRow Rd = TimelineRow.Dimmer, Rc = TimelineRow.Colour, Rs = TimelineRow.Strobe, Rp = TimelineRow.Position, Rm = TimelineRow.Movement;

    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    static FixtureProfile Par() => Profiles.Simple("par", "RGB par", ("Dimmer", "DIMMER"), ("Red", "RED"), ("Green", "GREEN"), ("Blue", "BLUE"));

    static FixtureProfile Bar()
    {
        var chans = new List<(string, string)> { ("Strobe", "STROBE") };
        for (var h = 1; h <= 3; h++)
            chans.AddRange([($"Red {h}", "RED"), ($"Green {h}", "GREEN"), ($"Blue {h}", "BLUE")]);
        chans.Add(("White 3", "WHITE"));
        return Profiles.Simple("bar", "3-head LED bar", [.. chans]);
    }

    static FixtureProfile TwoHeadBar() => Profiles.Simple("bar2", "Bar", ("R", "RED"), ("G", "GREEN"), ("B", "BLUE"), ("R", "RED"), ("G", "GREEN"), ("B", "BLUE"));

    static FixtureProfile Mover(params (string, string)[] extra) => Profiles.Simple("mv", "Mover", [("Pan", "PAN"), ("Tilt", "TILT"), .. extra]);

    static PatchedFixture Fx(string key, string name, FixtureProfile profile) => new() { Uid = key, Name = name, Profile = profile };

    static SelectionBlock B(double t0, double t1, params (string Key, TimelineRow Row)[] rows) =>
        SelectionBlock.Of([.. rows.Select(r => TimelineSelection.RowId(r.Key, r.Row))], t0, t1);

    static List<Vector2> V(params (float T, float L)[] nodes) => [.. nodes.Select(n => new Vector2(n.T, n.L))];

    /// <summary>
    /// A timeline for <paramref name="fixtures"/> with song <paramref name="song"/> open (no such file: no
    /// structure), 60 s long, its structure never analysed for real.
    /// </summary>
    static SongTimelines Open(List<PatchedFixture> fixtures, string song = "song.wav", double duration = 60, float beatGap = 0)
    {
        var tl = new SongTimelines { Fixtures = () => fixtures, AnalyseStructure = (_, _) => throw new InvalidDataException("not here") };
        tl.Open(song);
        tl.SetDuration(duration);
        if (beatGap > 0)
            tl.SetBeatGrid([.. Enumerable.Range(0, (int)(duration / beatGap) + 1).Select(i => i * beatGap)]);
        return tl;
    }

    static bool Near(Rgb? a, Rgb b) => a is { } c && Math.Abs(c.R - b.R) < 0.01 && Math.Abs(c.G - b.G) < 0.01 && Math.Abs(c.B - b.B) < 0.01;

    static int Row(SongTimelines tl, string key, TimelineRow row) => tl.RowList().FindIndex(r => r.Track.Key == key && r.Row == row);

    static void Drag(SongTimelines tl, (string Key, TimelineRow Row) from, double t0, (string Key, TimelineRow Row) to, double t1, bool ctrl = false, bool shift = false)
    {
        tl.SelectPress(Row(tl, from.Key, from.Row), t0, ctrl, shift, 0.15);
        tl.SelectMove(Row(tl, to.Key, to.Row), (t0 + t1) / 2);
        tl.SelectRelease(Row(tl, to.Key, to.Row), t1);
    }

    static void Drag(SongTimelines tl, string key, TimelineRow row, double t0, double t1, bool ctrl = false, bool shift = false) =>
        Drag(tl, (key, row), t0, (key, row), t1, ctrl, shift);

    static List<string> Ids(params (string Key, TimelineRow Row)[] rows) => [.. rows.Select(r => TimelineSelection.RowId(r.Key, r.Row))];

    static void Pump(SongTimelines tl, Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed.TotalSeconds < 10)
        {
            tl.Update();
            Thread.Sleep(2);
        }
        tl.Update();
    }

    // ----------------------------------------------------------- the tracks --

    [Fact]
    public void Timelines_for_a_fixture_and_its_heads()
    {
        var par = SongTimelines.FixtureTrackSpecs("0/1", "Par", Par(), 0);
        Assert.Single(par);
        Assert.Equal(("Par", TimelineTarget.Fixture), (par[0].Title, par[0].Target));
        Assert.Equal([Rc, Rd], par[0].Rows);
        var bar = SongTimelines.FixtureTrackSpecs("0/2", "Bar", Bar(), 0);
        Assert.Equal(4, bar.Count); // its own plus one per head
        Assert.Equal([Rs, Rc, Rd], bar[0].Rows); // the fixture's covers all its channels
        Assert.Equal((TimelineTarget.Head, "Bar\nHead 1", "Bar\nHead 3"), (bar[1].Target, bar[1].Title, bar[3].Title));
        Assert.Equal([Rc, Rd], bar[1].Rows); // a head's are its own channels' (no strobe)
        Assert.True(bar[1].Key != bar[2].Key && bar[0].Key == "0/2");
    }

    [Fact]
    public void The_fixtures_timelines()
    {
        var fixtures = new List<PatchedFixture> { Fx("0/1", "Par", Par()) };
        var tl = new SongTimelines { Fixtures = () => fixtures };
        Assert.Empty(tl.FixtureTracks); // nothing until refreshed or a song opens
        tl.Open(_dir.File("none.wav"));
        Assert.Equal("Par", Assert.Single(tl.FixtureTracks).Title);
        Assert.True(tl.Master.Target == TimelineTarget.Master && tl.Master.Rows.Count == 5);

        // Edits survive a refresh; three bars add four timelines each.
        tl.FixtureTracks[0].DimmerNodes = V((1, 0.5f));
        for (var i = 0; i < 3; i++)
            fixtures.Add(Fx($"0/{i + 2}", $"LED Bar {i + 1}", Bar()));
        tl.RefreshFixtures();
        Assert.Equal(13, tl.FixtureTracks.Count);
        Assert.Equal(V((1, 0.5f)), tl.FixtureTracks[0].DimmerNodes);
        Assert.Equal(TimelineTarget.Head, tl.FixtureTracks[2].Target);
        fixtures.Clear();
        tl.RefreshFixtures();
        Assert.Empty(tl.FixtureTracks);
    }

    // ------------------------------------------------------------ selection --

    [Fact]
    public void Selecting_areas_of_the_timelines()
    {
        var tl = Open([Fx("par", "Par", Par())], beatGap: 0.5f); // 120 BPM: bars of 2 s from 0
        var sel = tl.Selection;

        // Bar snapping (the default): whole bars on the row, without touching the line.
        Drag(tl, "master", Rd, 3.1, 6.9);
        var b = Assert.Single(sel.Blocks);
        Assert.Equal(Ids(("master", Rd)), b.Rows);
        Assert.Equal((2.0, 8.0), (b.T0, b.T1));
        Assert.Equal(V((0, 0), (60, 0)), tl.Master.DimmerNodes);
        Assert.True(sel.Has("master", Rd, 5) && !sel.Has("master", Rc, 5));
        // Another plain drag replaces it; Ctrl adds.
        Drag(tl, "master", Rm, 10.5, 11);
        b = Assert.Single(sel.Blocks);
        Assert.Equal(Ids(("master", Rm)), b.Rows);
        Assert.Equal((10.0, 12.0), (b.T0, b.T1));
        Drag(tl, "master", Rs, 14.2, 14.4, ctrl: true);
        Assert.True(sel.Blocks.Count == 2 && sel.Has("master", Rm, 11) && sel.Has("master", Rs, 15));
        // Shift: from the last area (strobe, 14-16 s) through the new one, the rows between too.
        Drag(tl, "master", Rd, 18.5, 18.6, shift: true);
        var last = sel.Blocks[^1];
        Assert.Equal(2, sel.Blocks.Count);
        Assert.Equal((14.0, 20.0), (last.T0, last.T1));
        Assert.Equal(Ids(("master", Rd), ("master", Rc), ("master", Rm), ("master", Rs)), last.Rows);
        Assert.True(sel.Has("master", Rm, 11)); // earlier Ctrl-added areas stay
        // Across tracks: every row between, top to bottom.
        Drag(tl, ("par", Rd), 4, ("master", Rp), 5);
        var rows = Assert.Single(sel.Blocks).Rows;
        Assert.Equal(7, rows.Count);
        Assert.Equal(Ids(("par", Rd), ("par", Rc), ("master", Rd)), rows.Take(3));
        Assert.Equal(TimelineSelection.RowId("master", Rp), rows[6]);
        // Beat and no snapping.
        tl.Snap = SnapMode.Beat;
        sel.Clear();
        Drag(tl, "master", Rm, 3.1, 3.3);
        Assert.Equal((3.0, 3.5), (sel.Blocks[0].T0, sel.Blocks[0].T1));
        tl.Snap = SnapMode.None;
        sel.Clear();
        Drag(tl, "master", Rm, 3.1, 7.3);
        Assert.Equal((3.1, 7.3), (sel.Blocks[0].T0, sel.Blocks[0].T1));
        // A plain click: with no snapping it clears the selection; with bars it selects the bar under it.
        Drag(tl, "master", Rm, 9, 9);
        Assert.True(sel.IsEmpty);
        tl.Snap = SnapMode.Bar;
        Drag(tl, "master", Rm, 9, 9);
        Assert.Equal((8.0, 10.0), (sel.Blocks[0].T0, sel.Blocks[0].T1));
        // Another song clears it.
        tl.Open("another.wav");
        Assert.True(sel.IsEmpty);
    }

    [Fact]
    public void Delete_clears_the_selected_areas()
    {
        var tl = Open([Fx("par", "Par", Par())]);
        var m = tl.Master;
        var par = tl.FixtureTracks[0];
        m.DimmerNodes = V((1, 0.5f), (5, 1.0f), (30, 0.5f));
        par.DimmerNodes = V((3, 0.8f));
        tl.Selection.SetBlocks([B(4, 8, ("master", Rd), ("master", Rm)), B(6, 12, ("master", Rd)), B(10, 20, ("par", Rd))]);
        Assert.True(tl.DeleteSelection());
        var mn = m.DimmerNodes;
        Assert.Equal(6, mn.Count); // one dark span over the merged 4-12 s
        Assert.Equal((new Vector2(1, 0.5f), new Vector2(4, 0), new Vector2(12, 0), new Vector2(30, 0.5f)), (mn[0], mn[2], mn[3], mn[5]));
        Assert.True(Gd.ApproxEqual(m.DimmerAt(4), 0.875) && Gd.ApproxEqual(m.DimmerAt(20), 0.7) && Gd.ApproxEqual(m.DimmerAfter(12), 0.86));
        Assert.True(par.DimmerNodes.Count == 4 && par.DimmerAt(15) == 0 && Gd.ApproxEqual(par.DimmerAt(5), 0.8) && !par.DimmerDefinedAt(25));
        Assert.True(tl.HasUnsavedTimelines);
        Assert.Equal(3, tl.Selection.Blocks.Count); // the selection stays
        tl.Selection.Clear();
        Assert.False(tl.DeleteSelection());
    }

    [Fact]
    public void Generate_with_areas_selected_fills_only_those()
    {
        var tl = Open([Fx("par", "Par", Par())]);
        var m = tl.Master;
        var par = tl.FixtureTracks[0];
        m.DimmerNodes = V((0, 0.5f), (60, 0.5f));
        m.ColourSegments = [new ColourSpan(0, 60, new Rgb(1, 0, 0), new Rgb(1, 0, 0))];
        par.DimmerNodes = V((0, 0.3f), (60, 0.3f));
        var show = new Dictionary<string, GeneratedRows>
        {
            ["master"] = new() { Dimmer = V((0, 1), (60, 1)), Colour = [new ColourSpan(0, 60, new Rgb(0, 0, 1), new Rgb(0, 0, 1))] },
            ["par"] = new() { Dimmer = V((0, 0.9f), (60, 0.9f)) },
        };
        tl.Selection.SetBlocks([B(10, 20, ("master", Rd), ("master", Rc))]);
        Assert.True(tl.SpliceShow(show));
        Assert.True(m.DimmerAt(15) == 1 && m.DimmerAt(5) == 0.5 && m.DimmerAfter(20) == 0.5);
        Assert.Equal((new Rgb(0, 0, 1), new Rgb(1, 0, 0), new Rgb(1, 0, 0)), (m.ColourAt(15), m.ColourAt(5), m.ColourAt(30)));
        Assert.True(Gd.ApproxEqual(par.DimmerAt(15), 0.3)); // nothing selected on it: left alone
        tl.Selection.SetBlocks([B(30, 40, ("par", Rd))]);
        Assert.False(tl.SpliceShow(show, new HashSet<string> { "par" })); // a left-out fixture
    }

    static SongAnalysis StructureAnalysis(string label) =>
        new()
        {
            Duration = 40,
            Bpm = 120,
            Sections = [new(0, 20, label, 0.3, 0), new(20, 40, "Chorus", 0.9, 1)],
        };

    [Fact]
    public void Shift_generate_reanalyses_the_structure_then_generates()
    {
        var path = _dir.File("reanalyse.wav");
        File.WriteAllText(path, "x");
        var label = "Intro";
        var tl = new SongTimelines { AnalyseStructure = (_, _) => StructureAnalysis(label) };
        tl.Open(path);
        tl.SetDuration(40);
        Pump(tl, () => !tl.IsAnalysing);
        Assert.Equal("Intro", tl.Structure!.Sections[0].Label);
        // An edit to the structure.
        tl.Structure.Sections[0] = tl.Structure.Sections[0] with { Label = "Verse" };
        tl.StructureEdited();
        var before = tl.Master.DimmerNodes;
        Assert.True(tl.ReanalyseAndGenerate());
        Pump(tl, () => !tl.IsAnalysing);
        Assert.False(tl.SongData().ContainsKey("structure")); // the structure's edits dropped
        Assert.Equal("Intro", tl.Structure!.Sections[0].Label); // the fresh analysis shown
        Assert.NotEqual(before, tl.Master.DimmerNodes); // and a show generated from it
        Assert.True(tl.Undo());
        Assert.Equal("Verse", tl.SongData().Obj("structure").Arr("sections")[0].Str("label"));
        Assert.Equal(before, tl.Master.DimmerNodes); // one undo: the edited structure and the old show
        Assert.False(new SongTimelines().ReanalyseAndGenerate()); // nothing open: nothing to re-analyse
    }

    [Fact]
    public void Copy_and_paste()
    {
        var tl = Open([Fx("p1", "Par 1", Par()), Fx("p2", "Par 2", Par())]);
        var m = tl.Master;
        var (p1, p2) = (tl.FixtureTracks[0], tl.FixtureTracks[1]);
        var sel = tl.Selection;
        m.DimmerNodes = V((4, 0), (6, 1), (8, 0));
        sel.SetBlocks([B(4, 8, ("master", Rd), ("master", Rm))]);
        Assert.True(tl.CopySelection());
        var clip = tl.Clipboard!;
        Assert.True(clip.Length == 4 && clip.Tracks.Count == 1 && clip.Tracks[0].Key == "master" && clip.Tracks[0].Rows.ContainsKey(Rd) && clip.Tracks[0].Rows.ContainsKey(Rm));
        Assert.Single(sel.Blocks);
        // At a selected spot on the same track.
        sel.SetBlocks([B(20, 22, ("master", Rd))]);
        Assert.True(tl.Paste());
        Assert.True(Gd.ApproxEqual(m.DimmerAt(22), 1) && m.DimmerAt(20) == 0 && Gd.ApproxEqual(m.DimmerAt(23), 0.5));
        Assert.True(m.DimmerAt(5) == 0.5 && m.DimmerAt(14) == 0 && m.DimmerAt(30) == 0);
        Assert.True(sel.Blocks.Count == 2 && sel.Blocks[0].T0 == 20 && sel.Blocks[0].T1 == 24); // the pasted areas selected
        Assert.True(tl.HasUnsavedTimelines);
        // One copied track into every selected track (an empty fixture row: just the pasted stretch).
        sel.SetBlocks([B(30, 31, ("p1", Rc), ("p1", Rm), ("p2", Rd))]);
        tl.Paste();
        Assert.True(Gd.ApproxEqual(p1.DimmerAt(32), 1) && Gd.ApproxEqual(p2.DimmerAt(32), 1) && p1.DimmerAfter(30) == 0 && !p1.DimmerDefinedAt(29) && p1.DimmerNodes.Count == 3);
        Assert.Equal(2, sel.Blocks.Count); // each its dimmer row (pars have no movement row)
        // Nothing selected: at the playhead, into the source track.
        sel.Clear();
        tl.SongPos = 40;
        tl.Paste();
        Assert.True(Gd.ApproxEqual(m.DimmerAt(42), 1) && m.DimmerAt(40) == 0);
        // Two copied tracks pair off with the selected ones, in order.
        p1.DimmerNodes = V((0, 0.3f), (60, 0.3f));
        p2.DimmerNodes = V((0, 0.7f), (60, 0.7f));
        sel.SetBlocks([B(0, 2, ("p1", Rd), ("p1", Rm), ("p2", Rd))]);
        tl.CopySelection();
        sel.SetBlocks([B(50, 51, ("master", Rd)), B(50, 51, ("p1", Rd))]);
        tl.Paste();
        Assert.True(Gd.ApproxEqual(p1.DimmerAt(51), 0.3) && Gd.ApproxEqual(m.DimmerAt(51), 0.7));
        // Cut: copied, then cleared like Delete; it pastes back elsewhere.
        m.DimmerNodes = V((0, 0.5f), (55, 1));
        sel.SetBlocks([B(10, 20, ("master", Rd))]);
        var at15 = m.DimmerAt(15);
        Assert.True(tl.CutSelection());
        Assert.True(tl.Clipboard!.Length == 10 && tl.Clipboard.Tracks[0].Key == "master");
        Assert.True(m.DimmerAfter(10) == 0 && m.DimmerAt(19) == 0 && Gd.ApproxEqual(m.DimmerAt(25), 0.5 + 0.5 * 25.0 / 55.0));
        Assert.Single(sel.Blocks);
        sel.SetBlocks([B(30, 31, ("master", Rd))]);
        tl.Paste();
        Assert.True(Gd.ApproxEqual(m.DimmerAt(35), at15));
        sel.Clear();
        Assert.False(tl.CutSelection());
        // The copy survives opening another song.
        tl.Open("song2.wav");
        Assert.NotNull(tl.Clipboard);
    }

    [Fact]
    public void Undo_and_redo()
    {
        var tl = Open([Fx("p1", "Par 1", Par())]);
        var m = tl.Master;
        var p1 = tl.FixtureTracks[0];
        Assert.False(tl.CanUndo || tl.CanRedo); // a freshly opened song
        Assert.Equal(V((0, 0), (60, 0)), m.DimmerNodes); // the master starts with 0 % at its start and end
        Assert.False(tl.HasUnsavedTimelines); // which isn't an edit
        // Three edits: the master's line, the fixture's, a Delete.
        m.DimmerNodes = V((10, 0.5f));
        tl.MarkEdited();
        p1.DimmerNodes = V((5, 0.2f), (15, 0.8f));
        tl.MarkEdited();
        tl.Selection.SetBlocks([B(8, 12, ("master", Rd))]);
        tl.DeleteSelection();
        var afterDelete = m.DimmerNodes;
        Assert.True(tl.CanUndo && !tl.CanRedo);
        tl.Undo();
        Assert.Equal(V((10, 0.5f)), m.DimmerNodes);
        tl.Undo();
        Assert.True(p1.DimmerNodes.Count == 0 && m.DimmerNodes.Count == 1);
        tl.Undo();
        Assert.Equal(V((0, 0), (60, 0)), m.DimmerNodes);
        Assert.False(tl.CanUndo);
        Assert.False(tl.Undo());
        tl.Redo();
        Assert.True(m.DimmerNodes.SequenceEqual(V((10, 0.5f))) && p1.DimmerNodes.Count == 0);
        tl.Redo();
        Assert.Equal(V((5, 0.2f), (15, 0.8f)), p1.DimmerNodes);
        tl.Redo();
        Assert.True(m.DimmerNodes.SequenceEqual(afterDelete) && !tl.CanRedo);
        Assert.True(tl.HasUnsavedTimelines);
        // A new edit after an undo drops the redo.
        tl.Undo();
        Assert.True(tl.CanRedo);
        m.DimmerNodes = V((30, 1));
        tl.MarkEdited();
        Assert.False(tl.CanRedo);
        tl.Undo();
        Assert.Equal(V((10, 0.5f)), m.DimmerNodes);
        // Paste undoes.
        tl.Selection.SetBlocks([B(0, 20, ("master", Rd))]);
        tl.CopySelection();
        tl.Selection.SetBlocks([B(30, 31, ("p1", Rd))]);
        var p1Before = p1.DimmerNodes;
        tl.Paste();
        Assert.NotEqual(p1Before, p1.DimmerNodes);
        tl.Undo();
        Assert.Equal(p1Before, p1.DimmerNodes);
        // The structure.
        var sa = new SongAnalysis { Duration = 60, Sections = [new(0, 30, "Verse", 0.4, 0), new(30, 60, "Chorus", 0.9, 1)] };
        tl.AddStructure(tl.Path, sa, null);
        sa.Sections[0] = sa.Sections[0] with { Label = "Intro" };
        tl.StructureEdited();
        Assert.True(tl.CanUndo);
        tl.Undo();
        Assert.Equal("Verse", sa.Sections[0].Label);
        tl.Redo();
        Assert.Equal("Intro", sa.Sections[0].Label);
        // Select all: every row of every timeline over the whole song.
        tl.Selection.SetBlocks([B(1, 2, ("master", Rm))]);
        Assert.True(tl.SelectAll());
        var all = Assert.Single(tl.Selection.Blocks);
        Assert.True(all.T0 == 0 && all.T1 == 60 && all.Rows.Count == p1.Rows.Count + m.Rows.Count);
        Assert.True(tl.Selection.Has("p1", Rd, 30) && tl.Selection.Has("master", Rp, 59));
        // Each song its own history; another show's folder, none.
        tl.Open("song2.wav");
        Assert.False(tl.CanUndo);
        tl.Open("song.wav");
        Assert.True(tl.CanUndo);
        tl.SetTimelineStore(new TimelineStore(_dir.File("other.timelines")));
        Assert.False(tl.CanUndo);
    }

    [Fact]
    public void The_dimmer_menus_shapes()
    {
        List<Vector2> Shape(DimShape k) => SongTimelines.DimmerShape(k, 10, 14, 0.3, 0.6);
        Assert.Equal(V((0, 0), (4, 0)), Shape(DimShape.Black));
        Assert.Equal(V((0, 1), (4, 1)), Shape(DimShape.Full));
        Assert.Equal(V((0, 0.5f), (4, 0.5f)), Shape(DimShape.Half));
        Assert.Equal(V((0, 0), (4, 1)), Shape(DimShape.FadeIn));
        Assert.Equal(V((0, 1), (4, 0)), Shape(DimShape.FadeOut));
        Assert.Equal(V((0, 0.3f), (2, 1), (4, 0.6f)), Shape(DimShape.PulseIn));
        Assert.Equal(V((0, 0.3f), (2, 0), (4, 0.6f)), Shape(DimShape.PulseOut));
        Assert.Equal(V((0, 0.3f), (4, 0.6f)), Shape(DimShape.Smooth));
        float[] beats = [9, 10.5f, 11, 13, 15];
        Assert.Equal(
            V((0, 0), (0.5f, 0), (0.5f, 1), (1, 0), (1, 0), (1, 1), (3, 0), (3, 0), (3, 1), (4, 0)),
            SongTimelines.DimmerShape(DimShape.BeatPulses, 10, 14, 0.3, 0.6, beats)
        );
        Assert.Empty(SongTimelines.DimmerShape(DimShape.BeatPulses, 20, 21, 0, 0, beats));
    }

    [Fact]
    public void The_rows_menus()
    {
        var tl = Open([Fx("p1", "Par 1", Par())]);
        var m = tl.Master;
        var p1 = tl.FixtureTracks[0];
        var sel = tl.Selection;
        Assert.False(tl.HasBeatGrid); // the beat pulses greyed out
        // Pulse In: from the level on the left up to full and back.
        m.DimmerNodes = V((0, 0.4f), (60, 0.4f));
        tl.MarkEdited();
        sel.SetBlocks([B(10, 14, ("master", Rd), ("p1", Rd))]);
        Assert.True(tl.ApplyDimmer(DimShape.PulseIn));
        Assert.True(Gd.ApproxEqual(m.DimmerAt(12), 1) && Gd.ApproxEqual(m.DimmerAt(10), 0.4) && Gd.ApproxEqual(m.DimmerAfter(14), 0.4));
        Assert.True(Gd.ApproxEqual(p1.DimmerAt(12), 1) && Gd.ApproxEqual(p1.DimmerAt(10), 0.4) && !p1.DimmerDefinedAt(9)); // the empty fixture: from the master's
        tl.Undo();
        Assert.True(Gd.ApproxEqual(m.DimmerAt(12), 0.4) && tl.CanRedo);
        tl.Redo();
        Assert.True(Gd.ApproxEqual(m.DimmerAt(12), 1));

        // Colours.
        sel.SetBlocks([B(20, 30, ("p1", Rc), ("master", Rc))]);
        tl.ApplyStaticColour(new Rgb(0, 0, 1));
        Assert.True(p1.ColourAt(25) == new Rgb(0, 0, 1) && m.ColourAt(25) == new Rgb(0, 0, 1) && p1.ColourAt(31) is null);
        Assert.True(tl.ApplyGradient(new Rgb(1, 0, 0), new Rgb(0, 1, 0)) && ColourSpan.ApproxEqual(p1.ColourAt(25)!.Value, new Rgb(0.5f, 0.5f, 0)));
        Assert.True(tl.ApplyRainbow() && p1.ColourSegments.Count == 6 && ColourSpan.ApproxEqual(p1.ColourAt(20)!.Value, new Rgb(1, 0, 0)));
        tl.ApplyStaticColour(new Rgb(1, 0.5f, 0));
        tl.CopySelection();
        sel.SetBlocks([B(40, 41, ("p1", Rc))]);
        tl.Paste();
        Assert.True(p1.ColourAt(45) == new Rgb(1, 0.5f, 0) && m.ColourAt(45) is null);
        sel.SetBlocks([B(22, 24, ("p1", Rc))]);
        tl.DeleteSelection();
        Assert.True(p1.ColourAt(23) is null && p1.ColourAt(21) == new Rgb(1, 0.5f, 0) && p1.ColourAt(25) == new Rgb(1, 0.5f, 0));
        tl.Collect();
        var saved = tl.SongData().Obj("tracks").Obj("p1").Arr("colour");
        Assert.True(saved.Count == 3 && saved[0]![2]!.GetValue<string>() == "ff8000"); // [t0, t1, colour, colour]
        tl.Undo();
        Assert.True(Near(p1.ColourAt(23), new Rgb(1, 0.5f, 0))); // (back through the saved 8-bit colour)
        // Paste Here: at the clicked time, into the clicked track.
        sel.Clear();
        tl.Paste(p1, 50);
        Assert.Equal(new Rgb(1, 0.5f, 0), p1.ColourAt(55));
    }

    [Fact]
    public void Dragging_a_selection_and_its_edges()
    {
        var tl = Open([Fx("p1", "Par 1", Par())], beatGap: 0.5f);
        var m = tl.Master;
        var p1 = tl.FixtureTracks[0];
        var sel = tl.Selection;
        // A dark span 4-8 s on the master's dimmer, selected.
        m.DimmerNodes = V((0, 0.5f), (4, 0.5f), (4, 0), (8, 0), (8, 0.5f), (12, 0.5f));
        tl.MarkEdited();
        sel.SetBlocks([B(4, 8, ("master", Rd), ("master", Rm))]);
        Assert.True(tl.OnEdge(Row(tl, "master", Rm), 8, 0.15) && !tl.OnEdge(Row(tl, "master", Rm), 6, 0.15));
        Assert.True(tl.InSelection(Row(tl, "master", Rm), 6));
        // The end from 8 s to 9.7 s: snapped to the bar at 10 s, the nodes on it going with it.
        Drag(tl, "master", Rm, 8, 9.7);
        Assert.True(sel.Blocks.Count == 1 && sel.Blocks[0].T0 == 4 && Gd.ApproxEqual(sel.Blocks[0].T1, 10));
        Assert.Equal(V((0, 0.5f), (4, 0.5f), (4, 0), (10, 0), (10, 0.5f), (12, 0.5f)), m.DimmerNodes);
        tl.Undo();
        Assert.Equal(8, m.DimmerNodes[3].X); // one undo step
        tl.Redo();
        // An edge with nodes stops at the next node along; the start can't pass the end.
        Drag(tl, "master", Rm, 10, 18);
        Assert.True(Gd.ApproxEqual(sel.Blocks[0].T1, 12) && m.DimmerNodes[3].X == 12);
        tl.Undo();
        Drag(tl, "master", Rm, 4, 15);
        Assert.True(sel.Blocks[0].T0 < sel.Blocks[0].T1 && sel.Blocks[0].T0 <= 10);
        tl.Undo();

        // Moving the whole selection: the areas follow (snapped), the content on release, with ghosts meanwhile.
        sel.SetBlocks([B(4, 10, ("master", Rd), ("master", Rm))]);
        var before = m.DimmerNodes;
        var row = Row(tl, "master", Rm);
        tl.SelectPress(row, 6, grab: 0.15);
        tl.SelectMove(row, 14.2);
        Assert.True(Gd.ApproxEqual(sel.Blocks[0].T0, 12) && m.DimmerNodes.SequenceEqual(before));
        Assert.True(m.Ghost is { At: 12, From: 4 } g && g.Rows.ContainsKey(Rd));
        tl.SelectRelease(row, 13);
        Assert.True(Gd.ApproxEqual(sel.Blocks[0].T0, 12) && Gd.ApproxEqual(sel.Blocks[0].T1, 18));
        Assert.Null(m.Ghost);
        Assert.True(m.DimmerAfter(12) == 0 && m.DimmerAt(17.9) == 0 && Gd.ApproxEqual(m.DimmerAfter(18), 0.5));
        Assert.True(m.DimmerAfter(4) == 0 && m.DimmerAt(9) == 0 && m.DimmerAt(3) == 0.5); // where it was is cleared
        tl.Undo();
        Assert.Equal(before, m.DimmerNodes);
        // A click inside without moving: selects the bar under it; Ctrl+drag inside adds an area.
        sel.SetBlocks([B(4, 10, ("master", Rm))]);
        tl.SelectPress(row, 6.5, grab: 0.15);
        Assert.Null(m.Ghost);
        tl.SelectRelease(row, 6.5);
        Assert.True(sel.Blocks.Count == 1 && sel.Blocks[0].T0 == 6 && sel.Blocks[0].T1 == 8);
        sel.SetBlocks([B(4, 10, ("master", Rm))]);
        Drag(tl, "master", Rm, 6.5, 7, ctrl: true);
        Assert.Equal(2, sel.Blocks.Count);

        // Colour moves with the selection; span boundaries on a dragged edge go with it.
        sel.SetBlocks([B(2, 4, ("p1", Rc))]);
        tl.ApplyStaticColour(new Rgb(1, 0, 0));
        tl.MoveSelection([.. sel.Blocks], 4);
        Assert.True(p1.ColourAt(3) is null && p1.ColourAt(7) == new Rgb(1, 0, 0));
        sel.SetBlocks([B(6, 8, ("p1", Rc))]);
        p1.ColourSegments = [new(2, 6, new Rgb(0, 1, 0), new Rgb(0, 1, 0)), new(6, 8, new Rgb(1, 0, 0), new Rgb(1, 0, 0)), new(12, 14, new Rgb(0, 0, 1), new Rgb(0, 0, 1))];
        tl.MarkEdited();
        Drag(tl, "p1", Rc, 8, 9.8);
        Assert.True(Gd.ApproxEqual(sel.Blocks[0].T1, 10) && Gd.ApproxEqual(p1.ColourSegments[1].T1, 10) && p1.ColourSegments[1].C1 == new Rgb(1, 0, 0));
        Drag(tl, "p1", Rc, 6, 3.9);
        Assert.True(Gd.ApproxEqual(sel.Blocks[0].T0, 4) && Gd.ApproxEqual(p1.ColourSegments[0].T1, 4) && Gd.ApproxEqual(p1.ColourSegments[1].T0, 4));
        Drag(tl, "p1", Rc, 10, 16);
        Assert.True(Gd.ApproxEqual(p1.ColourSegments[1].T1, 12) && p1.ColourSegments[2].T0 == 12); // stopped by the next
        tl.Undo();
        Assert.True(Gd.ApproxEqual(p1.ColourSegments[1].T1, 10));
    }

    [Fact]
    public void The_strobe_row()
    {
        var tl = Open([Fx("mv", "Mover", Mover(("Shutter", "STROBE"), ("Dimmer", "DIMMER")))], beatGap: 0.5f);
        var mv = tl.FixtureTracks[0];
        var m = tl.Master;
        var sel = tl.Selection;
        Assert.True(mv.HasRow(Rs));
        sel.SetBlocks([B(4, 8, ("mv", Rs), ("master", Rs))]);
        tl.ApplyStrobe(StrobeSpeed.Fast);
        Assert.True(mv.StrobeSpeedAt(6) == StrobeSpeed.Fast && m.StrobeAt(6) && !mv.StrobeAt(9));
        tl.ApplyStrobe(StrobeSpeed.Medium);
        Assert.True(mv.StrobeSpeedAt(6) == StrobeSpeed.Medium && mv.StrobeSegments.Count == 1);
        tl.Undo();
        Assert.Equal(StrobeSpeed.Fast, mv.StrobeSpeedAt(6));
        tl.Undo();
        Assert.False(mv.StrobeAt(6));
        tl.Redo();
        tl.Redo();
        tl.Collect();
        Assert.Equal("[[4,8,1]]", Json.Stringify(tl.SongData().Obj("tracks").Obj("mv")!["strobe"]));
        tl.CopySelection();
        sel.SetBlocks([B(20, 21, ("mv", Rs))]);
        tl.Paste();
        Assert.True(mv.StrobeAt(22) && !m.StrobeAt(22));
        sel.SetBlocks([B(5, 6, ("mv", Rs))]);
        tl.DeleteSelection();
        Assert.True(mv.StrobeAt(4.5) && !mv.StrobeAt(5.5) && mv.StrobeAt(7));
        tl.ApplyStrobe(StrobeSpeed.Medium);
        Assert.Equal(new StrobeSpan(4, 8, StrobeSpeed.Medium), mv.StrobeSegments[0]); // rejoined
        sel.SetBlocks([B(4, 8, ("mv", Rs))]);
        Drag(tl, "mv", Rs, 8, 9.8);
        Assert.True(Gd.ApproxEqual(mv.StrobeSegments[0].T1, 10));
        tl.MoveSelection([.. sel.Blocks], 10);
        Assert.True(!mv.StrobeAt(6) && mv.StrobeAt(16) && mv.StrobeAt(19.9));
        tl.ApplyStrobe(null);
        Assert.False(mv.StrobeAt(16));
    }

    [Fact]
    public void The_position_row()
    {
        var tl = Open([Fx("mv", "Mover", Mover(("Dimmer", "DIMMER")))], beatGap: 0.5f);
        var mv = tl.FixtureTracks[0];
        var m = tl.Master;
        var sel = tl.Selection;
        Assert.True(mv.HasRow(Rp));
        sel.SetBlocks([B(4, 8, ("mv", Rp), ("master", Rp))]);
        tl.ApplyPosition("stage");
        Assert.True(mv.PositionAt(6) == "stage" && m.PositionAt(6) == "stage" && mv.PositionAt(9) == "");
        tl.Undo();
        Assert.Equal("", mv.PositionAt(6));
        tl.Redo();
        tl.Collect();
        Assert.Equal("[[4,8,\"stage\"]]", Json.Stringify(tl.SongData().Obj("tracks").Obj("mv")!["position"]));
        tl.CopySelection();
        sel.SetBlocks([B(20, 21, ("mv", Rp))]);
        tl.Paste();
        Assert.True(mv.PositionAt(22) == "stage" && m.PositionAt(22) == "");
        sel.SetBlocks([B(4, 8, ("mv", Rp))]);
        Drag(tl, "mv", Rp, 8, 9.8);
        Assert.True(Gd.ApproxEqual(mv.PositionSegments[0].T1, 10));
        tl.MoveSelection([.. sel.Blocks], 2);
        Assert.True(mv.PositionAt(5) == "" && mv.PositionAt(11) == "stage");
        tl.ApplyPosition("");
        Assert.Equal("", mv.PositionAt(11));
    }

    [Fact]
    public void The_movement_row()
    {
        var tl = Open([Fx("mv", "Mover", Mover())], beatGap: 0.45f); // 133 BPM: a bar is 1.8 s
        var mv = tl.FixtureTracks[0];
        var m = tl.Master;
        var sel = tl.Selection;
        Assert.True(mv.HasRow(Rm));
        Assert.Equal((1, 1), (tl.MovementSize, tl.MovementCycle)); // Medium, 2 bars to start
        sel.SetBlocks([B(4, 8, ("mv", Rm), ("master", Rm))]);
        tl.ApplyMovement("circle");
        var sg = mv.MovementAt(6)!;
        Assert.True(sg.Effect == "circle" && m.MovementAt(6)?.Effect == "circle" && mv.MovementAt(9) is null);
        Assert.True(Gd.ApproxEqual(sg.Period, 3.6) && Gd.ApproxEqual(sg.Size, MovementSpan.SIZE)); // a cycle every two bars
        tl.ApplyMovement("none");
        Assert.Equal("none", mv.MovementAt(6)!.Effect); // a span that holds it still
        tl.Undo();
        Assert.Equal("circle", mv.MovementAt(6)!.Effect);
        tl.CopySelection();
        sel.SetBlocks([B(20, 21, ("mv", Rm))]);
        tl.Paste();
        Assert.True(mv.MovementAt(22)?.Effect == "circle" && Gd.ApproxEqual(mv.MovementAt(22)!.Period, 3.6));
        sel.SetBlocks([B(4, 8, ("mv", Rm))]);
        tl.MoveSelection([.. sel.Blocks], 2);
        Assert.True(mv.MovementAt(5) is null && mv.MovementAt(9)?.Effect == "circle");
        tl.ApplyMovement("");
        Assert.Null(mv.MovementAt(9));
        // Size and cycle: chosen ones go with a new effect, and stay chosen.
        sel.SetBlocks([B(2, 6, ("mv", Rm))]);
        tl.SetMovementSize(2);
        tl.SetMovementCycle(0);
        tl.ApplyMovement("figure8");
        var big = mv.MovementAt(4)!;
        Assert.True(big.Effect == "figure8" && Gd.ApproxEqual(big.Size, 0.08) && Gd.ApproxEqual(big.Period, 1.8));
        sel.SetBlocks([B(10, 12, ("mv", Rm))]);
        tl.ApplyMovement("circle");
        Assert.True(Gd.ApproxEqual(mv.MovementAt(11)!.Size, 0.08));
        // The menu shows the selection's; changing them changes the selected movements, effects and gaps kept.
        sel.Clear();
        tl.SetMovementSize(1); // (nothing selected: only what new ones get)
        sel.SetBlocks([B(0, 14, ("mv", Rm))]);
        tl.SyncMovementOptions();
        Assert.Equal((2, 0), (tl.MovementSize, tl.MovementCycle));
        tl.SetMovementSize(0);
        tl.SetMovementCycle(2);
        Assert.True(Gd.ApproxEqual(mv.MovementAt(4)!.Size, 0.02) && Gd.ApproxEqual(mv.MovementAt(4)!.Period, 7.2) && mv.MovementAt(4)!.Effect == "figure8");
        Assert.True(Gd.ApproxEqual(mv.MovementAt(11)!.Size, 0.02) && mv.MovementAt(11)!.Effect == "circle");
        Assert.True(mv.MovementAt(8) is null && Gd.ApproxEqual(mv.MovementAt(22)!.Size, 0.04));
        tl.Undo();
        Assert.True(Gd.ApproxEqual(mv.MovementAt(4)!.Period, 1.8));
    }

    [Fact]
    public void Fixture_and_head_dimmer_rows_inherit()
    {
        var tl = Open([Fx("bar", "Bar", Bar())]);
        var m = tl.Master;
        var (bar, head) = (tl.FixtureTracks[0], tl.FixtureTracks[1]);
        var sel = tl.Selection;
        Assert.True(bar.Inherits && head.Inherits && !m.Inherits);
        Assert.False(bar.DimmerDefinedAt(5));
        // Values over the selected stretch only.
        sel.SetBlocks([B(4, 8, ("bar", Rd))]);
        tl.ApplyDimmer(DimShape.Half);
        Assert.Equal(V((4, 0.5f), (8, 0.5f)), bar.DimmerNodes);
        Assert.True(bar.DimmerDefinedAt(6) && !bar.DimmerDefinedAt(3) && !bar.DimmerDefinedAt(9));
        // A head inherits its fixture's, which inherits the master's.
        m.DimmerNodes = V((0, 0.25f));
        Assert.True(Gd.ApproxEqual(tl.EffectiveDimmer(head, 6, false), 0.5) && Gd.ApproxEqual(tl.EffectiveDimmer(head, 2, false), 0.25));
        Assert.True(Gd.ApproxEqual(tl.EffectiveDimmer(bar, 10, false), 0.25));
        // Pulse In on an empty head: from the fixture's 50 % to the master's 25 %.
        sel.SetBlocks([B(6, 10, ("bar/h0", Rd))]);
        tl.ApplyDimmer(DimShape.PulseIn);
        Assert.Equal(V((6, 0.5f), (8, 1), (10, 0.25f)), head.DimmerNodes);
        // Copying a fixture row: just where it has values; an empty row: nothing to paste.
        sel.SetBlocks([B(0, 20, ("bar", Rd))]);
        tl.CopySelection();
        var part = tl.Clipboard!.Tracks[0].Rows[Rd][0];
        Assert.Equal((4.0, 8.0), (part.T0, part.T1));
        head.DimmerNodes = [];
        sel.SetBlocks([B(0, 20, ("bar/h0", Rd))]);
        tl.CopySelection();
        Assert.Null(tl.Clipboard!.Tracks[0].Rows[Rd][0].Nodes);
    }

    [Fact]
    public void Mute_and_solo()
    {
        var tl = Open([Fx("p1", "Par 1", Par()), Fx("b1", "Bar 1", TwoHeadBar())]);
        var m = tl.Master;
        var t = tl.FixtureTracks.ToDictionary(tr => tr.Key);
        Assert.Equal(["p1", "b1", "b1/h0", "b1/h1"], t.Keys);
        Assert.False(tl.HasUnsavedTimelines || t.Values.Any(tr => tr.Silenced));
        // A fixture muted: it and its heads left out.
        tl.SetMute(t["b1"], true);
        Assert.True(t["b1"].Silenced && t["b1/h0"].Silenced && t["b1/h1"].Silenced && !t["p1"].Silenced && !m.Silenced);
        Assert.True(tl.HasUnsavedTimelines && !tl.CanUndo); // saved, but not an undo step
        Assert.True(tl.LiveTracks()["b1"].Mute);
        tl.SetMute(t["b1"], false);
        Assert.False(t["b1/h1"].Silenced);
        // A head soloed: it plays with its fixture and the master; the rest don't.
        tl.SetSolo(t["b1/h1"], true);
        Assert.True(!t["b1/h1"].Silenced && !t["b1"].Silenced && !m.Silenced && t["b1/h0"].Silenced && t["p1"].Silenced);
        tl.SetSolo(t["b1"], true);
        Assert.True(!t["b1/h0"].Silenced && t["p1"].Silenced); // its fixture too: all its heads
        tl.SetMute(t["b1/h0"], true);
        Assert.True(t["b1/h0"].Silenced && !t["b1/h1"].Silenced);
        tl.SetMute(m, true);
        Assert.True(m.Silenced && t.Values.All(tr => tr.Silenced)); // the master muted: everything
        tl.SetMute(m, false);
        // Saved, and read back when the tracks are rebuilt.
        tl.Collect();
        var saved = tl.SongData().Obj("tracks")!;
        Assert.True(saved.Obj("b1").Bool("solo") && saved.Obj("b1/h1").Bool("solo") && saved.Obj("b1/h0").Bool("mute"));
        Assert.True(!saved.Obj("master").Has("mute") && !saved.ContainsKey("p1"));
        tl.RefreshFixtures();
        var again = tl.FixtureTracks.ToDictionary(tr => tr.Key);
        Assert.True(again["b1"].Solo && again["b1/h1"].Solo && again["b1/h0"].Mute && again["p1"].Silenced);
        // Undoing an edit leaves them be.
        m.DimmerNodes = V((10, 0.5f));
        tl.MarkEdited();
        tl.Undo();
        Assert.True(again["b1"].Solo && again["b1/h0"].Mute && tl.SongData().Obj("tracks").Obj("b1").Bool("solo"));
    }

    [Fact]
    public void Collapsing_a_timeline()
    {
        var tl = Open([Fx("p1", "Par 1", Par()), Fx("b1", "Bar 1", TwoHeadBar())]);
        var t = tl.FixtureTracks.ToDictionary(tr => tr.Key);
        tl.SelectAll();
        Assert.Single(tl.Selection.SpansFor("b1", Rd));
        tl.SetCollapsed(t["b1"], true);
        Assert.True(t["b1"].Collapsed && t["b1/h0"].Collapsed && t["b1/h1"].Collapsed && !t["p1"].Collapsed); // its heads with it
        Assert.True(tl.Selection.SpansFor("b1", Rd).Count == 0 && tl.Selection.SpansFor("b1/h1", Rd).Count == 0 && tl.Selection.SpansFor("p1", Rd).Count == 1);
        Assert.DoesNotContain(tl.RowList(), r => r.Track == t["b1"]); // area selections pass it by
        Assert.True(tl.HasUnsavedTimelines && !tl.CanUndo);
        tl.Collect();
        var saved = tl.SongData().Obj("tracks")!;
        Assert.True(saved.Obj("b1").Bool("collapsed") && saved.Obj("b1/h1").Bool("collapsed") && !saved.Obj("p1").Has("collapsed"));
        tl.RefreshFixtures();
        t = tl.FixtureTracks.ToDictionary(tr => tr.Key);
        Assert.True(t["b1"].Collapsed); // still collapsed when the tracks are rebuilt
        tl.SetCollapsed(t["b1"], false);
        Assert.True(!t["b1/h0"].Collapsed && !t["b1/h1"].Collapsed);
        tl.Collect();
        Assert.False(tl.SongData().Obj("tracks").Obj("b1").Has("collapsed"));
        // A head on its own; not the master.
        tl.SetCollapsed(t["b1/h1"], true);
        Assert.True(t["b1/h1"].Collapsed && !t["b1"].Collapsed && !t["b1/h0"].Collapsed);
        tl.SetCollapsed(tl.Master, true);
        Assert.False(tl.Master.Collapsed);
    }

    // ------------------------------------------------------------- saving --

    [Fact]
    public void A_songs_timelines_are_saved_when_its_left()
    {
        var song1 = _dir.File("one.song");
        var song2 = _dir.File("two.song");
        File.WriteAllText(song1, "song one");
        File.WriteAllText(song2, "song two");
        var fixtures = new List<PatchedFixture> { Fx("uid-par", "Par", Par()) };
        var store = new TimelineStore(_dir.File("a.timelines"));
        SongTimelines Make()
        {
            var tl = new SongTimelines { Fixtures = () => fixtures, AnalyseStructure = (_, _) => throw new InvalidDataException("no") };
            tl.SetTimelineStore(new TimelineStore(_dir.File("a.timelines")));
            return tl;
        }
        var tl = Make();
        tl.SetTimelineStore(store);
        tl.Open(song1);
        var par = tl.FixtureTracks[0];
        Assert.Equal("uid-par", par.Key);
        tl.Master.DimmerNodes = V((1, 0.25f));
        tl.MarkEdited();
        par.DimmerNodes = V((2, 0.5f), (3, 1));
        tl.MarkEdited();
        Assert.True(tl.HasUnsavedTimelines && store.LoadSong(song1).Count == 0); // unsaved until it's left
        tl.Open(song2);
        Assert.False(tl.HasUnsavedTimelines);
        var saved = store.LoadSong(song1);
        Assert.Equal("[[1,0.25]]", Json.Stringify(saved.Obj("tracks").Obj("master")!["dimmer"]));
        Assert.Equal("[[2,0.5],[3,1]]", Json.Stringify(saved.Obj("tracks").Obj("uid-par")!["dimmer"]));
        Assert.True(tl.Master.DimmerNodes.Count == 0 && tl.FixtureTracks[0].DimmerNodes.Count == 0); // the new song starts empty
        tl.Open(song1);
        Assert.Equal(V((1, 0.25f)), tl.Master.DimmerNodes);
        Assert.Equal(V((2, 0.5f), (3, 1)), tl.FixtureTracks[0].DimmerNodes);

        // A new session reads them from the folder.
        var tl2 = Make();
        tl2.Open(song1);
        Assert.True(tl2.FixtureTracks[0].DimmerNodes.Count == 2 && tl2.Master.DimmerNodes.Count == 1);
        // A removed fixture's timeline is kept, and comes back with it.
        fixtures.Clear();
        tl2.RefreshFixtures();
        tl2.MarkEdited();
        tl2.Flush();
        Assert.True(new TimelineStore(_dir.File("a.timelines")).LoadSong(song1).Obj("tracks")!.ContainsKey("uid-par"));
        fixtures.Add(Fx("uid-par", "Par", Par()));
        tl2.RefreshFixtures();
        Assert.Equal(2, tl2.FixtureTracks[0].DimmerNodes.Count);
        // Another show's folder: its own (here, none).
        tl2.SetTimelineStore(new TimelineStore(_dir.File("b.timelines")));
        Assert.Empty(tl2.Master.DimmerNodes);
    }

    [Fact]
    public void Generating_into_songs_not_open()
    {
        var songs = Enumerable.Range(0, 2).Select(k => _dir.File($"s{k}.wav")).ToList();
        foreach (var s in songs)
            File.WriteAllText(s, "song " + s);
        var tl = new SongTimelines { Fixtures = () => [Fx("p1", "Par", Par())], AnalyseStructure = (_, _) => throw new InvalidDataException("no") };
        Assert.Equal("save the show first", tl.GenerateInto(songs[0], null));
        tl.SetTimelineStore(new TimelineStore(_dir.File("show.timelines")));
        Assert.Equal("its structure hasn't been analysed", tl.GenerateInto(songs[0], null));
        var a = StructureAnalysis("Intro");
        tl.AddStructure(songs[0], a, null);
        Assert.Same(a, tl.CachedStructure(songs[0]));
        Assert.Equal("", tl.GenerateInto(songs[0], null));
        var store = new TimelineStore(_dir.File("show.timelines"));
        Assert.NotEmpty(store.LoadSong(songs[0]).Obj("tracks").Obj("master").Arr("dimmer"));
        Assert.False(store.HasSong(songs[1]));
        // Opened later: its new show, and an undo back to what it was (nothing).
        tl.Open(songs[0]);
        tl.SetDuration(40);
        Assert.True(tl.CanUndo && tl.Master.DimmerNodes.Count > 2);
        tl.Undo();
        Assert.Empty(tl.Master.DimmerNodes); // what it was: nothing
        tl.Redo();
        // The open song: its tracks replaced in place, saved, one undo step.
        tl.Master.DimmerNodes = V((0, 0.33f), (40, 0.33f));
        tl.MarkEdited();
        var edited = tl.Master.DimmerNodes;
        Assert.Equal("", tl.GenerateInto(songs[0], null));
        Assert.True(!tl.Master.DimmerNodes.SequenceEqual(edited) && !tl.HasUnsavedTimelines);
        tl.Undo();
        Assert.Equal(edited, tl.Master.DimmerNodes);
        // A temporary show (DJ Perform Mode) won't go over a song's own timelines; it's written in the background.
        var gen = tl.GenerateFor(songs[0])!;
        Assert.Equal("it has timelines of its own", tl.PutGenerated(songs[0], gen, temporary: true));
        tl.AddStructure(songs[1], StructureAnalysis("Verse"), null);
        Assert.Equal("", tl.PutGenerated(songs[1], tl.GenerateFor(songs[1])!, temporary: true));
        Pump(tl, () => !tl.IsSaving);
        Assert.True(new TimelineStore(_dir.File("show.timelines")).IsTemporary(songs[1]));
    }

    [Fact]
    public void Generate_makes_a_new_variation_each_press_and_clears_left_out_fixtures()
    {
        var fixtures = new List<PatchedFixture> { Fx("p1", "Par 1", Par()), Fx("p2", "Par 2", Par()) };
        fixtures[1].ExcludeGenerate = true;
        var tl = Open(fixtures, duration: 40);
        Assert.False(tl.GenerateShow()); // no structure yet
        tl.AddStructure(tl.Path, StructureAnalysis("Verse"), null);
        tl.FixtureTracks[1].DimmerNodes = V((0, 0.5f));
        Assert.True(tl.GenerateShow());
        var first = tl.FixtureTracks[0].DimmerNodes;
        Assert.NotEmpty(first);
        Assert.Empty(tl.FixtureTracks[1].DimmerNodes); // left out: cleared
        Assert.True(tl.CanUndo);
        tl.GenerateShow();
        Assert.NotEqual(first, tl.FixtureTracks[0].DimmerNodes);
        var placeholder = tl.PlaceholderFor("x.mp3", null, 128, 0.2);
        Assert.Contains("p1", placeholder.Keys);
        Assert.NotEmpty(placeholder.Show["master"].Dimmer);
    }

    [Fact]
    public void A_positions_spread_is_its_points_mean_distance_from_their_middle()
    {
        Assert.True(Gd.ApproxEqual(SongTimelines.Spread([new Vector2(0.4f, 0.5f), new Vector2(0.6f, 0.5f)]), 0.1));
        Assert.Equal(0, SongTimelines.Spread([new Vector2(0.4f, 0.5f)]));
    }

    // ------------------------------------------------------------- structure --

    [Fact]
    public void VirtualDjs_grid_on_an_analysed_structure()
    {
        var a = new SongAnalysis
        {
            Duration = 40,
            Bpm = 117,
            Sections = [new(0, 8.3, "Intro", 0.2, 0), new(8.3, 8.6, "Build", 0.5, 1), new(8.6, 24.9, "Chorus", 0.9, 2), new(24.9, 40, "Outro", 0.3, 3)],
        };
        var song = new DjSong { ScanBpm = 120, Phase = 0.25, Length = 40 }; // 0.5 s beats, 2 s bars from 0.25 s
        SongTimelines.ApplyVdjGrid(a, song);
        Assert.Equal(120, a.Bpm);
        Assert.True(a.BeatTimes.Length == 80 && Gd.ApproxEqual(a.BeatTimes[0], 0.25));
        Assert.True(a.Downbeats.Length == 20 && Gd.ApproxEqual(a.Downbeats[1], 2.25));
        Assert.Equal([0.0, 8.25, 24.25], a.Sections.Select(s => Gd.Snapped(s.Start, 0.01)));
        for (var i = 1; i < a.Sections.Count; i++)
            Assert.Equal(a.Sections[i - 1].End, a.Sections[i].Start);
        Assert.Equal(40, a.Sections[^1].End);
    }
}

using System.Numerics;
using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Fixtures;
using Cuedeck.Timelines;

namespace Cuedeck.Tests.Timelines;

/// <summary>
/// Lighting timelines as data: timeline_store_test.gd's store, and the model checks from timeline_track_test.gd (the
/// rest of it drives the timeline's screen, which comes with DJ Edit Mode).
/// </summary>
public sealed class TimelineTests : IDisposable
{
    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    static List<Vector2> V(params (float T, float L)[] nodes) => [.. nodes.Select(n => new Vector2(n.T, n.L))];

    /// <summary>
    /// A made-up song: <paramref name="n"/> bytes of a pattern from <paramref name="seed"/>.
    /// </summary>
    string Song(string name, int seed, int n = 200000)
    {
        var path = _dir.File("songs/" + name).Replace('\\', '/');
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. Enumerable.Range(0, n).Select(i => (byte)((i * 31 + 7 + seed * 13) & 255))]);
        return path;
    }

    // ----------------------------------------------------------------- store --

    [Fact]
    public void Fingerprints_match_sLights()
    {
        // What Godot's TimelineStore.fingerprint_of gave for these files: under 64 KB, between, and over 128 KB.
        foreach (var (n, want) in new[] { (1000, "2eb818c468d2aa0a"), (70000, "2615483516715943"), (200000, "874d3f3f20fe4eea") })
        {
            var path = _dir.File($"f{n}.bin");
            File.WriteAllBytes(path, [.. Enumerable.Range(0, n).Select(i => (byte)((i * 31 + 7) & 255))]);
            Assert.Equal(want, TimelineStore.FingerprintOf(path));
        }
    }

    [Fact]
    public void The_store_saves_and_finds_songs()
    {
        Assert.Equal("D:/Shows/Club.timelines", TimelineStore.FolderForShow("D:/Shows/Club.slight"));
        var a = Song("a.wav", 1);
        var b = Song("b.wav", 2);
        var fa = TimelineStore.FingerprintOf(a);
        Assert.Equal(16, fa.Length);
        Assert.Equal(fa, TimelineStore.FingerprintOf(a));
        Assert.NotEqual(fa, TimelineStore.FingerprintOf(b));
        Assert.Equal("", TimelineStore.FingerprintOf(_dir.File("nope.wav")));

        var dir = _dir.File("a.timelines").Replace('\\', '/');
        var st = new TimelineStore(dir);
        Assert.Empty(st.LoadSong(a));
        var data = (JsonObject)Json.Parse(
            """{"tracks": {"master": {"dimmer": [[1.0, 0.5], [2.0, 1.0]]}, "abc123/h0": {"dimmer": [[0.0, 0.0]]}}, "structure": {"sections": [{"start": 0.0, "end": 10.0, "label": "Intro"}]}}"""
        )!;
        Assert.Equal("", st.SaveSong(a, data));
        Assert.True(File.Exists($"{dir}/{fa}.json"));

        var back = new TimelineStore(dir).LoadSong(a);
        Assert.Equal(("sLight timelines", 1, a, fa), (back.Str("format"), back.Int("version"), back.Get("track").Str("path"), back.Get("track").Str("fingerprint")));
        Assert.True(Json.SameTree(data["tracks"], back["tracks"]) && Json.SameTree(data["structure"], back["structure"]));
        Assert.Equal("a", st.Index().Get(fa).Str("title"));

        // Moved or renamed, a song keeps its timelines.
        var moved = _dir.File("songs/moved.wav").Replace('\\', '/');
        File.Move(a, moved);
        Assert.True(Json.SameTree(data["tracks"], new TimelineStore(dir).LoadSong(moved)["tracks"]));
        File.Move(moved, a);

        // A copy for Save As.
        var copy = st.CopyTo(_dir.File("b.timelines").Replace('\\', '/'));
        Assert.True(Json.SameTree(data["tracks"], copy.LoadSong(a)["tracks"]));

        // From a newer sLight: left alone.
        File.WriteAllText($"{dir}/{fa}.json", """{"version": 99, "tracks": {"master": {}}}""");
        Assert.Empty(new TimelineStore(dir).LoadSong(a));
        Assert.NotEqual("", new TimelineStore("").SaveSong(a, data));
    }

    [Fact]
    public void The_store_knows_its_songs_without_reading_them()
    {
        var a = Song("a.wav", 1);
        var b = Song("b.wav", 2, 1234);
        var st = new TimelineStore(_dir.File("t").Replace('\\', '/'));
        Assert.Equal(HasTimelines.No, st.HasSongQuick(a)); // nothing indexed
        st.SaveSong(a, new JsonObject { ["temporary"] = true, ["tracks"] = new JsonObject() });
        Assert.Equal(HasTimelines.Yes, st.HasSongQuick(a));
        Assert.Equal(HasTimelines.No, st.HasSongQuick(b)); // not its size
        var c = Song("c.wav", 1); // a's twin, elsewhere
        Assert.Equal(HasTimelines.Maybe, st.HasSongQuick(c));
        Assert.True(st.HasSong(c)); // the same fingerprint
        Assert.True(st.IsTemporary(a));
        Assert.True(st.RemoveSong(a));
        Assert.False(st.HasSong(a));
        Assert.False(st.RemoveSong(a));
    }

    // ----------------------------------------------------------------- model --

    [Fact]
    public void Rows_come_from_a_fixtures_channels()
    {
        TimelineRow P = TimelineRow.Position, S = TimelineRow.Strobe, M = TimelineRow.Movement, C = TimelineRow.Colour, D = TimelineRow.Dimmer;
        Assert.Equal([P, S, M, C, D], TimelineTrack.RowsForRoles(["PAN", "PAN_FINE", "TILT", "DIMMER", "STROBE", "COLOR_WHEEL", "GOBO"]));
        Assert.Equal([C, D], TimelineTrack.RowsForRoles(["DIMMER", "RED", "GREEN", "BLUE"]));
        Assert.Equal([C, D], TimelineTrack.RowsForRoles(["RED", "GREEN", "BLUE", "WHITE"])); // the virtual dimmer
        Assert.Equal([C], TimelineTrack.RowsForRoles(["COLOR_WHEEL", "GOBO"]));
        Assert.Equal([D], TimelineTrack.RowsForRoles(["DIMMER"]));
        Assert.Equal([P, M], TimelineTrack.RowsForRoles(["PAN"]));

        var master = new TimelineTrack();
        Assert.Equal(5, master.Rows.Count);
        var fixture = new TimelineTrack { Target = TimelineTarget.Fixture, Rows = [D, M] }; // any order in
        Assert.Equal([M, D], fixture.Rows);
        Assert.False(fixture.HasRow(P));
    }

    [Fact]
    public void The_dimmer_line()
    {
        var tr = new TimelineTrack();
        Assert.Equal(1, tr.DimmerAt(5)); // no nodes: a flat 100 %
        tr.DimmerNodes = V((0.2f, 0), (0.6f, 1));
        Assert.Equal((0.0, 1.0), (tr.DimmerAt(0), tr.DimmerAt(0.9))); // holding level outside
        Assert.Equal(0.5, tr.DimmerAt(0.4), 6);

        Assert.Equal(1, tr.AddNodeAt(0.4));
        Assert.Equal(0.4f, tr.DimmerNodes[1].X);
        Assert.Equal(0.5f, tr.DimmerNodes[1].Y, 6); // 32-bit, as sLight's
        tr.MoveNode(1, 0.9, 1.5); // kept before the next node, and at most full
        Assert.Equal(new Vector2(0.6f, 1), tr.DimmerNodes[1]);
        tr.RemoveNode(1);
        Assert.Equal(2, tr.DimmerNodes.Count);

        // A stretch moved up together, kept in range.
        Assert.Equal([0, 1], tr.MoveLine(0.4, 0.5, tr.DimmerNodes));
        Assert.Equal(V((0.2f, 0), (0.6f, 1)), tr.DimmerNodes); // the top one already full: no room
        Assert.Equal([0], new TimelineTrack().MoveLine(1, 0, []));
        var flat = new TimelineTrack();
        flat.MoveLine(2, -0.25, []);
        Assert.Equal(V((0, 0.75f)), flat.DimmerNodes); // a node made to carry it
    }

    [Fact]
    public void A_span_of_the_dimmer_is_cleared()
    {
        var tr = new TimelineTrack { DimmerNodes = V((0, 0.2f), (4, 1), (6, 0.6f), (10, 0.2f)) };
        Assert.True(tr.ClearDimmer(2, 8));
        var n = tr.DimmerNodes;
        Assert.Equal(V((0, 0.2f), (2, 0.6f), (2, 0), (8, 0), (8, 0.4f), (10, 0.2f)), n); // steps down and back up
        Assert.Equal(0.4, tr.DimmerAt(1), 6);
        Assert.Equal(0.3, tr.DimmerAt(9), 6);
        Assert.Equal((0.0, 0.0), (tr.DimmerAt(3), tr.DimmerAt(7.9)));
        Assert.False(tr.ClearDimmer(2, 8));
        Assert.Equal(n, tr.DimmerNodes);

        tr.DimmerNodes = [];
        tr.ClearDimmer(3, 5);
        Assert.Equal(V((3, 1), (3, 0), (5, 0), (5, 1)), tr.DimmerNodes); // a flat line: a gap at 0
        tr.DimmerNodes = V((0, 0), (10, 1));
        tr.ClearDimmer(0, 5);
        Assert.Equal(V((0, 0), (5, 0), (5, 0.5f), (10, 1)), tr.DimmerNodes); // from the start: no node before it
        tr.DimmerNodes = V((2, 0), (4, 0));
        tr.ClearDimmer(2.5, 3.5);
        Assert.Equal(V((2, 0), (2.5f, 0), (3.5f, 0), (4, 0)), tr.DimmerNodes); // dark outside already
        Assert.False(tr.ClearDimmer(5, 5));
    }

    [Fact]
    public void A_stretch_of_the_dimmer_is_copied_and_pasted()
    {
        var tr = new TimelineTrack { DimmerNodes = V((0, 0.2f), (4, 1), (6, 0.6f), (10, 0.2f)) };
        var seg = tr.DimmerSegment(2, 8);
        Assert.Equal(V((0, 0.6f), (2, 1), (4, 0.6f), (6, 0.4f)), seg);
        tr.DimmerNodes = V((0, 0.5f), (20, 0.5f));
        Assert.True(tr.ReplaceDimmer(10, 16, seg));
        foreach (var (t, want) in new[] { (10.5, 0.7), (12.0, 1.0), (13.0, 0.8), (15.9, 0.41) })
            Assert.Equal(want, tr.DimmerAt(t), 5);
        Assert.Equal((0.5, 0.5, 0.5), (tr.DimmerAt(9), tr.DimmerAfter(16), tr.DimmerAt(18)));
        Assert.False(tr.ReplaceDimmer(10, 16, seg));

        // Cut off at the song's end.
        var m = new TimelineTrack { End = 12 };
        m.ReplaceDimmer(10, 16, seg);
        Assert.Equal(3, m.DimmerNodes.Count);
        Assert.Equal(12, m.DimmerNodes[^1].X);
        Assert.Equal(1, m.DimmerNodes[^1].Y, 5);
    }

    [Fact]
    public void A_fixtures_dimmer_inherits_outside_its_nodes()
    {
        var tr = new TimelineTrack { Target = TimelineTarget.Fixture, DimmerNodes = V((2, 0.5f), (6, 0.5f)) };
        Assert.True(tr.Inherits);
        Assert.False(tr.DimmerDefinedAt(1));
        Assert.True(tr.DimmerDefinedAt(4));
        Assert.Equal((2.0, 4.0), tr.DimmerDefinedSpan(0, 4));
        Assert.Null(tr.DimmerDefinedSpan(7, 9));
        Assert.Equal((0.0, 9.0), new TimelineTrack().DimmerDefinedSpan(0, 9));

        // Cleared past its end: no step back up after it, as it has no line there to keep.
        tr.ClearDimmer(4, 8);
        Assert.Equal(V((2, 0.5f), (4, 0.5f), (4, 0), (8, 0)), tr.DimmerNodes);
    }

    [Fact]
    public void Strobe_spans()
    {
        var tr = new TimelineTrack();
        Assert.False(tr.StrobeAt(1));
        Assert.True(tr.ReplaceStrobe(2, 4, [new StrobeSpan(0, 2)]));
        Assert.True(tr.StrobeAt(3) && !tr.StrobeAt(5));
        Assert.False(tr.ReplaceStrobe(2, 4, [new StrobeSpan(0, 2)]));
        tr.ReplaceStrobe(4, 6, [new StrobeSpan(0, 2)]);
        Assert.Equal([new StrobeSpan(2, 6, StrobeSpeed.Medium)], tr.StrobeSegments); // touching spans join
        tr.ReplaceStrobe(3, 4, []);
        Assert.Equal([new StrobeSpan(2, 3), new StrobeSpan(4, 6)], tr.StrobeSegments);
        tr.ReplaceStrobe(6, 8, [new StrobeSpan(0, 2, StrobeSpeed.Fast)]);
        Assert.Equal(3, tr.StrobeSegments.Count); // another speed beside it: kept apart
        Assert.Equal((StrobeSpeed.Fast, StrobeSpeed.Medium), (tr.StrobeSpeedAt(7)!.Value, tr.StrobeSpeedAt(5)!.Value));
        Assert.Null(tr.StrobeSpeedAt(3.5));
        Assert.Equal([new StrobeSpan(0, 0.5), new StrobeSpan(1.5, 2.5)], tr.StrobeSegment(2.5, 5));
    }

    [Fact]
    public void Colour_spans_fade_and_are_cut()
    {
        var red = new Rgb(1, 0, 0);
        var blue = new Rgb(0, 0, 1);
        var tr = new TimelineTrack();
        Assert.Null(tr.ColourAt(1));
        Assert.True(tr.ReplaceColour(0, 10, [new ColourSpan(0, 10, red, blue)]));
        Assert.Equal(new Rgb(0.5f, 0, 0.5f), tr.ColourAt(5));
        Assert.False(tr.ReplaceColour(0, 10, [new ColourSpan(0, 10, red, blue)]));

        // Copied from the middle: cut, keeping its colours there.
        var seg = tr.ColourSegment(2, 4);
        Assert.Equal([new ColourSpan(0, 2, new Rgb(0.8f, 0, 0.2f), new Rgb(0.6f, 0, 0.4f))], seg, new ColourComparer());

        // Cleared in the middle: two spans either side, keeping their colours at the cut.
        tr.ReplaceColour(4, 6, []);
        Assert.Equal(2, tr.ColourSegments.Count);
        Assert.Null(tr.ColourAt(5));
        Assert.True(ColourSpan.ApproxEqual(new Rgb(0.6f, 0, 0.4f), tr.ColourSegments[0].C1));
        Assert.True(ColourSpan.ApproxEqual(new Rgb(0.4f, 0, 0.6f), tr.ColourSegments[1].C0)); // 32-bit, as in Godot
    }

    sealed class ColourComparer : IEqualityComparer<ColourSpan>
    {
        public bool Equals(ColourSpan? a, ColourSpan? b) =>
            a is not null && b is not null && Gd.ApproxEqual(a.T0, b.T0) && Gd.ApproxEqual(a.T1, b.T1) && ColourSpan.ApproxEqual(a.C0, b.C0) && ColourSpan.ApproxEqual(a.C1, b.C1);

        public int GetHashCode(ColourSpan s) => 0;
    }

    [Fact]
    public void Position_and_movement_spans()
    {
        var tr = new TimelineTrack();
        tr.ReplacePosition(0, 4, [new PositionSpan(0, 4, "front")]);
        tr.ReplacePosition(4, 8, [new PositionSpan(0, 4, "front")]);
        Assert.Equal([new PositionSpan(0, 8, "front")], tr.PositionSegments);
        tr.ReplacePosition(2, 3, [new PositionSpan(0, 1, "back")]);
        Assert.Equal(["front", "back", "front"], tr.PositionSegments.Select(p => p.Position));
        Assert.Equal(("back", ""), (tr.PositionAt(2.5), tr.PositionAt(9)));

        tr.ReplaceMovement(0, 4, [new MovementSpan(0, 4, "circle", 2, 0.05)]);
        var m = tr.MovementAt(1)!;
        var (pan, tilt) = m.Offset(0.5); // a quarter of the way round
        Assert.Equal((0.0, 0.05), (Math.Round(pan, 9), Math.Round(tilt, 9)));
        Assert.Equal((0.0, 0.0), new MovementSpan(0, 1).Offset(0.3));
        Assert.Equal((0.05, 0.0), Round(new MovementSpan(0, 1, "pan_sweep", 4, 0.05).Offset(1)));
        Assert.Equal((0.0, 0.05), Round(new MovementSpan(0, 1, "tilt_sweep", 4, 0.05).Offset(1)));
        Assert.Equal((0.05, 0.0), Round(new MovementSpan(0, 1, "figure8", 4, 0.05).Offset(1)));
        Assert.Equal((0.0, 0.05), Round(new MovementSpan(0, 1, "circle", 4, 0.05, Phase: 0.25).Offset(0)));
    }

    static (double, double) Round((double A, double B) v) => (Math.Round(v.A, 9), Math.Round(v.B, 9));

    [Fact]
    public void A_track_is_saved_as_sLight_saved_it()
    {
        var tr = new TimelineTrack { Key = "uid-par", Target = TimelineTarget.Fixture };
        tr.DimmerNodes = V((1.0001f, 0.25f), (2, 1));
        tr.ColourSegments = [new ColourSpan(0, 2, new Rgb(1, 0, 0), new Rgb(0, 0, 1))];
        tr.StrobeSegments = [new StrobeSpan(1, 2, StrobeSpeed.Fast)];
        tr.PositionSegments = [new PositionSpan(0, 3, "p1")];
        tr.MovementSegments = [new MovementSpan(0, 3, "circle", 2, 0.05), new MovementSpan(3, 4, "figure8", 2, 0.05, 0.5)];
        tr.Mute = true;
        var entry = TimelineFormat.WriteTrack(tr, new JsonObject { ["kept"] = 1 })!;
        Assert.Equal(
            Json.Parse(
                """
                {"kept":1,"dimmer":[[1.0,0.25],[2.0,1.0]],"colour":[[0.0,2.0,"ff0000","0000ff"]],
                 "movement":[[0.0,3.0,"circle",2.0,0.05],[3.0,4.0,"figure8",2.0,0.05,0.5]],
                 "position":[[0.0,3.0,"p1"]],"strobe":[[1.0,2.0,2]],"mute":true}
                """
            ),
            entry,
            new TreeComparer()
        );

        var back = new TimelineTrack { Target = TimelineTarget.Fixture };
        TimelineFormat.ReadTrack(back, entry);
        Assert.Equal(V((1, 0.25f), (2, 1)), back.DimmerNodes);
        Assert.Equal(tr.StrobeSegments, back.StrobeSegments);
        Assert.Equal(tr.PositionSegments, back.PositionSegments);
        Assert.Equal(tr.MovementSegments, back.MovementSegments);
        Assert.Equal(new Rgb(1, 0, 0), back.ColourSegments[0].C0);
        Assert.True(back.Mute && !back.Solo);

        Assert.Null(TimelineFormat.WriteTrack(new TimelineTrack(), null)); // nothing: no entry
        // Bad entries are passed over: a backwards span, an unknown movement, a short colour.
        TimelineFormat.ReadTrack(back, Json.Parse("""{"strobe":[[2,1]],"movement":[[0,1,"spin"]],"colour":[[0,1,"ff0000"]],"position":[[0,1]]}"""));
        Assert.Empty(back.StrobeSegments.Concat<object>(back.MovementSegments).Concat(back.ColourSegments).Concat(back.PositionSegments));
        TimelineFormat.ReadTrack(back, Json.Parse("""{"strobe":[[0,1]]}"""));
        Assert.Equal(StrobeSpeed.Medium, back.StrobeSegments[0].Speed); // saved before speeds
    }

    sealed class TreeComparer : IEqualityComparer<JsonNode?>
    {
        public bool Equals(JsonNode? a, JsonNode? b) => Json.SameTree(a, b);

        public int GetHashCode(JsonNode? n) => 0;
    }

    // ------------------------------------------------------------- selection --

    [Fact]
    public void A_selection_spans_rows_of_several_tracks()
    {
        var sel = new TimelineSelection();
        var changes = 0;
        sel.Changed += () => changes++;
        Assert.True(sel.IsEmpty);
        sel.Clear();
        Assert.Equal(0, changes); // nothing to clear

        var dimmer = TimelineSelection.RowId("master", TimelineRow.Dimmer);
        var colour = TimelineSelection.RowId("uid/h0", TimelineRow.Colour);
        Assert.Equal("master|4", dimmer);
        sel.SetBlocks([SelectionBlock.Of([dimmer, colour], 5, 2), SelectionBlock.Of([dimmer], 8, 9)]);
        Assert.Equal(1, changes);
        Assert.Equal([(2.0, 5.0), (8.0, 9.0)], sel.SpansFor("master", TimelineRow.Dimmer));
        Assert.True(sel.Has("uid/h0", TimelineRow.Colour, 3));
        Assert.False(sel.Has("uid/h0", TimelineRow.Colour, 8.5));
        Assert.Empty(sel.SpansFor("master", TimelineRow.Strobe));
        sel.Clear();
        Assert.True(sel.IsEmpty && changes == 2);
        Assert.Empty(sel.SpansFor("master", TimelineRow.Dimmer));
    }
}

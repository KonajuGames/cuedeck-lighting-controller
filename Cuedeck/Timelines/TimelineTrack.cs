using System.Numerics;
using Cuedeck.Common;
using Cuedeck.Fixtures;

namespace Cuedeck.Timelines;

/// <summary>
/// One lighting timeline of a song in DJ Edit Mode: the master's (every fixture), one fixture's, or one head's. It has
/// a row for each kind of thing its target can do (see <see cref="RowsForRoles"/>):
/// <list type="bullet">
/// <item>Dimmer: a line of nodes (seconds, level 0 to 1). The master's runs the whole song (a flat 100 % with no
/// nodes); a fixture's or head's covers only from its first node to its last, and elsewhere it inherits.</item>
/// <item>Colour: spans with a colour at each end (the same for a steady colour, two for a fade); outside them the row
/// sets nothing.</item>
/// <item>Strobe: spans where the strobe's on, at a speed.</item>
/// <item>Position and Movement, for moving heads: a named position, and a shape traced around it.</item>
/// </list>
/// The data and its edits, without the drawing: sLight's <c>TimelineTrack</c>, whose drawing and mouse handling come
/// with DJ Edit Mode's screen. Nodes are 32-bit floats, as Godot's Vector2 kept them.
/// </summary>
sealed class TimelineTrack
{
    public static readonly TimelineRow[] RowOrder = [TimelineRow.Position, TimelineRow.Strobe, TimelineRow.Movement, TimelineRow.Colour, TimelineRow.Dimmer];

    public static readonly Dictionary<TimelineRow, string> RowNames = new()
    {
        [TimelineRow.Position] = "Position",
        [TimelineRow.Strobe] = "Strobe",
        [TimelineRow.Movement] = "Movement",
        [TimelineRow.Colour] = "Colour",
        [TimelineRow.Dimmer] = "Dimmer",
    };

    public static readonly Dictionary<StrobeSpeed, string> StrobeNames = new()
    {
        [StrobeSpeed.Slow] = "Slow",
        [StrobeSpeed.Medium] = "Medium",
        [StrobeSpeed.Fast] = "Fast",
    };

    List<TimelineRow> _rows = [.. RowOrder];

    public TimelineTarget Target { get; set; } = TimelineTarget.Master;
    public string Title { get; set; } = "Master";

    /// <summary>
    /// Its key in a song's timelines: "master", a fixture's uid, or "&lt;uid&gt;/h&lt;n&gt;" for a head.
    /// </summary>
    public string Key { get; set; } = "master";

    /// <summary>
    /// The rows it has, in <see cref="RowOrder"/> (whatever order they're given in).
    /// </summary>
    public IReadOnlyList<TimelineRow> Rows
    {
        get => _rows;
        set => _rows = [.. RowOrder.Where(value.Contains)];
    }

    public bool HasRow(TimelineRow r) => _rows.Contains(r);

    /// <summary>
    /// The song's length in seconds, which edits are cut off at; infinite while it isn't known.
    /// </summary>
    public double End { get; set; } = double.PositiveInfinity;

    /// <summary>
    /// The dimmer line: (seconds, level 0 to 1) in time order. None is a flat 100 %; before the first and after the
    /// last it holds level.
    /// </summary>
    public List<Vector2> DimmerNodes { get; set; } = [];

    /// <summary>
    /// In time order, not overlapping.
    /// </summary>
    public List<ColourSpan> ColourSegments { get; set; } = [];
    public List<StrobeSpan> StrobeSegments { get; set; } = [];
    public List<PositionSpan> PositionSegments { get; set; } = [];
    public List<MovementSpan> MovementSegments { get; set; } = [];

    /// <summary>
    /// Left out of playback; a muted master silences every fixture, a muted fixture its heads.
    /// </summary>
    public bool Mute { get; set; }

    /// <summary>
    /// While any track's soloed, only the soloed play (a soloed fixture's heads too, and a soloed head's fixture and
    /// the master, which it inherits from). Not on the master.
    /// </summary>
    public bool Solo { get; set; }

    /// <summary>
    /// Shown as a bar with no rows. Still plays.
    /// </summary>
    public bool Collapsed { get; set; }

    /// <summary>
    /// Left out of playback by mute or solo (its own or another track's): drawn faded.
    /// </summary>
    public bool Silenced { get; set; }

    /// <summary>
    /// Where a dragged selection's content would land, drawn faintly over the track: its rows' content as copied, the
    /// time it was copied from and where it would go. Null for none.
    /// </summary>
    public TrackGhost? Ghost { get; set; }

    /// <summary>
    /// What mute and solo leave playing, worked out once from every track's flags (by key: "master", a fixture's uid,
    /// "&lt;uid&gt;/h&lt;n&gt;"): whether any track's soloed, and the fixtures soloed or with a soloed head. For
    /// <see cref="Plays"/>.
    /// </summary>
    public static MixState MixStateOf(IReadOnlyDictionary<string, (bool Mute, bool Solo)> tracks)
    {
        var fixtures = new HashSet<string>();
        foreach (var (key, f) in tracks)
            if (key != "master" && f.Solo)
                fixtures.Add(FixtureOf(key));
        return new MixState(fixtures.Count > 0, fixtures);
    }

    /// <summary>
    /// Whether track <paramref name="key"/> plays, by mute and solo: the master unless it's muted (muted, nothing
    /// plays); a fixture unless it or the master's muted, and while any track's soloed only if it or one of its heads
    /// is; a head unless it, its fixture or the master's muted, and while any track's soloed only if it or its fixture
    /// is.
    /// </summary>
    public static bool Plays(IReadOnlyDictionary<string, (bool Mute, bool Solo)> tracks, MixState mix, string key)
    {
        bool Muted(string k) => tracks.TryGetValue(k, out var f) && f.Mute;
        bool Soloed(string k) => tracks.TryGetValue(k, out var f) && f.Solo;
        if (Muted("master"))
            return false;
        if (key == "master")
            return true;
        var fixture = FixtureOf(key);
        if (Muted(fixture) || mix.Solo && !mix.SoloFixtures.Contains(fixture))
            return false;
        if (key == fixture)
            return true;
        return !Muted(key) && (!mix.Solo || Soloed(fixture) || Soloed(key));
    }

    /// <summary>
    /// The fixture a track key belongs to: a head's ("&lt;uid&gt;/h&lt;n&gt;") its uid, otherwise the key itself.
    /// </summary>
    public static string FixtureOf(string key)
    {
        var i = key.LastIndexOf("/h", StringComparison.Ordinal);
        return i > 0 && Gd.IsValidInt(key[(i + 2)..]) ? key[..i] : key;
    }

    /// <summary>
    /// The rows a fixture (or head) with these channel roles gets: Position and Movement for pan or tilt, Strobe for a
    /// strobe or shutter, Colour for any colour channel or wheel, Dimmer for a dimmer, or for colour and white channels
    /// without one, which its virtual dimmer scales.
    /// </summary>
    public static List<TimelineRow> RowsForRoles(IReadOnlyCollection<string> roles)
    {
        var output = new List<TimelineRow>();
        var moves = roles.Contains(Roles.Pan) || roles.Contains(Roles.Tilt);
        if (moves)
            output.Add(TimelineRow.Position);
        if (roles.Contains(Roles.Strobe))
            output.Add(TimelineRow.Strobe);
        if (moves)
            output.Add(TimelineRow.Movement);
        if (new[] { Roles.Red, Roles.Green, Roles.Blue, Roles.White, Roles.Amber, Roles.Uv, Roles.ColorWheel }.Any(roles.Contains))
            output.Add(TimelineRow.Colour);
        if (roles.Contains(Roles.Dimmer) || Roles.VirtualDimmer.Any(roles.Contains))
            output.Add(TimelineRow.Dimmer);
        return output;
    }

    // ---------------------------------------------------------------- dimmer --

    /// <summary>
    /// The dimmer level at <paramref name="t"/> (straight between nodes).
    /// </summary>
    public double DimmerAt(double t) => LevelAt(DimmerNodes, t);

    /// <summary>
    /// Whether its dimmer line inherits where it has no values: a fixture's or head's (the master's runs the whole
    /// song).
    /// </summary>
    public bool Inherits => Target != TimelineTarget.Master;

    /// <summary>
    /// Whether the dimmer line has a value at <paramref name="t"/>: anywhere on the master; on a fixture or head, from
    /// its first node to its last.
    /// </summary>
    public bool DimmerDefinedAt(double t) => !Inherits || LineDefinedAt(DimmerNodes, t);

    public static bool LineDefinedAt(IReadOnlyList<Vector2> nodes, double t) => nodes.Count > 0 && t >= nodes[0].X && t <= nodes[^1].X;

    /// <summary>
    /// The part of <paramref name="t0"/> to <paramref name="t1"/> the dimmer line has values over (all of it on the
    /// master), or null where it has none.
    /// </summary>
    public (double T0, double T1)? DimmerDefinedSpan(double t0, double t1)
    {
        if (!Inherits)
            return (t0, t1);
        if (DimmerNodes.Count == 0)
            return null;
        var a = Math.Max(t0, DimmerNodes[0].X);
        var b = Math.Min(t1, DimmerNodes[^1].X);
        return b > a ? (a, b) : null;
    }

    /// <summary>
    /// A node line's level at <paramref name="t"/>: straight between nodes, holding the first or last level outside
    /// them, 1 with none. Where two nodes share a time (a step), the earlier's.
    /// </summary>
    public static double LevelAt(IReadOnlyList<Vector2> nodes, double t)
    {
        var n = nodes.Count;
        if (n == 0)
            return 1;
        if (t <= nodes[0].X)
            return nodes[0].Y;
        for (var i = 1; i < n; i++)
        {
            if (t <= nodes[i].X)
            {
                var a = nodes[i - 1];
                var b = nodes[i];
                return Gd.Lerp(a.Y, b.Y, (t - a.X) / Math.Max(b.X - a.X, 1e-6));
            }
        }
        return nodes[n - 1].Y;
    }

    /// <summary>
    /// The level just after <paramref name="t"/>: where two nodes share a time (a step), the later's
    /// (<see cref="DimmerAt"/> gives the earlier's).
    /// </summary>
    public double DimmerAfter(double t)
    {
        var n = DimmerNodes.Count;
        if (n == 0)
            return 1;
        for (var i = 0; i < n; i++)
        {
            if (DimmerNodes[i].X > t)
            {
                if (i == 0)
                    return DimmerNodes[0].Y;
                var a = DimmerNodes[i - 1];
                var b = DimmerNodes[i];
                return Gd.Lerp(a.Y, b.Y, (t - a.X) / Math.Max(b.X - a.X, 1e-6));
            }
        }
        return DimmerNodes[n - 1].Y;
    }

    double ClampTime(double t) => Math.Clamp(t, 0, End);

    /// <summary>
    /// Adds a node on the line at <paramref name="t"/> (its level the line's there), in time order. Returns its index.
    /// </summary>
    public int AddNodeAt(double t)
    {
        t = ClampTime(t);
        var level = DimmerAt(t);
        var i = 0;
        while (i < DimmerNodes.Count && DimmerNodes[i].X < t)
            i++;
        DimmerNodes.Insert(i, new Vector2((float)t, (float)level));
        return i;
    }

    public void RemoveNode(int i)
    {
        if (i >= 0 && i < DimmerNodes.Count)
            DimmerNodes.RemoveAt(i);
    }

    /// <summary>
    /// Moves node <paramref name="i"/> to <paramref name="t"/> and <paramref name="level"/>: its time stays between its
    /// neighbours' (and in the song), its level 0 to 1.
    /// </summary>
    public void MoveNode(int i, double t, double level)
    {
        if (i < 0 || i >= DimmerNodes.Count)
            return;
        t = ClampTime(t);
        if (i > 0)
            t = Math.Max(t, DimmerNodes[i - 1].X);
        if (i < DimmerNodes.Count - 1)
            t = Math.Min(t, DimmerNodes[i + 1].X);
        DimmerNodes[i] = new Vector2((float)t, (float)Math.Clamp(level, 0, 1));
    }

    /// <summary>
    /// Moves the stretch of line through <paramref name="t"/> by <paramref name="by"/> (in level, up positive) from
    /// the levels in <paramref name="from"/>: the nodes either side of it, or the end node it holds level from, all by
    /// the same, kept so none leaves 0 to 1. With no nodes (a flat 100 %), one's made at 0 s to carry the level.
    /// Returns the indices moved.
    /// </summary>
    public List<int> MoveLine(double t, double by, IReadOnlyList<Vector2> from)
    {
        var nodes = from.ToList();
        if (nodes.Count == 0)
        {
            if (by == 0)
                return [0];
            nodes.Add(new Vector2(0, 1));
        }
        var n = nodes.Count;
        var i = 0;
        while (i < n && nodes[i].X <= t)
            i++;
        List<int> idx = i == 0 ? [0] : i == n ? [n - 1] : [i - 1, i];
        var lo = idx.Min(k => nodes[k].Y);
        var hi = idx.Max(k => nodes[k].Y);
        var d = Math.Clamp(by, -lo, 1 - hi);
        foreach (var k in idx)
            nodes[k] = nodes[k] with { Y = (float)(nodes[k].Y + d) };
        DimmerNodes = nodes;
        return idx;
    }

    /// <summary>
    /// Blacks the dimmer out from <paramref name="t0"/> to <paramref name="t1"/>: the nodes in that span go, the
    /// level there's 0, and a node at each edge keeps the level just outside as it was (a step down at t0, back up at
    /// t1). No edge node where the span reaches the song's start or end, or where the level outside is 0 already.
    /// Returns whether anything changed.
    /// </summary>
    public bool ClearDimmer(double t0, double t1) => ReplaceDimmer(t0, t1, [new Vector2(0, 0), new Vector2((float)(t1 - t0), 0)]);

    /// <summary>
    /// The dimmer line from <paramref name="t0"/> to <paramref name="t1"/> (to copy), as nodes from t0: the level
    /// just after t0, every node strictly inside, and the level just before t1, so it pastes back exactly.
    /// </summary>
    public List<Vector2> DimmerSegment(double t0, double t1)
    {
        var output = new List<Vector2> { new(0, (float)DimmerAfter(t0)) };
        foreach (var p in DimmerNodes)
            if (p.X > t0 && p.X < t1)
                output.Add(new Vector2((float)(p.X - t0), p.Y));
        output.Add(new Vector2((float)(t1 - t0), (float)DimmerAt(t1)));
        return output;
    }

    /// <summary>
    /// Puts <paramref name="segment"/> (nodes from t0, 0 to t1 - t0, as <see cref="DimmerSegment"/> gives) in place
    /// of the dimmer line from <paramref name="t0"/> to <paramref name="t1"/>: the nodes in that span go, the
    /// segment's go in, and a node at each edge keeps the level just outside as it was (a step there, if it differs
    /// from the segment's own edge). Cut off at the song's end. Returns whether anything changed.
    /// </summary>
    public bool ReplaceDimmer(double t0, double t1, IReadOnlyList<Vector2> segment)
    {
        var shift = t0;
        t0 = Math.Max(t0, 0);
        t1 = Math.Min(t1, End);
        if (t1 <= t0 || segment.Count == 0)
            return false;

        // The segment in place, cut to t0..t1 (worked out where it's cut).
        var seg = new List<Vector2>();
        for (var k = 0; k < segment.Count; k++)
        {
            var px = (float)(segment[k].X + shift); // a Vector2 in sLight: 32-bit
            var py = segment[k].Y;
            if (px < t0)
            {
                if (k + 1 < segment.Count) // crossing into the window
                {
                    var qx = (float)(segment[k + 1].X + shift);
                    if (qx > t0)
                        seg.Add(new Vector2((float)t0, (float)Gd.Lerp(py, segment[k + 1].Y, (t0 - px) / (qx - px))));
                }
                continue;
            }
            if (px > t1)
            {
                if (k > 0) // crossing out of it
                {
                    var ox = (float)(segment[k - 1].X + shift);
                    if (ox < t1)
                        seg.Add(new Vector2((float)t1, (float)Gd.Lerp(segment[k - 1].Y, py, (t1 - ox) / (px - ox))));
                }
                break;
            }
            seg.Add(new Vector2(px, py));
        }
        if (seg.Count == 0)
            return false;

        var before = DimmerAt(t0); // just before t0 (the left of any step there)
        var after = DimmerAfter(t1); // just after t1 (the right of any step)
        var nodes = new List<Vector2>();
        var i = 0;
        while (i < DimmerNodes.Count && DimmerNodes[i].X < t0)
            nodes.Add(DimmerNodes[i++]);
        // Edge nodes keep the line outside as it was, only where it has values there (a fixture's or head's doesn't
        // spread past its ends).
        var n = DimmerNodes.Count;
        var lineBefore = !Inherits || n > 0 && t0 > DimmerNodes[0].X;
        var lineAfter = !Inherits || n > 0 && t1 < DimmerNodes[n - 1].X;
        if (t0 > 0 && lineBefore && !Gd.ApproxEqual(before, seg[0].Y))
            nodes.Add(new Vector2((float)t0, (float)before));
        nodes.AddRange(seg);
        if (t1 < End && lineAfter && !Gd.ApproxEqual(after, seg[^1].Y))
            nodes.Add(new Vector2((float)t1, (float)after));
        while (i < DimmerNodes.Count && DimmerNodes[i].X <= t1)
            i++; // inside the span: replaced
        while (i < DimmerNodes.Count)
            nodes.Add(DimmerNodes[i++]);
        if (nodes.SequenceEqual(DimmerNodes))
            return false;
        DimmerNodes = nodes;
        return true;
    }

    // ---------------------------------------------------------------- colour --

    /// <summary>
    /// The colour at <paramref name="t"/> (a span's, faded), or null where there's no span.
    /// </summary>
    public Rgb? ColourAt(double t) => SegmentColourAt(ColourSegments, t);

    public static Rgb? SegmentColourAt(IEnumerable<ColourSpan> segments, double t)
    {
        foreach (var sg in segments)
            if (t >= sg.T0 && t <= sg.T1)
                return sg.At(t);
        return null;
    }

    /// <summary>
    /// The colour spans from <paramref name="t0"/> to <paramref name="t1"/> (to copy), cut to it and from t0.
    /// </summary>
    public List<ColourSpan> ColourSegment(double t0, double t1)
    {
        var output = new List<ColourSpan>();
        foreach (var sg in ColourSegments)
        {
            if (sg.T1 <= t0 || sg.T0 >= t1)
                continue;
            var a = Math.Max(sg.T0, t0);
            var b = Math.Min(sg.T1, t1);
            output.Add(new ColourSpan(a - t0, b - t0, sg.At(a), sg.At(b)));
        }
        return output;
    }

    /// <summary>
    /// Puts <paramref name="segments"/> (from t0, as <see cref="ColourSegment"/> gives; none clears it) in place of the
    /// colour row from <paramref name="t0"/> to <paramref name="t1"/>: spans overlapping it are cut back to its edges
    /// (keeping their colours there). Cut off at the song's end. Returns whether anything changed.
    /// </summary>
    public bool ReplaceColour(double t0, double t1, IEnumerable<ColourSpan> segments)
    {
        var shift = t0;
        t0 = Math.Max(t0, 0);
        t1 = Math.Min(t1, End);
        if (t1 <= t0)
            return false;
        var output = new List<ColourSpan>();
        foreach (var sg in ColourSegments)
        {
            if (sg.T1 <= t0 || sg.T0 >= t1)
            {
                output.Add(sg);
                continue;
            }
            if (sg.T0 < t0)
                output.Add(new ColourSpan(sg.T0, t0, sg.C0, sg.At(t0)));
            if (sg.T1 > t1)
                output.Add(new ColourSpan(t1, sg.T1, sg.At(t1), sg.C1));
        }
        foreach (var sg in segments)
        {
            var whole = sg with { T0 = sg.T0 + shift, T1 = sg.T1 + shift };
            if (whole.T1 <= t0 || whole.T0 >= t1)
                continue;
            var ca = Math.Max(whole.T0, t0);
            var cb = Math.Min(whole.T1, t1);
            if (cb > ca)
                output.Add(new ColourSpan(ca, cb, whole.At(ca), whole.At(cb)));
        }
        output = [.. output.OrderBy(s => s.T0)];
        if (SameColours(output, ColourSegments))
            return false;
        ColourSegments = output;
        return true;
    }

    static bool SameColours(List<ColourSpan> a, List<ColourSpan> b) =>
        a.Count == b.Count
        && a.Zip(b).All(p =>
            Gd.ApproxEqual(p.First.T0, p.Second.T0)
            && Gd.ApproxEqual(p.First.T1, p.Second.T1)
            && ColourSpan.ApproxEqual(p.First.C0, p.Second.C0)
            && ColourSpan.ApproxEqual(p.First.C1, p.Second.C1)
        );

    // ---------------------------------------------------------------- strobe --

    public bool StrobeAt(double t) => SpanAt(StrobeSegments, t) is not null;

    /// <summary>
    /// The strobe's speed at <paramref name="t"/>, or null where it's off.
    /// </summary>
    public StrobeSpeed? StrobeSpeedAt(double t) => SpanAt(StrobeSegments, t)?.Speed;

    public List<StrobeSpan> StrobeSegment(double t0, double t1) => TaggedSegment(StrobeSegments, t0, t1);

    /// <summary>
    /// Puts <paramref name="segments"/> (from t0; none turns it off there) in place of the strobe row from
    /// <paramref name="t0"/> to <paramref name="t1"/>. Touching spans of the same speed join. Returns whether
    /// anything changed.
    /// </summary>
    public bool ReplaceStrobe(double t0, double t1, IEnumerable<StrobeSpan> segments)
    {
        var output = ReplaceTagged(StrobeSegments, t0, t1, segments);
        if (output.SequenceEqual(StrobeSegments))
            return false;
        StrobeSegments = output;
        return true;
    }

    // -------------------------------------------------------------- position --

    /// <summary>
    /// The named position's id at <paramref name="t"/>, or "" where there's none.
    /// </summary>
    public string PositionAt(double t) => SpanAt(PositionSegments, t)?.Position ?? "";

    public List<PositionSpan> PositionSegment(double t0, double t1) => TaggedSegment(PositionSegments, t0, t1);

    public bool ReplacePosition(double t0, double t1, IEnumerable<PositionSpan> segments)
    {
        var output = ReplaceTagged(PositionSegments, t0, t1, segments);
        if (output.SequenceEqual(PositionSegments))
            return false;
        PositionSegments = output;
        return true;
    }

    // -------------------------------------------------------------- movement --

    public MovementSpan? MovementAt(double t) => SpanAt(MovementSegments, t);

    public List<MovementSpan> MovementSegment(double t0, double t1) => TaggedSegment(MovementSegments, t0, t1);

    public bool ReplaceMovement(double t0, double t1, IEnumerable<MovementSpan> segments)
    {
        var output = ReplaceTagged(MovementSegments, t0, t1, segments);
        if (output.SequenceEqual(MovementSegments))
            return false;
        MovementSegments = output;
        return true;
    }

    // ---------------------------------------------------------- tagged spans --

    public static T? SpanAt<T>(IEnumerable<T> spans, double t)
        where T : TimelineSpan
    {
        foreach (var sg in spans)
            if (t >= sg.T0 && t <= sg.T1)
                return sg;
        return null;
    }

    /// <summary>
    /// The spans from <paramref name="t0"/> to <paramref name="t1"/>, cut to it and from t0.
    /// </summary>
    public static List<T> TaggedSegment<T>(IEnumerable<T> spans, double t0, double t1)
        where T : TimelineSpan
    {
        var output = new List<T>();
        foreach (var sg in spans)
            if (!(sg.T1 <= t0 || sg.T0 >= t1))
                output.Add((T)sg.Over(Math.Max(sg.T0, t0) - t0, Math.Min(sg.T1, t1) - t0));
        return output;
    }

    /// <summary>
    /// <paramref name="spans"/> with <paramref name="t0"/> to <paramref name="t1"/> replaced by
    /// <paramref name="segments"/> (from t0): the spans overlapping it cut back to its edges, the new ones put in (cut
    /// to it and the song's end), and touching spans holding the same joined.
    /// </summary>
    public List<T> ReplaceTagged<T>(List<T> spans, double t0, double t1, IEnumerable<T> segments)
        where T : TimelineSpan
    {
        var shift = t0;
        t0 = Math.Max(t0, 0);
        t1 = Math.Min(t1, End);
        if (t1 <= t0)
            return spans;
        var output = new List<T>();
        foreach (var sg in spans)
        {
            if (sg.T1 <= t0 || sg.T0 >= t1)
            {
                output.Add(sg);
                continue;
            }
            if (sg.T0 < t0)
                output.Add((T)sg.Over(sg.T0, t0));
            if (sg.T1 > t1)
                output.Add((T)sg.Over(t1, sg.T1));
        }
        foreach (var sg in segments)
        {
            var a = Math.Max(sg.T0 + shift, t0);
            var b = Math.Min(sg.T1 + shift, t1);
            if (b > a)
                output.Add((T)sg.Over(a, b));
        }
        var joined = new List<T>();
        foreach (var sg in output.OrderBy(s => s.T0))
        {
            if (joined.Count > 0 && sg.T0 <= joined[^1].T1 + 0.0005 && sg.SameButTimes(joined[^1]))
                joined[^1] = (T)joined[^1].Over(joined[^1].T0, Math.Max(joined[^1].T1, sg.T1));
            else
                joined.Add(sg);
        }
        return joined;
    }
}

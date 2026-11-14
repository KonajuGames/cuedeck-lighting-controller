using System.Numerics;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Fixtures;

namespace Cuedeck.Timelines;

/// <summary>
/// A timeline the generator fills: its key and target, the rows it has, and for a fixture its show category (one of
/// <see cref="FixtureProfile.ShowCategories"/>), category group (1 to 4) and, for one with a colour wheel and no RGB,
/// the colours the wheel makes.
/// </summary>
sealed record GeneratorTrack(
    string Key,
    TimelineTarget Target,
    IReadOnlyList<TimelineRow> Rows,
    string Category = "",
    int Group = 1,
    IReadOnlyList<Rgb>? Wheel = null
);

/// <summary>
/// A named position for the generator: its id, and how far apart it points the beams (0 for unknown).
/// </summary>
sealed record GeneratorPosition(string Id, double Spread = 0);

/// <summary>
/// A song's loudness over time, for the generator: columns spanning the song (a <see cref="SongAnalysis"/>'s peak, low
/// and high wave).
/// </summary>
sealed record SongEnvelope(float[] Peak, float[] Lo, float[] Hi);

/// <summary>
/// What the generator puts in one timeline's rows.
/// </summary>
sealed class GeneratedRows
{
    public List<Vector2> Dimmer { get; set; } = [];
    public List<ColourSpan> Colour { get; set; } = [];
    public List<StrobeSpan> Strobe { get; set; } = [];
    public List<PositionSpan> Position { get; set; } = [];
    public List<MovementSpan> Movement { get; set; } = [];
}

/// <summary>
/// Builds a light show into a song's timelines from its structure, tempo and energy: DJ Edit Mode's Generate.
/// <para>
/// Each section (its energy rescaled across the song, the quietest 0 and the loudest 1) is cut into phrases of
/// <see cref="PHRASE_BARS"/> bars, and every phrase picks its own look from what suits its energy, never one of the
/// last two picked, so a long section keeps changing. The picks come from a random generator seeded by the song and a
/// variation number, as sLight's (<see cref="GodotRandom"/>): the same song and variation always give the same show.
/// </para>
/// <para>
/// The master's dimmer follows the energy, riding the loudness bar by bar; fixtures play dimmer patterns by their
/// part (washes, movers, multi-cells; primary or in support); colours come from a palette per kind of section;
/// strobes build into big rises, with risers ramping up and colours fading to white; breakdowns go darker and cooler;
/// positions widen with the energy, and movement grows with it. Dips and returns in the music start phrases of their
/// own; a kind of section that comes back builds each time, and the last time the loudest comes round is the finale.
/// sLight's <c>TimelineGenerator</c>, ported line for line (its full description is there).
/// </para>
/// </summary>
static class TimelineGenerator
{
    // ------------------------------------------------------------- the tables --

    /// <summary>
    /// Energy (rescaled) above which a section is high, mid and low (below that, calm).
    /// </summary>
    internal const double HIGH = 0.75;
    internal const double MID = 0.5;
    internal const double LOW = 0.3;

    /// <summary>
    /// A rise in energy into the next section that earns a strobe build.
    /// </summary>
    internal const double BUILD_RISE = 0.35;

    /// <summary>
    /// Bars in a phrase: how often a section's look changes.
    /// </summary>
    public const int PHRASE_BARS = 8;

    /// <summary>
    /// A quiet section's moving heads go to a new position every this many phrases.
    /// </summary>
    internal const int QUIET_REPICK = 2;

    /// <summary>
    /// Bars a riser into a build lasts.
    /// </summary>
    internal const int RISER_BARS = 4;

    /// <summary>
    /// A bar under DIP × its section's median loudness is quiet (two in a row a dip); one JUMP × louder than the two
    /// before, a return.
    /// </summary>
    internal const double DIP = 0.6;
    internal const double JUMP = 1.4;

    /// <summary>
    /// How far the master's level rides the loudness.
    /// </summary>
    internal const double RIDE = 0.5;
    internal const double RIDE_MAX = 0.2;

    /// <summary>
    /// [main, accent] hues by section label, turned by a per-song offset.
    /// </summary>
    internal static readonly Dictionary<string, double[]> Palettes = new()
    {
        ["intro"] = [0.60, 0.72],
        ["verse"] = [0.55, 0.85],
        ["pre-chorus"] = [0.80, 0.95],
        ["chorus"] = [0.00, 0.10],
        ["drop"] = [0.95, 0.12],
        ["bridge"] = [0.45, 0.65],
        ["breakdown"] = [0.68, 0.75],
        ["outro"] = [0.62, 0.70],
        ["build"] = [0.08, 0.15],
    };

    /// <summary>
    /// The hue a breakdown's colours are pulled towards (blue), and how far.
    /// </summary>
    internal const double COOL_HUE = 0.62;
    internal const double COOL = 0.75;

    internal static (string, double)[] W(params (string, double)[] o) => o;

    internal static readonly (string, double)[][] DimPatterns =
    [
        W(("follow", 3), ("breathe", 2), ("solo", 1)),
        W(("breathe", 2), ("solo", 2), ("bar_hits", 2), ("wave", 1)),
        W(("bar_hits", 2), ("half_time", 2), ("odd_even", 2), ("wave", 2), ("ping_pong", 1)),
        W(("chase", 3), ("chase_back", 2), ("ping_pong", 2), ("centre_out", 2), ("every_beat", 2), ("offbeat", 1), ("odd_even", 1), ("sparkle", 1)),
    ];

    internal static readonly (string, double)[][] ColourModes =
    [
        W(("all", 3), ("gradient", 1)),
        W(("all", 2), ("split", 2), ("gradient", 1), ("sweep", 1)),
        W(("split", 2), ("thirds", 2), ("gradient", 2), ("sweep", 1)),
        W(("split", 2), ("thirds", 2), ("sweep", 2), ("bar_chase", 2), ("gradient", 1)),
    ];

    internal static readonly (string, double)[][] Moves =
    [
        W(("tilt_sweep", 2), ("pan_sweep", 2)),
        W(("tilt_sweep", 1), ("pan_sweep", 2), ("circle", 2)),
        W(("circle", 2), ("pan_sweep", 2), ("figure8", 1)),
        W(("figure8", 3), ("circle", 2), ("pan_sweep", 1)),
    ];

    /// <summary>
    /// Each movement tier's [size, bars a cycle].
    /// </summary>
    internal static readonly (double Size, double Bars)[] MoveScale = [(0.02, 8), (0.03, 4), (0.045, 2), (0.07, 1)];

    internal static readonly (string, double)[] BreakdownPatterns = W(("solo_dark", 2), ("duo", 2), ("breathe_slow", 2));
    internal static readonly (string, double)[] BreakdownColours = W(("all", 2), ("gradient", 2));
    internal static readonly (string, double)[] BreakdownMoves = W(("tilt_sweep", 2), ("pan_sweep", 1));
    internal static readonly (string, double)[] Rainbow = W(("rainbow", 3));

    /// <summary>
    /// The parts fixtures play.
    /// </summary>
    internal static readonly string[] RoleNames = ["wash", "mover", "multi"];

    internal static readonly (string, double)[][] MoverPatterns =
    [
        W(("follow", 3), ("breathe", 1)),
        W(("follow", 2), ("breathe", 2), ("solo", 1)),
        W(("follow", 1), ("half_time", 2), ("odd_even", 2), ("bar_hits", 1), ("wave", 1)),
        W(("chase", 2), ("ping_pong", 2), ("odd_even", 2), ("every_beat", 1), ("half_time", 1)),
    ];

    internal static readonly (string, double)[][] MoverColours = [W(("all", 1)), W(("all", 3), ("split", 1)), W(("all", 2), ("split", 2)), W(("all", 1), ("split", 2), ("bar_chase", 1))];

    internal static readonly (string, double)[] MoverBreakdown = W(("breathe_slow", 2), ("solo_dark", 1));

    internal static readonly (string, double)[][] CellPatterns =
    [
        W(("follow", 2), ("breathe", 2)),
        W(("breathe", 2), ("wave", 2), ("solo", 1)),
        W(("wave", 2), ("chase", 2), ("ping_pong", 2), ("odd_even", 1), ("centre_out", 1), ("stack", 1)),
        W(("chase", 2), ("chase_back", 1), ("ping_pong", 2), ("centre_out", 2), ("stack", 2), ("sparkle", 1), ("odd_even", 1)),
    ];

    internal static readonly (string, double)[] CellBreakdown = W(("breathe_slow", 2), ("wave", 1), ("solo_dark", 1));

    /// <summary>
    /// How often a phrase has multi-head movers' heads play as cells, by tier, and in a breakdown.
    /// </summary>
    internal static readonly double[] MoverCellsChance = [0.2, 0.35, 0.5, 0.6];
    internal const double MOVER_CELLS_BREAKDOWN = 0.35;

    /// <summary>
    /// How bright secondary fixtures run while supporting the primaries.
    /// </summary>
    internal const double SUPPORT_LEVEL = 0.75;

    internal static readonly (string, double)[] SupportMoves = W(("mirror", 2), ("half", 2), ("still", 1));
    internal static readonly (string, double)[] SupportMovesCalm = W(("half", 2), ("still", 1));

    /// <summary>
    /// Beat patterns a secondary may answer with offbeats.
    /// </summary>
    internal static readonly string[] OnBeat = ["every_beat", "half_time", "bar_hits"];

    /// <summary>
    /// Patterns a bass-heavy or treble-heavy phrase favours.
    /// </summary>
    internal static readonly string[] BassPatterns = ["every_beat", "bar_hits", "half_time"];
    internal static readonly string[] TreblePatterns = ["sparkle", "offbeat"];

    // -------------------------------------------------------- working classes --

    /// <summary>
    /// Weighted random picks that avoid repeating the last two (or, from a short list, the last one).
    /// </summary>
    internal sealed class Picker(GodotRandom rng)
    {
        public List<string> Recent { get; } = [];

        public string Pick(IReadOnlyList<(string Name, double Weight)> options)
        {
            var avoid = options.Count > 1 ? Recent.Skip(Math.Max(Recent.Count - Math.Min(2, options.Count - 1), 0)).ToList() : [];
            var pool = options.Where(o => !avoid.Contains(o.Name)).ToList();
            var total = pool.Sum(o => o.Weight);
            var x = rng.Randf() * total;
            var chosen = pool[^1].Name;
            foreach (var o in pool)
            {
                x -= o.Weight;
                if (x <= 0)
                {
                    chosen = o.Name;
                    break;
                }
            }
            Recent.Add(chosen);
            if (Recent.Count > 2)
                Recent.RemoveAt(0);
            return chosen;
        }
    }

    internal sealed class Section
    {
        public double Start, End, Energy, E, Lift;
        public string Label = "verse";
        public int Tier, Rep;
        public bool Breakdown, Finale;
    }

    internal sealed class Phrase
    {
        public double T0, T1, Fill, Lo = 1, Hi = 1;
        public int S;
        public List<float> Bars = [];
        public bool Dip, Drop;
        public Vector2 Riser = new(-1, -1);
    }

    internal sealed class Envelope
    {
        public float[] Peak = [], Lo = [], Hi = [];
        public double Dur, LoMean, HiMean;
    }

    internal sealed class PlanEntry
    {
        public string Dim = "", Fill = "", Colour = "", Move = "", Fan = "";
        public int MoveTier, Tier, Td, Tc;
        public List<List<int>> Sparkle = [];
        public bool Calm, Fin;
    }

    internal sealed class Pick
    {
        public string Dim = "follow", Fill = "", Colour = "all", Span = "each", Move = "mirror";
        public int Tier, Rot;
        public double Scale = 1;
        public bool ByGroup;
        public List<List<int>> Sparkle = [];
        public Pick? Cells;
    }

    internal sealed record RolePlan(bool Merge, Dictionary<string, Pick> Picks);

    internal sealed class Unit
    {
        public required GeneratorTrack Track;
        public int Rank, Group, Order;
        public List<GeneratorTrack> Heads = [];
    }

    internal sealed record Arrangement(Pick Pick, List<Unit> Units);

    internal sealed record Slot(string Key, Pick Pick, int I, int N, bool Own);

    internal sealed record Context(float[] Beats, double BeatLen, double BarLen, int PerBar, int BarPhase, List<Vector2> Master, Envelope? Env);

    internal sealed record Builds(List<StrobeSpan> Spans, List<Vector2> Blackouts, List<Vector2> Risers);

    // -------------------------------------------------------------- generate --

    /// <summary>
    /// The show. <paramref name="tracks"/> in order (the master, fixtures, their heads); <paramref name="beats"/> the
    /// beat times; <paramref name="perBar"/> beats a bar; <paramref name="positions"/> the named positions;
    /// <paramref name="seedText"/> and <paramref name="variation"/> seed the picks; <paramref name="barPhase"/> the
    /// index of a beat that starts a bar; <paramref name="env"/> the song's loudness, or null. Returns each track's
    /// rows by key (only the rows each has get anything).
    /// </summary>
    public static Dictionary<string, GeneratedRows> Generate(
        IReadOnlyList<SongSection> sections,
        float[] beats,
        int perBar,
        double duration,
        IReadOnlyList<GeneratorTrack> tracks,
        IReadOnlyList<GeneratorPosition>? positions = null,
        string seedText = "",
        int variation = 0,
        int barPhase = 0,
        SongEnvelope? env = null
    )
    {
        positions ??= [];
        var secs = Normalised(sections, duration);
        if (beats.Length == 0)
            beats = EvenBeats(120, duration);
        perBar = Math.Max(perBar, 1);
        var beatLen = MedianGap(beats);
        var barLen = beatLen * perBar;
        var rng = new GodotRandom(GodotRandom.Hash($"{seedText}|{variation}"));
        double lookHue = rng.Randf();
        double lookSat = rng.RandfRange(0.75f, 0.95f);
        double lookThird = rng.RandfRange(0.3f, 0.5f);
        MarkRepeats(secs);
        var en = MakeEnvelope(env, duration);
        var barStarts = BarStarts(beats, perBar, barPhase);
        var phrases = Phrases(secs, barStarts, barLen, Events(secs, barStarts, barLen, en));
        MarkBands(phrases, en);
        var builds = StrobeBuilds(secs, barLen, beatLen, rng);
        foreach (var ph in phrases)
        {
            ph.Riser = new Vector2(-1, -1);
            foreach (var r in builds.Risers)
                if (r.X < ph.T1 - 0.001 && r.Y > ph.T0 + 0.001)
                    ph.Riser = r;
        }

        var output = new Dictionary<string, GeneratedRows>();
        GeneratorTrack? master = null;
        var fixtures = new List<GeneratorTrack>();
        var headsOf = new Dictionary<string, List<GeneratorTrack>>();
        foreach (var t in tracks)
        {
            output[t.Key] = new GeneratedRows();
            switch (t.Target)
            {
                case TimelineTarget.Master:
                    master = t;
                    break;
                case TimelineTarget.Fixture:
                    fixtures.Add(t);
                    break;
                case TimelineTarget.Head:
                    var parent = t.Key.Split('/')[0];
                    if (!headsOf.TryGetValue(parent, out var list))
                        headsOf[parent] = list = [];
                    list.Add(t);
                    break;
            }
        }
        var ens = Ensembles(fixtures, headsOf);
        var most = tracks.Count(t => t.Rows.Contains(TimelineRow.Dimmer));
        var plan = Plan(secs, phrases, rng, most, beatLen);
        var roleRng = new GodotRandom(GodotRandom.Hash($"{seedText}|{variation}|roles"));
        var roles = RolePlans(plan, phrases, ens, roleRng, most, beatLen);

        // The master.
        var mo = master is not null ? output[master.Key] : null;
        var masterLine = MasterDimmer(secs, beatLen, duration, barStarts, barLen, en);
        foreach (var r in builds.Risers)
            masterLine = RampUp(masterLine, r.X, r.Y);
        foreach (var w in builds.Blackouts)
            masterLine = CutBlack(masterLine, w.X, w.Y, TimelineTrack.LevelAt(masterLine, w.Y + 0.5 * Math.Min(beatLen, 1.0)));
        if (master is not null && mo is not null)
        {
            if (master.Rows.Contains(TimelineRow.Dimmer))
                mo.Dimmer = masterLine;
            if (master.Rows.Contains(TimelineRow.Strobe))
                mo.Strobe = WithHits(builds.Spans, secs, phrases, barLen, beatLen);
            if (master.Rows.Contains(TimelineRow.Position) && positions.Count > 0)
                mo.Position = Positions(secs, phrases, Ranked(positions), rng);
        }

        // Dimmers: per phrase, each line's pick and its place among the fixtures (or heads) playing it together; a
        // line runs only from the first phrase it does something of its own to the last.
        var ctx = new Context(beats, beatLen, barLen, perBar, barPhase, masterLine, en);
        var jobs = new Dictionary<string, Slot?[]>();
        for (var p = 0; p < phrases.Count; p++)
        {
            foreach (var role in RoleNames)
            {
                foreach (var arr in Arrangements(roles[p], ens, role))
                {
                    foreach (var slot in DimSlots(arr, role))
                    {
                        if (!jobs.TryGetValue(slot.Key, out var js))
                            jobs[slot.Key] = js = new Slot?[phrases.Count];
                        js[p] = slot;
                    }
                }
            }
        }
        foreach (var (key, js) in jobs)
        {
            int first = -1, last = -1;
            for (var p = 0; p < phrases.Count; p++)
            {
                if (js[p] is { Own: true } j && (j.Pick.Dim != "follow" || j.Pick.Fill != "" || j.Pick.Scale < 1.0 || phrases[p].Riser.X >= 0))
                {
                    if (first < 0)
                        first = p;
                    last = p;
                }
            }
            if (first < 0)
                continue;
            var line = new List<Vector2>();
            for (var p = first; p <= last; p++)
            {
                var j = js[p];
                var pick = j?.Pick ?? new Pick();
                foreach (var pt in PhraseDimmer(pick, phrases[p], j?.I ?? 0, j?.N ?? 1, ctx))
                    Append(line, new Vector2(pt.X, (float)(pt.Y * pick.Scale)));
            }
            foreach (var w in builds.Blackouts)
                line = CutBlack(line, w.X, w.Y, -1);
            output[key].Dimmer = line;
        }

        // Colour.
        for (var p = 0; p < phrases.Count; p++)
        {
            var ph = phrases[p];
            var s = secs[ph.S];
            var pal = Palette(s.Label, lookHue, lookSat, lookThird, s.Breakdown);
            if (master is not null && mo is not null && master.Rows.Contains(TimelineRow.Colour))
                mo.Colour.Add(new ColourSpan(ph.T0, ph.T1, pal[0], plan[p].Colour == "sweep" ? pal[1] : pal[0]));
            List<Vector2> SpansOf(string mode)
            {
                if (mode is "bar_chase" or "rainbow")
                    return [.. ph.Bars.Select((t, m) => new Vector2(t, m + 1 < ph.Bars.Count ? ph.Bars[m + 1] : (float)ph.T1))];
                return [new Vector2((float)ph.T0, (float)ph.T1)];
            }
            foreach (var role in RoleNames)
            {
                foreach (var each in Arrangements(roles[p], ens, role))
                {
                    var arr = each;
                    var pick = arr.Pick;
                    var mode = pick.Colour;
                    // Multi-head movers playing as cells: the cells' colours across their heads; the rest as movers.
                    if (pick.Cells is { } cells)
                    {
                        var heady = arr.Units.Where(u => MultiHead(u, TimelineRow.Colour)).ToList();
                        if (heady.Count > 0)
                        {
                            ColourCells(output, new Arrangement(cells, heady), cells.Colour, SpansOf(cells.Colour), cells.Rot == 0 ? pal : [pal[1], pal[0], pal[2]], lookSat);
                            arr = new Arrangement(pick, [.. arr.Units.Where(u => !heady.Contains(u))]);
                            if (arr.Units.Count == 0)
                                continue;
                        }
                    }
                    // All in the main colour: the master's colour does it, but a colour wheel's row gets it.
                    if (mode == "all" && pick.Rot == 0)
                    {
                        var wheels = arr.Units.Where(u => u.Track.Wheel is not null).ToList();
                        if (wheels.Count == 0)
                            continue;
                        arr = new Arrangement(pick, wheels);
                    }
                    Rgb[] cols = pick.Rot == 0 ? pal : [pal[1], pal[0], pal[2]];
                    if (role == "multi")
                        ColourCells(output, arr, mode, SpansOf(mode), cols, lookSat);
                    else
                        ColourUnits(output, arr, mode, SpansOf(mode), cols, lookSat);
                }
            }
        }
        // Risers: every colour that's set there fades towards white.
        foreach (var r in builds.Risers)
        {
            foreach (var rows in output.Values)
            {
                if (TimelineTrack.SegmentColourAt(rows.Colour, r.X + 0.001) is { } c)
                    rows.Colour = Overlay(rows.Colour, new ColourSpan(r.X, r.Y, c, ColourSpan.Lerp(c, Rgb.White, 0.7)));
            }
        }
        // A colour wheel has only its own colours.
        foreach (var t in tracks)
            if (t.Wheel is { Count: > 0 } wheel)
                output[t.Key].Colour = OnWheel(output[t.Key].Colour, wheel);
        foreach (var rows in output.Values)
            rows.Colour = MergedColours(rows.Colour);

        // Movement, around a position: the master's carries the movers; a fan gives them rows of their own, and
        // secondary movers answer the primaries.
        if (master is not null && mo is not null && master.Rows.Contains(TimelineRow.Movement) && mo.Position.Count > 0)
        {
            List<Unit> Moving(List<Unit> units) => [.. units.Where(u => u.Track.Rows.Contains(TimelineRow.Movement))];
            var primM = Moving(ens["mover"][0]);
            var secM = Moving(ens["mover"][1]);
            for (var p = 0; p < phrases.Count; p++)
            {
                var ph = phrases[p];
                var s = secs[ph.S];
                var scale = MoveScale[plan[p].MoveTier];
                var size = scale.Size * (1.0 + 0.4 * s.Lift) * (s.Finale ? 1.25 : 1.0);
                var mv = new MovementSpan(ph.T0, ph.T1, plan[p].Move, barLen * scale.Bars, size);
                mo.Movement.Add(mv);
                var merge = roles[p].Merge || secM.Count == 0;
                var fanned = merge ? [.. primM, .. secM] : primM;
                var fan = plan[p].Fan;
                if (fan != "" && fanned.Count >= 2)
                {
                    var byGroup = roles[p].Picks.TryGetValue("mover/1", out var mp) && mp.ByGroup;
                    var places = Places(fanned, byGroup);
                    for (var j = 0; j < fanned.Count; j++)
                    {
                        var (at, n) = places[j];
                        output[fanned[j].Track.Key].Movement.Add(mv with { Phase = fan == "spread" ? (double)at / n : 0.5 * (at % 2) });
                    }
                }
                if (merge)
                    continue;
                var how = roles[p].Picks.TryGetValue("mover/2", out var sp) ? sp.Move : "mirror";
                foreach (var u in secM)
                {
                    output[u.Track.Key].Movement.Add(
                        how switch
                        {
                            "mirror" => mv with { Phase = 0.5 },
                            "half" => mv with { Period = mv.Period * 2.0, Size = size * 0.6 },
                            "still" => mv with { Effect = "none" },
                            _ => mv,
                        }
                    );
                }
            }
            foreach (var rows in output.Values)
                rows.Movement = MergedMovements(rows.Movement);
        }
        return output;
    }

    // --------------------------------------------------------- the structure --

    /// <summary>
    /// The sections, sorted and filling the song, their energy rescaled to 0 to 1 across it and their tier. None: the
    /// whole song as one.
    /// </summary>
    internal static List<Section> Normalised(IReadOnlyList<SongSection> sections, double duration)
    {
        var secs = sections
            .Where(s => s.End > s.Start)
            .Select(s => new Section { Start = s.Start, End = Math.Min(s.End, duration), Label = s.Label.ToLowerInvariant(), Energy = s.Energy })
            .OrderBy(s => s.Start)
            .ToList();
        if (secs.Count == 0)
            secs.Add(new Section { Start = 0, End = duration, Label = "verse", Energy = 0.6 });
        var lo = secs.Min(s => s.Energy);
        var hi = secs.Max(s => s.Energy);
        foreach (var s in secs)
        {
            s.E = hi - lo < 0.001 ? 0.6 : (s.Energy - lo) / (hi - lo);
            s.Tier = s.E >= HIGH ? 3 : s.E >= MID ? 2 : s.E >= LOW ? 1 : 0;
        }
        // Breakdowns: so named, or a quiet section between two louder ones.
        for (var i = 0; i < secs.Count; i++)
        {
            var s = secs[i];
            s.Breakdown = s.Label.Contains("breakdown") || s.E <= LOW && i > 0 && i < secs.Count - 1 && secs[i - 1].E >= MID && secs[i + 1].E >= MID;
        }
        return secs;
    }

    /// <summary>
    /// Each section's place among its kind: its repeat, its lift (0 the first time to 1 the last), and the finale (the
    /// last time the loudest returning kind comes round, or with none, the last of the loudest).
    /// </summary>
    internal static void MarkRepeats(List<Section> secs)
    {
        var count = new Dictionary<string, int>();
        var energy = new Dictionary<string, double>();
        foreach (var s in secs)
        {
            s.Rep = count.GetValueOrDefault(s.Label);
            count[s.Label] = s.Rep + 1;
            energy[s.Label] = energy.GetValueOrDefault(s.Label) + s.E;
        }
        var best = "";
        var bestE = HIGH;
        foreach (var (k, n) in count)
        {
            var mean = energy[k] / n;
            if (n > 1 && mean >= bestE)
            {
                best = k;
                bestE = mean;
            }
        }
        var fin = -1;
        for (var i = 0; i < secs.Count; i++)
        {
            var s = secs[i];
            var n = count[s.Label];
            s.Lift = n > 1 ? (double)s.Rep / (n - 1) : 0;
            s.Finale = false;
            if (best != "" && s.Label == best || best == "" && s.E >= 0.99)
                fin = i;
        }
        if (fin >= 0)
            secs[fin].Finale = true;
    }

    internal static List<float> BarStarts(float[] beats, int perBar, int barPhase) => [.. beats.Where((_, i) => Gd.PosMod(i - barPhase, perBar) == 0)];

    internal static Envelope? MakeEnvelope(SongEnvelope? env, double duration)
    {
        if (env is null || env.Peak.Length == 0 || duration <= 0)
            return null;
        var en = new Envelope
        {
            Peak = env.Peak,
            Lo = env.Lo,
            Hi = env.Hi,
            Dur = duration,
        };
        en.LoMean = EnvMean(en, en.Lo, 0, duration);
        en.HiMean = EnvMean(en, en.Hi, 0, duration);
        return en;
    }

    /// <summary>
    /// The mean of an envelope band from <paramref name="t0"/> to <paramref name="t1"/>, or -1 with none.
    /// </summary>
    internal static double EnvMean(Envelope? en, float[] col, double t0, double t1)
    {
        if (en is null || col.Length == 0)
            return -1;
        var n = col.Length;
        var c0 = Math.Clamp((int)(t0 / en.Dur * n), 0, n - 1);
        var c1 = Math.Clamp((int)Math.Ceiling(t1 / en.Dur * n), c0 + 1, n);
        double sum = 0;
        for (var c = c0; c < c1; c++)
            sum += col[c];
        return sum / (c1 - c0);
    }

    internal static float[] Band(Envelope? en, string band) =>
        en is null ? []
        : band == "lo" ? en.Lo
        : band == "hi" ? en.Hi
        : en.Peak;

    internal sealed record Event(double T, string Kind, double TEnd = 0);

    /// <summary>
    /// Where the music drops out and comes back inside each section, by its bars' loudness.
    /// </summary>
    internal static Dictionary<int, List<Event>> Events(List<Section> secs, List<float> barStarts, double barLen, Envelope? en)
    {
        var output = new Dictionary<int, List<Event>>();
        if (en is null)
            return output;
        for (var si = 0; si < secs.Count; si++)
        {
            var s = secs[si];
            var bars = new List<double> { s.Start };
            foreach (var t in barStarts)
                if (t > s.Start + 0.01 && t < s.End - 0.5 * barLen)
                    bars.Add(t);
            if (bars.Count < 4)
                continue;
            var loud = bars.Select(t => EnvMean(en, en.Peak, t, Math.Min(t + barLen, s.End))).ToList();
            var sorted = loud.Order().ToList();
            var med = sorted[sorted.Count / 2];
            var evs = new List<Event>();
            var k = 1;
            while (k < bars.Count)
            {
                if (loud[k] < DIP * med)
                {
                    var e = k;
                    while (e < bars.Count && loud[e] < DIP * med)
                        e++;
                    if (e - k >= 2)
                    {
                        var tEnd = e < bars.Count ? bars[e] : s.End;
                        evs.Add(new Event(bars[k], "dip", tEnd));
                        if (e < bars.Count)
                            evs.Add(new Event(bars[e], "drop"));
                    }
                    k = e + 1;
                    continue;
                }
                if (k >= 2 && loud[k] > JUMP * 0.5 * (loud[k - 1] + loud[k - 2]) && loud[k] >= 0.8 * med)
                    evs.Add(new Event(bars[k], "drop"));
                k++;
            }
            if (evs.Count > 0)
                output[si] = evs;
        }
        return output;
    }

    /// <summary>
    /// The song's phrases: each section cut every <see cref="PHRASE_BARS"/> bars (a leftover under half a phrase joins
    /// the one before), and at its dips and returns.
    /// </summary>
    internal static List<Phrase> Phrases(List<Section> secs, List<float> barStarts, double barLen, Dictionary<int, List<Event>>? events = null)
    {
        var output = new List<Phrase>();
        for (var si = 0; si < secs.Count; si++)
        {
            var s = secs[si];
            double a = s.Start, b = s.End;
            var inside = barStarts.Where(t => t > a + 0.01 && t < b - 0.01).ToList();
            var cuts = new List<double> { a };
            for (var k = PHRASE_BARS - 1; k < inside.Count; k += PHRASE_BARS)
                if (b - inside[k] >= 0.5 * PHRASE_BARS * barLen)
                    cuts.Add(inside[k]);
            var evs = (events?.GetValueOrDefault(si) ?? []).Where(ev => ev.T - a >= 2.0 * barLen - 0.01 && b - ev.T >= 2.0 * barLen - 0.01).ToList();
            if (evs.Count > 0)
            {
                cuts = [.. cuts.Where(c => c == a || !evs.Any(ev => Math.Abs(ev.T - c) < 2.0 * barLen - 0.01))];
                foreach (var ev in evs)
                    if (!cuts.Any(c => Math.Abs(c - ev.T) < 0.01))
                        cuts.Add(ev.T);
                cuts.Sort();
            }
            cuts.Add(b);
            for (var c = 0; c < cuts.Count - 1; c++)
            {
                double t0 = cuts[c], t1 = cuts[c + 1];
                var bars = new List<float> { (float)t0 };
                foreach (var t in inside)
                    if (t > t0 + 0.01 && t < t1 - 0.01)
                        bars.Add(t);
                double fill = bars.Count >= 2 && t1 - bars[^1] >= 0.75 * barLen ? bars[^1] : -1.0;
                var mid = (t0 + t1) * 0.5;
                output.Add(
                    new Phrase
                    {
                        T0 = t0,
                        T1 = t1,
                        S = si,
                        Bars = bars,
                        Fill = fill,
                        Dip = evs.Any(ev => ev.Kind == "dip" && mid >= ev.T && mid < ev.TEnd),
                        Drop = evs.Any(ev => ev.Kind == "drop" && Math.Abs(ev.T - t0) < 0.01),
                    }
                );
            }
        }
        return output;
    }

    /// <summary>
    /// Each phrase's bass and treble against the song's (1 = its average; 1 without an envelope).
    /// </summary>
    internal static void MarkBands(List<Phrase> phrases, Envelope? en)
    {
        foreach (var ph in phrases)
        {
            ph.Lo = ph.Hi = 1;
            if (en is null)
                continue;
            var lo = EnvMean(en, en.Lo, ph.T0, ph.T1);
            var hi = EnvMean(en, en.Hi, ph.T0, ph.T1);
            if (lo >= 0 && en.LoMean > 0.0001)
                ph.Lo = lo / en.LoMean;
            if (hi >= 0 && en.HiMean > 0.0001)
                ph.Hi = hi / en.HiMean;
        }
    }

    // ------------------------------------------------------------- the plan --

    /// <summary>
    /// Each phrase's picks: dimmer pattern, fill, colour mode, movement and its tier, fan, tier, sparkle orders.
    /// </summary>
    internal static List<PlanEntry> Plan(List<Section> secs, List<Phrase> phrases, GodotRandom rng, int n, double beatLen)
    {
        var dimPick = new Picker(rng);
        var colPick = new Picker(rng);
        var movePick = new Picker(rng);
        var output = new List<PlanEntry>();
        foreach (var ph in phrases)
        {
            var s = secs[ph.S];
            var tier = s.Tier;
            var lift = s.Lift;
            var fin = s.Finale;
            var calm = s.Breakdown;
            var td = tier;
            if (ph.Dip)
            {
                // The music's dropped out: two tiers down, no lift.
                td = Math.Max(tier - 2, 0);
                lift = 0;
                fin = false;
            }
            else if (fin)
                td = 3;
            else if (lift >= 0.5 && tier >= 1 && tier < 3 && rng.Randf() < 0.5)
                td++;
            var dim = dimPick.Pick(calm ? BreakdownPatterns : Weighted(DimPatterns[td], ph, td));
            var fill = "";
            if (!calm && td >= 2 && ph.Fill >= 0 && (fin || rng.Randf() < 0.35 + 0.4 * lift))
                fill = td == 3 ? "fill_chase" : "fill_roll";
            var tc = fin ? 3 : Math.Min(td + (lift >= 0.5 ? 1 : 0), 3);
            var colour = colPick.Pick(calm ? BreakdownColours : fin ? [.. ColourModes[tc], .. Rainbow] : ColourModes[tc]);
            var tm = calm ? 0 : fin ? 3 : Math.Min(td, tier);
            var move = movePick.Pick(calm ? BreakdownMoves : Moves[tm]);
            var fan = "";
            if (!calm && tm >= 2 && (fin || rng.Randf() < 0.3 + 0.5 * lift))
                fan = rng.Randf() < 0.5 ? "spread" : "alternate";
            var sparkle = new List<List<int>>();
            if (dim == "sparkle" && n > 0)
                sparkle = Shuffles(rng, n, (int)Math.Ceiling((ph.T1 - ph.T0) / beatLen) + 2);
            // Where the music comes back, the phrase before ends on a fill.
            if (ph.Drop && output.Count > 0 && phrases[output.Count - 1].Fill >= 0 && output[^1].Fill == "")
                output[^1].Fill = "fill_roll";
            output.Add(
                new PlanEntry
                {
                    Dim = dim,
                    Fill = fill,
                    Colour = colour,
                    Move = move,
                    MoveTier = tm,
                    Fan = fan,
                    Tier = calm ? 0 : td,
                    Sparkle = sparkle,
                    Td = td,
                    Tc = tc,
                    Calm = calm,
                    Fin = fin,
                }
            );
        }
        return output;
    }

    /// <summary>
    /// The part a fixture plays, and whether it's primary (1) or secondary (2): from its category, else its rows and
    /// coloured heads.
    /// </summary>
    internal static (string Role, int Rank) RoleOf(GeneratorTrack t, List<GeneratorTrack> heads)
    {
        var cat = t.Category.ToLowerInvariant();
        string role;
        if (cat.Contains("wash"))
            role = "wash";
        else if (cat.Contains("mover"))
            role = "mover";
        else if (cat.Contains("multi"))
            role = "multi";
        else if (t.Rows.Contains(TimelineRow.Movement))
            role = "mover";
        else if (heads.Count(h => h.Rows.Contains(TimelineRow.Colour)) > 1)
            role = "multi";
        else
            role = "wash";
        return (role, cat.StartsWith("secondary", StringComparison.Ordinal) ? 2 : 1);
    }

    /// <summary>
    /// The fixtures by part: [primary units, secondary units], each in category group order (then the rig's). A part
    /// with only secondary fixtures has them lead it.
    /// </summary>
    internal static Dictionary<string, List<Unit>[]> Ensembles(List<GeneratorTrack> fixtures, Dictionary<string, List<GeneratorTrack>> headsOf)
    {
        var output = RoleNames.ToDictionary(r => r, _ => new[] { new List<Unit>(), new List<Unit>() });
        for (var k = 0; k < fixtures.Count; k++)
        {
            var f = fixtures[k];
            var heads = headsOf.GetValueOrDefault(f.Key, []);
            var (role, rank) = RoleOf(f, heads);
            output[role][rank - 1].Add(new Unit { Track = f, Rank = rank, Group = Math.Clamp(f.Group, 1, 4), Heads = heads, Order = k });
        }
        foreach (var role in RoleNames)
        {
            for (var i = 0; i < 2; i++)
                output[role][i] = [.. output[role][i].OrderBy(u => u.Group).ThenBy(u => u.Order)];
            if (output[role][0].Count == 0)
                output[role] = [output[role][1], []];
        }
        return output;
    }

    /// <summary>
    /// Per phrase, each part's picks: primary and (unless they play as one) secondary.
    /// </summary>
    internal static List<RolePlan> RolePlans(List<PlanEntry> plan, List<Phrase> phrases, Dictionary<string, List<Unit>[]> ens, GodotRandom rng, int most, double beatLen)
    {
        var pickers = new Dictionary<string, Picker>();
        Picker PickerOf(string name)
        {
            if (!pickers.TryGetValue(name, out var p))
                pickers[name] = p = new Picker(rng);
            return p;
        }
        var output = new List<RolePlan>();
        for (var p = 0; p < phrases.Count; p++)
        {
            var b = plan[p];
            var ph = phrases[p];
            var (td, calm, fin, tc) = (b.Td, b.Calm, b.Fin, b.Tc);
            var steps = (int)Math.Ceiling((ph.T1 - ph.T0) / beatLen) + 2;
            var merge = fin || ph.Riser.X >= 0 || td == 3 && !calm && rng.Randf() < 0.4;
            var picks = new Dictionary<string, Pick>();
            foreach (var role in RoleNames)
            {
                var pair = ens[role];
                if (pair[0].Count == 0)
                    continue;
                Pick prim;
                if (role == "wash")
                    prim = new Pick { Dim = b.Dim, Fill = b.Fill, Colour = b.Colour, Sparkle = b.Sparkle };
                else
                {
                    IReadOnlyList<(string, double)> dims, cols;
                    if (role == "mover")
                    {
                        dims = calm ? MoverBreakdown : Weighted(MoverPatterns[td], ph, td);
                        cols = calm ? BreakdownColours : MoverColours[tc];
                    }
                    else
                    {
                        dims = calm ? CellBreakdown : Weighted(CellPatterns[td], ph, td);
                        cols = calm ? BreakdownColours : ColourModes[tc];
                    }
                    if (fin)
                        cols = [.. cols, .. Rainbow];
                    var dim = PickerOf(role + "/dim").Pick(dims);
                    var colour = PickerOf(role + "/colour").Pick(cols);
                    prim = new Pick
                    {
                        Dim = dim,
                        Fill = b.Fill,
                        Colour = colour,
                        Sparkle = dim == "sparkle" ? Shuffles(rng, most, steps) : [],
                    };
                }
                prim.Tier = b.Tier;
                prim.Scale = 1;
                prim.Rot = role == "mover" ? 1 : 0;
                prim.ByGroup = prim.Dim != "sparkle" && rng.Randf() < 0.6;
                var together = pair[0].Count + (merge ? pair[1].Count : 0);
                prim.Span = together >= 2 && rng.Randf() < 0.35 ? "across" : "each";
                if (role == "mover")
                    prim.Cells = MoverCells(merge ? [.. pair[0], .. pair[1]] : pair[0], prim, ph, td, tc, calm, fin, PickerOf, "", rng, most, steps);
                picks[role + "/1"] = prim;
                if (pair[1].Count == 0 || merge)
                    continue;

                // Secondary: in support, from the tier below, not the primary's pattern where there's another;
                // offbeats against beat patterns.
                var below = Math.Max(td - 1, 0);
                var pool = role switch
                {
                    "wash" => calm ? BreakdownPatterns : DimPatterns[below],
                    "mover" => calm ? MoverBreakdown : MoverPatterns[below],
                    _ => calm ? CellBreakdown : CellPatterns[below],
                };
                var other = pool.Where(o => o.Item1 != prim.Dim).ToList();
                var dim2 = PickerOf(role + "/dim2").Pick(other.Count > 0 ? other : pool);
                if (!calm && td >= 2 && OnBeat.Contains(prim.Dim) && rng.Randf() < 0.4)
                    dim2 = "offbeat";
                var cols2 = calm ? BreakdownColours : role == "mover" ? MoverColours[tc] : ColourModes[Math.Min(tc, 2)];
                var colour2 = PickerOf(role + "/colour2").Pick(cols2);
                var sparkle2 = dim2 == "sparkle" ? Shuffles(rng, most, steps) : [];
                var byGroup2 = dim2 != "sparkle" && rng.Randf() < 0.6;
                var move2 = PickerOf(role + "/move2").Pick(calm || td <= 1 ? SupportMovesCalm : SupportMoves);
                var sec = new Pick
                {
                    Dim = dim2,
                    Fill = "",
                    Colour = colour2,
                    Sparkle = sparkle2,
                    Tier = Math.Min(b.Tier, 2),
                    Scale = SUPPORT_LEVEL,
                    Rot = 1 - prim.Rot,
                    ByGroup = byGroup2,
                    Span = "each",
                    Move = move2,
                };
                picks[role + "/2"] = sec;
                if (role == "mover")
                    sec.Cells = MoverCells(pair[1], sec, ph, Math.Max(td - 1, 0), Math.Min(tc, 2), calm, false, PickerOf, "2", rng, most, steps);
            }
            output.Add(new RolePlan(merge, picks));
        }
        return output;
    }

    /// <summary>
    /// What a mover pick's multi-head movers play across their heads this phrase, or null where they play its
    /// pattern as one (always with no multi-head movers: the generator's untouched then, so such rigs' shows don't
    /// change).
    /// </summary>
    internal static Pick? MoverCells(
        List<Unit> units,
        Pick pick,
        Phrase ph,
        int td,
        int tc,
        bool calm,
        bool fin,
        Func<string, Picker> pickerOf,
        string suffix,
        GodotRandom rng,
        int most,
        int steps
    )
    {
        if (!units.Any(u => MultiHead(u, TimelineRow.Dimmer) || MultiHead(u, TimelineRow.Colour)))
            return null;
        if (rng.Randf() >= (calm ? MOVER_CELLS_BREAKDOWN : MoverCellsChance[td]))
            return null;
        var dims = calm ? CellBreakdown : suffix != "" ? CellPatterns[td] : Weighted(CellPatterns[td], ph, td);
        IReadOnlyList<(string, double)> cols = calm ? BreakdownColours : fin ? [.. ColourModes[tc], .. Rainbow] : ColourModes[tc];
        var dim = pickerOf("mover/cells" + suffix).Pick(dims);
        var colour = pickerOf("mover/cell_colour" + suffix).Pick(cols);
        return new Pick
        {
            Dim = dim,
            Fill = pick.Fill,
            Tier = pick.Tier,
            Scale = pick.Scale,
            Rot = pick.Rot,
            Colour = colour,
            Span = pick.Span,
            Sparkle = dim == "sparkle" ? Shuffles(rng, most, steps) : [],
        };
    }

    /// <summary>
    /// <paramref name="steps"/> random orders of 0 to n - 1.
    /// </summary>
    internal static List<List<int>> Shuffles(GodotRandom rng, int n, int steps)
    {
        var output = new List<List<int>>();
        for (var j = 0; j < steps; j++)
        {
            var order = Enumerable.Range(0, n).ToList();
            for (var x = n - 1; x > 0; x--)
            {
                var y = rng.RandiRange(0, x);
                (order[x], order[y]) = (order[y], order[x]);
            }
            output.Add(order);
        }
        return output;
    }

    /// <summary>
    /// Who plays which pick in a phrase's part: primary and secondary together under the primary's pick when merged.
    /// </summary>
    internal static List<Arrangement> Arrangements(RolePlan rp, Dictionary<string, List<Unit>[]> ens, string role)
    {
        var pair = ens[role];
        if (!rp.Picks.TryGetValue(role + "/1", out var prim))
            return [];
        if (!rp.Picks.TryGetValue(role + "/2", out var sec))
            return [new Arrangement(prim, [.. pair[0], .. pair[1]])];
        return [new Arrangement(prim, pair[0]), new Arrangement(sec, pair[1])];
    }

    /// <summary>
    /// Each unit's place in a step, (i, n): by category group when asked and there are two or more, else one by one.
    /// </summary>
    internal static List<(int I, int N)> Places(List<Unit> units, bool byGroup)
    {
        var groups = new List<int>();
        foreach (var u in units)
        {
            var g = u.Rank * 10 + u.Group;
            if (!groups.Contains(g))
                groups.Add(g);
        }
        return [.. units.Select((u, k) => byGroup && groups.Count >= 2 ? (groups.IndexOf(u.Rank * 10 + u.Group), groups.Count) : (k, units.Count))];
    }

    /// <summary>
    /// The cells a multi-cell unit steps across: its heads with the row, or (fewer than two) itself if it has it.
    /// </summary>
    internal static List<GeneratorTrack> Cells(Unit u, TimelineRow row)
    {
        var heads = u.Heads.Where(h => h.Rows.Contains(row)).ToList();
        if (heads.Count >= 2)
            return heads;
        return u.Track.Rows.Contains(row) ? [u.Track] : [];
    }

    internal static bool MultiHead(Unit u, TimelineRow row) => u.Heads.Count(h => h.Rows.Contains(row)) >= 2;

    /// <summary>
    /// An arrangement's dimmer lines: washes and movers by fixture (a multi-head mover's heads too, playing with it,
    /// or as cells); multi-cells by head, each fixture's across its own or all of them in a line.
    /// </summary>
    internal static List<Slot> DimSlots(Arrangement arr, string role)
    {
        var pick = arr.Pick;
        var output = new List<Slot>();
        if (role != "multi")
        {
            var units = arr.Units.Where(u => u.Track.Rows.Contains(TimelineRow.Dimmer)).ToList();
            var places = Places(units, pick.ByGroup);
            var heady = new List<Unit>();
            for (var k = 0; k < units.Count; k++)
            {
                output.Add(new Slot(units[k].Track.Key, pick, places[k].I, places[k].N, true));
                if (role == "mover" && MultiHead(units[k], TimelineRow.Dimmer))
                {
                    if (pick.Cells is null)
                        foreach (var h in Cells(units[k], TimelineRow.Dimmer))
                            output.Add(new Slot(h.Key, pick, places[k].I, places[k].N, false));
                    else
                        heady.Add(units[k]);
                }
            }
            if (heady.Count > 0)
                output.AddRange(DimSlots(new Arrangement(pick.Cells!, heady), "multi"));
            return output;
        }
        foreach (var u in arr.Units)
        {
            var cells = Cells(u, TimelineRow.Dimmer);
            for (var c = 0; c < cells.Count; c++)
                output.Add(new Slot(cells[c].Key, pick, c, cells.Count, true));
        }
        if (pick.Span == "across")
            output = [.. output.Select((s, k) => s with { I = k, N = output.Count })];
        return output;
    }

    // ---------------------------------------------------------------- colour --

    internal static void ColourUnits(Dictionary<string, GeneratedRows> output, Arrangement arr, string mode, List<Vector2> spans, Rgb[] cols, double sat)
    {
        var units = new List<(GeneratorTrack? Fixture, List<GeneratorTrack> Heads, int Rank, int Group)>();
        foreach (var u in arr.Units)
        {
            var heads = u.Heads.Where(h => h.Rows.Contains(TimelineRow.Colour)).ToList();
            if (heads.Count > 1)
                units.Add((null, heads, u.Rank, u.Group));
            else if (u.Track.Rows.Contains(TimelineRow.Colour))
                units.Add((u.Track, [], u.Rank, u.Group));
        }
        var places = Places([.. units.Select(u => new Unit { Track = u.Fixture ?? u.Heads[0], Rank = u.Rank, Group = u.Group })], arr.Pick.ByGroup);
        for (var k = 0; k < units.Count; k++)
        {
            var (at, n) = places[k];
            for (var m = 0; m < spans.Count; m++)
            {
                var cs = UnitColour(mode, at, n, m, cols, sat);
                if (units[k].Fixture is { } fixture)
                {
                    output[fixture.Key].Colour.Add(new ColourSpan(spans[m].X, spans[m].Y, cs.C0, cs.C1));
                    continue;
                }
                var nxt = UnitColour(mode, at + 1, n, m, cols, sat);
                var heads = units[k].Heads;
                for (var hi = 0; hi < heads.Count; hi++)
                {
                    var w = (double)hi / (heads.Count - 1);
                    output[heads[hi].Key].Colour.Add(new ColourSpan(spans[m].X, spans[m].Y, ColourSpan.Lerp(cs.C0, nxt.C0, w), ColourSpan.Lerp(cs.C1, nxt.C1, w)));
                }
            }
        }
    }

    internal static void ColourCells(Dictionary<string, GeneratedRows> output, Arrangement arr, string mode, List<Vector2> spans, Rgb[] cols, double sat)
    {
        var all = new List<(string Key, int I, int N)>();
        foreach (var u in arr.Units)
        {
            var cells = Cells(u, TimelineRow.Colour);
            for (var c = 0; c < cells.Count; c++)
                all.Add((cells[c].Key, c, cells.Count));
        }
        if (arr.Pick.Span == "across")
            all = [.. all.Select((x, k) => (x.Key, k, all.Count))];
        foreach (var (key, i, n) in all)
        {
            for (var m = 0; m < spans.Count; m++)
            {
                var cs = UnitColour(mode, i, n, m, cols, sat);
                output[key].Colour.Add(new ColourSpan(spans[m].X, spans[m].Y, cs.C0, cs.C1));
            }
        }
    }

    /// <summary>
    /// A pattern pool weighted for a phrase's sound: bass-heavy favours beat patterns, treble-heavy sparkle and
    /// offbeats.
    /// </summary>
    internal static IReadOnlyList<(string, double)> Weighted((string, double)[] pool, Phrase ph, int tier)
    {
        string[] favour = [];
        if (ph.Lo > 1.15 && ph.Lo >= ph.Hi)
            favour = BassPatterns;
        else if (ph.Hi > 1.15)
            favour = TreblePatterns;
        if (favour.Length == 0 || tier == 0)
            return pool;
        var output = pool.Select(o => (o.Item1, o.Item2 * (favour.Contains(o.Item1) ? 2.0 : 1.0))).ToList();
        foreach (var f in favour)
            if (!pool.Any(o => o.Item1 == f))
                output.Add((f, 1.0));
        return output;
    }

    internal static float[] EvenBeats(double bpm, double duration)
    {
        var output = new List<float>();
        var step = 60.0 / Math.Max(bpm, 1);
        for (var t = 0.0; t < duration; t += step)
            output.Add((float)t);
        return [.. output];
    }

    internal static double MedianGap(float[] beats)
    {
        if (beats.Length < 2)
            return 0.5;
        var gaps = new List<double>();
        for (var i = 1; i < beats.Length; i++)
            gaps.Add((double)beats[i] - beats[i - 1]);
        gaps.Sort();
        return Math.Max(gaps[gaps.Count / 2], 0.05);
    }

    /// <summary>
    /// [main, accent, third] for a section label, the hues turned per song; a breakdown's pulled towards blue.
    /// </summary>
    internal static Rgb[] Palette(string label, double hue, double sat, double third, bool cool)
    {
        var key = label;
        if (!Palettes.ContainsKey(key))
            key = label.Contains("chorus") ? "chorus" : label.Contains("verse") ? "verse" : "bridge";
        var hues = Palettes[key];
        var main = (hues[0] + hue) % 1.0;
        double[] output = [main, (hues[1] + hue) % 1.0, (main + third) % 1.0];
        if (cool)
            for (var i = 0; i < 3; i++)
                output[i] = Gd.FPosMod(output[i] + (Gd.FPosMod(COOL_HUE - output[i] + 0.5, 1.0) - 0.5) * COOL, 1.0);
        return [Rgb.FromHsv(output[0], sat, 1), Rgb.FromHsv(output[1], sat * 0.95, 1), Rgb.FromHsv(output[2], sat, 1)];
    }

    /// <summary>
    /// Unit <paramref name="u"/> of <paramref name="count"/>'s colours in bar <paramref name="m"/> of a phrase, for a
    /// colour mode.
    /// </summary>
    internal static (Rgb C0, Rgb C1) UnitColour(string mode, int u, int count, int m, Rgb[] pal, double sat)
    {
        var c = pal[0];
        switch (mode)
        {
            case "split":
                c = pal[u % 2];
                break;
            case "thirds":
                c = pal[u % 3];
                break;
            case "gradient":
                c = ColourSpan.Lerp(pal[0], pal[1], Math.Clamp(u / Math.Max(count - 1.0, 1), 0, 1));
                break;
            case "sweep":
                return u % 2 == 0 ? (pal[0], pal[1]) : (pal[1], pal[0]);
            case "bar_chase":
                c = pal[(u + m) % 3];
                break;
            case "rainbow":
                c = Rgb.FromHsv(Gd.FPosMod(pal[0].Hue + u / Math.Max((double)count, 1) + m * 0.125, 1), sat, 1);
                break;
        }
        return (c, c);
    }

    // ---------------------------------------------------------------- dimmer --

    /// <summary>
    /// The master's line: each section at 25 to 100 % by its energy (a breakdown at 60 % of that), riding the
    /// loudness bar by bar, a beat's fade between sections; up from 0 at the start, down to 0 at the end.
    /// </summary>
    internal static List<Vector2> MasterDimmer(List<Section> secs, double beatLen, double duration, List<float> barStarts, double barLen, Envelope? en)
    {
        var output = new List<Vector2> { new(0, 0) };
        var fade = Math.Min(beatLen, 1.0);
        for (var i = 0; i < secs.Count; i++)
        {
            var s = secs[i];
            var level = (0.25 + 0.75 * s.E) * (s.Breakdown ? 0.6 : 1.0);
            var a = s.Start + (i == 0 ? fade : fade * 0.5);
            var b = s.End - (i == secs.Count - 1 ? fade : fade * 0.5);
            if (b <= a)
            {
                a = (s.Start + s.End) * 0.5;
                b = a;
            }
            var whole = EnvMean(en, Band(en, "peak"), s.Start, s.End);
            output.Add(new Vector2((float)a, (float)Ride(level, whole, a, s, barStarts, barLen, en)));
            if (whole > 0)
                foreach (var bt in barStarts)
                    if (bt > a + 0.01 && bt < b - 0.01)
                        output.Add(new Vector2(bt, (float)Ride(level, whole, bt, s, barStarts, barLen, en)));
            if (b > a)
                output.Add(new Vector2((float)b, (float)Ride(level, whole, Math.Max(b - 0.01, a), s, barStarts, barLen, en)));
        }
        output.Add(new Vector2((float)duration, 0));
        return output;
    }

    /// <summary>
    /// Godot's <c>PackedFloat32Array.bsearch()</c>: where the (32-bit) value would go, before any equal.
    /// </summary>
    internal static int BSearch(IReadOnlyList<float> list, double value) => Gd.BSearch(list, value);

    internal static double Ride(double level, double whole, double t, Section s, List<float> barStarts, double barLen, Envelope? en)
    {
        if (whole <= 0)
            return level;
        var k = BSearch(barStarts, t + 0.0001) - 1;
        var barT = k >= 0 ? barStarts[k] : t;
        var here = EnvMean(en, Band(en, "peak"), Math.Max(barT, s.Start), Math.Min(barT + barLen, s.End));
        if (s.Label == "outro")
        {
            // Tailing off with the music, against its opening bars, all the way down.
            var opening = EnvMean(en, Band(en, "peak"), s.Start, Math.Min(s.Start + barLen * 2.0, s.End));
            return Math.Clamp(level * here / Math.Max(opening, 0.01), 0, level);
        }
        var d = Math.Clamp(RIDE * (here - whole) / Math.Max(whole, 0.05), -RIDE_MAX, RIDE_MAX);
        return Math.Clamp(level * (1.0 + d), 0.05, 1.0);
    }

    internal static List<Vector2> RampUp(List<Vector2> line, double a, double b)
    {
        var output = line.Where(p => p.X < a).ToList();
        output.Add(new Vector2((float)a, (float)TimelineTrack.LevelAt(line, a)));
        output.Add(new Vector2((float)b, 1));
        output.AddRange(line.Where(p => p.X > b));
        return output;
    }

    /// <summary>
    /// <paramref name="line"/> at 0 from <paramref name="a"/> to <paramref name="b"/>, back up to
    /// <paramref name="after"/> (or, below 0, where it was). A line that doesn't reach the window is left alone.
    /// </summary>
    internal static List<Vector2> CutBlack(List<Vector2> line, double a, double b, double after)
    {
        if (line.Count == 0 || line[0].X > a || line[^1].X < b)
            return line;
        var up = after >= 0 ? after : TimelineTrack.LevelAt(line, b + 0.0001);
        var output = line.Where(p => p.X < a).ToList();
        output.Add(new Vector2((float)a, (float)TimelineTrack.LevelAt(line, a)));
        output.Add(new Vector2((float)a, 0));
        output.Add(new Vector2((float)b, 0));
        output.Add(new Vector2((float)b, (float)up));
        output.AddRange(line.Where(p => p.X > b));
        return output;
    }

    internal static void Append(List<Vector2> line, Vector2 p)
    {
        if (line.Count == 0 || p.X >= line[^1].X)
            line.Add(p);
    }

    internal static List<Vector2> PhraseDimmer(Pick pick, Phrase ph, int i, int n, Context ctx)
    {
        var riser = ph.Riser;
        var end = riser.X < 0 ? ph.T1 : Math.Max(riser.X, ph.T0);
        var fillAt = pick.Fill != "" && riser.X < 0 ? ph.Fill : -1.0;
        var output = Pattern(pick.Dim, ph.T0, fillAt > 0 ? fillAt : end, i, n, pick, ctx);
        if (fillAt > 0)
            foreach (var p in Pattern(pick.Fill, fillAt, ph.T1, i, n, pick, ctx))
                Append(output, p);
        if (riser.X >= 0)
        {
            foreach (var p in Riser(end, Math.Min(riser.Y, ph.T1), riser, i, n, ctx))
                Append(output, p);
            if (riser.Y < ph.T1)
                Append(output, new Vector2((float)ph.T1, 0));
        }
        return output;
    }

    /// <summary>
    /// A riser: a chase across the rig, a beat a step, eighths over the last two bars and sixteenths over the last,
    /// rising from 40 % to full.
    /// </summary>
    internal static List<Vector2> Riser(double a, double b, Vector2 riser, int i, int n, Context ctx)
    {
        var hits = new List<(double T, double Level)>();
        var j = 0;
        var bi = BSearch(ctx.Beats, a - 0.001);
        var decay = ctx.BeatLen;
        while (bi < ctx.Beats.Length && ctx.Beats[bi] < b - 0.001)
        {
            var left = riser.Y - ctx.Beats[bi];
            var sub = left <= ctx.BarLen + 0.001 ? 4 : left <= 2.0 * ctx.BarLen + 0.001 ? 2 : 1;
            for (var q = 0; q < sub; q++)
            {
                var t = ctx.Beats[bi] + q * ctx.BeatLen / sub;
                if (t >= b - 0.001)
                    break;
                if (StepOn("chase", j, i, n))
                    hits.Add((t, Gd.Lerp(0.4, 1.0, Math.Clamp((t - riser.X) / Math.Max(riser.Y - riser.X, 0.01), 0, 1))));
                j++;
            }
            decay = ctx.BeatLen * 1.5 / sub;
            bi++;
        }
        return PulseLine(hits, a, b, decay);
    }

    internal static List<Vector2> Pattern(string name, double a, double b, int i, int n, Pick pick, Context ctx)
    {
        var master = ctx.Master;
        if (b <= a)
            return [];
        if (name == "follow")
        {
            var output = new List<Vector2> { new((float)a, (float)TimelineTrack.LevelAt(master, a)) };
            output.AddRange(master.Where(p => p.X > a && p.X < b));
            output.Add(new Vector2((float)b, (float)TimelineTrack.LevelAt(master, b)));
            return output;
        }
        var line = Shape(name, a, b, i, n, pick, ctx);
        if (pick.Tier < 3)
        {
            // Nodes where the master has them too, so the product follows it between the pattern's own.
            var both = new List<Vector2>();
            var k = 0;
            foreach (var p in line)
            {
                while (k < master.Count && master[k].X < p.X)
                {
                    if (master[k].X > a && (both.Count == 0 || master[k].X > both[^1].X))
                        both.Add(new Vector2(master[k].X, (float)TimelineTrack.LevelAt(line, master[k].X)));
                    k++;
                }
                both.Add(p);
            }
            for (var j = 0; j < both.Count; j++)
                both[j] = both[j] with { Y = (float)(both[j].Y * TimelineTrack.LevelAt(master, both[j].X)) };
            return both;
        }
        return line;
    }

    internal static List<Vector2> Shape(string name, double a, double b, int i, int n, Pick pick, Context ctx)
    {
        var beat = ctx.BeatLen;
        var bar = ctx.BarLen;
        const double PEAK = 1.0;
        switch (name)
        {
            case "breathe" or "breathe_slow":
            {
                var period = bar * (name == "breathe_slow" ? 4.0 : 2.0);
                return Smooth(a, b, beat * 0.5, t => PEAK * (0.25 + 0.75 * (0.5 - 0.5 * Math.Cos(Math.Tau * ((t - a) / period - 0.5 * i / Math.Max(n, 1.0))))));
            }
            case "wave":
                return Smooth(a, b, beat * 0.25, t => PEAK * (0.5 + 0.5 * Math.Sin(Math.Tau * ((t - a) / bar - (double)i / Math.Max(n, 1.0)))));
            case "solo" or "solo_dark" or "duo":
            {
                var output = new List<Vector2>();
                var t = a;
                var m = 0;
                var span = bar * (name == "duo" ? 2.0 : 1.0);
                var low = name == "solo" ? PEAK * 0.12 : 0.0;
                // At least one step, even for a span shorter than the tolerance.
                while (t < b - 0.001 || output.Count == 0)
                {
                    var e = Math.Min(t + span, b);
                    var lit = m % Math.Max(n, 1) == i;
                    if (name == "duo")
                    {
                        var pairs = Math.Max((n + 1) / 2, 1);
                        lit = m % pairs == i % pairs;
                    }
                    var lvl = lit ? PEAK : low;
                    Append(output, new Vector2((float)t, (float)lvl));
                    Append(output, new Vector2((float)Math.Max(e - beat, t), (float)lvl));
                    t = e;
                    m++;
                }
                Append(output, new Vector2((float)b, output[^1].Y));
                return output;
            }
        }

        // The rest are pulses on beats or steps.
        var beats = ctx.Beats;
        var hits = new List<(double T, double Level)>();
        var decay = beat;
        var bi = BSearch(beats, a - 0.001);
        var j = 0;
        var sub = name is "fill_chase" or "fill_roll" || n > ctx.PerBar && name is "chase" or "chase_back" or "ping_pong" or "centre_out" ? 2 : 1;
        while (bi < beats.Length && beats[bi] < b - 0.001)
        {
            double bt = beats[bi];
            var k = Gd.PosMod(bi - ctx.BarPhase, ctx.PerBar);
            for (var q = 0; q < sub; q++)
            {
                var t = bt + q * beat / sub;
                if (t >= b - 0.001)
                    break;
                var on = false;
                var lvl = PEAK;
                switch (name)
                {
                    case "every_beat":
                        on = true;
                        decay = beat * 0.8;
                        break;
                    case "bar_hits":
                        on = k == 0;
                        decay = bar * 0.9;
                        break;
                    case "half_time":
                        on = k % 2 == 0;
                        decay = beat * 1.6;
                        break;
                    case "offbeat":
                        t = bt + beat * 0.5;
                        on = t < b - 0.001;
                        decay = beat * 0.45;
                        break;
                    case "odd_even":
                        on = j % 2 == i % 2;
                        break;
                    case "chase" or "chase_back" or "ping_pong" or "centre_out" or "fill_chase":
                        on = StepOn(name, j, i, n);
                        decay = beat * 1.5 / sub;
                        break;
                    case "stack":
                        on = i <= j % Math.Max(n, 1);
                        decay = beat * 1.05;
                        break;
                    case "sparkle":
                    {
                        // A third of the rig, more where it's brighter.
                        var order = j < pick.Sparkle.Count ? pick.Sparkle[j] : [];
                        if (order.Count > n)
                            order = [.. order.Where(x => x < n)];
                        var count = Math.Clamp(Gd.RoundToInt(n / 3.0 * Treble(ctx.Env, t, beat)), 1, n);
                        var at = order.IndexOf(i);
                        on = at >= 0 && at < count;
                        decay = beat * 0.6;
                        break;
                    }
                    case "fill_roll":
                        on = true;
                        lvl = PEAK * Gd.Lerp(0.5, 1.0, (t - a) / (b - a));
                        decay = beat * 0.4;
                        break;
                }
                if (on)
                    hits.Add((t, lvl));
                j++;
            }
            bi++;
        }
        // Hits follow the bass: the strongest at their level, the weakest at 55 % of it.
        var lows = hits.Select(h => EnvMean(ctx.Env, Band(ctx.Env, "lo"), h.T, h.T + beat * 0.5)).ToList();
        var strongest = lows.Count > 0 ? Math.Max(0, lows.Max()) : 0;
        if (strongest > 0.0001)
            for (var k = 0; k < hits.Count; k++)
                hits[k] = (hits[k].T, hits[k].Level * Math.Clamp(0.55 + 0.45 * lows[k] / strongest, 0.55, 1.0));
        return PulseLine(hits, a, b, decay);
    }

    internal static double Treble(Envelope? en, double t, double beat)
    {
        var hi = EnvMean(en, Band(en, "hi"), t, t + beat);
        if (hi < 0 || en is null || en.HiMean <= 0.0001)
            return 1;
        return hi / en.HiMean;
    }

    /// <summary>
    /// Whether a stepping pattern lights fixture <paramref name="i"/> of <paramref name="n"/> on step
    /// <paramref name="j"/>.
    /// </summary>
    internal static bool StepOn(string name, int j, int i, int n)
    {
        if (n <= 1)
            return true;
        switch (name)
        {
            case "chase_back":
                return n - 1 - j % n == i;
            case "ping_pong":
                var pos = j % (2 * n - 2);
                return (pos < n ? pos : 2 * n - 2 - pos) == i;
            case "centre_out":
                // The fixtures as far from the centre light together, inner first.
                var rings = (int)Math.Floor((n - 1) / 2.0) + 1;
                return j % rings == (int)Math.Floor(Math.Abs(i - (n - 1) / 2.0));
        }
        return j % n == i;
    }

    internal static List<Vector2> Smooth(double a, double b, double step, Func<double, double> level)
    {
        var output = new List<Vector2>();
        for (var t = a; t < b; t += Math.Max(step, 0.01))
            output.Add(new Vector2((float)t, (float)Math.Clamp(level(t), 0, 1)));
        output.Add(new Vector2((float)b, (float)Math.Clamp(level(b), 0, 1)));
        return output;
    }

    /// <summary>
    /// Pulses from <paramref name="a"/> to <paramref name="b"/>: at each hit a step up to its level fading to 0 over
    /// <paramref name="decay"/> (or until the next).
    /// </summary>
    internal static List<Vector2> PulseLine(List<(double T, double Level)> hits, double a, double b, double decay)
    {
        var output = new List<Vector2>();
        for (var k = 0; k < hits.Count; k++)
        {
            var (at, lvl) = hits[k];
            var nxt = k + 1 < hits.Count ? hits[k + 1].T : b;
            if (output.Count == 0 && at > a)
                output.Add(new Vector2((float)a, 0));
            if (output.Count == 0 || output[^1].X < at)
                Append(output, new Vector2((float)at, 0));
            Append(output, new Vector2((float)at, (float)lvl));
            var gone = at + decay;
            if (gone < nxt)
            {
                Append(output, new Vector2((float)gone, 0));
                Append(output, new Vector2((float)nxt, 0));
            }
            else
                Append(output, new Vector2((float)nxt, (float)(lvl * (1.0 - (nxt - at) / decay))));
        }
        if (output.Count == 0)
            output.Add(new Vector2((float)a, 0));
        if (output[^1].X < b)
            output.Add(new Vector2((float)b, 0));
        return output;
    }

    // ---------------------------------------------------------------- strobe --

    /// <summary>
    /// Strobe builds: the last two bars before a section that rises sharply (medium, then fast), half the time (always
    /// into the finale) stopping a beat short for a blackout; and the risers, over the last
    /// <see cref="RISER_BARS"/> bars.
    /// </summary>
    internal static Builds StrobeBuilds(List<Section> secs, double barLen, double beatLen, GodotRandom rng)
    {
        var spans = new List<StrobeSpan>();
        var blackouts = new List<Vector2>();
        var risers = new List<Vector2>();
        for (var i = 0; i < secs.Count - 1; i++)
        {
            var s = secs[i];
            var nxt = secs[i + 1];
            if (nxt.E - s.E < BUILD_RISE || nxt.E < HIGH)
                continue;
            var end = s.End;
            var gap = (nxt.Finale || rng.Randf() < 0.5) && end - s.Start > 3.0 * barLen ? beatLen : 0.0;
            var a = Math.Max(end - 2.0 * barLen, s.Start);
            var mid = Math.Max(end - barLen, a);
            if (mid > a)
                spans.Add(new StrobeSpan(a, mid, StrobeSpeed.Medium));
            if (end - gap > mid)
                spans.Add(new StrobeSpan(mid, end - gap, StrobeSpeed.Fast));
            if (gap > 0)
                blackouts.Add(new Vector2((float)(end - gap), (float)end));
            risers.Add(new Vector2((float)Math.Max(end - RISER_BARS * barLen, s.Start), (float)(end - gap)));
        }
        return new Builds(spans, blackouts, risers);
    }

    /// <summary>
    /// The strobe spans plus fast hits: in the finale over each phrase's last half bar; where the music comes back in
    /// a mid-tier section or louder, over its first beat; each where nothing's running already.
    /// </summary>
    internal static List<StrobeSpan> WithHits(List<StrobeSpan> spans, List<Section> secs, List<Phrase> phrases, double barLen, double beatLen)
    {
        var output = spans.ToList();
        foreach (var ph in phrases)
        {
            var s = secs[ph.S];
            var want = new List<Vector2>();
            if (s.Finale)
                want.Add(new Vector2((float)Math.Max(ph.T1 - barLen * 0.5, ph.T0), (float)ph.T1));
            if (ph.Drop && s.Tier >= 2)
                want.Add(new Vector2((float)ph.T0, (float)Math.Min(ph.T0 + beatLen, ph.T1)));
            foreach (var w in want)
                if (!output.Any(sp => sp.T0 < w.Y && sp.T1 > w.X))
                    output.Add(new StrobeSpan(w.X, w.Y, StrobeSpeed.Fast));
        }
        return [.. output.OrderBy(s => s.T0)];
    }

    // ------------------------------------------------------------- positions --

    /// <summary>
    /// The named positions' ids, narrowest first.
    /// </summary>
    internal static List<string> Ranked(IReadOnlyList<GeneratorPosition> positions) =>
        [.. positions.Select((p, i) => (p.Id, p.Spread, i)).OrderBy(x => x.Spread).ThenBy(x => x.i).Select(x => x.Id)];

    /// <summary>
    /// The master's positions per phrase: by the section's place among the song's by energy (the quietest the
    /// narrowest), around that rank and not the one it's at; a new pick each loud phrase, else each section's first
    /// and every <see cref="QUIET_REPICK"/> phrases.
    /// </summary>
    internal static List<PositionSpan> Positions(List<Section> secs, List<Phrase> phrases, List<string> places, GodotRandom rng)
    {
        var output = new List<PositionSpan>();
        var at = "";
        var pick = new Picker(rng);
        var ranks = EnergyRanks(secs);
        var held = 0;
        for (var p = 0; p < phrases.Count; p++)
        {
            var ph = phrases[p];
            var s = secs[ph.S];
            var first = p == 0 || phrases[p - 1].S != ph.S;
            held = first ? 0 : held + 1;
            if (first || !ph.Dip && (s.Tier >= 2 || held % QUIET_REPICK == 0))
            {
                var x = s.Breakdown ? 0.0 : s.Finale ? 1.0 : ranks[ph.S] - (ph.Dip ? 0.4 : 0.0);
                var r = Math.Clamp(Gd.RoundToInt(Math.Clamp(x, 0, 1) * (places.Count - 1)), 0, places.Count - 1);
                var options = new List<(string, double)>();
                var sides = (r > 0 ? 1 : 0) + (r < places.Count - 1 ? 1 : 0);
                foreach (var k in new[] { r - 1, r, r + 1 })
                    if (k >= 0 && k < places.Count && (places[k] != at || places.Count == 1))
                        options.Add((places[k], k == r ? 3 : sides == 2 ? 1 : 2)); // one neighbour only: both sides' share
                if (options.Count == 0)
                    options.Add((places[r], 1));
                pick.Recent.Clear();
                at = pick.Pick(options);
            }
            output.Add(new PositionSpan(ph.T0, ph.T1, at));
        }
        return MergedPositions(output);
    }

    /// <summary>
    /// Each section's place among the song's by energy, 0 (the quietest) to 1 (the loudest), evenly by rank; sections
    /// of the same energy share one.
    /// </summary>
    internal static List<double> EnergyRanks(List<Section> secs)
    {
        var levels = new List<double>();
        foreach (var s in secs)
            if (!levels.Any(l => Math.Abs(l - s.E) < 0.01))
                levels.Add(s.E);
        levels.Sort();
        var output = new List<double>();
        foreach (var s in secs)
        {
            var k = 0;
            while (k < levels.Count - 1 && Math.Abs(levels[k] - s.E) >= 0.01)
                k++;
            output.Add(levels.Count > 1 ? (double)k / (levels.Count - 1) : s.E);
        }
        return output;
    }

    // ------------------------------------------------------------- the rest --

    /// <summary>
    /// Colour spans with <paramref name="seg"/> laid over them: what it covers cut away, the parts either side kept.
    /// </summary>
    internal static List<ColourSpan> Overlay(List<ColourSpan> segs, ColourSpan seg)
    {
        var output = new List<ColourSpan>();
        foreach (var sg in segs)
        {
            if (sg.T1 <= seg.T0 || sg.T0 >= seg.T1)
            {
                output.Add(sg);
                continue;
            }
            var span = Math.Max(sg.T1 - sg.T0, 0.0001);
            if (sg.T0 < seg.T0)
                output.Add(new ColourSpan(sg.T0, seg.T0, sg.C0, ColourSpan.Lerp(sg.C0, sg.C1, (seg.T0 - sg.T0) / span)));
            if (sg.T1 > seg.T1)
                output.Add(new ColourSpan(seg.T1, sg.T1, ColourSpan.Lerp(sg.C0, sg.C1, (seg.T1 - sg.T0) / span), sg.C1));
        }
        output.Add(seg);
        return [.. output.OrderBy(s => s.T0)];
    }

    /// <summary>
    /// Colour spans in a colour wheel's colours: each held at the nearest to its start, a fade split at its middle.
    /// </summary>
    internal static List<ColourSpan> OnWheel(List<ColourSpan> segs, IReadOnlyList<Rgb> wheel)
    {
        var output = new List<ColourSpan>();
        foreach (var sg in segs)
        {
            var a = Nearest(sg.C0, wheel);
            var b = Nearest(sg.C1, wheel);
            if (a == b)
            {
                output.Add(new ColourSpan(sg.T0, sg.T1, a, a));
                continue;
            }
            var mid = (sg.T0 + sg.T1) * 0.5;
            output.Add(new ColourSpan(sg.T0, mid, a, a));
            output.Add(new ColourSpan(mid, sg.T1, b, b));
        }
        return output;
    }

    internal static Rgb Nearest(Rgb c, IReadOnlyList<Rgb> colours)
    {
        var best = colours[0];
        foreach (var o in colours)
            if (FixtureProfile.ColourDistance(c, o) < FixtureProfile.ColourDistance(c, best))
                best = o;
        return best;
    }

    /// <summary>
    /// Touching spans that hold one same colour joined.
    /// </summary>
    internal static List<ColourSpan> MergedColours(List<ColourSpan> segs)
    {
        var output = new List<ColourSpan>();
        foreach (var sg in segs)
        {
            if (output.Count > 0)
            {
                var prev = output[^1];
                if (Math.Abs(prev.T1 - sg.T0) < 0.0001 && prev.C0 == sg.C0 && prev.C1 == sg.C1 && prev.C0 == prev.C1)
                {
                    output[^1] = prev with { T1 = sg.T1 };
                    continue;
                }
            }
            output.Add(sg);
        }
        return output;
    }

    internal static List<PositionSpan> MergedPositions(List<PositionSpan> segs)
    {
        var output = new List<PositionSpan>();
        foreach (var sg in segs)
        {
            if (output.Count > 0 && Math.Abs(output[^1].T1 - sg.T0) < 0.0001 && output[^1].Position == sg.Position)
                output[^1] = output[^1] with { T1 = sg.T1 };
            else
                output.Add(sg);
        }
        return output;
    }

    internal static List<MovementSpan> MergedMovements(List<MovementSpan> segs)
    {
        var output = new List<MovementSpan>();
        foreach (var sg in segs)
        {
            if (output.Count > 0)
            {
                var prev = output[^1];
                if (Math.Abs(prev.T1 - sg.T0) < 0.0001 && prev.Effect == sg.Effect && prev.Period == sg.Period && prev.Size == sg.Size && prev.Phase == sg.Phase)
                {
                    output[^1] = prev with { T1 = sg.T1 };
                    continue;
                }
            }
            output.Add(sg);
        }
        return output;
    }
}

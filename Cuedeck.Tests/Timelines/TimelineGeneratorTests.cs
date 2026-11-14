using System.Numerics;
using System.Text;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Timelines;
using G = Cuedeck.Timelines.TimelineGenerator;

namespace Cuedeck.Tests.Timelines;

/// <summary>
/// timeline_generator_test.gd: the Generate button's show, built into the timelines from a song's structure, tempo and
/// energy. (Its check of a named position's spread comes with DJ Edit Mode, which works spreads out.)
/// </summary>
public class TimelineGeneratorTests
{
    const TimelineRow Rc = TimelineRow.Colour, Rd = TimelineRow.Dimmer, Rs = TimelineRow.Strobe, Rp = TimelineRow.Position, Rm = TimelineRow.Movement;

    // ------------------------------------------------------------ the song --

    /// <summary>
    /// A song: 120 BPM (bars of 2 s), six sections.
    /// </summary>
    static List<SongSection> Sections() =>
        [
            new(0, 16, "Intro", 0.2, 0),
            new(16, 40, "Verse", 0.4, 0),
            new(40, 64, "Chorus", 0.9, 0),
            new(64, 80, "Bridge", 0.3, 0),
            new(80, 104, "Chorus", 0.95, 0),
            new(104, 120, "Outro", 0.2, 0),
        ];

    static float[] Beats(int n = 240) => [.. Enumerable.Range(0, n).Select(i => i * 0.5f)];

    static GeneratorTrack T(string key, TimelineTarget target, params TimelineRow[] rows) => new(key, target, rows);

    static List<GeneratorTrack> Tracks() =>
        [
            new("master", TimelineTarget.Master, TimelineTrack.RowOrder),
            T("par1", TimelineTarget.Fixture, Rc, Rd),
            T("par2", TimelineTarget.Fixture, Rc, Rd),
            T("bar", TimelineTarget.Fixture, Rs, Rc, Rd),
            T("bar/h0", TimelineTarget.Head, Rc, Rd),
            T("bar/h1", TimelineTarget.Head, Rc, Rd),
            T("bar/h2", TimelineTarget.Head, Rc, Rd),
            T("spot", TimelineTarget.Fixture, Rp, Rm, Rd),
            T("spot2", TimelineTarget.Fixture, Rp, Rm, Rd),
        ];

    static List<GeneratorPosition> Ids(params string[] ids) => [.. ids.Select(id => new GeneratorPosition(id))];

    static Dictionary<string, GeneratedRows> Show(int variation = 0, string seed = "song.mp3") =>
        G.Generate(Sections(), Beats(), 4, 120, Tracks(), Ids("posA", "posB"), seed, variation);

    /// <summary>
    /// An envelope over the 120 s song: loudness by section energy, with the first chorus dropping out from 48 s to
    /// 52 s and the second verse bar (18-20 s) quieter; bass strong on every other half beat; bright 84-92 s.
    /// </summary>
    static SongEnvelope Env()
    {
        var peak = new float[1200];
        var lo = new float[1200];
        var hi = new float[1200];
        for (var c = 0; c < 1200; c++)
        {
            var t = c * 0.1;
            var e = 0.5;
            foreach (var sec in Sections())
                if (t >= sec.Start && t < sec.End)
                    e = sec.Energy;
            if (t >= 48 && t < 52)
                e = 0.1;
            if (t >= 18 && t < 20)
                e *= 0.6;
            peak[c] = (float)e;
            lo[c] = t % 1.0 < 0.5 ? 0.8f : 0.2f;
            hi[c] = t >= 84 && t < 92 ? 0.9f : 0.3f;
        }
        return new SongEnvelope(peak, lo, hi);
    }

    static List<GeneratorPosition> SpreadPositions() => [new("wide", 0.4), new("narrow", 0), new("mid", 0.2), new("wider", 0.3)];

    static G.Context Ctx(float[] beats, int barPhase, List<Vector2> master, G.Envelope? env = null) => new(beats, 0.5, 2, 4, barPhase, master, env);

    static G.Context CtxAt(double t, G.Envelope? en) =>
        Ctx([.. Enumerable.Range(0, 8).Select(i => (float)(t + i * 0.5))], 0, [new(0, 1), new(120, 1)], en);

    static G.Pick Tier(int tier, List<List<int>>? sparkle = null) => new() { Tier = tier, Sparkle = sparkle ?? [] };

    // ----------------------------------------------------------- helpers --

    static double Level(List<Vector2> line, double t) => TimelineTrack.LevelAt(line, t);

    static StrobeSpeed? Speed(List<StrobeSpan> spans, double t) => TimelineTrack.SpanAt(spans, t)?.Speed;

    static Rgb? ColourAt(List<ColourSpan> segs, double t)
    {
        foreach (var sg in segs)
            if (t >= sg.T0 && t < sg.T1)
                return sg.C0;
        return null;
    }

    static double Peak(List<Vector2> line, double a, double b)
    {
        double m = 0;
        foreach (var p in line)
            if (p.X >= a && p.X <= b)
                m = Math.Max(m, p.Y);
        return m;
    }

    static double Low(List<Vector2> line, double a, double b)
    {
        double m = 1;
        foreach (var p in line)
            if (p.X >= a && p.X <= b)
                m = Math.Min(m, p.Y);
        return m;
    }

    static bool Differs(List<Vector2> x, List<Vector2> y, double a, double b)
    {
        for (var t = a; t < b; t += 0.125)
            if (Math.Abs(Level(x, t) - Level(y, t)) > 0.2)
                return true;
        return false;
    }

    static bool IsSorted(List<Vector2> line)
    {
        for (var i = 1; i < line.Count; i++)
            if (line[i].X < line[i - 1].X - 0.0001)
                return false;
        return true;
    }

    static bool StepUpAt(List<Vector2> line, double t)
    {
        for (var i = 0; i < line.Count - 1; i++)
        {
            if (Gd.ApproxEqual(line[i].X, t) && Gd.ApproxEqual(line[i + 1].X, t) && line[i + 1].Y > line[i].Y)
                return true;
            if (Gd.ApproxEqual(line[i].X, t) && i == 0 && line[i].Y >= 0.99)
                return true;
        }
        return false;
    }

    static double LevelAfter(List<Vector2> line, double t) => Level(line, t + 0.0001);

    static int StepUps(List<Vector2> line, double a, double b)
    {
        var n = 0;
        for (var i = 1; i < line.Count; i++)
            if (line[i].X >= a && line[i].X < b && Gd.ApproxEqual(line[i].X, line[i - 1].X) && line[i].Y > line[i - 1].Y + 0.05)
                n++;
        return n;
    }

    /// <summary>
    /// A show as text, to tell whether two are the same.
    /// </summary>
    static string Dump(Dictionary<string, GeneratedRows> show)
    {
        var sb = new StringBuilder();
        foreach (var (k, r) in show)
        {
            sb.Append(k).Append(':');
            foreach (var p in r.Dimmer)
                sb.Append(p.X.ToString("R")).Append(',').Append(p.Y.ToString("R")).Append(';');
            foreach (var x in r.Colour)
                sb.Append(x).Append(';');
            foreach (var x in r.Strobe)
                sb.Append(x).Append(';');
            foreach (var x in r.Position)
                sb.Append(x).Append(';');
            foreach (var x in r.Movement)
                sb.Append(x).Append(';');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static string DumpLine(List<Vector2> line) => string.Join(";", line.Select(p => $"{p.X:R},{p.Y:R}"));

    // ---------------------------------------------------------------- show --

    [Fact]
    public void A_show_from_the_structure()
    {
        var show = Show();
        Assert.Equal(9, show.Count);
        var m = show["master"];
        var md = m.Dimmer;
        Assert.Equal(new Vector2(0, 0), md[0]);
        Assert.Equal(new Vector2(120, 0), md[^1]);
        double verse = Level(md, 28), chorus = Level(md, 52), intro = Level(md, 8);
        Assert.True(chorus > verse && verse > intro, $"intro {intro:F2} < verse {verse:F2} < chorus {chorus:F2}");
        Assert.True(Gd.ApproxEqual(chorus, 0.25 + 0.75 * (0.9 - 0.2) / 0.75), "its level follows the energy");
        Assert.True(Gd.ApproxEqual(Level(md, 101), 1.0), "the loudest section: 100 %");

        // A palette per kind of section: both choruses start in the same colour, the verse another.
        var c1 = ColourAt(m.Colour, 45);
        Assert.NotNull(c1);
        Assert.Equal(c1, ColourAt(m.Colour, 85));
        Assert.NotEqual(c1, ColourAt(m.Colour, 20));

        // Strobe builds in the two bars before each chorus: medium, then fast; none in the verse or outro.
        Assert.Equal(StrobeSpeed.Medium, Speed(m.Strobe, 37));
        Assert.Equal(StrobeSpeed.Fast, Speed(m.Strobe, 38.5));
        Assert.Equal(StrobeSpeed.Medium, Speed(m.Strobe, 77));
        Assert.Equal(StrobeSpeed.Fast, Speed(m.Strobe, 78.5));
        Assert.Null(Speed(m.Strobe, 30));
        Assert.Null(Speed(m.Strobe, 106));

        // Named positions across the song, every movement over one.
        var pos = m.Position;
        Assert.NotEmpty(pos);
        Assert.All(pos, p => Assert.Contains(p.Position, new[] { "posA", "posB" }));
        Assert.True(Gd.ApproxEqual(pos[0].T0, 0) && Gd.ApproxEqual(pos[^1].T1, 120));
        Assert.NotEmpty(m.Movement);
        Assert.All(m.Movement, g => Assert.Contains(pos, p => p.T0 <= g.T0 + 0.001 && p.T1 >= g.T1 - 0.001));
        // Quiet: a small slow sweep; loud: bigger and faster.
        var quiet = TimelineTrack.SpanAt(m.Movement, 8)!;
        var loud = TimelineTrack.SpanAt(m.Movement, 50)!;
        Assert.Contains(quiet.Effect, new[] { "tilt_sweep", "pan_sweep" });
        Assert.True(Gd.ApproxEqual(quiet.Period, 16) && loud.Size > quiet.Size && loud.Period < quiet.Period);

        // Fixtures in the quieter sections: never brighter than the master.
        var p1 = show["par1"].Dimmer;
        var p2 = show["par2"].Dimmer;
        var brighter = -1.0;
        foreach (var (a, b) in new[] { (0.0, 40.0), (64.0, 80.0), (104.0, 120.0) })
            for (var t = a; t < b; t += 0.1)
                if (TimelineTrack.LineDefinedAt(p1, t) && Level(p1, t) > Level(md, t) + 0.02)
                    brighter = t;
        Assert.True(brighter < 0, $"brighter than the master at {brighter:F1} s");
        Assert.True(Peak(p1, 40, 64) > 0.99 && Differs(p1, p2, 40, 64), "in a chorus: full-level hits, the fixtures not all alike");
        Assert.True(IsSorted(p1) && IsSorted(p2));

        // Repeatable; a different song, different colours.
        Assert.Equal(Dump(show), Dump(Show()));
        Assert.NotEqual(c1, ColourAt(Show(0, "another.mp3")["master"].Colour, 45));
    }

    [Fact]
    public void Phrases()
    {
        var secs = G.Normalised([new(0, 64, "Verse", 0.5, 0)], 64);
        List<float> bars = [.. Enumerable.Range(0, 32).Select(i => i * 2f)];
        var ph = G.Phrases(secs, bars, 2);
        Assert.Equal(4, ph.Count); // a 32-bar section: four 8-bar phrases
        Assert.True(Gd.ApproxEqual(ph[1].T0, 16) && Gd.ApproxEqual(ph[3].T1, 64));
        Assert.Equal(8, ph[0].Bars.Count); // each knowing its bars and its last bar (a fill's place)
        Assert.True(Gd.ApproxEqual(ph[0].Fill, 14));
        secs = G.Normalised([new(0, 22, "Verse", 0.5, 0)], 22);
        Assert.Single(G.Phrases(secs, bars, 2)); // an 11-bar section: a 3-bar leftover joins it

        // Bars start on the bar phase's beat (0.5 s, 2.5 s — not 0 s), and the phrases cut on them.
        var bs = G.BarStarts(Beats(80), 4, 1);
        Assert.True(Gd.ApproxEqual(bs[0], 0.5) && Gd.ApproxEqual(bs[1], 2.5));
        ph = G.Phrases(G.Normalised([new(0.5, 40.5, "Verse", 0.5, 0)], 40.5), bs, 2);
        Assert.Equal(3, ph.Count); // 20 bars: 8 + 8 + 4
        Assert.True(Gd.ApproxEqual(ph[1].T0, 16.5) && Gd.ApproxEqual(ph[2].T0, 32.5));
    }

    [Fact]
    public void Patterns()
    {
        List<int> Steps(string name, int js, int n)
        {
            var seq = new List<int>();
            for (var j = 0; j < js; j++)
                for (var i = 0; i < n; i++)
                    if (G.StepOn(name, j, i, n))
                        seq.Add(i);
            return seq;
        }
        Assert.Equal([0, 1, 2, 3, 4, 5], Steps("chase", 6, 6)); // across all the fixtures (six, not four)
        Assert.Equal([0, 1, 2, 1, 0], Steps("ping_pong", 5, 3)); // there and back
        Assert.Equal([1, 2], Enumerable.Range(0, 4).Where(i => G.StepOn("centre_out", 0, i, 4)));
        Assert.Equal([0, 3], Enumerable.Range(0, 4).Where(i => G.StepOn("centre_out", 1, i, 4)));

        // Bar hits on the real bar lines (bar phase 1: 0.5 s, 2.5 s).
        var ctx = Ctx(Beats(40), 1, [new(0, 1), new(20, 1)]);
        var line = G.Pattern("bar_hits", 0, 8, 0, 2, Tier(3), ctx);
        Assert.True(StepUpAt(line, 0.5) && StepUpAt(line, 2.5) && !StepUpAt(line, 2.0));
        line = G.Pattern("follow", 2, 6, 0, 2, Tier(0), ctx);
        Assert.True(line.Count == 2 && Gd.ApproxEqual(line[0].Y, 1.0), "follow: the master's line");
        line = G.Pattern("breathe", 0, 8, 0, 2, Tier(1), ctx);
        Assert.True(Peak(line, 0, 8) <= 1 && Low(line, 0, 8) > 0.2 && Peak(line, 0, 8) - Low(line, 0, 8) > 0.5, "breathe: a slow swell that never goes dark");
        var a = G.Pattern("solo", 0, 8, 0, 2, Tier(1), ctx);
        var b = G.Pattern("solo", 0, 8, 1, 2, Tier(1), ctx);
        Assert.True(Level(a, 0.5) > Level(b, 0.5) && Level(b, 2.5) > Level(a, 2.5), "solo: one fixture at a time, a bar each");

        // A movement's phase starts it that far into its cycle; saved and loaded only where it isn't 0.
        var sg = new MovementSpan(0, 8, "circle", 2, 0.1);
        var half = sg with { Phase = 0.5 };
        var (hp, ht) = half.Offset(0);
        var (sp, st) = sg.Offset(1);
        Assert.True(Gd.ApproxEqual(hp, sp) && Gd.ApproxEqual(ht, st));
        var saved = TimelineFormat.MovementsTo([half, sg]);
        Assert.Equal(6, saved[0]!.AsArray().Count);
        Assert.Equal(5, saved[1]!.AsArray().Count);
        var back = TimelineFormat.MovementsFrom(saved);
        Assert.True(Gd.ApproxEqual(back[0].Phase, 0.5) && back[1].Phase == 0);
    }

    [Fact]
    public void Variety()
    {
        var rng = new GodotRandom(7);
        var pk = new G.Picker(rng);
        var picks = Enumerable.Range(0, 200).Select(_ => pk.Pick(G.DimPatterns[3])).ToList();
        for (var i = 2; i < picks.Count; i++)
            Assert.True(picks[i] != picks[i - 1] && picks[i] != picks[i - 2], "picks never repeat the last two");
        Assert.Equal(G.DimPatterns[3].Length, picks.Distinct().Count()); // and use every pattern

        // A long loud section keeps changing.
        var secs = G.Normalised([new(0, 16, "Intro", 0.1, 0), new(16, 144, "Drop", 1.0, 0)], 144);
        G.MarkRepeats(secs);
        var ph = G.Phrases(secs, [.. Enumerable.Range(0, 72).Select(i => i * 2f)], 2);
        var plan = G.Plan(secs, ph, rng, 4, 0.5);
        var later = plan.Skip(1).ToList();
        Assert.Equal(9, ph.Count);
        Assert.True(later.Select(p => p.Dim).Distinct().Count() >= 4, "dimmer patterns");
        Assert.True(later.Select(p => p.Colour).Distinct().Count() >= 3, "colour modes");
        Assert.True(later.Select(p => p.Move).Distinct().Count() >= 2, "movements");

        // Another variation: another show; the same each time.
        var v1 = Show(1);
        Assert.NotEqual(DumpLine(Show(0)["par1"].Dimmer), DumpLine(v1["par1"].Dimmer));
        Assert.Equal(Dump(v1), Dump(Show(1)));
    }

    [Fact]
    public void Repeats_build()
    {
        var secs = G.Normalised(Sections(), 120);
        G.MarkRepeats(secs);
        Assert.True(secs[2].Rep == 0 && secs[2].Lift == 0 && secs[4].Rep == 1 && Gd.ApproxEqual(secs[4].Lift, 1), "the second chorus: lifted");
        Assert.True(secs[4].Finale && !secs[2].Finale && secs.Count(s => s.Finale) == 1, "the last chorus is the finale (only it)");
        var single = G.Normalised([new(0, 10, "Verse", 0.2, 0), new(10, 20, "Drop", 0.9, 0)], 20);
        G.MarkRepeats(single);
        Assert.True(single[1].Finale, "no returning loud kind: the loudest section is the finale");

        var show = Show();
        var m = show["master"];
        var mv1 = TimelineTrack.SpanAt(m.Movement, 45)!;
        var mv2 = TimelineTrack.SpanAt(m.Movement, 85)!;
        Assert.True(mv2.Size > mv1.Size, $"the finale moves bigger ({mv2.Size:F3} > {mv1.Size:F3})");
        Assert.Contains(mv2.Effect, new[] { "figure8", "circle", "pan_sweep" });
        var s1 = TimelineTrack.SpanAt(show["spot"].Movement, 85);
        var s2 = TimelineTrack.SpanAt(show["spot2"].Movement, 85);
        Assert.True(s1 is not null && s2 is not null && s1.Effect == mv2.Effect && s1.Phase != s2.Phase, "its movers fanned out across one shape");
        Assert.True(Speed(m.Strobe, 95.5) == StrobeSpeed.Fast && Speed(m.Strobe, 103.5) == StrobeSpeed.Fast && Speed(m.Strobe, 90) is null, "strobe hits ending each of its phrases");

        // The build into the finale stops a beat early on a blackout, then straight up; the fixtures dark too.
        var md = m.Dimmer;
        Assert.True(Speed(m.Strobe, 79.7) is null && Level(md, 79.75) < 0.001 && Level(md, 80.01) > 0.9);
        var p1 = show["par1"].Dimmer;
        Assert.True(!TimelineTrack.LineDefinedAt(p1, 79.75) || Level(p1, 79.75) < 0.001);
    }

    [Fact]
    public void Well_formed_over_30_variations()
    {
        for (var v = 0; v < 30; v++)
        {
            var show =
                v % 2 == 0 ? Show(v, "valid.mp3") : G.Generate(Sections(), Beats(), 4, 120, Tracks(), SpreadPositions(), "valid.mp3", v, 0, Env());
            foreach (var (k, c) in show)
            {
                Assert.True(IsSorted(c.Dimmer), $"{k} dimmer (variation {v})");
                Assert.All(c.Dimmer, p => Assert.InRange(p.Y, -0.001f, 1.001f));
                void Spans<TS>(List<TS> segs, string row)
                    where TS : TimelineSpan
                {
                    for (var i = 0; i < segs.Count; i++)
                        Assert.False(segs[i].T1 <= segs[i].T0 || i > 0 && segs[i].T0 < segs[i - 1].T1 - 0.0001, $"{k} {row} (variation {v})");
                }
                for (var i = 0; i < c.Colour.Count; i++)
                    Assert.False(c.Colour[i].T1 <= c.Colour[i].T0 || i > 0 && c.Colour[i].T0 < c.Colour[i - 1].T1 - 0.0001, $"{k} colour (variation {v})");
                Spans(c.Strobe, "strobe");
                Spans(c.Position, "position");
                Spans(c.Movement, "movement");
            }
        }
    }

    [Fact]
    public void The_envelope()
    {
        var show = G.Generate(Sections(), Beats(), 4, 120, Tracks(), Ids("posA", "posB"), "song.mp3", 0, 0, Env());
        var m = show["master"];
        var md = m.Dimmer;
        Assert.True(Level(md, 18.5) < Level(md, 22.5) - 0.02, $"the master rides the loudness ({Level(md, 18.5):F2} < {Level(md, 22.5):F2})");
        var none = Show()["master"].Dimmer;
        Assert.Equal(Level(none, 18.5), Level(none, 22.5)); // without one, flat across the section

        // A dip inside the chorus: its own phrase, the return too.
        var secs = G.Normalised(Sections(), 120);
        var en = G.MakeEnvelope(Env(), 120);
        var bars = G.BarStarts(Beats(), 4, 0);
        var evs = G.Events(secs, bars, 2, en);
        Assert.True(evs.ContainsKey(2));
        Assert.Contains(evs[2], e => e.Kind == "dip" && Gd.ApproxEqual(e.T, 48));
        Assert.Contains(evs[2], e => e.Kind == "drop" && Gd.ApproxEqual(e.T, 52));
        var ph = G.Phrases(secs, bars, 2, evs);
        var dip = ph.Where(p => Gd.ApproxEqual(p.T0, 48)).ToList();
        var back = ph.Where(p => Gd.ApproxEqual(p.T0, 52)).ToList();
        Assert.True(dip.Count == 1 && dip[0].Dip && back.Count == 1 && back[0].Drop, "each starting a phrase");
        Assert.True(Speed(m.Strobe, 52.2) == StrobeSpeed.Fast && Speed(m.Strobe, 53) is null, "a strobe hit on the beat it comes back");
        Assert.True(Level(md, 50) < Level(md, 44), "the master dips with it");

        // The outro (104-120 s) tailing off: the master follows it down.
        var fading = Env();
        for (var c = 1040; c < 1200; c++)
            fading.Peak[c] = (float)(0.2 * (1.0 - (c - 1040) / 160.0));
        var fd = G.Generate(Sections(), Beats(), 4, 120, Tracks(), Ids("posA", "posB"), "song.mp3", 0, 0, fading)["master"].Dimmer;
        double o0 = Level(fd, 105), o1 = Level(fd, 112), o2 = Level(fd, 117);
        Assert.True(o1 < o0 * 0.7 && o2 < o0 * 0.35, $"the master tails off with the outro ({o0:F2}, {o1:F2}, {o2:F2})");

        // Bass accents: every beat, strong bass at full, weak lower.
        var line = G.Pattern("every_beat", 0, 4, 0, 2, Tier(3), Ctx(Beats(40), 0, [new(0, 1), new(20, 1)], en));
        Assert.True(LevelAfter(line, 1.0) > 0.99 && Math.Abs(LevelAfter(line, 1.5) - (0.55 + 0.45 * 0.25)) < 0.02, $"weak at {LevelAfter(line, 1.5):F2}");

        // Sparkle: thicker where it's brighter.
        var order = Enumerable.Range(0, 40).Select(_ => Enumerable.Range(0, 9).ToList()).ToList();
        int bright = 0, dull = 0;
        for (var i = 0; i < 9; i++)
        {
            bright += LevelAfter(G.Pattern("sparkle", 84, 86, i, 9, Tier(3, order), CtxAt(84, en)), 84) > 0.01 ? 1 : 0;
            dull += LevelAfter(G.Pattern("sparkle", 60, 62, i, 9, Tier(3, order), CtxAt(60, en)), 60) > 0.01 ? 1 : 0;
        }
        Assert.True(bright > dull && dull >= 1, $"{bright} lit where it's bright, {dull} where it isn't");

        // A bass-heavy phrase favours the beat patterns.
        var w = G.Weighted(G.DimPatterns[2], new G.Phrase { Lo = 1.5, Hi = 0.8 }, 2).ToDictionary(o => o.Item1, o => o.Item2);
        Assert.True(w["bar_hits"] == 4 && w["half_time"] == 4 && w["wave"] == 2 && w.ContainsKey("every_beat"));
    }

    [Fact]
    public void Contrast()
    {
        var secs = G.Normalised(Sections(), 120);
        Assert.True(secs[3].Breakdown && !secs[1].Breakdown && !secs[5].Breakdown, "the quiet bridge between the choruses: a breakdown");
        var show = Show();
        var m = show["master"];
        var md = m.Dimmer;
        Assert.True(Gd.ApproxEqual(Level(md, 72), 0.6 * (0.25 + 0.75 * secs[3].E)), "darker");
        var c = ColourAt(m.Colour, 70)!.Value;
        Assert.True(Math.Abs(Gd.FPosMod(c.Hue - G.COOL_HUE + 0.5, 1) - 0.5) < 0.16, $"cooler (hue {c.Hue:F2})");

        // One or two fixtures at a time or slow swells, no fills, no fanning.
        var bars = G.BarStarts(Beats(), 4, 0);
        G.MarkRepeats(secs);
        var ph = G.Phrases(secs, bars, 2);
        var plan = G.Plan(secs, ph, new GodotRandom((ulong)Environment.TickCount64), 5, 0.5);
        for (var p = 0; p < ph.Count; p++)
            if (ph[p].S == 3)
                Assert.True(plan[p].Dim is "solo_dark" or "duo" or "breathe_slow" && plan[p].Fill == "" && plan[p].Fan == "");

        // Duo: a pair lit, two bars each, the rest dark.
        var dctx = Ctx(Beats(20), 0, [new(0, 1), new(10, 1)]);
        var a = G.Pattern("duo", 0, 8, 0, 4, Tier(0), dctx);
        var b = G.Pattern("duo", 0, 8, 1, 4, Tier(0), dctx);
        Assert.True(Level(a, 1) > 0.99 && Level(b, 1) < 0.01 && Level(b, 5) > 0.99);

        // The riser into the first chorus (the verse's last four bars): the master climbs to full.
        Assert.True(Level(md, 38.5) > Level(md, 33) + 0.3 && Level(md, 33) < 0.7, $"{Level(md, 33):F2} -> {Level(md, 38.5):F2}");
        int early = 0, late = 0;
        foreach (var k in new[] { "par1", "par2", "bar", "spot", "spot2" })
        {
            early += StepUps(show[k].Dimmer, 32, 34);
            late += StepUps(show[k].Dimmer, 38, 39.5);
        }
        Assert.True(late >= 2 * early && early >= 3, $"the chase speeding up ({early} hits in its first bar, {late} in its last)");
        var c0 = TimelineTrack.SegmentColourAt(m.Colour, 32.1)!.Value;
        var c1 = TimelineTrack.SegmentColourAt(m.Colour, 39.4)!.Value;
        Assert.True(c1.Saturation < c0.Saturation - 0.3, $"the colour fading towards white ({c0.Saturation:F2} -> {c1.Saturation:F2})");
    }

    [Fact]
    public void Positions_by_energy()
    {
        Assert.Equal(["narrow", "mid", "wider", "wide"], G.Ranked(SpreadPositions()));
        Assert.Equal(["b", "a"], G.Ranked(Ids("b", "a"))); // ids alone keep their order
        Assert.Equal([0.0, 2.0 / 4, 3.0 / 4, 1.0 / 4, 1.0, 0.0], G.EnergyRanks(G.Normalised(Sections(), 120)));

        // Over many songs, every position gets a fair share of the time.
        var time = new Dictionary<string, double>();
        for (var v = 0; v < 30; v++)
            foreach (var sp in G.Generate(Sections(), Beats(), 4, 120, Tracks(), Ids("A", "B", "C", "D", "E"), $"fair{v}.mp3", v)["master"].Position)
                time[sp.Position] = time.GetValueOrDefault(sp.Position) + sp.T1 - sp.T0;
        var share = time.Values.Select(x => x / (30 * 120.0)).ToList();
        Assert.True(time.Count == 5 && share.Min() > 0.12 && share.Max() < 0.28, string.Join(", ", time.Select(kv => $"{kv.Key} {kv.Value:F0}")));

        var pos = G.Generate(Sections(), Beats(), 4, 120, Tracks(), SpreadPositions(), "song.mp3")["master"].Position;
        string? At(double t) => TimelineTrack.SpanAt(pos, t)?.Position;
        Assert.Equal("narrow", At(70)); // the breakdown: the narrowest
        Assert.Contains(At(85), new[] { "wider", "wide" }); // the finale: the widest
        Assert.Contains(At(100), new[] { "wider", "wide" });
        Assert.NotEqual(At(41), At(57)); // a loud section: a new one each phrase
        Assert.DoesNotContain(pos, sg => sg.T0 > 16 && sg.T0 < 40); // a quieter one: one per section
        Assert.Contains(At(5), new[] { "narrow", "mid" }); // the intro narrow
    }

    [Fact]
    public void Edge_cases()
    {
        var show = G.Generate([], [], 4, 60, [T("master", TimelineTarget.Master, Rd, Rp, Rm)]);
        var md = show["master"].Dimmer;
        Assert.True(md.Count >= 3 && md[^1] == new Vector2(60, 0), "no structure: the whole song as one section");
        Assert.Empty(show["master"].Position); // no named positions: no position segments
        Assert.Empty(show["master"].Movement); // and no movement (it needs a position to move around)
        show = G.Generate([], [], 4, 60, [T("master", TimelineTarget.Master, Rd, Rm)], Ids("posA"));
        Assert.Empty(show["master"].Movement); // no position row: no movement either
        Assert.Empty(show["master"].Colour); // a row the track hasn't got: left empty
        show = G.Generate([new(0, 30, "Verse", 0.5, 0), new(30, 60, "Chorus", 0.5, 0)], [], 4, 60, [T("master", TimelineTarget.Master, Rd)]);
        Assert.True(Gd.ApproxEqual(Level(show["master"].Dimmer, 15), Level(show["master"].Dimmer, 45)), "sections of equal energy: equal levels");
        show = G.Generate(Sections(), Beats(), 4, 120, [T("solo", TimelineTarget.Fixture, Rd, Rc)]);
        Assert.True(show.Count == 1 && IsSorted(show["solo"].Dimmer), "no master timeline, one fixture: still a show");
    }

    // ---------------------------------------------------------- categories --

    /// <summary>
    /// A rig with every category: washes in two groups (and one secondary), movers (one secondary), two four-head bars.
    /// </summary>
    static List<GeneratorTrack> CatTracks()
    {
        List<GeneratorTrack> output =
        [
            new("master", TimelineTarget.Master, TimelineTrack.RowOrder),
            new("w1", TimelineTarget.Fixture, [Rc, Rd], "Primary Wash", 1),
            new("w2", TimelineTarget.Fixture, [Rc, Rd], "Primary Wash", 2),
            new("w3", TimelineTarget.Fixture, [Rc, Rd], "Primary Wash", 2),
            new("ws", TimelineTarget.Fixture, [Rc, Rd], "Secondary Wash", 1),
            new("m1", TimelineTarget.Fixture, [Rp, Rm, Rd], "Primary Mover", 1),
            new("m2", TimelineTarget.Fixture, [Rp, Rm, Rd], "Primary Mover", 1),
            new("ms", TimelineTarget.Fixture, [Rp, Rm, Rd], "Secondary Mover", 1),
        ];
        foreach (var b in new[] { "b1", "b2" })
        {
            output.Add(new(b, TimelineTarget.Fixture, [Rc, Rd], "Primary Multi-cell", 1));
            for (var h = 0; h < 4; h++)
                output.Add(T($"{b}/h{h}", TimelineTarget.Head, Rc, Rd));
        }
        return output;
    }

    static Dictionary<string, GeneratedRows> CatShow(int variation) =>
        G.Generate(Sections(), Beats(), 4, 120, CatTracks(), Ids("posA", "posB"), "cats.mp3", variation);

    [Fact]
    public void Fixture_categories_and_groups()
    {
        var plain = Tracks();
        Assert.Equal(("wash", 1), G.RoleOf(plain[1], []));
        Assert.Equal(("mover", 1), G.RoleOf(plain[7], []));
        Assert.Equal(("multi", 1), G.RoleOf(plain[3], [plain[4], plain[5], plain[6]]));
        Assert.Equal(("mover", 2), G.RoleOf(new GeneratorTrack("x", TimelineTarget.Fixture, [Rc], "Secondary Mover"), [])); // a category overrides the rows

        // Stack: one more head lit each beat, the first held throughout.
        var sctx = CtxAt(0, null);
        bool Lit(int i, double t) => Level(G.Pattern("stack", 0, 2, i, 4, Tier(3), sctx), t) > 0.5;
        Assert.True(Lit(0, 0.1) && !Lit(1, 0.1) && Lit(0, 1.6) && Lit(3, 1.6) && !Lit(3, 1.1));

        var shows = Enumerable.Range(0, 10).Select(CatShow).ToList();
        Assert.True(shows[0].ContainsKey("w1") && shows[0].ContainsKey("b1/h3") && shows[0].ContainsKey("ms"), "every track filled");

        // Multi-cells: their heads carry the patterns and colours, the fixture follows.
        Assert.Contains(shows, sh => Differs(sh["b1/h0"].Dimmer, sh["b1/h3"].Dimmer, 40, 64));
        Assert.All(shows, sh => Assert.Empty(sh["b1"].Dimmer));
        Assert.Contains(shows, sh => ColourAt(sh["b1/h0"].Colour, 44) is { } c0 && ColourAt(sh["b1/h3"].Colour, 44) is { } c3 && c0 != c3);

        // Secondary: in support, below the primaries; never over its share of the master outside a riser or the
        // finale; at full with them in the finale; in the palette's other colour.
        Assert.Contains(shows, sh => Peak(sh["ws"].Dimmer, 40, 56) <= G.SUPPORT_LEVEL + 0.01 && Peak(sh["w1"].Dimmer, 40, 56) >= 0.95);
        Assert.All(shows, sh => Assert.True(Peak(sh["ws"].Dimmer, 16, 30) <= G.SUPPORT_LEVEL * Peak(sh["master"].Dimmer, 16, 30) + 0.02));
        Assert.All(shows, sh => Assert.True(Peak(sh["ws"].Dimmer, 80, 104) >= 0.95));
        Assert.All(shows, sh => Assert.True(ColourAt(sh["ws"].Colour, 20) is { } c && c != ColourAt(sh["master"].Colour, 20)));

        // Movers: secondary ones mirror, follow slower or hold against the primaries.
        var answered = false;
        foreach (var sh in shows)
        {
            foreach (var mv in sh["ms"].Movement)
            {
                MovementSpan? main = null;
                foreach (var mm in sh["master"].Movement)
                    if (mm.T0 <= mv.T0 + 0.001 && mm.T1 > mv.T0)
                        main = mm;
                if (main is not null && (Gd.ApproxEqual(mv.Phase, 0.5) || mv.Effect == "none" || mv.Period > main.Period + 0.001))
                    answered = true;
            }
        }
        Assert.True(answered);

        // Category groups: a group's fixtures step together, another group apart.
        Assert.Contains(
            shows,
            sh => new[] { 40.0, 56.0, 80.0, 96.0 }.Any(a => !Differs(sh["w2"].Dimmer, sh["w3"].Dimmer, a, a + 8) && Differs(sh["w1"].Dimmer, sh["w2"].Dimmer, a, a + 8))
        );

        Assert.All(shows, sh => Assert.All(sh.Values, r => Assert.True(IsSorted(r.Dimmer))));
        Assert.Equal(Dump(shows[3]), Dump(CatShow(3)));
    }

    /// <summary>
    /// A rig with two three-head movers, a plain mover and two washes.
    /// </summary>
    static List<GeneratorTrack> HeadMoverTracks()
    {
        List<GeneratorTrack> output =
        [
            new("master", TimelineTarget.Master, TimelineTrack.RowOrder),
            T("w1", TimelineTarget.Fixture, Rc, Rd),
            T("w2", TimelineTarget.Fixture, Rc, Rd),
            T("spot", TimelineTarget.Fixture, Rp, Rm, Rd),
        ];
        foreach (var m in new[] { "mh1", "mh2" })
        {
            output.Add(T(m, TimelineTarget.Fixture, Rp, Rm, Rc, Rd));
            for (var h = 0; h < 3; h++)
                output.Add(T($"{m}/h{h}", TimelineTarget.Head, Rc, Rd));
        }
        return output;
    }

    [Fact]
    public void Movers_with_several_heads()
    {
        var tracks = HeadMoverTracks();
        Assert.Equal(("mover", 1), G.RoleOf(tracks[4], [tracks[5], tracks[6], tracks[7]]));
        var shows = Enumerable.Range(0, 10).Select(v => G.Generate(Sections(), Beats(), 4, 120, tracks, Ids("posA", "posB"), "heads.mp3", v)).ToList();
        bool moved = false, fixtureLines = false, cells = false, asOne = false, headColours = false;
        foreach (var sh in shows)
        {
            moved |= sh["mh1"].Movement.Count > 0;
            fixtureLines |= sh["mh1"].Dimmer.Count > 0;
            for (var a = 0; a < 112; a += 8)
            {
                var h0 = sh["mh1/h0"].Dimmer;
                var h2 = sh["mh1/h2"].Dimmer;
                if (Differs(h0, h2, a, a + 8))
                    cells = true;
                else if (
                    h0.Count >= 2
                    && h0[0].X <= a
                    && h0[^1].X >= a + 8
                    && !Differs(h0, sh["mh1"].Dimmer, a, a + 8)
                    && Differs(sh["mh1"].Dimmer, sh["master"].Dimmer, a, a + 8)
                )
                    asOne = true;
                if (ColourAt(sh["mh1/h0"].Colour, a + 1) is { } c0 && ColourAt(sh["mh1/h2"].Colour, a + 1) is { } c2 && c0 != c2)
                    headColours = true;
            }
            Assert.All(sh.Values, r => Assert.True(IsSorted(r.Dimmer)));
        }
        Assert.True(moved, "a multi-head mover still moves (fanned with the movers)");
        Assert.True(fixtureLines, "...and plays the movers' patterns on its own dimmer");
        Assert.True(cells, "its heads sometimes play patterns across them, as cells");
        Assert.True(asOne, "...and around those, the mover's pattern together");
        Assert.True(headColours, "...and colours across them");
        Assert.Equal(Dump(shows[4]), Dump(G.Generate(Sections(), Beats(), 4, 120, tracks, Ids("posA", "posB"), "heads.mp3", 4)));
    }

    [Fact]
    public void Colour_wheel_fixtures()
    {
        Rgb[] wheel = [Rgb.White, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(1, 1, 0)];
        List<GeneratorTrack> tracks =
        [
            new("master", TimelineTarget.Master, TimelineTrack.RowOrder),
            T("par", TimelineTarget.Fixture, Rc, Rd),
            new("spot", TimelineTarget.Fixture, [Rp, Rm, Rc, Rd], Wheel: wheel),
        ];
        for (var v = 0; v < 6; v++)
        {
            var segs = G.Generate(Sections(), Beats(), 4, 120, tracks, Ids("posA"), "wheel.mp3", v)["spot"].Colour;
            Assert.All(segs, sg => Assert.True(wheel.Contains(sg.C0) && wheel.Contains(sg.C1), "only its wheel's colours"));
            Assert.All(segs, sg => Assert.Equal(sg.C0, sg.C1)); // each held (a wheel turns, it can't fade)
            Assert.True(segs.Select(sg => sg.C0).Distinct().Count() >= 2 && segs[0].T0 <= 0.01 && segs[^1].T1 >= 119.9, "covering the song and changing through it");
        }
    }
}

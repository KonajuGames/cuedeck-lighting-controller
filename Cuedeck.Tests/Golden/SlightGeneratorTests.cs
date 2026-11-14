using System.Text.Json.Nodes;
using Cuedeck.Audio;
using Cuedeck.Common;
using Cuedeck.Timelines;

namespace Cuedeck.Tests.Golden;

/// <summary>
/// Checks the timeline generator's shows against sLight's for the same songs, rigs, seeds and variations
/// (generator.json, recorded by tools/slight_generator.gd): every dimmer node, colour, strobe, position and movement
/// the same, to the last bit. Godot's random numbers and 32-bit colour maths are copied exactly, so a variation
/// generated in Cuedeck is the one sLight would have made.
/// </summary>
public class SlightGeneratorTests
{
    const TimelineRow Rc = TimelineRow.Colour, Rd = TimelineRow.Dimmer, Rs = TimelineRow.Strobe, Rp = TimelineRow.Position, Rm = TimelineRow.Movement;

    static List<SongSection> Sections() =>
        [
            new(0, 16, "Intro", 0.2, 0),
            new(16, 40, "Verse", 0.4, 0),
            new(40, 64, "Chorus", 0.9, 0),
            new(64, 80, "Bridge", 0.3, 0),
            new(80, 104, "Chorus", 0.95, 0),
            new(104, 120, "Outro", 0.2, 0),
        ];

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

    static List<GeneratorTrack> WheelTracks() =>
        [
            new("master", TimelineTarget.Master, TimelineTrack.RowOrder),
            T("par", TimelineTarget.Fixture, Rc, Rd),
            new("spot", TimelineTarget.Fixture, [Rp, Rm, Rc, Rd], Wheel: [Rgb.White, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(1, 1, 0)]),
        ];

    /// <summary>
    /// The envelope timeline_generator_test.gd uses, built the same way (in doubles, stored as 32-bit floats).
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

    static List<GeneratorPosition> Ids(params string[] ids) => [.. ids.Select(id => new GeneratorPosition(id))];

    static List<GeneratorPosition> Spread() => [new("wide", 0.4), new("narrow", 0), new("mid", 0.2), new("wider", 0.3)];

    /// <summary>
    /// The cases, in the order tools/slight_generator.gd recorded them.
    /// </summary>
    static List<(string Name, Dictionary<string, GeneratedRows> Show)> Cases()
    {
        float[] beats = [.. Enumerable.Range(0, 240).Select(i => i * 0.5f)];
        var output = new List<(string, Dictionary<string, GeneratedRows>)>();
        void Add(string name, List<GeneratorTrack> tracks, List<GeneratorPosition> positions, string seed, int v, SongEnvelope? env, int barPhase = 0) =>
            output.Add((name, TimelineGenerator.Generate(Sections(), beats, 4, 120, tracks, positions, seed, v, barPhase, env)));
        for (var v = 0; v < 3; v++)
            Add("plain", Tracks(), Ids("posA", "posB"), "song.mp3", v, null);
        foreach (var v in new[] { 1, 3 })
            Add("env", Tracks(), Spread(), "valid.mp3", v, Env());
        Add("phase", Tracks(), Ids("posA", "posB", "posC"), "phase.mp3", 2, Env(), 1);
        for (var v = 0; v < 3; v++)
            Add("cats", CatTracks(), Ids("posA", "posB"), "cats.mp3", v, null);
        Add("cats_env", CatTracks(), Spread(), "cats.mp3", 5, Env());
        for (var v = 0; v < 3; v++)
            Add("heads", HeadMoverTracks(), Ids("posA", "posB"), "heads.mp3", v, null);
        Add("heads_env", HeadMoverTracks(), Ids("posA", "posB"), "heads.mp3", 7, Env());
        for (var v = 0; v < 2; v++)
            Add("wheel", WheelTracks(), Ids("posA"), "wheel.mp3", v, null);
        return output;
    }

    static JsonArray Col(Rgb c) => [(double)c.R, (double)c.G, (double)c.B, (double)c.A];

    static JsonObject Dump(Dictionary<string, GeneratedRows> show)
    {
        var output = new JsonObject();
        foreach (var (k, c) in show)
        {
            output[k] = new JsonObject
            {
                ["dimmer"] = new JsonArray([.. c.Dimmer.Select(p => (JsonNode)new JsonArray((double)p.X, (double)p.Y))]),
                ["colour"] = new JsonArray([.. c.Colour.Select(s => (JsonNode)new JsonArray(s.T0, s.T1, Col(s.C0), Col(s.C1)))]),
                ["strobe"] = new JsonArray([.. c.Strobe.Select(s => (JsonNode)new JsonArray(s.T0, s.T1, (double)(int)s.Speed))]),
                ["position"] = new JsonArray([.. c.Position.Select(s => (JsonNode)new JsonArray(s.T0, s.T1, s.Position))]),
                ["movement"] = new JsonArray([.. c.Movement.Select(s => (JsonNode)new JsonArray(s.T0, s.T1, s.Effect, s.Period, s.Size, s.Phase))]),
            };
        }
        return output;
    }

    /// <summary>
    /// The first place <paramref name="mine"/> and <paramref name="theirs"/> differ, or null: numbers exactly.
    /// </summary>
    static string? FirstDifference(JsonNode? mine, JsonNode? theirs, string path)
    {
        switch (theirs)
        {
            case JsonObject o:
                if (mine is not JsonObject m || m.Count != o.Count || !m.Select(kv => kv.Key).SequenceEqual(o.Select(kv => kv.Key)))
                    return $"{path}: keys {string.Join(",", (mine as JsonObject)?.Select(kv => kv.Key) ?? [])} vs {string.Join(",", o.Select(kv => kv.Key))}";
                foreach (var (k, v) in o)
                    if (FirstDifference(m[k], v, $"{path}.{k}") is { } d)
                        return d;
                return null;
            case JsonArray a:
                if (mine is not JsonArray b || b.Count != a.Count)
                    return $"{path}: {(mine as JsonArray)?.Count} items vs {a.Count}: {mine?.ToJsonString()[..Math.Min(mine.ToJsonString().Length, 400)]} vs {a.ToJsonString()[..Math.Min(a.ToJsonString().Length, 400)]}";
                for (var i = 0; i < a.Count; i++)
                    if (FirstDifference(b[i], a[i], $"{path}[{i}]") is { } d)
                        return d;
                return null;
            case JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number:
                var want = v.GetValue<double>();
                var got = mine is JsonValue mv && mv.GetValueKind() == System.Text.Json.JsonValueKind.Number ? mv.GetValue<double>() : double.NaN;
                return got == want ? null : $"{path}: {got:R} vs {want:R}";
            default:
                return mine?.ToJsonString() == theirs?.ToJsonString() ? null : $"{path}: {mine?.ToJsonString()} vs {theirs?.ToJsonString()}";
        }
    }

    [Fact]
    public void Generated_shows_are_sLights()
    {
        var recorded = JsonNode.Parse(File.ReadAllText(Path.Combine(GoldenScenario.Folder, "generator.json")))!.AsArray();
        var cases = Cases();
        Assert.Equal(recorded.Count, cases.Count);
        for (var i = 0; i < cases.Count; i++)
        {
            Assert.Equal(recorded[i]!["name"]!.GetValue<string>(), cases[i].Name);
            var d = FirstDifference(Dump(cases[i].Show), recorded[i]!["show"], $"case {i} ({cases[i].Name})");
            Assert.True(d is null, d);
        }
    }
}

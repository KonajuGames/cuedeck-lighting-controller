using System.Text;
using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Show;

namespace Cuedeck.Tests.Golden;

/// <summary>
/// Plays <see cref="GoldenScenario"/> frame by frame and checks every universe's output against what sLight put out
/// for the same show and script (scenario.frames.json, recorded by tools/slight_frames.gd): the same DMX, byte for
/// byte, through cues, fades, delays, follows, palettes, chases, effects, faders, virtual dimmers, a live flash, the
/// grand master and a blackout.
/// </summary>
public class SlightOutputTests
{
    /// <summary>
    /// Every frame's output, a hex string of each universe's first <see cref="GoldenScenario.Channels"/> channels.
    /// </summary>
    static List<string[]> Play(LightingShow show)
    {
        show.Dmx.Master = GoldenScenario.GrandMaster;
        var frames = new List<string[]>();
        for (var frame = 0; frame < GoldenScenario.Frames; frame++)
        {
            foreach (var (at, action) in GoldenScenario.Script)
                if (at == frame)
                    GoldenScenario.Do(show, action);
            show.Tick(GoldenScenario.FrameSeconds, transmit: false);
            frames.Add([.. show.Dmx.Universes.Select(u => Convert.ToHexStringLower(u.Output, 0, GoldenScenario.Channels))]);
        }
        return frames;
    }

    [Fact]
    public void The_scenario_show_is_the_one_built_here()
    {
        GoldenScenario.WriteShowIfAsked();
        using var built = GoldenScenario.Build();
        var saved = Json.Parse(File.ReadAllText(Path.Combine(GoldenScenario.Folder, "scenario.slight")));
        Assert.True(Json.SameTree(ShowFile.ToJson(built), saved), "scenario.slight is out of date: run with CUEDECK_WRITE_GOLDEN=1, then record sLight's frames again");
    }

    [Fact]
    public void Every_frame_matches_sLights()
    {
        using var show = new LightingShow();
        Assert.Null(ShowFile.Load(show, Path.Combine(GoldenScenario.Folder, "scenario.slight")));
        var ours = Play(show);

        var recorded = Json.Parse(File.ReadAllText(Path.Combine(GoldenScenario.Folder, "scenario.frames.json")));
        var theirs = recorded.Arr("frames").Select(f => ((JsonArray)f!).Select(u => Json.AsString(u)).ToArray()).ToList();
        Assert.Equal(GoldenScenario.Frames, theirs.Count);

        for (var frame = 0; frame < ours.Count; frame++)
        {
            for (var u = 0; u < ours[frame].Length; u++)
            {
                if (ours[frame][u] == theirs[frame][u])
                    continue;
                var a = Convert.FromHexString(theirs[frame][u]);
                var b = Convert.FromHexString(ours[frame][u]);
                var report = new StringBuilder($"frame {frame} (after {string.Join(", ", GoldenScenario.Script.Where(s => s.Frame <= frame).Select(s => s.Action))}), universe {u}:");
                for (var c = 0; c < a.Length; c++)
                    if (a[c] != b[c])
                        report.Append($" ch {c}: sLight {a[c]}, Cuedeck {b[c]};");
                Assert.Fail(report.ToString());
            }
        }
    }
}

using System.Text.Json.Nodes;
using Cuedeck.Common;
using Cuedeck.Show;

namespace Cuedeck.Tests.Show;

/// <summary>
/// sLight's cue_timing_test.gd: fades, delays and follows, and what takes over from them. (How the cue list shows
/// them comes with the Cues tab.)
/// </summary>
public sealed class CueTimingTests : IDisposable
{
    readonly LightingShow _show = new();

    public void Dispose() => _show.Dispose();

    CueList Cues => _show.Cues;

    /// <summary>
    /// Runs the fades and the cue timers on by <paramref name="secs"/>, in 50 ms steps.
    /// </summary>
    void Step(double secs)
    {
        var n = (int)Math.Round(secs / 0.05, MidpointRounding.AwayFromZero);
        for (var i = 0; i < n; i++)
        {
            _show.Dmx.Advance(0.05);
            Cues.Advance(0.05);
        }
    }

    static Cue MakeCue(int level, double fade, double delay = 0, bool follow = false, double wait = 0)
    {
        var c = new Cue($"L{level}", fade, fade) { Delay = delay, Follow = follow, FollowWait = wait };
        c.SetLevels([new Dictionary<int, int> { [0] = level }]);
        return c;
    }

    int Level => _show.Dmx.Universes[0].GetChannel(0);

    void Reset(params Cue[] list)
    {
        Cues.Halt();
        _show.Dmx.Universes[0].SetAll(0);
        Cues.FromJson(new JsonObject { ["cues"] = new JsonArray([.. list.Select(c => (JsonNode)c.ToJson())]) });
    }

    [Fact]
    public void Delay_and_follow_are_saved_with_the_cue()
    {
        var back = Cue.FromJson(MakeCue(10, 1.0, 1.5, true, 2.5).ToJson());
        Assert.Equal((1.5, true, 2.5), (back.Delay, back.Follow, back.FollowWait));
        var old = Cue.FromJson(Json.Parse("""{"label":"x","levels":[]}"""));
        Assert.Equal((0.0, false), (old.Delay, old.Follow));
        Assert.Equal(0.0, Cue.FromJson(Json.Parse("""{"delay":-3}""")).Delay);
    }

    [Fact]
    public void A_plain_cue_and_the_progress_bar()
    {
        Reset(MakeCue(200, 2.0));
        Cues.Go();
        Assert.True(Cues.Progress.Value < 0.01);
        Assert.Contains("Cue 1", Cues.Progress.Text);
        Step(1.0);
        Assert.True(Math.Abs(Cues.Progress.Value - 0.5) < 0.05, $"{Cues.Progress.Value}");
        Assert.InRange(Level, 97, 103);
        Assert.Contains("50%", Cues.Progress.Text);
        Assert.Contains("1.0s", Cues.Progress.Text);
        Step(1.1);
        Assert.Equal(200, Level);
        Assert.Equal((1.0, ""), Cues.Progress);
        Assert.False(Cues.IsWaiting);

        // Only rising channels: the fade's as long as its up time.
        var c = MakeCue(100, 1.0);
        c.FadeDown = 9;
        Reset(c);
        Cues.Go();
        Step(0.5);
        Assert.True(Math.Abs(_show.Dmx.FadeProgress - 0.5) < 0.06, $"{_show.Dmx.FadeProgress}");
    }

    [Fact]
    public void Delay()
    {
        Reset(MakeCue(200, 1.0, 2.0));
        Cues.Go();
        Assert.Equal(1, Cues.CurrentNumber);
        Assert.True(Cues.IsWaiting);
        Step(1.0);
        Assert.Equal(0, Level); // nothing moves during the delay
        Assert.Contains("Delay 1.0s", Cues.Progress.Text);
        Assert.True(Math.Abs(Cues.Progress.Value - 0.5) < 0.05);
        Step(1.5);
        Assert.InRange(Level, 1, 199); // then the fade starts
        Assert.Contains("Cue 1", Cues.Progress.Text);
        Step(1.0);
        Assert.Equal(200, Level);
    }

    [Fact]
    public void Follow()
    {
        Reset(MakeCue(100, 1.0, 0, true, 1.0), MakeCue(200, 0, 0, true), MakeCue(50, 1.0), MakeCue(0, 0));
        Cues.Go();
        Step(1.1);
        Assert.Equal(100, Level);
        Assert.Equal(1, Cues.CurrentNumber);
        Assert.True(Cues.IsWaiting); // its follow waits
        Assert.Contains("Follow in", Cues.Progress.Text);
        Step(0.5);
        Assert.Equal(1, Cues.CurrentNumber);
        Step(0.6);
        Assert.True(Cues.CurrentNumber >= 2); // then GOes cue 2 by itself
        Step(0.1);
        Assert.Equal(3, Cues.CurrentNumber); // cue 2 (no fade, no wait) follows straight on
        Step(1.1);
        Assert.Equal(50, Level);
        Assert.Equal(3, Cues.CurrentNumber);
        Assert.False(Cues.IsWaiting); // cue 3 has no follow: the sequence stops

        // The last cue's follow has nowhere to go.
        Reset(MakeCue(100, 0, 0, true));
        Cues.Go();
        Step(0.2);
        Assert.False(Cues.IsWaiting);
        Assert.Equal(1, Cues.CurrentNumber);
    }

    [Fact]
    public void Go_halt_and_loading_take_over()
    {
        Reset(MakeCue(100, 0.5, 0, true, 5.0), MakeCue(200, 0), MakeCue(30, 0));
        Cues.Go();
        Step(0.6);
        Assert.True(Cues.IsWaiting);
        Cues.Go();
        Assert.Equal(2, Cues.CurrentNumber);
        Assert.False(Cues.IsWaiting); // GO takes over
        Step(6.0);
        Assert.Equal(2, Cues.CurrentNumber); // and it doesn't fire later

        Reset(MakeCue(100, 0.5, 0, true, 1.0), MakeCue(200, 0));
        Cues.Go();
        Step(0.6);
        Cues.Halt();
        Step(2.0);
        Assert.Equal(1, Cues.CurrentNumber);
        Assert.False(Cues.IsWaiting); // Halt stops a follow

        Reset(MakeCue(100, 1.0, 3.0), MakeCue(200, 0));
        Cues.Go();
        Step(1.0);
        Cues.Halt();
        Step(3.0);
        Assert.Equal(0, Level); // and a delay

        Reset(MakeCue(100, 1.0, 3.0));
        Cues.Go();
        Cues.FromJson(new JsonObject { ["cues"] = new JsonArray(MakeCue(5, 0).ToJson()) });
        Step(4.0);
        Assert.Equal(0, Level);
        Assert.False(Cues.IsWaiting); // loading another cue list drops what was waiting

        // A delayed cue lets the last cue's fade run on.
        Reset(MakeCue(200, 2.0), MakeCue(10, 0, 5.0));
        Cues.Go();
        Step(1.0);
        Cues.Go();
        Step(1.05);
        Assert.Equal(200, Level);
        Assert.Equal(2, Cues.CurrentNumber);
        Assert.Contains("Delay", Cues.Progress.Text);
    }

    [Fact]
    public void Tracking_cues_carry_through_until_a_block()
    {
        var u = _show.Dmx.Universes[0];
        u.SetChannel(0, 100);
        u.SetChannel(1, 50);
        Cues.Record(-1); // tracking: both
        u.SetChannel(1, 0);
        u.SetChannel(2, 200);
        Cues.Record(0); // tracking: 1 to 0, 2 up
        Assert.Equal(new Dictionary<int, int> { [1] = 0, [2] = 200 }, Cues.Cues[1].Levels.Universes[0]);
        Assert.Equal((100, 0, 200), (Cues.StandingLook(2)!.Level(0, 0), Cues.StandingLook(2)!.Level(0, 1), Cues.StandingLook(2)!.Level(0, 2)));

        Cues.TrackingEnabled = false;
        u.SetAll(0);
        u.SetChannel(5, 9);
        Cues.Record(1); // a block wipes what came before
        Assert.Equal(new Dictionary<int, int> { [5] = 9 }, Cues.Cues[2].Levels.Universes[0]);
        Assert.Equal(0, Cues.StandingLook(3)!.Level(0, 0));

        // Editing an upstream cue ripples down to the block.
        Cues.Cues[0].Levels.Universes[0][0] = 255;
        Assert.Equal(255, Cues.StandingLook(2)!.Level(0, 0));
        Assert.Equal(0, Cues.StandingLook(3)!.Level(0, 0));
    }
}
